using Data;
using Model;
using System.Collections.Generic;
using UnityEngine;

namespace Generators
{
    /// <summary>
    /// Hybrid generator: RWC-style grid room placement + DK-style connectivity.
    ///
    /// Room placement  — fixed chunk grid (10×8 tiles per room), same as RoomsWithCorridors.
    /// Connectivity    — Bowyer-Watson Delaunay triangulation → Kruskal MST,
    ///                   then smart loop edges chosen by shortcut score
    ///                   (MST path distance / Euclidean distance), favouring physically
    ///                   nearby rooms that are path-far in the MST.
    /// Corridor style  — RWC convention: 1-wide L-shaped, Floor for adjacent rooms
    ///                   (chunkDist == 1), Path for distant rooms.
    /// Post-processing — RWC step 6 / 6b: restore room interiors and upgrade
    ///                   adjacent-room corridor tiles to Floor.
    /// Adjacency data  — fully compatible with MapTraversal (inherits RWC export API).
    /// </summary>
    [CreateAssetMenu(fileName = "DelaunayRWCGenerator", menuName = "Generators/DelaunayRWC")]
    public class DelaunayRWCGenerator : RoomsWithCorridorsGenerator
    {
        public override string Name => "DelaunayRWC";

        // Shadow the parent's private constants (RWC declares them private, not protected)
        private const int   RoomW        = 10;
        private const int   RoomH        = 8;
        private const float LoopFraction = 0.15f;

        // ── Entry point ───────────────────────────────────────────────────────────

