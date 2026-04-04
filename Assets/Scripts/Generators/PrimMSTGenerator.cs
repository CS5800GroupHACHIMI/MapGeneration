using Data;
using Model;
using System.Collections.Generic;
using UnityEngine;

namespace Generators
{
    [CreateAssetMenu(fileName = "PrimMSTGenerator", menuName = "Generators/PrimMSTGenerator")]
    public class PrimMSTGenerator : RoomsWithCorridorsGenerator
    {
        public override string Name => "PrimMSTGenerator";

        public override void Generate(MapGrid grid, MapConfig config)
        {
            // Step 1: Generate rooms + corridors
            base.Generate(grid, config);

            // Step 2: Extract MST edges (safe, loop-free)
            var mstEdges = ExtractMSTFromAdjacencyMatrix(_weightedAdjacencyMatrix, _coord2VertexId);

            // Step 3: Draw MST along actual corridor tiles
            DrawMSTLines(grid, mstEdges);
        }

        // =========================
        // DRAW MST USING CORRIDOR TILES
        // =========================
        private void DrawMSTLines(MapGrid grid, List<(Vector2Int from, Vector2Int to)> mstEdges)
        {
            if (mstEdges.Count == 0) return;

            GameObject parent = new GameObject("MSTLines");

            foreach (var (from, to) in mstEdges)
            {
                GameObject go = new GameObject("MSTEdge");
                go.transform.parent = parent.transform;

                LineRenderer lr = go.AddComponent<LineRenderer>();
                lr.material = new Material(Shader.Find("Sprites/Default"));
                lr.startColor = lr.endColor = new Color(1f, 0.5f, 0f, 1f); // orange
                lr.startWidth = lr.endWidth = 0.05f;
                lr.useWorldSpace = true;
                lr.sortingOrder = 10;

                // Use actual corridor tiles if available
                List<Vector2Int> tiles;
                if (_edgeTiles != null && _edgeTiles.TryGetValue((from, to), out tiles))
                {
                    Vector3[] positions = new Vector3[tiles.Count];
                    for (int i = 0; i < tiles.Count; i++)
                        positions[i] = GridToWorld(tiles[i]);
                    lr.positionCount = positions.Length;
                    lr.SetPositions(positions);
                }
                else
                {
                    // Fallback: straight line between room centers
                    Vector2Int centerFrom = GetRoomCenter(from);
                    Vector2Int centerTo = GetRoomCenter(to);
                    Vector3[] positions = new Vector3[] { GridToWorld(centerFrom), GridToWorld(centerTo) };
                    lr.positionCount = 2;
                    lr.SetPositions(positions);
                }
            }
        }

        private static Vector3 GridToWorld(Vector2Int gridPos)
        {
            return new Vector3(gridPos.x + 0.5f, gridPos.y + 0.5f, 0f);
        }

        private static Vector2Int GetRoomCenter(Vector2Int gridPos)
        {
            const int RoomW = 10;
            const int RoomH = 8;
            return new Vector2Int(gridPos.x * RoomW + RoomW / 2, gridPos.y * RoomH + RoomH / 2);
        }

        // =========================
        // MST EXTRACTION (Prim's algorithm, loop-free)
        // =========================

    private static List<(Vector2Int from, Vector2Int to)> ExtractMSTFromAdjacencyMatrix(
        int[,] adjacencyMatrix, Dictionary<Vector2Int, int> coord2Id)
        {
            int n = adjacencyMatrix.GetLength(0);
            var mstEdges = new List<(Vector2Int from, Vector2Int to)>();
            if (n == 0) return mstEdges;

            // Map ID → coordinates
            var id2Coord = new Vector2Int[n];
            foreach (var kvp in coord2Id)
                id2Coord[kvp.Value] = kvp.Key;

            var inMST = new HashSet<int>();

            var seenEdges = new HashSet<(int from, int to)>();

            // Min-heap using (weight, from, to)
            var edgeHeap = new SortedSet<(int weight, int from, int to)>(
                Comparer<(int weight, int from, int to)>.Create((a, b) =>
                {
                    int cmp = a.weight.CompareTo(b.weight);
                    if (cmp == 0) cmp = a.from.CompareTo(b.from);
                    if (cmp == 0) cmp = a.to.CompareTo(b.to);
                    return cmp;
                })
            );

            // Start with vertex 0
            // Find the vertex ID corresponding to (0,0)
            int startVertex = 0;
            inMST.Add(startVertex);

            // Add all edges from startVertex
            for (int v = 0; v < n; v++)
            {
                if (adjacencyMatrix[startVertex, v] > 0)
                    edgeHeap.Add((adjacencyMatrix[startVertex, v], startVertex, v));
                Debug.Log($"edge from and to: {startVertex}, {v} ");
            }

            while (inMST.Count < n)
            {
                if (edgeHeap.Count == 0) break; // no more edges to process
                // Pick the smallest edge
                var minEdge = edgeHeap.Min;
                edgeHeap.Remove(minEdge);

                int u = minEdge.from;
                int v = minEdge.to;

                if (inMST.Contains(v)) continue;

                // Add edge to MST 
                mstEdges.Add((id2Coord[u], id2Coord[v]));
                inMST.Add(v);

                foreach (var edge in mstEdges)
                {
                    Debug.Log($"Edge from ({edge.from.x},{edge.from.y}) to ({edge.to.x},{edge.to.y})");
                }


                // Add all edges from newly added vertex v
                for (int w = 0; w < n; w++)
                {
                    if (!inMST.Contains(w) && adjacencyMatrix[v, w] > 0)
                    {
                        edgeHeap.Add((adjacencyMatrix[v, w], v, w));
                    }
                }
            }

            var allEdges = new List<(Vector2Int from, Vector2Int to)>();
            for (int i = 0; i < n; i++)
                {
                    for (int j = 0; j < n; j++)
                    {
                        if (adjacencyMatrix[i, j] > 0)
                        {
                            allEdges.Add((id2Coord[i], id2Coord[j]));
                        }
                    }
            }

            foreach (var edge in allEdges)
            {
                Debug.Log($"Seen edge: {edge.from} -> {edge.to}");
            }

            // Debug print edges
            foreach (var edge in mstEdges)
            {
                Debug.Log($"Edge: {edge.from} -> {edge.to}");
            }

            return mstEdges;
     }
}
}