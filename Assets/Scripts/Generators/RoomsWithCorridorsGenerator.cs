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

        // Adjacency with corridor tiles
        protected Dictionary<Vector2Int, List<(Vector2Int neighbor, int weight, List<Vector2Int> corridorTiles)>> _weightedAdjacencyList;
        protected Dictionary<Vector2Int, int> _coord2VertexId;
        protected int[,] _weightedAdjacencyMatrix;

        // Optional: lookup edges to corridor tiles
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

            // 3️⃣ Initialize adjacency list and edge tiles
            _weightedAdjacencyList = new Dictionary<Vector2Int, List<(Vector2Int, int, List<Vector2Int>)>>();
            _edgeTiles = new Dictionary<(Vector2Int, Vector2Int), List<Vector2Int>>();

            foreach (var room in roomPositions)
                _weightedAdjacencyList[room] = new List<(Vector2Int, int, List<Vector2Int>)>();

            // 4️⃣ Ensure connectivity (chain rooms)
            for (int i = 0; i < roomPositions.Count - 1; i++)
                ConnectRooms(grid, roomPositions[i], roomPositions[i + 1]);

            // 5️⃣ Add extra random connections (optional loops)
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

            // 6️⃣ Spawn
            Vector2Int spawnRoom = roomPositions[Random.Range(0, roomPositions.Count)];
            _startPosition = GetRoomCenter(spawnRoom);

            // 7️⃣ Build adjacency matrix
            _coord2VertexId = new Dictionary<Vector2Int, int>();
            for (int i = 0; i < roomPositions.Count; i++)
                _coord2VertexId[roomPositions[i]] = i;

            int n = roomPositions.Count;
            _weightedAdjacencyMatrix = new int[n, n];

    
            foreach (var kvp in _weightedAdjacencyList)
            {
                int fromId = _coord2VertexId[kvp.Key];
                foreach (var (neighbor, weight, tiles) in kvp.Value)
                {
                    // Only include edges that do NOT pass through another room
                    if (!DoesEdgePassThroughOtherRoom(kvp.Key, neighbor, tiles))
                    {
                        int toId = _coord2VertexId[neighbor];
                        _weightedAdjacencyMatrix[fromId, toId] = weight;
                        _weightedAdjacencyMatrix[toId, fromId] = weight;
                        _edgeTiles[(kvp.Key, neighbor)] = tiles;
                        _edgeTiles[(neighbor, kvp.Key)] = tiles;
                    }
                }
            }

            // re-make the adjacency list based on the adjacency matrix to ensure consistency
        }

        // =========================
        // CORE CONNECTION LOGIC
        // =========================

        private void ConnectRooms(MapGrid grid, Vector2Int a, Vector2Int b)
        {


            Vector2Int centerA = GetRoomCenter(a);
            Vector2Int centerB = GetRoomCenter(b);

            List<Vector2Int> corridorTiles = CarveCorridor(grid, centerA, centerB);

            // Check if corridor passes through another room
            Vector2Int? blockingRoom = null;

            foreach (var room in _weightedAdjacencyList.Keys)
            {
                if (room == a || room == b) continue;
                var roomTiles = GetRoomTiles(room);
                if (corridorTiles.Exists(tile => roomTiles.Contains(tile)))
                {
                    blockingRoom = room;
                    break;
                }
            }

            if (blockingRoom == null)
            {
                // No blocking room, safe to add edge
                int weight = corridorTiles.Count;
                _weightedAdjacencyList[a].Add((b, weight, corridorTiles));
                _weightedAdjacencyList[b].Add((a, weight, corridorTiles));
                _edgeTiles[(a, b)] = corridorTiles;
                _edgeTiles[(b, a)] = corridorTiles;
            }
            else
            {
                Debug.Log($"Edge {a} <-> {b} blocked by {blockingRoom.Value}, splitting corridor");
                // Split corridor through blocking room c
                Vector2Int c = blockingRoom.Value;
                Vector2Int centerC = GetRoomCenter(c);

                // 1️⃣ a -> c
                List<Vector2Int> corridorAC = CarveCorridor(grid, centerA, centerC);
                int weightAC = corridorAC.Count;
                _weightedAdjacencyList[a].Add((c, weightAC, corridorAC));
                _weightedAdjacencyList[c].Add((a, weightAC, corridorAC));
                _edgeTiles[(a, c)] = corridorAC;
                _edgeTiles[(c, a)] = corridorAC;

                // 2️⃣ c -> b
                List<Vector2Int> corridorCB = CarveCorridor(grid, centerC, centerB);
                int weightCB = corridorCB.Count;
                _weightedAdjacencyList[c].Add((b, weightCB, corridorCB));
                _weightedAdjacencyList[b].Add((c, weightCB, corridorCB));
                _edgeTiles[(c, b)] = corridorCB;
                _edgeTiles[(b, c)] = corridorCB;

                // 3️⃣ Remove direct a -> b if it exists
                _weightedAdjacencyList[a].RemoveAll(e => e.neighbor == b);
                _weightedAdjacencyList[b].RemoveAll(e => e.neighbor == a);
                _edgeTiles.Remove((a, b));
                _edgeTiles.Remove((b, a));
            }
        }

        private bool HasEdge(Vector2Int a, Vector2Int b)
        {
            return _weightedAdjacencyList[a].Exists(e => e.neighbor == b);
        }

        private bool DoesEdgePassThroughOtherRoom(Vector2Int from, Vector2Int to, List<Vector2Int> corridorTiles)
        {
            foreach (var room in _weightedAdjacencyList.Keys)
            {
                if (room == from || room == to) continue;

                foreach (var tile in corridorTiles)
                {
                    var roomTiles = GetRoomTiles(room);
                    if (roomTiles.Contains(tile))
                        return true;
                }
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

        private bool AreRoomsOrthogonal(Vector2Int a, Vector2Int b)
        {
            // Return true if rooms share the same row or column
            return a.x == b.x || a.y == b.y;
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