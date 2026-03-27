using Data;
using Model;
using System.Collections.Generic;
using UnityEngine;

namespace Generators
{
    /// <summary>
    /// Generates a Metroidvania-style room-based map.
    ///
    /// Algorithm:
    ///   1. Divide the map into a grid of room cells (RoomW x RoomH tiles each)
    ///   2. Starting from the center cell, expand via random walk to reach target room count
    ///   3. Add extra connections (loops) so the layout isn't a pure tree
    ///   4. Draw each room (wall border + floor interior) and open doorways between connected rooms
    ///
    /// Recommended MapConfig size: 64x64 or larger.
    /// </summary>
    [CreateAssetMenu(fileName = "MetroidvaniaGenerator", menuName = "Generators/Metroidvania")]
    public class MetroidvaniaGenerator : MapGeneratorBase
    {
        public override string Name => "Metroidvania";

        private const int RoomW    = 10; // room width  in tiles (including 1-tile walls on each side)
        private const int RoomH    = 8;  // room height in tiles (including 1-tile walls on each side)
        private const int DoorSize = 2;  // door opening width/height in tiles

        // right, left, up, down
        private static readonly int[] Dx = {  1, -1,  0,  0 };
        private static readonly int[] Dy = {  0,  0,  1, -1 };

        private Dictionary<Vector2Int, List<(Vector2Int, int)>> _weightedAdjacencyList;
        private Dictionary<Vector2Int, int> _coord2VertexId;
        private int[,] _weightedAdjacencyMatrix;

        public override void Generate(MapGrid grid, MapConfig config)
        {
            Random.InitState(config.seed);

            int cols = Mathf.Max(2, grid.Width  / RoomW);
            int rows = Mathf.Max(2, grid.Height / RoomH);

            var hasRoom = new bool[cols, rows];
            var connH   = new bool[cols - 1, rows];  // connH[cx,cy]: room(cx,cy) <-> room(cx+1,cy)
            var connV   = new bool[cols, rows - 1];  // connV[cx,cy]: room(cx,cy) <-> room(cx,cy+1)

            // Fill everything with solid Wall — rooms and corridors will be carved out
            for (int x = 0; x < grid.Width;  x++)
            for (int y = 0; y < grid.Height; y++)
                grid.Set(x, y, TileType.Wall);

            int startCol    = cols / 2;
            int startRow    = rows / 2;
            int targetRooms = Mathf.Clamp(cols * rows / 2, 4, 30);

            BuildRoomGraph(hasRoom, connH, connV, cols, rows, startCol, startRow, targetRooms);

            // Carve room interiors (borders stay Wall = solid rock)
            for (int cx = 0; cx < cols; cx++)
            for (int cy = 0; cy < rows; cy++)
                if (hasRoom[cx, cy])
                    CarveRoom(grid, cx, cy);

            // Carve door openings through the shared walls between adjacent rooms
            for (int cx = 0; cx < cols - 1; cx++)
            for (int cy = 0; cy < rows; cy++)
                if (connH[cx, cy])
                    CarveDoorH(grid, cx, cy);

            for (int cx = 0; cx < cols; cx++)
            for (int cy = 0; cy < rows - 1; cy++)
                if (connV[cx, cy])
                    CarveDoorV(grid, cx, cy);

            // Player starts at the interior center of the starting room
            _startPosition = new Vector2Int(
                startCol * RoomW + RoomW / 2,
                startRow * RoomH + RoomH / 2
            );
        }

        // ─── Room Graph ──────────────────────────────────────────────────────────

