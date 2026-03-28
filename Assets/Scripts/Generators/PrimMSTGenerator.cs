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
            // Step 1: Generate the base rooms + corridors
            base.Generate(grid, config);

            // Step 2: Extract MST edges from the adjacency matrix
            var mstEdges = ExtractMSTFromAdjacencyMatrix(_weightedAdjacencyMatrix, _coord2VertexId);

            // Step 3: Draw MST lines on top of the map
            DrawMSTLines(grid, mstEdges);
        }

        // Draw thin orange MST lines
        private static void DrawMSTLines(MapGrid grid, List<(Vector2Int, Vector2Int)> mstEdges)
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
                lr.positionCount = 0;
                lr.useWorldSpace = true;
                lr.sortingOrder = 10; // ensure it's on top of tiles

                // Follow the L-shaped corridor (horizontal then vertical)
                List<Vector3> points = new List<Vector3>();
                Vector2Int cur = GetRoomCenter(from);

                points.Add(GridToWorld(cur));

                // Horizontal movement
                while (cur.x != GetRoomCenter(to).x)
                {
                    cur.x += (GetRoomCenter(to).x > cur.x) ? 1 : -1;
                    points.Add(GridToWorld(cur));
                }

                // Vertical movement
                while (cur.y != GetRoomCenter(to).y)
                {
                    cur.y += (GetRoomCenter(to).y > cur.y) ? 1 : -1;
                    points.Add(GridToWorld(cur));
                }

                lr.positionCount = points.Count;
                lr.SetPositions(points.ToArray());
            }
        }

        // Convert grid coordinate to Unity world position (z=0 plane)
        private static Vector3 GridToWorld(Vector2Int gridPos)
        {
            return new Vector3(gridPos.x + 0.5f, gridPos.y + 0.5f, 0f);
        }

        // Center of a room
        private static Vector2Int GetRoomCenter(Vector2Int gridPos)
        {
            const int RoomW = 10;
            const int RoomH = 8;
            return new Vector2Int(gridPos.x * RoomW + RoomW / 2, gridPos.y * RoomH + RoomH / 2);
        }

        // Compute MST using Prim's algorithm from adjacency matrix
        private static List<(Vector2Int, Vector2Int)> ExtractMSTFromAdjacencyMatrix(
            int[,] adjacencyMatrix, Dictionary<Vector2Int, int> coord2Id)
        {
            int n = adjacencyMatrix.GetLength(0);
            var mstEdges = new List<(Vector2Int, Vector2Int)>();
            if (n == 0) return mstEdges;

            var inTree = new bool[n];
            var keys = new int[n];
            var parent = new int[n];
            for (int i = 0; i < n; i++)
            {
                keys[i] = int.MaxValue;
                parent[i] = -1;
            }

            keys[0] = 0;

            for (int count = 0; count < n; count++)
            {
                int u = -1;
                int minKey = int.MaxValue;
                for (int v = 0; v < n; v++)
                {
                    if (!inTree[v] && keys[v] < minKey)
                    {
                        minKey = keys[v];
                        u = v;
                    }
                }

                inTree[u] = true;

                for (int v = 0; v < n; v++)
                {
                    if (adjacencyMatrix[u, v] != 0 && !inTree[v] && adjacencyMatrix[u, v] < keys[v])
                    {
                        keys[v] = adjacencyMatrix[u, v];
                        parent[v] = u;
                    }
                }
            }

            var id2Coord = new Vector2Int[n];
            foreach (var kvp in coord2Id)
                id2Coord[kvp.Value] = kvp.Key;

            for (int v = 1; v < n; v++)
            {
                if (parent[v] != -1)
                    mstEdges.Add((id2Coord[parent[v]], id2Coord[v]));
            }

            return mstEdges;
        }
    }
}
