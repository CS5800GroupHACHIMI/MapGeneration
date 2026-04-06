using Data;
using Model;
using System.Collections.Generic;
using UnityEngine;

namespace Generators
{
    /// <summary>
    /// Delaunay triangulation + Kruskal MST map generator.
    ///
    /// Algorithm:
    ///   1. Randomly place non-overlapping rooms across the map
    ///   2. Build a Delaunay triangulation of room centers (Bowyer-Watson, O(n²))
    ///      → produces only "spatially natural" edges; no cross-map corridors
    ///   3. Extract an MST via Kruskal on those edges (weights = Euclidean distance)
    ///      → guarantees full connectivity with minimum total corridor length
    ///   4. Restore ~15% of non-MST Delaunay edges as shortcuts (loops/cycles)
    ///   5. Carve corridors: 3-tile-wide for MST edges, 1-tile-wide for loop edges
    ///
    /// Recommended MapConfig size: 64×64 or larger.
    /// </summary>
    [CreateAssetMenu(fileName = "DelaunayKruskalGenerator", menuName = "Generators/DelaunayKruskal")]
    public class DelaunayKruskalGenerator : MapGeneratorBase
    {
        public override string Name => "DelaunayKruskal";

        private const int RoomW        = 14;   // tiles per room (wall-to-wall)
        private const int RoomH        = 12;
        private const int RoomGap      = 2;    // minimum empty tile border between rooms
        private const int PlaceAttempts = 300;
        private const float LoopFraction = 0.15f; // fraction of non-MST edges to restore
        private const int MinStraightOverlap = 3; // facing-wall overlap needed to use a straight corridor

        // ── Entry point ──────────────────────────────────────────────────────────

        public override void Generate(MapGrid grid, MapConfig config)
        {
            Random.InitState(config.seed);

            for (int x = 0; x < grid.Width;  x++)
            for (int y = 0; y < grid.Height; y++)
                grid.Set(x, y, TileType.Wall);

            var rooms = PlaceRooms(grid);
            if (rooms.Count == 0)
            {
                _startPosition = new Vector2Int(grid.Width / 2, grid.Height / 2);
                return;
            }

            foreach (var r in rooms) CarveRoom(grid, r);

            var centers = new List<Vector2Int>(rooms.Count);
            foreach (var r in rooms)
                centers.Add(new Vector2Int(r.x + RoomW / 2, r.y + RoomH / 2));

            // Triangulate — fall back to a simple chain when fewer than 3 rooms
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

            var mstEdges  = Kruskal(centers, allEdges);
            var loopEdges = PickLoopEdges(centers, allEdges, mstEdges);

            foreach (var (a, b) in mstEdges)
                CarveCorridor(grid, rooms[a], rooms[b], width: 2, TileType.Floor);
            foreach (var (a, b) in loopEdges)
            {
                // Only carve loop corridor if its open-space path is entirely Wall.
                // If it would cross an existing corridor or room, skip it —
                // the rooms are already "connected" via that shared passable space.
                if (LoopPathClear(grid, rooms[a], rooms[b], centers[a].y, centers[b].y))
                    CarveCorridor(grid, rooms[a], rooms[b], width: 1, TileType.Path);
            }

            _startPosition = centers[Random.Range(0, centers.Count)];
        }

        // ── Room placement ────────────────────────────────────────────────────────

        private static List<RectInt> PlaceRooms(MapGrid grid)
        {
            int maxFit = (grid.Width / (RoomW + RoomGap)) * (grid.Height / (RoomH + RoomGap));
            int target = Random.Range(6, Mathf.Min(20, maxFit) + 1);
            var rooms = new List<RectInt>();

            for (int i = 0; i < PlaceAttempts && rooms.Count < target; i++)
            {
                int x = Random.Range(1, grid.Width  - RoomW - 1);
                int y = Random.Range(1, grid.Height - RoomH - 1);
                var candidate = new RectInt(x, y, RoomW, RoomH);

                bool overlaps = false;
                foreach (var r in rooms)
                {
                    var exp = new RectInt(r.x - RoomGap, r.y - RoomGap,
                                         r.width + RoomGap * 2, r.height + RoomGap * 2);
                    if (exp.Overlaps(candidate)) { overlaps = true; break; }
                }
                if (!overlaps) rooms.Add(candidate);
            }
            return rooms;
        }

        private static void CarveRoom(MapGrid grid, RectInt r)
        {
            for (int x = r.x + 1; x < r.x + r.width  - 1; x++)
            for (int y = r.y + 1; y < r.y + r.height - 1; y++)
                grid.Set(x, y, TileType.Floor);
        }