        private void BuildRoomGraph(
            bool[,] hasRoom, bool[,] connH, bool[,] connV,
            int cols, int rows, int startCol, int startRow, int targetRooms)
        {
            hasRoom[startCol, startRow] = true;
            var frontier = new List<Vector2Int> { new Vector2Int(startCol, startRow) };
            int count = 1;

            // Random-walk expansion: pick a random frontier room, try to grow in a random direction
            while (count < targetRooms && frontier.Count > 0)
            {
                int fi  = Random.Range(0, frontier.Count);
                var cur = frontier[fi];

                var dirs = new List<int> { 0, 1, 2, 3 };
                Shuffle(dirs);
                bool expanded = false;

                foreach (int d in dirs)
                {
                    int nx = cur.x + Dx[d];
                    int ny = cur.y + Dy[d];

                    if (nx < 0 || nx >= cols || ny < 0 || ny >= rows) continue;
                    if (hasRoom[nx, ny]) continue;

                    hasRoom[nx, ny] = true;
                    SetConn(connH, connV, cur.x, cur.y, nx, ny, d);
                    frontier.Add(new Vector2Int(nx, ny));
                    count++;
                    expanded = true;
                    break;
                }

                if (!expanded) frontier.RemoveAt(fi);
            }

            // Add extra connections (loops) so the map has shortcuts and cycles
            int extras = Mathf.Max(1, count / 3);
            for (int attempt = 0; attempt < extras * 15 && extras > 0; attempt++)
            {
                int cx = Random.Range(0, cols);
                int cy = Random.Range(0, rows);
                if (!hasRoom[cx, cy]) continue;

                int d  = Random.Range(0, 4);
                int nx = cx + Dx[d];
                int ny = cy + Dy[d];

                if (nx < 0 || nx >= cols || ny < 0 || ny >= rows) continue;
                if (!hasRoom[nx, ny]) continue;
                if (IsConn(connH, connV, cx, cy, d)) continue;

                SetConn(connH, connV, cx, cy, nx, ny, d);
                extras--;
            }

            _weightedAdjacencyList = new Dictionary<Vector2Int, List<(Vector2Int, int)>>();

            // Loop over array: cx = column (x), cy = row (y)
            for (int cx = 0; cx < cols; cx++)
            {
                for (int cy = 0; cy < rows; cy++)
                {
                    if (!hasRoom[cx, cy])
                        continue;

                    // Flip y so 0 = bottom row in Unity
                    int y = rows - 1 - cy;
                    Vector2Int room1 = new Vector2Int(cx, y);

                    if (!_weightedAdjacencyList.ContainsKey(room1))
                        _weightedAdjacencyList[room1] = new List<(Vector2Int, int)>();

                    // Right neighbor
                    if (cx + 1 < cols && hasRoom[cx + 1, cy] && connH[cx, cy])
                    {
                        Vector2Int room2 = new Vector2Int(cx + 1, y);
                        if (!_weightedAdjacencyList.ContainsKey(room2))
                            _weightedAdjacencyList[room2] = new List<(Vector2Int, int)>();

                        _weightedAdjacencyList[room1].Add((room2, 1));
                        _weightedAdjacencyList[room2].Add((room1, 1));
                    }

                    // Top neighbor
                    if (cy + 1 < rows && hasRoom[cx, cy + 1] && connV[cx, cy])
                    {
                        int ny = rows - 1 - (cy + 1); // flip y
                        Vector2Int room3 = new Vector2Int(cx, ny);
                        if (!_weightedAdjacencyList.ContainsKey(room3))
                            _weightedAdjacencyList[room3] = new List<(Vector2Int, int)>();

                        _weightedAdjacencyList[room1].Add((room3, 1));
                        _weightedAdjacencyList[room3].Add((room1, 1));
                    }
                }
            }

            // Build coordinate to vertex mapping
            _coord2VertexId = new Dictionary<Vector2Int, int>();
            for (int cx = 0; cx < cols; cx++)
            {
                for (int cy = 0; cy < rows; cy++)
                {
                    Vector2Int temp = new Vector2Int(cx, rows - 1 - cy); // flip y
                    if (hasRoom[cx, cy])
                        _coord2VertexId[temp] = _coord2VertexId.Count; // sequential IDs
                }
            }

            // Initialize adjacency matrix
            int n = _coord2VertexId.Count;
            _weightedAdjacencyMatrix = new int[n, n]; // automatically filled with 0

            // Fill adjacency matrix from list
            foreach (var kvp in _weightedAdjacencyList)
            {
                Vector2Int room = kvp.Key;
                int fromId = _coord2VertexId[room];

                foreach (var (neighbor, weight) in kvp.Value)
                {
                    if (!_coord2VertexId.ContainsKey(neighbor)) continue;

                    int toId = _coord2VertexId[neighbor];
                    _weightedAdjacencyMatrix[fromId, toId] = weight;
                    _weightedAdjacencyMatrix[toId, fromId] = weight; // undirected
                }
            }
        }