        public override void Generate(MapGrid grid, MapConfig config)
        {
            Random.InitState(config.seed);

            for (int x = 0; x < grid.Width;  x++)
            for (int y = 0; y < grid.Height; y++)
                grid.Set(x, y, TileType.Wall);

            // ── 1. Place rooms on chunk grid ──────────────────────────────────────
            int cols = Mathf.Max(2, grid.Width  / RoomW);
            int rows = Mathf.Max(2, grid.Height / RoomH);
            var hasRoom = new bool[cols, rows];
            int target  = Random.Range(6, Mathf.Min(30, cols * rows));
            var roomPositions = new List<Vector2Int>();

            while (roomPositions.Count < target)
            {
                int cx = Random.Range(0, cols);
                int cy = Random.Range(0, rows);
                if (hasRoom[cx, cy]) continue;
                hasRoom[cx, cy] = true;
                roomPositions.Add(new Vector2Int(cx, cy));
            }

            // ── 2. Carve room interiors ───────────────────────────────────────────
            foreach (var pos in roomPositions)
                CarveRoom(grid, pos.x, pos.y);

            // ── 3. Room centers in world space (for Delaunay) ─────────────────────
            var centers = new List<Vector2Int>(roomPositions.Count);
            foreach (var pos in roomPositions)
                centers.Add(new Vector2Int(pos.x * RoomW + RoomW / 2,
                                           pos.y * RoomH + RoomH / 2));

            // ── 4. Delaunay triangulation ─────────────────────────────────────────
            List<(int, int)> allEdges;
            if (centers.Count < 3)
            {
                allEdges = new List<(int, int)>();
                for (int i = 0; i < centers.Count - 1; i++)
                    allEdges.Add((i, i + 1));
            }
            else
            {
                allEdges = Triangulate(centers);
            }

            // ── 5. Kruskal MST ────────────────────────────────────────────────────
            var mstEdges  = Kruskal(centers, allEdges);

            // ── 6. Smart loop edges (shortcut score) ──────────────────────────────
            var loopEdges = PickLoopEdges(centers, allEdges, mstEdges);

            // ── 7. Initialise adjacency structures (required by MapTraversal) ─────
            _weightedAdjacencyList = new Dictionary<Vector2Int,
                List<(Vector2Int neighbor, int weight, List<Vector2Int> corridorTiles)>>();
            _edgeTiles = new Dictionary<(Vector2Int, Vector2Int), List<Vector2Int>>();
            foreach (var room in roomPositions)
                _weightedAdjacencyList[room] = new List<(Vector2Int, int, List<Vector2Int>)>();

            // ── 8. Carve corridors (RWC style) ────────────────────────────────────
            foreach (var (a, b) in mstEdges)
                ConnectRoomsRWC(grid, roomPositions[a], roomPositions[b]);
            foreach (var (a, b) in loopEdges)
            {
                if (!HasEdgeRWC(roomPositions[a], roomPositions[b]))
                    ConnectRoomsRWC(grid, roomPositions[a], roomPositions[b]);
            }

            // ── 9. Post-process: restore room interiors ───────────────────────────
            foreach (var pos in roomPositions)
            {
                int ox = pos.x * RoomW, oy = pos.y * RoomH;
                for (int x = ox + 1; x < ox + RoomW - 1; x++)
                for (int y = oy + 1; y < oy + RoomH - 1; y++)
                    if (grid.GetTileType(x, y) == TileType.Path)
                        grid.Set(x, y, TileType.Floor);
            }

            // ── 10. Post-process: upgrade adjacent-room corridors to Floor ─────────
            for (int i = 0; i < roomPositions.Count; i++)
            for (int j = i + 1; j < roomPositions.Count; j++)
            {
                var ra = roomPositions[i];
                var rb = roomPositions[j];
                if (Mathf.Abs(ra.x - rb.x) + Mathf.Abs(ra.y - rb.y) != 1) continue;

                var (exitA, exitB) = GetCorridorEndpointsRWC(ra, rb);
                var cur = exitA;
                while (cur != exitB)
                {
                    if (grid.GetTileType(cur.x, cur.y) == TileType.Path)
                        grid.Set(cur.x, cur.y, TileType.Floor);
                    if (cur.x != exitB.x) cur.x += cur.x < exitB.x ? 1 : -1;
                    else                  cur.y += cur.y < exitB.y ? 1 : -1;
                }
                if (grid.GetTileType(exitB.x, exitB.y) == TileType.Path)
                    grid.Set(exitB.x, exitB.y, TileType.Floor);
            }

            // ── 11. Spawn point ───────────────────────────────────────────────────
            _startPosition = centers[Random.Range(0, centers.Count)];

            // ── 12. Build adjacency matrix for MapTraversal ───────────────────────
            _coord2VertexId = new Dictionary<Vector2Int, int>();
            for (int i = 0; i < roomPositions.Count; i++)
                _coord2VertexId[roomPositions[i]] = i;

            int n = roomPositions.Count;
            _weightedAdjacencyMatrix = new int[n, n];
            var newEdgeTiles = new Dictionary<(Vector2Int, Vector2Int), List<Vector2Int>>();

            foreach (var kvp in _weightedAdjacencyList)
            {
                var from   = kvp.Key;
                int fromId = _coord2VertexId[from];
                foreach (var (neighbor, weight, tiles) in kvp.Value)
                {
                    int toId = _coord2VertexId[neighbor];
                    _weightedAdjacencyMatrix[fromId, toId] = weight;
                    _weightedAdjacencyMatrix[toId, fromId] = weight;
                    newEdgeTiles[(from, neighbor)] = tiles;
                    newEdgeTiles[(neighbor, from)] = tiles;
                }
            }

            var newAdjList = new Dictionary<Vector2Int,
                List<(Vector2Int, int, List<Vector2Int>)>>();
            foreach (var room in roomPositions)
                newAdjList[room] = new List<(Vector2Int, int, List<Vector2Int>)>();

            foreach (var from in roomPositions)
            {
                int i = _coord2VertexId[from];
                foreach (var to in roomPositions)
                {
                    int j = _coord2VertexId[to];
                    if (_weightedAdjacencyMatrix[i, j] > 0 &&
                        newEdgeTiles.TryGetValue((from, to), out var tiles))
                        newAdjList[from].Add((to, _weightedAdjacencyMatrix[i, j], tiles));
                }
            }

            _weightedAdjacencyList = newAdjList;
            _edgeTiles = newEdgeTiles;
        }

        // ── RWC-style room / corridor helpers ─────────────────────────────────────

        private static void CarveRoom(MapGrid grid, int cx, int cy)
        {
            int ox = cx * RoomW, oy = cy * RoomH;
            for (int x = ox + 1; x < ox + RoomW - 1; x++)
            for (int y = oy + 1; y < oy + RoomH - 1; y++)
                grid.Set(x, y, TileType.Floor);
        }

        private void ConnectRoomsRWC(MapGrid grid, Vector2Int a, Vector2Int b)
        {
            int chunkDist   = Mathf.Abs(a.x - b.x) + Mathf.Abs(a.y - b.y);
            TileType tType  = chunkDist == 1 ? TileType.Floor : TileType.Path;

            var (exitA, exitB) = GetCorridorEndpointsRWC(a, b);
            var tiles = CarveCorridorRWC(grid, exitA, exitB, tType);

            if (!_weightedAdjacencyList[a].Exists(e => e.neighbor == b))
                _weightedAdjacencyList[a].Add((b, tiles.Count, tiles));
            if (!_weightedAdjacencyList[b].Exists(e => e.neighbor == a))
                _weightedAdjacencyList[b].Add((a, tiles.Count, tiles));

            _edgeTiles[(a, b)] = tiles;
            _edgeTiles[(b, a)] = tiles;
        }

