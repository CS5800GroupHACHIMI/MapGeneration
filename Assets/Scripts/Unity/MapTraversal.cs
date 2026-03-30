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

    private readonly MapGrid _grid;
    private readonly Player  _player;

    private Queue<(int x, int y)> _path;
    private float _moveTimer;
    private const float MoveInterval = 0.1f;

    public MapTraversal(MapGrid grid, Player player)
    {
        _grid   = grid;
        _player = player;
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

        if (!IsAutoWalking || _path == null || _path.Count == 0)
        {
            if (IsAutoWalking)
            {
                IsAutoWalking = false;
                Debug.Log("[MapTraversal] Traversal complete");
            }
            return;
        }

        _moveTimer -= Time.deltaTime;
        if (_moveTimer > 0f) return;

        var (x, y) = _path.Dequeue();
        _player.MoveTo(x, y);
        _moveTimer = MoveInterval;
    }

    // ─── Public API ──────────────────────────────────────────────────────────

    public void Begin()
    {
        var rooms = DetectRooms();
        if (rooms.Count == 0) { Debug.LogWarning("[MapTraversal] No rooms found"); return; }

        var adj       = BuildAdjacency(rooms);
        int startRoom = FindPlayerRoom(rooms);

        // Get the room visit sequence (DFS includes backtracks)
        List<int> sequence = Algorithm == TraversalAlgorithm.BFS
            ? BFSSequence(adj, startRoom)
            : DFSSequence(adj, startRoom);

        _path = BuildPathFromSequence(rooms, sequence);
        IsAutoWalking = true;
        _moveTimer = 0f;

        Debug.Log($"[MapTraversal] {Algorithm}: {rooms.Count} rooms, {_path.Count} steps");
    }

    public void Stop()
    {
        IsAutoWalking = false;
        _path?.Clear();
        Debug.Log("[MapTraversal] Stopped");
    }

    // ─── Room Detection (chunk-based, 10×8) ─────────────────────────────────

    private struct Room
    {
        public int chunkX, chunkY;
        public Vector2Int center;
    }

    private List<Room> DetectRooms()
    {
        const int chunkW = 10, chunkH = 8;
        int cols = Mathf.Max(1, _grid.Width  / chunkW);
        int rows = Mathf.Max(1, _grid.Height / chunkH);
        var rooms = new List<Room>();

        for (int cx = 0; cx < cols; cx++)
        for (int cy = 0; cy < rows; cy++)
        {
            var tiles = new List<(int x, int y)>();
            for (int x = cx * chunkW; x < (cx + 1) * chunkW && x < _grid.Width; x++)
            for (int y = cy * chunkH; y < (cy + 1) * chunkH && y < _grid.Height; y++)
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

    // ─── BFS / DFS Room Sequences ────────────────────────────────────────────

    /// <summary>
    /// BFS: returns room visit order [R0, R1, R2, R3].
    /// Player walks between consecutive rooms using tile-level shortest path.
    /// May pass through already-visited rooms — that's inherent to BFS on a physical map.
    /// </summary>
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

    /// <summary>
    /// DFS: returns room visit order WITH backtracks.
    /// [R0, R1, R3, R1, R0, R2, R0]
    ///              ↑backtrack  ↑backtrack
    /// Player only ever walks between adjacent rooms — no shortcuts.
    /// </summary>
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
            seq.Add(cur); // backtrack to current room
        }
    }

    // ─── Build Tile Path from Room Sequence ─────────────────────────────────

    private Queue<(int x, int y)> BuildPathFromSequence(
        List<Room> rooms, List<int> sequence)
    {
        var fullPath = new Queue<(int x, int y)>();
        int curX = _player.X, curY = _player.Y;

        foreach (int ri in sequence)
        {
            var target = rooms[ri].center;
            var segment = FindPath(curX, curY, target.x, target.y);

            // Skip first tile (current position)
            for (int j = 1; j < segment.Count; j++)
                fullPath.Enqueue(segment[j]);

            curX = target.x;
            curY = target.y;
        }

        return fullPath;
    }

    // ─── Tile-level Pathfinding (BFS on Floor grid) ──────────────────────────

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
