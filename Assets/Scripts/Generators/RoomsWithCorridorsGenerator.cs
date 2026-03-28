using Data;
using Model;
using System.Collections.Generic;
using UnityEngine;

namespace Generators
{
    [CreateAssetMenu(fileName = "RoomsWithCorridorsGenerator", menuName = "Generators/RoomsWithCorridorsGenerator")]
    public class RoomsWithCorridorsGenerator : MapGeneratorBase
    {
        public override string Name => "RoomsWithCorridorsGenerator";

        private const int RoomW = 10;
        private const int RoomH = 8;
        private const int DoorSize = 2;

        protected Dictionary<Vector2Int, List<(Vector2Int, int)>> _weightedAdjacencyList;
        protected Dictionary<Vector2Int, int> _coord2VertexId;
        protected int[,] _weightedAdjacencyMatrix;

        public override void Generate(MapGrid grid, MapConfig config)
        {
            Random.InitState(config.seed);

            int cols = Mathf.Max(2, grid.Width / RoomW);
            int rows = Mathf.Max(2, grid.Height / RoomH);

            var hasRoom = new bool[cols, rows];

            // Fill map with walls
            for (int x = 0; x < grid.Width; x++)
                for (int y = 0; y < grid.Height; y++)
                    grid.Set(x, y, TileType.Wall);

            // 1️⃣ Randomly place rooms anywhere in the grid
            int totalRooms = Random.Range(4, Mathf.Min(30, cols * rows));
            List<Vector2Int> roomPositions = new List<Vector2Int>();
            int attempts = 0;

            while (roomPositions.Count < totalRooms && attempts < totalRooms * 10)
            {
                int cx = Random.Range(0, cols);
                int cy = Random.Range(0, rows);
                if (!hasRoom[cx, cy])
                {
                    hasRoom[cx, cy] = true;
                    roomPositions.Add(new Vector2Int(cx, cy));
                }
                attempts++;
            }

            // 2️⃣ Carve room interiors
            foreach (var pos in roomPositions)
                CarveRoom(grid, pos.x, pos.y);

            // 3️⃣ Connect rooms randomly (can be non-adjacent)
            _weightedAdjacencyList = new Dictionary<Vector2Int, List<(Vector2Int, int)>>();
            for (int i = 0; i < roomPositions.Count; i++)
            {
                Vector2Int a = roomPositions[i];
                if (!_weightedAdjacencyList.ContainsKey(a))
                    _weightedAdjacencyList[a] = new List<(Vector2Int, int)>();

                // Connect to 1–2 other rooms randomly
                int connections = Random.Range(1, 3);
                int tries = 0;

                while (connections > 0 && tries < roomPositions.Count * 2)
                {
                    int j = Random.Range(0, roomPositions.Count);
                    if (i == j) { tries++; continue; }

                    Vector2Int b = roomPositions[j];

                    // Check for existing edge
                    bool exists = _weightedAdjacencyList[a].Exists(e => e.Item1 == b);
                    if (exists) { tries++; continue; }

                    // Add edge both ways
                    _weightedAdjacencyList[a].Add((b, 1));
                    if (!_weightedAdjacencyList.ContainsKey(b))
                        _weightedAdjacencyList[b] = new List<(Vector2Int, int)>();
                    _weightedAdjacencyList[b].Add((a, 1));

                    // Carve a corridor between centers
                    CarveCorridor(grid, GetRoomCenter(a), GetRoomCenter(b));
                    connections--;
                }
            }

            // 4️⃣ Set player/sprite position in a random room
            Vector2Int spawnRoom = roomPositions[Random.Range(0, roomPositions.Count)];
            _startPosition = GetRoomCenter(spawnRoom);

            // 5️⃣ Build adjacency matrix for pathfinding
            _coord2VertexId = new Dictionary<Vector2Int, int>();
            for (int v = 0; v < roomPositions.Count; v++)
                _coord2VertexId[roomPositions[v]] = v;

            int n = roomPositions.Count;
            _weightedAdjacencyMatrix = new int[n, n];
            foreach (var kvp in _weightedAdjacencyList)
            {
                int fromId = _coord2VertexId[kvp.Key];
                foreach (var (neighbor, weight) in kvp.Value)
                {
                    int toId = _coord2VertexId[neighbor];
                    _weightedAdjacencyMatrix[fromId, toId] = weight;
                    _weightedAdjacencyMatrix[toId, fromId] = weight;
                }
            }
        }

        private static Vector2Int GetRoomCenter(Vector2Int gridPos)
        {
            return new Vector2Int(gridPos.x * RoomW + RoomW / 2, gridPos.y * RoomH + RoomH / 2);
        }

        private static void CarveRoom(MapGrid grid, int cx, int cy)
        {
            int ox = cx * RoomW;
            int oy = cy * RoomH;
            for (int x = ox + 1; x < ox + RoomW - 1; x++)
                for (int y = oy + 1; y < oy + RoomH - 1; y++)
                    grid.Set(x, y, TileType.Floor);
        }

        private static void CarveCorridor(MapGrid grid, Vector2Int from, Vector2Int to)
        {
            Vector2Int cur = from;

            // Simple L-shaped corridor
            while (cur.x != to.x)
            {
                grid.Set(cur.x, cur.y, TileType.Floor);
                cur.x += (to.x > cur.x) ? 1 : -1;
            }
            while (cur.y != to.y)
            {
                grid.Set(cur.x, cur.y, TileType.Floor);
                cur.y += (to.y > cur.y) ? 1 : -1;
            }
        }

        public Dictionary<Vector2Int, List<(Vector2Int, int)>> ExportWeightedAdjacencyList()
        {
            return _weightedAdjacencyList;
        }

        public (Dictionary<Vector2Int, int>, int[,]) ExportWeightedAdjacencyMatrix()
        {
            return (_coord2VertexId, _weightedAdjacencyMatrix);
        }
    }
}