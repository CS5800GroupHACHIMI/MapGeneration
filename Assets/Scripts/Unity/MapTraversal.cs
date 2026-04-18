using System.Collections.Generic;
using Data;
using Model;
using UnityEngine;
using UnityEngine.InputSystem;
using VContainer.Unity;

/// <summary>
/// Two-phase fog-aware auto-walk. Press T to start/stop.
///
/// Phase 1 – Explore
///   Walk to the nearest revealed tile adjacent to unrevealed terrain
///   (frontier exploration). Opportunistic chest detour when HP is low.
///   Key is picked up automatically when revealed and stepped on.
///
/// Phase 2 – SeekExit
///   Once the key is collected and the exit tile is revealed, uses
///   room-level Dijkstra + tile A* to reach the exit, avoiding
///   monster-inhabited rooms when possible.
/// </summary>
public class MapTraversal : ITickable
{
    public bool IsAutoWalking { get; private set; }

    private readonly MapGrid     _grid;
    private readonly Player      _player;
    private readonly FogOfWar    _fog;
    private readonly RoomManager _roomManager;
    private readonly ExitDoor    _exitDoor;

    // Walk state
    private Queue<(int x, int y)> _path;
    private float                 _moveTimer;
    private const float MoveInterval = 0.12f;   // match PlayerView.moveDuration for smooth chaining

    // Goal state machine. None = never started (default), distinguishes fresh state
    // from "paused mid-exploration" so auto-resume doesn't fire on untouched levels.
    private enum AutoGoal { None, Explore, SeekExit, Done }
    private AutoGoal _goal;

    // Room graph (rebuilt on Begin)
    private List<RoomInfo>              _rooms;
    private Dictionary<int, List<int>>  _adj;
    private int                         _exitRoomIdx;   // -1 if not found

    // Danger map: extra A* tile cost near live monsters
    private float[,] _dangerMap;
    private const float DangerWeight    = 18f;
    private const int   DangerRadius    = 3;
    private const float MonsterRoomCost = 12f;

    // Chest detour
    private const float ChestHpRatio  = 0.65f;
    private const int   ChestMaxSteps = 18;

    // 4-directional only – no diagonal clipping
    private static readonly int[] Dx = {  1, -1,  0,  0 };
    private static readonly int[] Dy = {  0,  0,  1, -1 };

    private const int ChunkW = 10;
    private const int ChunkH = 8;

    // ─── Room metadata ───────────────────────────────────────────────────────

    private class RoomInfo
    {
        public int         chunkX, chunkY;
        public Vector2Int  center;
        public bool        hasMonster;
        public bool        isExit;
    }

    public MapTraversal(MapGrid grid, Player player, FogOfWar fog,
                        RoomManager roomManager, ExitDoor exitDoor)
    {
        _grid        = grid;
        _player      = player;
        _fog         = fog;
        _roomManager = roomManager;
        _exitDoor    = exitDoor;
    }

    // ─── ITickable ───────────────────────────────────────────────────────────

    public void Tick()
    {
        if (Keyboard.current != null && Keyboard.current[Key.T].wasPressedThisFrame)
        {
            if (IsAutoWalking) Stop(); else Begin();
        }

        // Auto-resume: key was picked up while traversal was paused
        if (!IsAutoWalking && _goal == AutoGoal.Explore && _player.HasKey)
        {
            _goal         = AutoGoal.SeekExit;
            _path         = new Queue<(int, int)>();
            _moveTimer    = 0f;
            IsAutoWalking = true;
            BuildDangerMap();
            Debug.Log("[MapTraversal] Phase 2 – key collected while paused, heading to exit");
        }

        if (!IsAutoWalking) return;
        if (_player.IsDead) { Stop(); return; }

        _moveTimer -= Time.deltaTime;
        if (_moveTimer > 0f) return;

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
        _rooms = DetectAndTagRooms();
        if (_rooms.Count == 0) { Debug.LogWarning("[MapTraversal] No rooms found"); return; }

        _adj         = BuildRoomAdjacency(_rooms);
        _exitRoomIdx = FindExitRoomIndex(_rooms);
        BuildDangerMap();

        _path      = new Queue<(int, int)>();
        _moveTimer = 0f;
        // If key already collected (e.g. restarting after partial run), skip straight to exit
        _goal = _player.HasKey ? AutoGoal.SeekExit : AutoGoal.Explore;
        IsAutoWalking = true;

        Debug.Log($"[MapTraversal] Begin – {_rooms.Count} rooms, " +
                  $"exit room: {_exitRoomIdx}");
    }