        private static void SetConn(bool[,] connH, bool[,] connV,
            int x, int y, int nx, int ny, int dir)
        {
            switch (dir)
            {
                case 0: connH[x,  y ] = true; break; // right: slot (x,   y)
                case 1: connH[nx, y ] = true; break; // left:  slot (x-1, y)
                case 2: connV[x,  y ] = true; break; // up:    slot (x,   y)
                case 3: connV[x,  ny] = true; break; // down:  slot (x,   y-1)
            }
        }

        private static bool IsConn(bool[,] connH, bool[,] connV, int x, int y, int dir)
        {
            switch (dir)
            {
                case 0: return x     < connH.GetLength(0) && connH[x,     y];
                case 1: return x - 1 >= 0                  && connH[x - 1, y];
                case 2: return y     < connV.GetLength(1)  && connV[x,     y];
                case 3: return y - 1 >= 0                  && connV[x,     y - 1];
                default: return false;
            }
        }

        // ─── Return adjacency list and matrix for Prim's, Kruskal's, Dijkstra's, etc. ────

        public Dictionary<Vector2Int, List<(Vector2Int, int)>> ExportWeightedAdjacencyList()
        {
            return _weightedAdjacencyList;
        }

        public (Dictionary<Vector2Int, int>, int[,]) ExportWeightedAdjacencyMatrix()
        {
            return (_coord2VertexId, _weightedAdjacencyMatrix);
        }

        // ─── Drawing ─────────────────────────────────────────────────────────────

        // Carve the interior of a room to Floor; the border tiles remain Wall (solid rock)
        private static void CarveRoom(MapGrid grid, int cx, int cy)
        {
            int ox = cx * RoomW;
            int oy = cy * RoomH;

            for (int x = ox + 1; x < ox + RoomW - 1; x++)
            for (int y = oy + 1; y < oy + RoomH - 1; y++)
                grid.Set(x, y, TileType.Floor);
        }

        // Carve a horizontal door: punch through the shared wall between room(cx,cy) and room(cx+1,cy)
        private static void CarveDoorH(MapGrid grid, int cx, int cy)
        {
            int wallX  = cx * RoomW + RoomW - 1;  // right border of room(cx,cy)
            int midY   = cy * RoomH + RoomH / 2;
            int yStart = midY - DoorSize / 2;
            int yMin   = cy * RoomH + 1;
            int yMax   = cy * RoomH + RoomH - 2;

            for (int i = 0; i < DoorSize; i++)
            {
                int y = yStart + i;
                if (y < yMin || y > yMax) continue;
                grid.Set(wallX,     y, TileType.Floor);  // right wall of left room
                grid.Set(wallX + 1, y, TileType.Floor);  // left  wall of right room
            }
        }

        // Carve a vertical door: punch through the shared wall between room(cx,cy) and room(cx,cy+1)
        private static void CarveDoorV(MapGrid grid, int cx, int cy)
        {
            int wallY  = cy * RoomH + RoomH - 1;  // top border of room(cx,cy)
            int midX   = cx * RoomW + RoomW / 2;
            int xStart = midX - DoorSize / 2;
            int xMin   = cx * RoomW + 1;
            int xMax   = cx * RoomW + RoomW - 2;

            for (int i = 0; i < DoorSize; i++)
            {
                int x = xStart + i;
                if (x < xMin || x > xMax) continue;
                grid.Set(x, wallY,     TileType.Floor);  // top    wall of bottom room
                grid.Set(x, wallY + 1, TileType.Floor);  // bottom wall of top    room
            }
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