        // ── Corridor carving ──────────────────────────────────────────────────────

        /// <summary>
        /// Connects two rooms with a corridor.
        ///
        /// Three routing strategies based on the center-to-center direction:
        ///   |dx| >= 2·|dy|  → H-H: straight or Z-shape between facing horizontal walls
        ///   |dy| >= 2·|dx|  → V-V: straight or Z-shape between facing vertical walls
        ///   otherwise       → L-shape: A exits through its nearer H wall, B is entered
        ///                     through its nearer V wall, single bend in open space.
        ///
        /// The stricter 2:1 threshold keeps Z-shapes only for nearly-axis-aligned pairs,
        /// routing diagonal pairs through the cleaner single-bend L-shape so corridors
        /// never run parallel to a room wall.
        /// </summary>
        private static void CarveCorridor(MapGrid grid, RectInt a, RectInt b, int width, TileType tileType)
        {
            int cax = a.x + RoomW / 2, cay = a.y + RoomH / 2;
            int cbx = b.x + RoomW / 2, cby = b.y + RoomH / 2;
            int dx = cbx - cax, dy = cby - cay;
            int adx = Mathf.Abs(dx), ady = Mathf.Abs(dy);

            if (adx == 0 && ady == 0) return;

            if (adx >= ady * 2)
                CarveHConnection(grid, a, b, cax, cay, cbx, cby, dx, width, tileType);
            else if (ady >= adx * 2)
                CarveVConnection(grid, a, b, cax, cay, cbx, cby, dy, width, tileType);
            else
                CarveLConnection(grid, a, b, cax, cay, cbx, cby, dx, dy, width, tileType);
        }

        private static void CarveHConnection(MapGrid grid, RectInt a, RectInt b,
            int cax, int cay, int cbx, int cby, int dx, int width, TileType t)
        {
            bool aLeft  = dx > 0;
            int  exitAx = aLeft ? a.x + RoomW - 1 : a.x;
            int  exitBx = aLeft ? b.x              : b.x + RoomW - 1;

            // Y-ranges of facing walls (interior rows only)
            int olMin = Mathf.Max(a.y + 1, b.y + 1);
            int olMax = Mathf.Min(a.y + RoomH - 2, b.y + RoomH - 2);

            if (olMax - olMin + 1 >= MinStraightOverlap)
            {
                // Wide overlap → straight corridor through overlap center
                PaintH(grid, exitAx, exitBx, (olMin + olMax) / 2, width, t);
            }
            else
            {
                // Z-shape: both bends at midX (open space between the two walls)
                int midX = (exitAx + exitBx) / 2;
                PaintH(grid, exitAx, midX,   cay, width, t);
                PaintBox(grid, midX, cay, width, t);
                PaintV(grid, midX,   cay, cby, width, t);
                PaintBox(grid, midX, cby, width, t);
                PaintH(grid, midX,   exitBx, cby, width, t);
            }
        }

        private static void CarveVConnection(MapGrid grid, RectInt a, RectInt b,
            int cax, int cay, int cbx, int cby, int dy, int width, TileType t)
        {
            bool aBottom = dy > 0;
            int  exitAy  = aBottom ? a.y + RoomH - 1 : a.y;
            int  exitBy  = aBottom ? b.y              : b.y + RoomH - 1;

            // X-ranges of facing walls (interior columns only)
            int olMin = Mathf.Max(a.x + 1, b.x + 1);
            int olMax = Mathf.Min(a.x + RoomW - 2, b.x + RoomW - 2);

            if (olMax - olMin + 1 >= MinStraightOverlap)
            {
                // Wide overlap → straight corridor through overlap center
                PaintV(grid, (olMin + olMax) / 2, exitAy, exitBy, width, t);
            }
            else
            {
                // Z-shape: both bends at midY (open space between walls)
                int midY = (exitAy + exitBy) / 2;
                PaintV(grid, cax, exitAy, midY,  width, t);
                PaintBox(grid, cax, midY, width, t);
                PaintH(grid, cax, cbx,   midY,  width, t);
                PaintBox(grid, cbx, midY, width, t);
                PaintV(grid, cbx, midY,  exitBy, width, t);
            }
        }