    public void Stop()
    {
        IsAutoWalking = false;
        _path?.Clear();
        Debug.Log("[MapTraversal] Stopped");
    }

    /// <summary>
    /// Full state reset. Called on level transition so stale room data from the
    /// previous map cannot trigger auto-resume on a fresh level.
    /// </summary>
    public void Reset()
    {
        IsAutoWalking = false;
        _path?.Clear();
        _goal        = AutoGoal.None;
        _rooms       = null;
        _adj         = null;
        _dangerMap   = null;
        _exitRoomIdx = -1;
    }

    // ─── State Machine ────────────────────────────────────────────────────────

    private bool AdvanceState()
    {
        if (_goal == AutoGoal.Explore && _player.HasKey)
        {
            _goal = AutoGoal.SeekExit;
            _path = new Queue<(int, int)>();
            Debug.Log("[MapTraversal] Phase 2 – key collected mid-walk, heading to exit");
        }

        return _goal switch
        {
            AutoGoal.Explore  => AdvanceExplore(),
            AutoGoal.SeekExit => AdvanceSeekExit(),
            _                 => false
        };
    }

    // ── Phase 1: Frontier exploration (fog-of-war aware) ─────────────────────
    // Walks to the nearest revealed tile that borders unrevealed terrain.
    // A* only uses revealed tiles, so the path is always through known terrain.

    private bool AdvanceExplore()
    {
        // Opportunistic chest grab when HP is low
        var chest = FindChestDetour();
        if (chest.HasValue)
        {
            var s = FindPath(_player.X, _player.Y, chest.Value.x, chest.Value.y);
            if (s.Count > 1) { _path = ToQueue(s, 1); return true; }
        }

        // If key is visible, go collect it immediately (don't wait for full exploration)
        foreach (var key in _roomManager.ActiveKeys)
        {
            if (!key.IsActive) continue;
            if (_fog != null && !_fog.IsRevealed(key.TileX, key.TileY)) continue;
            var keyPath = FindPath(_player.X, _player.Y, key.TileX, key.TileY);
            if (keyPath.Count > 1) { _path = ToQueue(keyPath, 1); return true; }
        }

        // Find nearest frontier tile (revealed tile adjacent to unrevealed floor)
        var frontier = FindNearestFrontier();
        if (frontier.HasValue)
        {
            var s = FindPath(_player.X, _player.Y, frontier.Value.x, frontier.Value.y);
            if (s.Count > 1) { _path = ToQueue(s, 1); return true; }
        }

        Debug.Log("[MapTraversal] Phase 1 complete – no frontiers remain");
        IsAutoWalking = false;
        return false;
    }

    // BFS through revealed floor tiles from the player.
    // Returns the nearest revealed tile that is adjacent to at least one unrevealed floor tile.
    private (int x, int y)? FindNearestFrontier()
    {
        var queue   = new Queue<(int x, int y)>();
        var visited = new HashSet<(int, int)>();
        queue.Enqueue((_player.X, _player.Y));
        visited.Add((_player.X, _player.Y));

        while (queue.Count > 0)
        {
            var (cx, cy) = queue.Dequeue();

            for (int d = 0; d < 4; d++)
            {
                int nx = cx + Dx[d], ny = cy + Dy[d];
                if (!_grid.InBounds(nx, ny)) continue;
                var t = _grid.GetTileType(nx, ny);
                if (t == TileType.Wall || t == TileType.Air) continue;

                if (_fog != null && !_fog.IsRevealed(nx, ny))
                {
                    // (cx, cy) is the frontier: last revealed tile before unknown
                    return (cx, cy);
                }

                if (visited.Add((nx, ny)))
                    queue.Enqueue((nx, ny));
            }
        }
        return null; // no frontier — fully revealed
    }

    // ── Phase 2: Dijkstra room path → exit ───────────────────────────────────