        private bool HasEdgeRWC(Vector2Int a, Vector2Int b) =>
            _weightedAdjacencyList.ContainsKey(a) &&
            _weightedAdjacencyList[a].Exists(e => e.neighbor == b);

        // Mirrors RWC's GetCorridorEndpoints: L-shape, horizontal-first.
        private static (Vector2Int, Vector2Int) GetCorridorEndpointsRWC(Vector2Int a, Vector2Int b)
        {
            int ox_A = a.x * RoomW, oy_A = a.y * RoomH;
            int ox_B = b.x * RoomW, oy_B = b.y * RoomH;
            int cx_A = ox_A + RoomW / 2, cy_A = oy_A + RoomH / 2;
            int cx_B = ox_B + RoomW / 2, cy_B = oy_B + RoomH / 2;

            Vector2Int exitA, exitB;
            if (a.x != b.x)
            {
                exitA = b.x > a.x
                    ? new Vector2Int(ox_A + RoomW - 1, cy_A)
                    : new Vector2Int(ox_A,              cy_A);

                if      (cy_A < cy_B) exitB = new Vector2Int(cx_B, oy_B);
                else if (cy_A > cy_B) exitB = new Vector2Int(cx_B, oy_B + RoomH - 1);
                else                  exitB = b.x > a.x
                                            ? new Vector2Int(ox_B,             cy_B)
                                            : new Vector2Int(ox_B + RoomW - 1, cy_B);
            }
            else
            {
                exitA = b.y > a.y
                    ? new Vector2Int(cx_A, oy_A + RoomH - 1)
                    : new Vector2Int(cx_A, oy_A);
                exitB = b.y > a.y
                    ? new Vector2Int(cx_B, oy_B)
                    : new Vector2Int(cx_B, oy_B + RoomH - 1);
            }
            return (exitA, exitB);
        }

        private static List<Vector2Int> CarveCorridorRWC(
            MapGrid grid, Vector2Int from, Vector2Int to, TileType tileType)
        {
            var cur   = from;
            var tiles = new List<Vector2Int> { cur };
            grid.Set(cur.x, cur.y, tileType);

            while (cur.x != to.x)
            {
                cur.x += to.x > cur.x ? 1 : -1;
                grid.Set(cur.x, cur.y, tileType);
                tiles.Add(cur);
            }
            while (cur.y != to.y)
            {
                cur.y += to.y > cur.y ? 1 : -1;
                grid.Set(cur.x, cur.y, tileType);
                tiles.Add(cur);
            }
            return tiles;
        }

        // ── Delaunay triangulation (Bowyer-Watson) ────────────────────────────────

        private static List<(int, int)> Triangulate(List<Vector2Int> pts)
        {
            int n = pts.Count;
            var p = new Vector2[n + 3];
            for (int i = 0; i < n; i++) p[i] = new Vector2(pts[i].x, pts[i].y);

            float minX = p[0].x, minY = p[0].y, maxX = p[0].x, maxY = p[0].y;
            for (int i = 1; i < n; i++)
            {
                if (p[i].x < minX) minX = p[i].x;
                if (p[i].y < minY) minY = p[i].y;
                if (p[i].x > maxX) maxX = p[i].x;
                if (p[i].y > maxY) maxY = p[i].y;
            }

            float d = Mathf.Max(maxX - minX, maxY - minY) * 10f;
            p[n]   = new Vector2(minX - d,                 minY - d);
            p[n+1] = new Vector2(maxX + d,                 minY - d);
            p[n+2] = new Vector2((minX + maxX) / 2f,       maxY + d);

            var tris = new List<(int a, int b, int c)> { (n, n+1, n+2) };

            for (int i = 0; i < n; i++)
            {
                var bad = new List<(int a, int b, int c)>();
                foreach (var t in tris)
                    if (InCircumcircle(p[t.a], p[t.b], p[t.c], p[i]))
                        bad.Add(t);

                var boundary = new List<(int u, int v)>();
                foreach (var t in bad)
                {
                    (int u, int v)[] edges = { (t.a, t.b), (t.b, t.c), (t.c, t.a) };
                    foreach (var (u, v) in edges)
                    {
                        bool shared = false;
                        foreach (var other in bad)
                        {
                            if (other.Equals(t)) continue;
                            if (TriHasEdge(other, v, u)) { shared = true; break; }
                        }
                        if (!shared) boundary.Add((u, v));
                    }
                }

                foreach (var t in bad) tris.Remove(t);
                foreach (var (ea, eb) in boundary) tris.Add((ea, eb, i));
            }

            var edgeSet = new HashSet<(int, int)>();
            foreach (var t in tris)
            {
                if (t.a >= n || t.b >= n || t.c >= n) continue;
                NormEdge(edgeSet, t.a, t.b);
                NormEdge(edgeSet, t.b, t.c);
                NormEdge(edgeSet, t.c, t.a);
            }
            return new List<(int, int)>(edgeSet);
        }

