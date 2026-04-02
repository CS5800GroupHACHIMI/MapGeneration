using Data;
using Model;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace Generators
{
    [CreateAssetMenu(fileName = "RoomsWithCorridorsGenerator", menuName = "Generators/RoomsWithCorridorsGenerator")]
    public class RoomsWithCorridorsGenerator : MapGeneratorBase
    {
        public override string Name => "RoomsWithCorridorsGenerator";

        private const int RoomW = 10;
        private const int RoomH = 8;

        // Adjacency with corridor tiles
        protected Dictionary<Vector2Int, List<(Vector2Int neighbor, int weight, List<Vector2Int> corridorTiles)>> _weightedAdjacencyList;
        protected Dictionary<Vector2Int, int> _coord2VertexId;
        protected int[,] _weightedAdjacencyMatrix;

        // Lookup edges to corridor tiles
        protected Dictionary<(Vector2Int, Vector2Int), List<Vector2Int>> _edgeTiles;

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

            // 1️⃣ Place rooms
            int totalRooms = Random.Range(6, Mathf.Min(30, cols * rows));
            List<Vector2Int> roomPositions = new List<Vector2Int>();
            while (roomPositions.Count < totalRooms)
            {
                int cx = Random.Range(0, cols);
                int cy = Random.Range(0, rows);
                if (!hasRoom[cx, cy])
                {
                    hasRoom[cx, cy] = true;
                    roomPositions.Add(new Vector2Int(cx, cy));
                }
            }

            // 2️⃣ Carve rooms
            foreach (var pos in roomPositions)
                CarveRoom(grid, pos.x, pos.y);

            // 3️⃣ Initialize adjacency structures
            _weightedAdjacencyList = new Dictionary<Vector2Int, List<(Vector2Int, int, List<Vector2Int>)>>();
            _edgeTiles = new Dictionary<(Vector2Int, Vector2Int), List<Vector2Int>>();

            foreach (var room in roomPositions)
                _weightedAdjacencyList[room] = new List<(Vector2Int, int, List<Vector2Int>)>();

            // 4️⃣ Ensure connectivity (chain rooms)
            for (int i = 0; i < roomPositions.Count - 1; i++)
                ConnectRooms(grid, roomPositions[i], roomPositions[i + 1]);

            // 5️⃣ Add extra random connections
            int extraEdges = roomPositions.Count / 2;
            for (int k = 0; k < extraEdges; k++)
            {
                int i = Random.Range(0, roomPositions.Count);
                int j = Random.Range(0, roomPositions.Count);
                if (i == j) continue;

                Vector2Int a = roomPositions[i];
                Vector2Int b = roomPositions[j];
                if (HasEdge(a, b)) continue;

                ConnectRooms(grid, a, b);
            }

            // 6️⃣ Spawn point
            Vector2Int spawnRoom = roomPositions[Random.Range(0, roomPositions.Count)];
            _startPosition = GetRoomCenter(spawnRoom);

            // 7️⃣ Build adjacency matrix (no filtering)
            _coord2VertexId = new Dictionary<Vector2Int, int>();
            for (int i = 0; i < roomPositions.Count; i++)
                _coord2VertexId[roomPositions[i]] = i;

            int n = roomPositions.Count;
            _weightedAdjacencyMatrix = new int[n, n];
            var newEdgeTiles = new Dictionary<(Vector2Int, Vector2Int), List<Vector2Int>>();

            // Add all edges
            foreach (var kvp in _weightedAdjacencyList)
            {
                var from = kvp.Key;
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

            // Rebuild adjacency list from matrix (preserve all edges)
            var newAdjList = new Dictionary<Vector2Int, List<(Vector2Int, int, List<Vector2Int>)>>();
            foreach (var room in roomPositions)
                newAdjList[room] = new List<(Vector2Int, int, List<Vector2Int>)>();

            foreach (var from in roomPositions)
            {
                int i = _coord2VertexId[from];
                foreach (var to in roomPositions)
                {
                    int j = _coord2VertexId[to];
                    if (_weightedAdjacencyMatrix[i, j] > 0)
                    {
                        if (newEdgeTiles.TryGetValue((from, to), out var tiles))
                        {
                            int weight = _weightedAdjacencyMatrix[i, j];
                            newAdjList[from].Add((to, weight, tiles));
                        }
                    }
                }
            }

            _weightedAdjacencyList = newAdjList;
            _edgeTiles = newEdgeTiles;

        }

        // =========================
        // CORE CONNECTION LOGIC
        // =========================
        private void ConnectRooms(MapGrid grid, Vector2Int a, Vector2Int b)
        {
            Vector2Int centerA = GetRoomCenter(a);
            Vector2Int centerB = GetRoomCenter(b);

            // Carve the corridor between the two room centers
            List<Vector2Int> corridorTiles = CarveCorridor(grid, centerA, centerB);

            // Compute weight based on corridor length
            int weight = corridorTiles.Count;

            // Add edge to adjacency list
            if (!_weightedAdjacencyList[a].Exists(e => e.neighbor == b))
                _weightedAdjacencyList[a].Add((b, weight, corridorTiles));

            if (!_weightedAdjacencyList[b].Exists(e => e.neighbor == a))
                _weightedAdjacencyList[b].Add((a, weight, corridorTiles));

            // Add edge to edgeTiles dictionary
            _edgeTiles[(a, b)] = corridorTiles;
            _edgeTiles[(b, a)] = corridorTiles;
        }


        private bool HasEdge(Vector2Int a, Vector2Int b)
        {
            return _weightedAdjacencyList.ContainsKey(a) && _weightedAdjacencyList[a].Exists(e => e.neighbor == b);
        }

        private bool DoesEdgePassThroughOtherRoom(Vector2Int from, Vector2Int to, List<Vector2Int> corridorTiles)
        {
            foreach (var room in _weightedAdjacencyList.Keys)
            {
                // Skip the endpoints
                if (room == from || room == to) continue;

                var roomTiles = GetRoomTiles(room);

                // Exclude overlapping tile at start/end
                if (corridorTiles.Skip(1).Take(corridorTiles.Count - 3).Any(tile => roomTiles.Contains(tile)))
                    return true;
            }
            return false;
        }

        private List<Vector2Int> GetRoomTiles(Vector2Int roomCenter)
        {
            var tiles = new List<Vector2Int>();
            int ox = roomCenter.x - RoomW / 2;
            int oy = roomCenter.y - RoomH / 2;
            for (int x = ox; x < ox + RoomW; x++)
                for (int y = oy; y < oy + RoomH; y++)
                    tiles.Add(new Vector2Int(x, y));
            return tiles;
        }

        // =========================
        // HELPERS
        // =========================

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

        private static List<Vector2Int> CarveCorridor(MapGrid grid, Vector2Int from, Vector2Int to)
        {
            Vector2Int cur = from;
            List<Vector2Int> tiles = new List<Vector2Int> { cur };

            while (cur.x != to.x)
            {
                cur.x += (to.x > cur.x) ? 1 : -1;
                grid.Set(cur.x, cur.y, TileType.Floor);
                tiles.Add(cur);
            }

            while (cur.y != to.y)
            {
                cur.y += (to.y > cur.y) ? 1 : -1;
                grid.Set(cur.x, cur.y, TileType.Floor);
                tiles.Add(cur);
            }

            return tiles;
        }

        // =========================
        // EXPORTERS
        // =========================

        public Dictionary<Vector2Int, List<(Vector2Int neighbor, int weight, List<Vector2Int> corridorTiles)>> ExportWeightedAdjacencyList()
        {
            return _weightedAdjacencyList;
        }

        public (Dictionary<Vector2Int, int>, int[,], Dictionary<(Vector2Int, Vector2Int), List<Vector2Int>>) ExportWeightedAdjacencyMatrix()
        {
            return (_coord2VertexId, _weightedAdjacencyMatrix, _edgeTiles);
        }
    }
}