    private bool AdvanceSeekExit()
    {
        if (!_exitDoor.IsPlaced || _exitRoomIdx < 0)
        {
            Debug.LogWarning("[MapTraversal] Exit not reachable");
            _goal = AutoGoal.Done;
            return false;
        }

        int ex = _exitDoor.ExitX, ey = _exitDoor.ExitY;
        if (_player.X == ex && _player.Y == ey) { _goal = AutoGoal.Done; return false; }

        // Exit not yet seen: keep exploring until fog reveals it
        if (_fog != null && !_fog.IsRevealed(ex, ey))
        {
            var frontier = FindNearestFrontier();
            if (frontier.HasValue)
            {
                var s = FindPath(_player.X, _player.Y, frontier.Value.x, frontier.Value.y);
                if (s.Count > 1) { _path = ToQueue(s, 1); return true; }
            }
            // No frontiers left but exit still hidden — map may be disconnected
            Debug.LogWarning("[MapTraversal] Exit never revealed, map may be disconnected");
            _goal = AutoGoal.Done;
            return false;
        }

        // Opportunistic chest grab (exit is known, quick detour if HP low)
        var chest = FindChestDetour();
        if (chest.HasValue)
        {
            var s = FindPath(_player.X, _player.Y, chest.Value.x, chest.Value.y);
            if (s.Count > 1) { _path = ToQueue(s, 1); return true; }
        }

        // Exit is revealed — navigate directly (fog-unconstrained, shortest path)
        var navPath = NavigateToRoom(_exitRoomIdx, fogAware: false);
        if (navPath.Count > 0) { _path = navPath; return true; }

        var finalSeg = FindPath(_player.X, _player.Y, ex, ey, fogAware: false);
        if (finalSeg.Count > 1) { _path = ToQueue(finalSeg, 1); return true; }

        Debug.LogWarning("[MapTraversal] Exit tile unreachable");
        _goal = AutoGoal.Done;
        return false;
    }

    // ─── Two-Level Navigation ─────────────────────────────────────────────────

    // Returns a tile-level queue that routes through the cheapest room path to
    // targetRoomIdx (skipping monster rooms when possible).
    // Returns empty queue if already in target room.
    private Queue<(int, int)> NavigateToRoom(int targetRoomIdx, bool fogAware = true)
    {
        int currentRoom = GetCurrentRoomIndex();
        if (currentRoom == targetRoomIdx) return new Queue<(int, int)>();

        // Room-level Dijkstra: find cheapest sequence of rooms to pass through
        var roomPath = RoomDijkstra(currentRoom, targetRoomIdx);

        // If Dijkstra couldn't reach target (rooms separated by corridor-only chunks),
        // fall back to direct tile A* to the target room's safe waypoint.
        if (roomPath.Count <= 1)
        {
            var wpt    = GetRoomWaypoint(targetRoomIdx);
            var direct = FindPath(_player.X, _player.Y, wpt.x, wpt.y, fogAware);
            var dq = new Queue<(int, int)>();
            for (int i = 1; i < direct.Count; i++) dq.Enqueue(direct[i]);
            return dq;
        }

        // Tile A* through intermediate room waypoints.
        var combined = new List<(int, int)>();
        int px = _player.X, py = _player.Y;

        for (int i = 1; i < roomPath.Count; i++)
        {
            var wpt = GetRoomWaypoint(roomPath[i]);
            var seg = FindPath(px, py, wpt.x, wpt.y, fogAware);
            if (seg.Count > 1)
            {
                for (int j = 1; j < seg.Count; j++) combined.Add(seg[j]);
                px = wpt.x;
                py = wpt.y;
            }
        }

        var q = new Queue<(int, int)>();
        foreach (var step in combined) q.Enqueue(step);
        return q;
    }

    // Dijkstra on room graph. Monster rooms are penalised (cost MonsterRoomCost extra).
    private List<int> RoomDijkstra(int from, int to)
    {
        if (from < 0 || from >= _rooms.Count || to < 0 || to >= _rooms.Count)
            return new List<int>();

        var dist   = new Dictionary<int, float>();
        var prev   = new Dictionary<int, int>();
        var open   = new List<(float d, int r)>();
        var closed = new HashSet<int>();

        dist[from] = 0f;
        open.Add((0f, from));

        while (open.Count > 0)
        {
            // Pop minimum-cost room
            int minIdx = 0;
            for (int i = 1; i < open.Count; i++)
                if (open[i].d < open[minIdx].d) minIdx = i;
            var (d, cur) = open[minIdx];
            open.RemoveAt(minIdx);

            if (!closed.Add(cur)) continue;
            if (cur == to) break;

            foreach (int nb in _adj[cur])
            {
                if (closed.Contains(nb)) continue;
                float stepCost = 1f + (_rooms[nb].hasMonster ? MonsterRoomCost : 0f);
                float nd = d + stepCost;
                if (!dist.TryGetValue(nb, out float od) || nd < od)
                {
                    dist[nb] = nd;
                    prev[nb] = cur;
                    open.Add((nd, nb));
                }
            }
        }

        // Reconstruct room path
        if (!prev.ContainsKey(to) && from != to) return new List<int> { from };

        var path = new List<int>();
        int node = to;
        while (node != from) { path.Add(node); node = prev[node]; }
        path.Add(from);
        path.Reverse();
        return path;
    }