        private static bool InCircumcircle(Vector2 a, Vector2 b, Vector2 c, Vector2 p)
        {
            double ax = a.x - p.x, ay = a.y - p.y;
            double bx = b.x - p.x, by = b.y - p.y;
            double cx = c.x - p.x, cy = c.y - p.y;
            double det = ax * (by * (cx*cx + cy*cy) - cy * (bx*bx + by*by))
                       - ay * (bx * (cx*cx + cy*cy) - cx * (bx*bx + by*by))
                       + (ax*ax + ay*ay) * (bx*cy - by*cx);
            return det > 0;
        }

        private static bool TriHasEdge((int a, int b, int c) t, int u, int v) =>
            (t.a == u && t.b == v) || (t.b == u && t.c == v) || (t.c == u && t.a == v);

        private static void NormEdge(HashSet<(int, int)> set, int a, int b)
        {
            if (a > b) (a, b) = (b, a);
            set.Add((a, b));
        }

        // ── Kruskal MST ───────────────────────────────────────────────────────────

        private static List<(int, int)> Kruskal(List<Vector2Int> pts, List<(int, int)> edges)
        {
            var sorted = new List<(int, int)>(edges);
            sorted.Sort((e1, e2) =>
                Vector2Int.Distance(pts[e1.Item1], pts[e1.Item2])
                    .CompareTo(Vector2Int.Distance(pts[e2.Item1], pts[e2.Item2])));

            int n = pts.Count;
            var parent = new int[n];
            var rank   = new int[n];
            for (int i = 0; i < n; i++) parent[i] = i;

            var mst = new List<(int, int)>();
            foreach (var (a, b) in sorted)
            {
                int ra = Find(parent, a), rb = Find(parent, b);
                if (ra == rb) continue;
                if      (rank[ra] < rank[rb]) parent[ra] = rb;
                else if (rank[ra] > rank[rb]) parent[rb] = ra;
                else { parent[rb] = ra; rank[ra]++; }
                mst.Add((a, b));
                if (mst.Count == n - 1) break;
            }
            return mst;
        }

        private static int Find(int[] parent, int x)
        {
            while (parent[x] != x) { parent[x] = parent[parent[x]]; x = parent[x]; }
            return x;
        }

        // ── Smart loop edge selection ─────────────────────────────────────────────

        private static List<(int, int)> PickLoopEdges(
            List<Vector2Int> pts, List<(int, int)> all, List<(int, int)> mst)
        {
            int n = pts.Count;
            var adj = new List<(int nbr, float dist)>[n];
            for (int i = 0; i < n; i++) adj[i] = new List<(int, float)>();
            foreach (var (a, b) in mst)
            {
                float d = Vector2Int.Distance(pts[a], pts[b]);
                adj[a].Add((b, d));
                adj[b].Add((a, d));
            }

            var mstSet = new HashSet<(int, int)>(mst);
            var scored = new List<(int a, int b, float score)>();
            foreach (var e in all)
            {
                if (mstSet.Contains(e) || mstSet.Contains((e.Item2, e.Item1))) continue;
                float euclidean = Vector2Int.Distance(pts[e.Item1], pts[e.Item2]);
                if (euclidean < 0.001f) continue;
                float pathDist = MstPathDist(adj, n, e.Item1, e.Item2);
                scored.Add((e.Item1, e.Item2, pathDist / euclidean));
            }

            scored.Sort((x, y) => y.score.CompareTo(x.score));
            int count  = Mathf.Max(1, Mathf.RoundToInt(scored.Count * LoopFraction));
            var result = new List<(int, int)>();
            for (int i = 0; i < Mathf.Min(count, scored.Count); i++)
                result.Add((scored[i].a, scored[i].b));
            return result;
        }

        private static float MstPathDist(List<(int nbr, float dist)>[] adj, int n, int start, int end)
        {
            var visited = new bool[n];
            var dist    = new float[n];
            for (int i = 0; i < n; i++) dist[i] = float.MaxValue;
            dist[start] = 0f;
            var queue = new Queue<int>();
            queue.Enqueue(start);
            visited[start] = true;
            while (queue.Count > 0)
            {
                int cur = queue.Dequeue();
                if (cur == end) break;
                foreach (var (nbr, d) in adj[cur])
                {
                    if (visited[nbr]) continue;
                    visited[nbr] = true;
                    dist[nbr] = dist[cur] + d;
                    queue.Enqueue(nbr);
                }
            }
            return dist[end];
        }
    }
}