using System.Collections.Generic;
using Data;
using Model;
using UnityEngine;
using UnityEngine.InputSystem;
using VContainer.Unity;

public enum TraversalAlgorithm { BFS, DFS }

public class MapTraversal : ITickable
{
    public bool IsAutoWalking { get; private set; }
    public TraversalAlgorithm Algorithm { get; set; } = TraversalAlgorithm.BFS;

    private readonly MapGrid  _grid;
    private readonly Player   _player;
    private readonly FogOfWar _fog;

    // State machine
    private List<Room>  _rooms;
    private List<int>   _roomSequence;   // BFS/DFS order (DFS includes backtracks)
    private int         _seqIndex;       // current index in _roomSequence
    private Queue<(int x, int y)> _path; // current tile-level walk segment
    private float       _moveTimer;
    private const float MoveInterval = 0.1f;

    private const int ChunkW = 10;
    private const int ChunkH = 8;

    public MapTraversal(MapGrid grid, Player player, FogOfWar fog)
    {
        _grid   = grid;
        _player = player;
        _fog    = fog;
    }

    public void Tick()
    {
        if (Keyboard.current != null)
        {
            if (Keyboard.current[Key.T].wasPressedThisFrame)
            {
                if (IsAutoWalking) Stop();
                else               Begin();
            }

            if (Keyboard.current[Key.Y].wasPressedThisFrame)
            {
                Algorithm = Algorithm == TraversalAlgorithm.BFS
                    ? TraversalAlgorithm.DFS
                    : TraversalAlgorithm.BFS;
                Debug.Log($"[MapTraversal] Switched to {Algorithm}");
            }
        }

        if (!IsAutoWalking) return;

        _moveTimer -= Time.deltaTime;
        if (_moveTimer > 0f) return;

        // If current path segment is done, decide next action
        if (_path == null || _path.Count == 0)
        {
            if (!AdvanceState())
            {
                IsAutoWalking = false;
                Debug.Log("[MapTraversal] Traversal complete");
                return;
            }
        }

        if (_path != null && _path.Count > 0)
        {
            var (x, y) = _path.Dequeue();
            _player.MoveTo(x, y);
            _moveTimer = MoveInterval;
        }
    }

    // ─── Public API ──────────────────────────────────────────────────────────

    public void Begin()
    {
        _rooms = DetectRooms();
        if (_rooms.Count == 0) { Debug.LogWarning("[MapTraversal] No rooms found"); return; }

        var adj      = BuildAdjacency(_rooms);
        int startIdx = FindPlayerRoom(_rooms);

        _roomSequence = Algorithm == TraversalAlgorithm.BFS
            ? BFSSequence(adj, startIdx)
            : DFSSequence(adj, startIdx);

        _seqIndex = 0;
        _path     = new Queue<(int, int)>();
        _moveTimer = 0f;
        IsAutoWalking = true;

        Debug.Log($"[MapTraversal] {Algorithm}: {_rooms.Count} rooms");
    }

    public void Stop()
    {
        IsAutoWalking = false;
        _path?.Clear();
        Debug.Log("[MapTraversal] Stopped");
    }

    // ─── State Machine ───────────────────────────────────────────────────────

    /// <summary>
    /// Decides what to do next. Returns false when traversal is complete.
    ///
    /// Logic:
    ///   1. If current room has unrevealed fog → walk to nearest unrevealed tile
    ///   2. If current room is fully revealed → advance to next room in sequence
    ///   3. If no more rooms → done
    /// </summary>
    private bool AdvanceState()
    {
        if (_roomSequence == null || _rooms == null) return false;

        // Check if current room still has fog
        if (_seqIndex < _roomSequence.Count)
        {
            var room = _rooms[_roomSequence[_seqIndex]];

            if (!IsRoomFullyRevealed(room))
            {
                // Walk to nearest unrevealed floor tile in this room
                var target = FindNearestUnrevealed(room);
                if (target.HasValue)
                {
                    var segment = FindPath(_player.X, _player.Y, target.Value.x, target.Value.y);
                    _path = ToQueue(segment, 1);
                    return true;
                }
            }

            // Room fully revealed — move to next in sequence
            _seqIndex++;
        }

        // Find next unvisited room in sequence
        while (_seqIndex < _roomSequence.Count)
        {
            var nextRoom = _rooms[_roomSequence[_seqIndex]];
            var segment  = FindPath(_player.X, _player.Y, nextRoom.center.x, nextRoom.center.y);
            _path = ToQueue(segment, 1);

            if (_path.Count > 0)
                return true;

            // Already at this room's center — will check fog on next tick
            if (!IsRoomFullyRevealed(nextRoom))
                return true;

            _seqIndex++;
        }

        return false; // all rooms done
    }