    // Returns a walkable waypoint for the room.
    // For monster rooms: the floor tile farthest from all monsters in that chunk.
    // For safe rooms: the precomputed center.
    private Vector2Int GetRoomWaypoint(int roomIdx)
    {
        var room = _rooms[roomIdx];
        if (!room.hasMonster) return room.center;

        int x0 = room.chunkX * ChunkW, y0 = room.chunkY * ChunkH;
        int x1 = x0 + ChunkW,         y1 = y0 + ChunkH;

        float    bestDist = -1f;
        Vector2Int best   = room.center;

        for (int x = x0; x < x1 && x < _grid.Width; x++)
        for (int y = y0; y < y1 && y < _grid.Height; y++)
        {
            if (_grid.GetTileType(x, y) != TileType.Floor) continue;

            // Minimum Chebyshev distance to any live monster in this room
            float minD = float.MaxValue;
            foreach (var m in _roomManager.LiveMonsters)
            {
                if (!m.IsActive) continue;
                // if (!m.IsAlive) continue;
                if (m.TileX / ChunkW != room.chunkX || m.TileY / ChunkH != room.chunkY) continue;
                float d = Mathf.Max(Mathf.Abs(x - m.TileX), Mathf.Abs(y - m.TileY));
                if (d < minD) minD = d;
            }

            if (minD > bestDist) { bestDist = minD; best = new Vector2Int(x, y); }
        }
        return best;
    }

    // Returns the room index containing the player, or nearest room.
    private int GetCurrentRoomIndex()
    {
        int px = _player.X, py = _player.Y;
        int cx = px / ChunkW, cy = py / ChunkH;
        for (int i = 0; i < _rooms.Count; i++)
            if (_rooms[i].chunkX == cx && _rooms[i].chunkY == cy) return i;
        return FindNearestRoomIndex(_rooms);
    }

    // BFS from player – closest reachable unrevealed floor tile inside the room chunk.
    // Used by AdvanceSeekExit to reveal the exit tile.
    private (int x, int y)? FindNearestUnrevealed(RoomInfo room)
    {
        int x0 = room.chunkX * ChunkW, y0 = room.chunkY * ChunkH;
        int x1 = x0 + ChunkW,         y1 = y0 + ChunkH;

        var queue   = new Queue<(int x, int y)>();
        var visited = new HashSet<(int, int)>();
        queue.Enqueue((_player.X, _player.Y));
        visited.Add((_player.X, _player.Y));

        while (queue.Count > 0)
        {
            var (cx, cy) = queue.Dequeue();
            bool inRoom = cx >= x0 && cx < x1 && cy >= y0 && cy < y1;
            if (inRoom)
            {
                var t = _grid.GetTileType(cx, cy);
                if (t != TileType.Wall && t != TileType.Air && !_fog.IsRevealed(cx, cy))
                    return (cx, cy);
            }
            for (int d = 0; d < 4; d++)
            {
                int nx = cx + Dx[d], ny = cy + Dy[d];
                if (!_grid.InBounds(nx, ny)) continue;
                if (_grid.GetTileType(nx, ny) == TileType.Wall) continue;
                if (!visited.Add((nx, ny))) continue;
                queue.Enqueue((nx, ny));
            }
        }
        return null;
    }

    // ─── Chest Detour ────────────────────────────────────────────────────────

    private (int x, int y)? FindChestDetour()
    {
        if ((float)_player.Health / _player.MaxHealth >= ChestHpRatio) return null;
        int  best = int.MaxValue;
        (int x, int y)? target = null;
        foreach (var c in _roomManager.ActiveChests)
        {
            if (!c.IsActive) continue;
            if (_fog != null && !_fog.IsRevealed(c.TileX, c.TileY)) continue; // only grab seen chests
            var p = FindPath(_player.X, _player.Y, c.TileX, c.TileY);
            if (p.Count < 2 || p.Count - 1 > ChestMaxSteps) continue;
            if (p.Count < best) { best = p.Count; target = (c.TileX, c.TileY); }
        }
        return target;
    }