        /// <summary>
        /// L-shape corridor for diagonal room pairs (aspect ratio between 1:2 and 2:1).
        /// Room A exits through its nearer horizontal wall (left/right).
        /// Room B is entered through its nearer vertical wall (bottom/top).
        /// A single bend is placed in the open space between the two rooms.
        ///
        ///   exitA ──────────── bend
        ///                        │
        ///                      exitB
        /// </summary>
        private static void CarveLConnection(MapGrid grid, RectInt a, RectInt b,
            int cax, int cay, int cbx, int cby, int dx, int dy, int width, TileType t)
        {
            // A exits through its horizontal wall
            int exitAx = dx > 0 ? a.x + RoomW - 1 : a.x;
            int exitAy = cay; // A's center row — always interior

            // B is entered through its vertical wall (approaching from A's side)
            int exitBx = cbx; // B's center column — always interior
            int exitBy = dy > 0 ? b.y : b.y + RoomH - 1;

            // L-shape: H then V, single bend at (exitBx, exitAy)
            PaintH(grid, exitAx, exitBx, exitAy, width, t);
            PaintBox(grid, exitBx, exitAy, width, t);
            PaintV(grid, exitBx, exitAy, exitBy, width, t);
        }

        /// <summary>
        /// Returns true if the corridor path between the two rooms passes only through
        /// Wall tiles in the open space (i.e., does not cross any existing corridor or room).
        /// Uses the same routing strategy as CarveCorridor so the check matches the carve.
        /// </summary>
        private static bool LoopPathClear(MapGrid grid, RectInt a, RectInt b, int cay, int cby)
        {
            int cax = a.x + RoomW / 2, cbx = b.x + RoomW / 2;
            int dx = cbx - cax, dy = cby - cay;
            int adx = Mathf.Abs(dx), ady = Mathf.Abs(dy);

            if (adx >= ady * 2)
            {
                // H-H Z-shape check
                bool aLeft = dx > 0;
                int exitAx = aLeft ? a.x + RoomW - 1 : a.x;
                int exitBx = aLeft ? b.x              : b.x + RoomW - 1;
                int midX   = (exitAx + exitBx) / 2;

                int x1s = exitAx + (exitAx < midX ? 1 : -1);
                for (int x = Mathf.Min(x1s, midX); x <= Mathf.Max(x1s, midX); x++)
                    if (grid.InBounds(x, cay) && grid.GetTileType(x, cay) != TileType.Wall) return false;
                for (int y = Mathf.Min(cay, cby); y <= Mathf.Max(cay, cby); y++)
                    if (grid.InBounds(midX, y) && grid.GetTileType(midX, y) != TileType.Wall) return false;
                int x2e = exitBx + (exitBx > midX ? -1 : 1);
                for (int x = Mathf.Min(midX, x2e); x <= Mathf.Max(midX, x2e); x++)
                    if (grid.InBounds(x, cby) && grid.GetTileType(x, cby) != TileType.Wall) return false;
            }
            else if (ady >= adx * 2)
            {
                // V-V Z-shape check
                bool aBottom = dy > 0;
                int exitAy = aBottom ? a.y + RoomH - 1 : a.y;
                int exitBy = aBottom ? b.y              : b.y + RoomH - 1;
                int midY   = (exitAy + exitBy) / 2;

                int y1s = exitAy + (exitAy < midY ? 1 : -1);
                for (int y = Mathf.Min(y1s, midY); y <= Mathf.Max(y1s, midY); y++)
                    if (grid.InBounds(cax, y) && grid.GetTileType(cax, y) != TileType.Wall) return false;
                for (int x = Mathf.Min(cax, cbx); x <= Mathf.Max(cax, cbx); x++)
                    if (grid.InBounds(x, midY) && grid.GetTileType(x, midY) != TileType.Wall) return false;
                int y2e = exitBy + (exitBy > midY ? -1 : 1);
                for (int y = Mathf.Min(midY, y2e); y <= Mathf.Max(midY, y2e); y++)
                    if (grid.InBounds(cbx, y) && grid.GetTileType(cbx, y) != TileType.Wall) return false;
            }
            else
            {
                // L-shape check: H segment then V segment
                int exitAx = dx > 0 ? a.x + RoomW - 1 : a.x;
                int exitBx = cbx;
                int exitBy = dy > 0 ? b.y : b.y + RoomH - 1;

                // H: just outside A's exit wall → exitBx at y=cay
                int hStart = exitAx + (dx > 0 ? 1 : -1);
                for (int x = Mathf.Min(hStart, exitBx); x <= Mathf.Max(hStart, exitBx); x++)
                    if (grid.InBounds(x, cay) && grid.GetTileType(x, cay) != TileType.Wall) return false;
                // V: just past cay → exitBy at x=exitBx
                int vStart = cay + (dy > 0 ? 1 : -1);
                for (int y = Mathf.Min(vStart, exitBy); y <= Mathf.Max(vStart, exitBy); y++)
                    if (grid.InBounds(exitBx, y) && grid.GetTileType(exitBx, y) != TileType.Wall) return false;
            }
            return true;
        }