    private bool IsRoomFullyRevealed(Room room)
    {
        int x0 = room.chunkX * ChunkW;
        int y0 = room.chunkY * ChunkH;

        for (int x = x0; x < x0 + ChunkW && x < _grid.Width; x++)
        for (int y = y0; y < y0 + ChunkH && y < _grid.Height; y++)
        {
            var t = _grid.GetTileType(x, y);
            if (t == TileType.Wall || t == TileType.Air) continue;
            if (!_fog.IsRevealed(x, y)) return false;
        }
        return true;
    }

    private (int x, int y)? FindNearestUnrevealed(Room room)
    {
        int x0 = room.chunkX * ChunkW;
        int y0 = room.chunkY * ChunkH;

        float minDist = float.MaxValue;
        (int x, int y)? best = null;

        for (int x = x0; x < x0 + ChunkW && x < _grid.Width; x++)
        for (int y = y0; y < y0 + ChunkH && y < _grid.Height; y++)
        {
            var t = _grid.GetTileType(x, y);
            if (t == TileType.Wall || t == TileType.Air) continue;
            if (_fog.IsRevealed(x, y)) continue;

            float d = Mathf.Abs(x - _player.X) + Mathf.Abs(y - _player.Y);
            if (d < minDist) { minDist = d; best = (x, y); }
        }
        return best;
    }

    private static Queue<(int, int)> ToQueue(List<(int x, int y)> path, int skipFirst)
    {
        var q = new Queue<(int, int)>();
        for (int i = skipFirst; i < path.Count; i++)
            q.Enqueue(path[i]);
        return q;
    }

    // ─── Room Detection ──────────────────────────────────────────────────────

    private struct Room
    {
        public int chunkX, chunkY;
        public Vector2Int center;
    }

    private List<Room> DetectRooms()
    {
        int cols = Mathf.Max(1, _grid.Width  / ChunkW);
        int rows = Mathf.Max(1, _grid.Height / ChunkH);
        var rooms = new List<Room>();

        for (int cx = 0; cx < cols; cx++)
        for (int cy = 0; cy < rows; cy++)
        {
            var tiles = new List<(int x, int y)>();
            for (int x = cx * ChunkW; x < (cx + 1) * ChunkW && x < _grid.Width; x++)
            for (int y = cy * ChunkH; y < (cy + 1) * ChunkH && y < _grid.Height; y++)
            {
                var t = _grid.GetTileType(x, y);
                if (t != TileType.Wall && t != TileType.Air)
                    tiles.Add((x, y));
            }

            if (tiles.Count < 6) continue;

            rooms.Add(new Room
            {
                chunkX = cx,
                chunkY = cy,
                center = FindWalkableCenter(tiles)
            });
        }

        return rooms;
    }

    private Vector2Int FindWalkableCenter(List<(int x, int y)> tiles)
    {
        int sumX = 0, sumY = 0;
        foreach (var (x, y) in tiles) { sumX += x; sumY += y; }
        int cx = sumX / tiles.Count, cy = sumY / tiles.Count;

        float minDist = float.MaxValue;
        var best = new Vector2Int(tiles[0].x, tiles[0].y);
        foreach (var (x, y) in tiles)
        {
            float d = Mathf.Abs(x - cx) + Mathf.Abs(y - cy);
            if (d < minDist) { minDist = d; best = new Vector2Int(x, y); }
        }
        return best;
    }

