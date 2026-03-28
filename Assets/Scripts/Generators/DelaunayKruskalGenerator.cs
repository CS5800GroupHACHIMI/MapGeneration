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

        private const int RoomW        = 10;   // tiles per room (wall-to-wall)
        private const int RoomH        = 8;
        private const int RoomGap      = 2;    // minimum empty tile border between rooms
        private const int PlaceAttempts = 300;
        private const float LoopFraction = 0.15f; // fraction of non-MST edges to restore

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
            var loopEdges = PickLoopEdges(allEdges, mstEdges);

            foreach (var (a, b) in mstEdges)
                CarveCorridor(grid, centers[a], centers[b], width: 3);
            foreach (var (a, b) in loopEdges)
                CarveCorridor(grid, centers[a], centers[b], width: 1);

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
        /// L-shaped corridor from <paramref name="from"/> to <paramref name="to"/>.
        /// Horizontal leg first, then vertical; <paramref name="width"/> tiles wide.
        /// </summary>
        private static void CarveCorridor(MapGrid grid, Vector2Int from, Vector2Int to, int width)
        {
            int half = width / 2;
            var cur = from;

            // Horizontal leg: step in x, paint ±half in y
            while (cur.x != to.x)
            {
                for (int w = -half; w <= half; w++)
                    if (grid.InBounds(cur.x, cur.y + w))
                        grid.Set(cur.x, cur.y + w, TileType.Floor);
                cur.x += (to.x > cur.x) ? 1 : -1;
            }

            // Vertical leg: step in y, paint ±half in x
            while (cur.y != to.y)
            {
                for (int w = -half; w <= half; w++)
                    if (grid.InBounds(cur.x + w, cur.y))
                        grid.Set(cur.x + w, cur.y, TileType.Floor);
                cur.y += (to.y > cur.y) ? 1 : -1;
            }

            // Paint destination
            for (int wx = -half; wx <= half; wx++)
            for (int wy = -half; wy <= half; wy++)
                if (grid.InBounds(to.x + wx, to.y + wy))
                    grid.Set(to.x + wx, to.y + wy, TileType.Floor);
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

        private static List<(int, int)> PickLoopEdges(List<(int, int)> all, List<(int, int)> mst)
        {
            var mstSet = new HashSet<(int, int)>(mst);
            var pool   = new List<(int, int)>();
            foreach (var e in all)
                if (!mstSet.Contains(e) && !mstSet.Contains((e.Item2, e.Item1)))
                    pool.Add(e);

            Shuffle(pool);
            int count = Mathf.Max(1, Mathf.RoundToInt(pool.Count * LoopFraction));
            return pool.GetRange(0, Mathf.Min(count, pool.Count));
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