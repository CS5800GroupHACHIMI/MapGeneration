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

            var inMST = new bool[n];
            var minEdge = new int[n];
            var parent = new int[n];

            for (int i = 0; i < n; i++)
            {
                minEdge[i] = int.MaxValue;
                parent[i] = -1;
            }

            minEdge[0] = 0;

            for (int count = 0; count < n; count++)
            {
                // Pick vertex with minimum edge weight not in MST
                int u = -1;
                int minKey = int.MaxValue;
                for (int v = 0; v < n; v++)
                {
                    if (!inMST[v] && minEdge[v] < minKey)
                    {
                        minKey = minEdge[v];
                        u = v;
                    }
                }

                if (u == -1) break; // disconnected graph

                inMST[u] = true;

                if (parent[u] != -1)
                    mstEdges.Add((id2Coord[parent[u]], id2Coord[u]));

                // Update neighboring vertices
                for (int v = 0; v < n; v++)
                {
                    if (!inMST[v] && adjacencyMatrix[u, v] > 0 && adjacencyMatrix[u, v] < minEdge[v])
                    {
                        minEdge[v] = adjacencyMatrix[u, v];
                        parent[v] = u;
                    }
                }
            }

            return mstEdges;
        }
    }
}