    // ─── Danger Map ──────────────────────────────────────────────────────────

    private void BuildDangerMap()
    {
        _dangerMap = new float[_grid.Width, _grid.Height];
        foreach (var m in _roomManager.LiveMonsters)
        {
            // if (!m.IsAlive) continue;
            if (!m.IsActive) continue;
            int mx = m.TileX, my = m.TileY;
            for (int dy = -DangerRadius; dy <= DangerRadius; dy++)
            for (int dx = -DangerRadius; dx <= DangerRadius; dx++)
            {
                int x = mx + dx, y = my + dy;
                if (!_grid.InBounds(x, y)) continue;
                int cheby = Mathf.Max(Mathf.Abs(dx), Mathf.Abs(dy));
                _dangerMap[x, y] += DangerWeight * (DangerRadius + 1 - cheby);
            }
        }
    }

    // ─── A* Tile Pathfinding (4-directional) ─────────────────────────────────

    private static float Heuristic(int x, int y, int tx, int ty) =>
        Mathf.Abs(x - tx) + Mathf.Abs(y - ty);

    private List<(int x, int y)> FindPath(int fx, int fy, int tx, int ty, bool fogAware = true)
    {
        if (fx == tx && fy == ty) return new List<(int, int)>();

        var open   = new List<(float f, int x, int y)>();
        var gCost  = new Dictionary<(int, int), float>();
        var parent = new Dictionary<(int, int), (int, int)>();
        var closed = new HashSet<(int, int)>();

        gCost[(fx, fy)]  = 0f;
        parent[(fx, fy)] = (-1, -1);
        open.Add((Heuristic(fx, fy, tx, ty), fx, fy));

        while (open.Count > 0)
        {
            int mi = 0;
            for (int i = 1; i < open.Count; i++)
                if (open[i].f < open[mi].f) mi = i;
            var (_, cx, cy) = open[mi];
            open.RemoveAt(mi);

            if (cx == tx && cy == ty) return ReconstructPath(parent, tx, ty);
            if (!closed.Add((cx, cy))) continue;

            float g = gCost[(cx, cy)];
            for (int d = 0; d < 4; d++)
            {
                int nx = cx + Dx[d], ny = cy + Dy[d];
                if (!_grid.InBounds(nx, ny)) continue;
                if (_grid.GetTileType(nx, ny) == TileType.Wall) continue;
                if (closed.Contains((nx, ny))) continue;

                // Fog constraint: only traverse revealed tiles (except the exact target)
                if (fogAware && _fog != null && !_fog.IsRevealed(nx, ny) && (nx != tx || ny != ty)) continue;

                float danger = _dangerMap != null ? _dangerMap[nx, ny] : 0f;
                float ng     = g + 1f + danger;
                if (!gCost.TryGetValue((nx, ny), out float og) || ng < og)
                {
                    gCost[(nx, ny)]  = ng;
                    parent[(nx, ny)] = (cx, cy);
                    open.Add((ng + Heuristic(nx, ny, tx, ty), nx, ny));
                }
            }
        }
        return new List<(int, int)>();
    }

    private static List<(int x, int y)> ReconstructPath(
        Dictionary<(int, int), (int, int)> parent, int tx, int ty)
    {
        var path = new List<(int, int)>();
        var pos  = (tx, ty);
        while (pos != (-1, -1)) { path.Add(pos); pos = parent[pos]; }
        path.Reverse();
        return path;
    }

    private static Queue<(int, int)> ToQueue(List<(int x, int y)> path, int skip)
    {
        var q = new Queue<(int, int)>();
        for (int i = skip; i < path.Count; i++) q.Enqueue(path[i]);
        return q;
    }

    // ─── Room Detection & Tagging ────────────────────────────────────────────