        // Paint a horizontal strip of given width centered on y,
        // spanning x in [min(x0,x1), max(x0,x1)].
        // width=1 → only y; width=2 → y-1,y; width=3 → y-1,y,y+1
        private static void PaintH(MapGrid grid, int x0, int x1, int y, int width, TileType t)
        {
            int xMin = Mathf.Min(x0, x1), xMax = Mathf.Max(x0, x1);
            int lo = -(width / 2), hi = (width - 1) / 2;
            for (int x = xMin; x <= xMax; x++)
                for (int w = lo; w <= hi; w++)
                    SafeSet(grid, x, y + w, t);
        }

        // Paint a vertical strip of given width centered on x,
        // spanning y in [min(y0,y1), max(y0,y1)].
        private static void PaintV(MapGrid grid, int x, int y0, int y1, int width, TileType t)
        {
            int yMin = Mathf.Min(y0, y1), yMax = Mathf.Max(y0, y1);
            int lo = -(width / 2), hi = (width - 1) / 2;
            for (int y = yMin; y <= yMax; y++)
                for (int w = lo; w <= hi; w++)
                    SafeSet(grid, x + w, y, t);
        }

        // Fill a width² box at (cx, cy) to close bend junctions.
        private static void PaintBox(MapGrid grid, int cx, int cy, int width, TileType t)
        {
            int lo = -(width / 2), hi = (width - 1) / 2;
            for (int wx = lo; wx <= hi; wx++)
            for (int wy = lo; wy <= hi; wy++)
                SafeSet(grid, cx + wx, cy + wy, t);
        }

        // Tile priority: Floor > Path.
        // Room interiors (Floor) are never overwritten by corridor tiles.
        // Wide corridors (Floor) are never downgraded to narrow Path.
        private static void SafeSet(MapGrid grid, int x, int y, TileType t)
        {
            if (!grid.InBounds(x, y)) return;
            if (grid.GetTileType(x, y) == TileType.Floor) return;
            grid.Set(x, y, t);
        }

        // ── Delaunay triangulation (Bowyer-Watson, O(n²)) ─────────────────────────

        /// <summary>
        /// Returns the set of Delaunay edges as normalized (min,max) index pairs.
        /// Requires at least 3 points.
        /// </summary>
        private static List<(int, int)> Triangulate(List<Vector2Int> pts)
        {
            int n = pts.Count;
            var p = new Vector2[n + 3];
            for (int i = 0; i < n; i++) p[i] = new Vector2(pts[i].x, pts[i].y);

            // Bounding box
            float minX = p[0].x, minY = p[0].y, maxX = p[0].x, maxY = p[0].y;
            for (int i = 1; i < n; i++)
            {
                if (p[i].x < minX) minX = p[i].x;
                if (p[i].y < minY) minY = p[i].y;
                if (p[i].x > maxX) maxX = p[i].x;
                if (p[i].y > maxY) maxY = p[i].y;
            }

            // CCW super-triangle that strictly contains all points
            float d  = Mathf.Max(maxX - minX, maxY - minY) * 10f;
            p[n]   = new Vector2(minX - d,                   minY - d); // bottom-left
            p[n+1] = new Vector2(maxX + d,                   minY - d); // bottom-right
            p[n+2] = new Vector2((minX + maxX) / 2f,         maxY + d); // top-center

            // CCW: cross((BR-BL), (TC-BL)).z > 0 — verified analytically
            var tris = new List<(int a, int b, int c)> { (n, n+1, n+2) };

            for (int i = 0; i < n; i++)
            {
                // Triangles whose circumcircle contains p[i]
                var bad = new List<(int a, int b, int c)>();
                foreach (var t in tris)
                    if (InCircumcircle(p[t.a], p[t.b], p[t.c], p[i]))
                        bad.Add(t);

                // Boundary: edges NOT shared between two bad triangles (keep direction)
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
                            // In CCW triangulation, the shared edge appears reversed in the neighbor
                            if (TriHasDirectedEdge(other, v, u)) { shared = true; break; }
                        }
                        if (!shared) boundary.Add((u, v));
                    }
                }