    private int FindPlayerRoom(List<Room> rooms)
    {
        int px = _player.X, py = _player.Y;
        float minDist = float.MaxValue;
        int nearest = 0;
        for (int i = 0; i < rooms.Count; i++)
        {
            float d = Vector2Int.Distance(rooms[i].center, new Vector2Int(px, py));
            if (d < minDist) { minDist = d; nearest = i; }
        }
        return nearest;
    }

    // ─── Room Graph ──────────────────────────────────────────────────────────

    private Dictionary<int, List<int>> BuildAdjacency(List<Room> rooms)
    {
        var chunkToRoom = new Dictionary<(int, int), int>();
        for (int i = 0; i < rooms.Count; i++)
            chunkToRoom[(rooms[i].chunkX, rooms[i].chunkY)] = i;

        var adj = new Dictionary<int, List<int>>();
        for (int i = 0; i < rooms.Count; i++)
            adj[i] = new List<int>();

        int[] dx = { 1, 0, -1, 0 };
        int[] dy = { 0, 1, 0, -1 };

        for (int i = 0; i < rooms.Count; i++)
        {
            for (int d = 0; d < 4; d++)
            {
                var key = (rooms[i].chunkX + dx[d], rooms[i].chunkY + dy[d]);
                if (chunkToRoom.TryGetValue(key, out int j) && !adj[i].Contains(j))
                {
                    adj[i].Add(j);
                    adj[j].Add(i);
                }
            }
        }

        return adj;
    }

    // ─── BFS / DFS ───────────────────────────────────────────────────────────

    private static List<int> BFSSequence(Dictionary<int, List<int>> adj, int start)
    {
        var visited = new HashSet<int> { start };
        var queue   = new Queue<int>();
        var order   = new List<int> { start };

        queue.Enqueue(start);
        while (queue.Count > 0)
        {
            int cur = queue.Dequeue();
            foreach (int nb in adj[cur])
            {
                if (visited.Add(nb))
                {
                    queue.Enqueue(nb);
                    order.Add(nb);
                }
            }
        }
        return order;
    }

    private static List<int> DFSSequence(Dictionary<int, List<int>> adj, int start)
    {
        var visited  = new HashSet<int>();
        var sequence = new List<int>();
        DFSHelper(adj, start, visited, sequence);
        return sequence;
    }

    private static void DFSHelper(
        Dictionary<int, List<int>> adj, int cur,
        HashSet<int> visited, List<int> seq)
    {
        visited.Add(cur);
        seq.Add(cur);
        foreach (int nb in adj[cur])
        {
            if (visited.Contains(nb)) continue;
            DFSHelper(adj, nb, visited, seq);
            seq.Add(cur); // backtrack
        }
    }

    // ─── Tile Pathfinding ────────────────────────────────────────────────────

    private List<(int x, int y)> FindPath(int fromX, int fromY, int toX, int toY)
    {
        if (fromX == toX && fromY == toY)
            return new List<(int, int)>();

        var queue  = new Queue<(int x, int y)>();
        var parent = new Dictionary<(int, int), (int x, int y)>();

        queue.Enqueue((fromX, fromY));
        parent[(fromX, fromY)] = (-1, -1);

        int[] dx = { 1, -1, 0, 0 };
        int[] dy = { 0, 0, 1, -1 };

        while (queue.Count > 0)
        {
            var (cx, cy) = queue.Dequeue();
            if (cx == toX && cy == toY)
            {
                var path = new List<(int, int)>();
                var pos  = (toX, toY);
                while (pos != (-1, -1))
                {
                    path.Add(pos);
                    pos = parent[pos];
                }
                path.Reverse();
                return path;
            }

            for (int d = 0; d < 4; d++)
            {
                int nx = cx + dx[d], ny = cy + dy[d];
                if (!_grid.InBounds(nx, ny)) continue;
                if (_grid.GetTileType(nx, ny) == TileType.Wall) continue;
                if (parent.ContainsKey((nx, ny))) continue;
                parent[(nx, ny)] = (cx, cy);
                queue.Enqueue((nx, ny));
            }
        }

        return new List<(int, int)>();
    }
}