    private List<RoomInfo> DetectAndTagRooms()
    {
        int cols  = Mathf.Max(1, _grid.Width  / ChunkW);
        int rows  = Mathf.Max(1, _grid.Height / ChunkH);
        var rooms = new List<RoomInfo>();

        for (int cx = 0; cx < cols; cx++)
        for (int cy = 0; cy < rows; cy++)
        {
            var seeds = new HashSet<(int, int)>();
            for (int x = cx * ChunkW; x < (cx+1)*ChunkW && x < _grid.Width; x++)
            for (int y = cy * ChunkH; y < (cy+1)*ChunkH && y < _grid.Height; y++)
            {
                var t = _grid.GetTileType(x, y);
                if (t != TileType.Wall && t != TileType.Air) seeds.Add((x, y));
            }
            var comp = LargestComponent(seeds);
            if (comp.Count == 0) continue;

            int minX = int.MaxValue, maxX = int.MinValue;
            int minY = int.MaxValue, maxY = int.MinValue;
            foreach (var (x, y) in comp)
            {
                if (x < minX) minX = x;  if (x > maxX) maxX = x;
                if (y < minY) minY = y;  if (y > maxY) maxY = y;
            }
            if (maxX - minX < 3 || maxY - minY < 3) continue;

            // Tag: monster present in this chunk?
            bool hasMonster = false;
            foreach (var m in _roomManager.LiveMonsters)
                if (/*m.IsAlive*/ m.IsActive && m.TileX/ChunkW == cx && m.TileY/ChunkH == cy)
                    { hasMonster = true; break; }

            // Tag: is this the exit room?
            bool isExit = _exitDoor.IsPlaced
                          && _exitDoor.ExitX/ChunkW == cx
                          && _exitDoor.ExitY/ChunkH == cy;

            rooms.Add(new RoomInfo
            {
                chunkX     = cx,
                chunkY     = cy,
                center     = FindWalkableCenter(comp),
                hasMonster = hasMonster,
                isExit     = isExit
            });
        }
        return rooms;
    }

    private int FindExitRoomIndex(List<RoomInfo> rooms)
    {
        for (int i = 0; i < rooms.Count; i++)
            if (rooms[i].isExit) return i;
        return -1;
    }

    private static List<(int, int)> LargestComponent(HashSet<(int, int)> tiles)
    {
        var remaining = new HashSet<(int, int)>(tiles);
        var best = new List<(int, int)>();
        int[] dx = { 1,-1,0,0 }, dy = { 0,0,1,-1 };
        while (remaining.Count > 0)
        {
            var en = remaining.GetEnumerator(); en.MoveNext();
            var seed = en.Current;
            var comp = new List<(int, int)>(); var q = new Queue<(int, int)>();
            q.Enqueue(seed); remaining.Remove(seed);
            while (q.Count > 0)
            {
                var (cx, cy) = q.Dequeue(); comp.Add((cx, cy));
                for (int d = 0; d < 4; d++)
                { var n = (cx+dx[d], cy+dy[d]); if (remaining.Remove(n)) q.Enqueue(n); }
            }
            if (comp.Count > best.Count) best = comp;
        }
        return best;
    }

    private static Vector2Int FindWalkableCenter(List<(int x, int y)> tiles)
    {
        int sx = 0, sy = 0;
        foreach (var (x, y) in tiles) { sx += x; sy += y; }
        int cx = sx / tiles.Count, cy = sy / tiles.Count;
        float minD = float.MaxValue;
        var best = new Vector2Int(tiles[0].x, tiles[0].y);
        foreach (var (x, y) in tiles)
        { float d = Mathf.Abs(x-cx)+Mathf.Abs(y-cy); if (d < minD) { minD=d; best=new Vector2Int(x,y); } }
        return best;
    }

    // ─── Room Graph ──────────────────────────────────────────────────────────

    private Dictionary<int, List<int>> BuildRoomAdjacency(List<RoomInfo> rooms)
    {
        var lookup = new Dictionary<(int, int), int>();
        for (int i = 0; i < rooms.Count; i++)
            lookup[(rooms[i].chunkX, rooms[i].chunkY)] = i;

        var adj = new Dictionary<int, List<int>>();
        for (int i = 0; i < rooms.Count; i++) adj[i] = new List<int>();

        int[] dx = { 1,0,-1,0 }, dy = { 0,1,0,-1 };
        for (int i = 0; i < rooms.Count; i++)
        for (int d = 0; d < 4; d++)
        {
            var key = (rooms[i].chunkX+dx[d], rooms[i].chunkY+dy[d]);
            if (lookup.TryGetValue(key, out int j) && !adj[i].Contains(j))
                { adj[i].Add(j); adj[j].Add(i); }
        }
        return adj;
    }

    private int FindNearestRoomIndex(List<RoomInfo> rooms)
    {
        float minD = float.MaxValue; int best = 0;
        var p = new Vector2Int(_player.X, _player.Y);
        for (int i = 0; i < rooms.Count; i++)
        {
            float d = Vector2Int.Distance(rooms[i].center, p);
            if (d < minD) { minD = d; best = i; }
        }
        return best;
    }

}