                foreach (var t in bad) tris.Remove(t);
                foreach (var (ea, eb) in boundary) tris.Add((ea, eb, i));
            }

            // Collect edges, excluding any triangle that touches the super-triangle
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

        /// <summary>
        /// Returns true if point p lies strictly inside the circumcircle of the
        /// counter-clockwise triangle (a, b, c). Uses the standard 3×3 determinant
        /// method with double precision to avoid floating-point sign errors.
        /// </summary>
        private static bool InCircumcircle(Vector2 a, Vector2 b, Vector2 c, Vector2 p)
        {
            double ax = a.x - p.x, ay = a.y - p.y;
            double bx = b.x - p.x, by = b.y - p.y;
            double cx = c.x - p.x, cy = c.y - p.y;
            double det = ax * (by * (cx * cx + cy * cy) - cy * (bx * bx + by * by))
                       - ay * (bx * (cx * cx + cy * cy) - cx * (bx * bx + by * by))
                       + (ax * ax + ay * ay) * (bx * cy - by * cx);
            return det > 0;
        }

        // Does triangle t contain the directed edge u→v?
        private static bool TriHasDirectedEdge((int a, int b, int c) t, int u, int v) =>
            (t.a == u && t.b == v) || (t.b == u && t.c == v) || (t.c == u && t.a == v);

        // Insert edge as (min, max) to deduplicate undirected edges
        private static void NormEdge(HashSet<(int, int)> set, int a, int b)
        {
            if (a > b) (a, b) = (b, a);
            set.Add((a, b));
        }

        // ── Kruskal MST ───────────────────────────────────────────────────────────

        /// <summary>
        /// Returns the minimum spanning tree of <paramref name="pts"/> using
        /// Kruskal's algorithm with Union-Find (path compression + union by rank).
        /// Edge weights are Euclidean distances.  O(E log E).
        /// </summary>
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

                // Union by rank
                if (rank[ra] < rank[rb])      parent[ra] = rb;
                else if (rank[ra] > rank[rb]) parent[rb] = ra;
                else { parent[rb] = ra; rank[ra]++; }

                mst.Add((a, b));
                if (mst.Count == n - 1) break;
            }
            return mst;
        }

        // Path-compressed Find
        private static int Find(int[] parent, int x)
        {
            while (parent[x] != x) { parent[x] = parent[parent[x]]; x = parent[x]; }
            return x;
        }

        // ── Loop restoration ──────────────────────────────────────────────────────

        /// <summary>
        /// Selects loop (shortcut) edges from non-MST Delaunay edges.
        ///
        /// Selection criterion: shortcut value = MST_path_distance / Euclidean_distance.
        /// A high ratio means the two rooms are physically close but the MST forces a long
        /// detour to reach one another — exactly where a shortcut helps most.
        /// Edges are ranked by shortcut value descending; the top LoopFraction are kept.
        /// </summary>
        private static List<(int, int)> PickLoopEdges(
            List<Vector2Int> pts, List<(int, int)> all, List<(int, int)> mst)
        {
            // Build MST adjacency with Euclidean weights for path-distance queries
            int n = pts.Count;
            var mstAdj = new List<(int nbr, float dist)>[n];
            for (int i = 0; i < n; i++) mstAdj[i] = new List<(int, float)>();
            foreach (var (a, b) in mst)
            {
                float d = Vector2Int.Distance(pts[a], pts[b]);
                mstAdj[a].Add((b, d));
                mstAdj[b].Add((a, d));
            }

            // Score every non-MST edge
            var mstSet = new HashSet<(int, int)>(mst);
            var scored = new List<(int a, int b, float score)>();

            foreach (var e in all)
            {
                if (mstSet.Contains(e) || mstSet.Contains((e.Item2, e.Item1))) continue;

                float euclidean = Vector2Int.Distance(pts[e.Item1], pts[e.Item2]);
                if (euclidean < 0.001f) continue;

                float pathDist = MstPathDist(mstAdj, n, e.Item1, e.Item2);
                scored.Add((e.Item1, e.Item2, pathDist / euclidean));
            }

            // Sort descending: largest shortcut ratio first
            scored.Sort((x, y) => y.score.CompareTo(x.score));

            int count = Mathf.Max(1, Mathf.RoundToInt(scored.Count * LoopFraction));
            var result = new List<(int, int)>();
            for (int i = 0; i < Mathf.Min(count, scored.Count); i++)
                result.Add((scored[i].a, scored[i].b));
            return result;
        }

        /// <summary>
        /// BFS on the MST tree to find the weighted path distance between two nodes.
        /// </summary>
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

        private static void Shuffle<T>(List<T> list)
        {
            for (int i = list.Count - 1; i > 0; i--)
            {
                int j = Random.Range(0, i + 1);
                (list[i], list[j]) = (list[j], list[i]);
            }
        }
    }
}