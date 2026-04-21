using System.Collections.Generic;
using Data;
using Model;
using UnityEngine;
using UnityEngine.InputSystem;
using VContainer.Unity;

/// <summary>
/// Goal-oriented auto-pilot. Press G to start/stop.
///
/// Priority-based greedy planner:
///   1. No key?       → A* to nearest active key
///   2. HP &lt; 50%? → A* to nearest active chest
///   3. Has key?      → A* to the exit door
///
/// A* uses a monster danger map (radius-2 penalty) + path-tile penalty
/// to avoid taking damage whenever an alternative exists.
/// Re-plans whenever the current path is exhausted.
/// </summary>
public class GoalAI : ITickable
{
    public bool   IsRunning     { get; private set; }
    public bool   FairMode      { get; private set; }   // true = only use fog-revealed info
    public bool   EfficientMode { get; private set; }   // true = goal-directed, minimal detours
    public string CurrentTarget { get; private set; } = "None";

    private readonly MapGrid      _grid;
    private readonly Player       _player;
    private readonly RoomManager  _roomManager;
    private readonly ExitDoor     _exitDoor;
    private readonly MapTraversal _traversal;
    private readonly FogOfWar     _fog;

    private Queue<(int x, int y)> _path;
    private float                 _moveTimer;
    private float[,]              _dangerMap;
    private HashSet<(int, int)>   _coinTiles;   // rebuilt at plan time
    private (int x, int y)?       _dfsTarget;    // persistent DFS commit target
    private Vector2               _lastDfsDir;   // direction of last DFS target (momentum)
    private bool                  _efficientSeeking;              // false=Explore, true=SeekExit
    private HashSet<(int, int)>   _visitedChunks = new HashSet<(int, int)>(); // explored room chunks

    public bool EfficientSeeking => _efficientSeeking;

    // Risk/reward tuning constants
    private const float PathPenalty         = 10f;
    private const float CoinAttraction      = 10f;
    private const int   CoinPickupRange     = 8;
    private const int   ChestStepsProactive = 18;
    private const int   ChestStepsCritical  = 40;
    private const int   ChestStepsSameRoom  = 8;  // max steps for proactive heal while exploring rooms (avoids corridor crossing)
    private const int   ChunkW              = 10;  // room chunk width  (matches MapTraversal)
    private const int   ChunkH              = 8;   // room chunk height (matches MapTraversal)

    private const float MoveInterval = 0.12f;

    private static readonly int[] Dx = { 1, -1, 0, 0 };
    private static readonly int[] Dy = { 0,  0, 1, -1 };

    public GoalAI(MapGrid grid, Player player, RoomManager roomManager,
                  ExitDoor exitDoor, MapTraversal traversal, FogOfWar fog)
    {
        _grid        = grid;
        _player      = player;
        _roomManager = roomManager;
        _exitDoor    = exitDoor;
        _traversal   = traversal;
        _fog         = fog;
    }

    // ─── ITickable ───────────────────────────────────────────────────────────

    public void Tick()
    {
        // Auto-stop if MapTraversal takes over
        if (IsRunning && _traversal.IsAutoWalking) Stop();

        if (Keyboard.current != null)
        {
            if (Keyboard.current[Key.G].wasPressedThisFrame)
            {
                if (IsRunning && !FairMode && !EfficientMode) Stop();
                else Start(fair: false, efficient: false);
            }
            if (Keyboard.current[Key.H].wasPressedThisFrame)
            {
                if (IsRunning && FairMode) Stop();
                else Start(fair: true, efficient: false);
            }
            if (Keyboard.current[Key.J].wasPressedThisFrame)
            {
                if (IsRunning && EfficientMode) Stop();
                else Start(fair: true, efficient: true);
            }
        }

        if (!IsRunning) return;
        if (_player.IsDead) { Stop(); return; }

        _moveTimer -= Time.deltaTime;
        if (_moveTimer > 0f) return;

        if (_path == null || _path.Count == 0)
        {
            bool planned = EfficientMode ? PlanNextTargetEfficient() : PlanNextTarget();
            if (!planned)
            {
                Stop();
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

    public void Start(bool fair = false, bool efficient = false)
    {
        _traversal.Stop();
        FairMode      = fair;
        EfficientMode = efficient;
        _path             = new Queue<(int, int)>();
        _moveTimer        = 0f;
        _lastDfsDir       = Vector2.zero;
        _efficientSeeking = false;
        _visitedChunks.Clear();
        IsRunning         = true;
        string mode   = efficient ? "Efficient" : (fair ? "Fair" : "Omniscient");
        Debug.Log($"[GoalAI] Started ({mode})");
    }

    public void Stop()
    {
        IsRunning         = false;
        _path?.Clear();
        CurrentTarget     = "None";
        _dfsTarget        = null;
        _lastDfsDir       = Vector2.zero;
        _efficientSeeking = false;
        _visitedChunks.Clear();
    }

    // ─── Priority Planner ────────────────────────────────────────────────────

    private bool PlanNextTarget()
    {
        BuildDangerMap();

        // 1. Get the key first (must be in revealed area if FairMode)
        if (!_player.HasKey)
        {
            var key = FindNearestActive(_roomManager.ActiveKeys);
            if (key.HasValue && TryPathTo(key.Value, "Key")) return true;
        }

        // 2. Any HP loss → go grab a chest (chests only heal 5, but every bit counts)
        if (_player.Health < _player.MaxHealth)
        {
            var chest = FindNearestActive(_roomManager.ActiveChests);
            if (chest.HasValue && TryPathTo(chest.Value, "Chest (Heal)")) return true;
        }

        // 3. Has key → exit.
        //    Defer the exit when above survival threshold AND still exploring.
        //    Below HP 30% we're in survival mode — go straight to exit.
        if (_player.HasKey && _exitDoor.IsPlaced &&
            (!FairMode || _fog.IsRevealed(_exitDoor.ExitX, _exitDoor.ExitY)))
        {
            bool aboveSurvival = _player.Health * 10 >= _player.MaxHealth * 3;  // HP ≥ 30%
            bool deferExit     = FairMode && aboveSurvival && FindFrontierTile().HasValue;

            if (!deferExit &&
                TryPathTo((_exitDoor.ExitX, _exitDoor.ExitY), "Exit"))
                return true;
        }

        // 4. Opportunistic coin grab — active whenever above survival threshold (≥ 30% HP).
        //    Below 30%, coins are ignored entirely (matches TileCost behaviour).
        if (_player.Health * 10 >= _player.MaxHealth * 3)
        {
            var nearCoin = FindNearbyRevealedCoin();
            if (nearCoin.HasValue && TryPathTo(nearCoin.Value, "Coin")) return true;
        }

        // 5. Fair mode fallback: explore — walk toward the nearest reachable frontier
        if (FairMode)
        {
            var frontier = FindFrontierTile();
            if (frontier.HasValue && TryPathTo(frontier.Value, "Explore")) return true;
        }

        return false;
    }

    // ─── Efficient Planner (press J) ─────────────────────────────────────────
    // Two-phase design derived from MapTraversal (main branch):
    //
    //   Phase 1 – Explore: fog-aware DFS frontier exploration until key is held.
    //     • Key visible → go collect it immediately → triggers Phase 2 next tick
    //     • Bounded chest detour: only detour if chest is within ChestStepsProactive A* steps
    //     • Nearby coin (≤3 tiles) opportunistically collected when HP > 50%
    //     • Direction-aware DFS frontier: go deep in current direction, nearest backtrack
    //
    //   Phase 2 – SeekExit: navigate to exit once key is held.
    //     • Exit not yet revealed → keep exploring (Phase 1 frontier logic)
    //     • Exit revealed → bounded chest detour only, then direct A* to exit
    //
    // Key improvements over previous Efficient and Fair:
    //   • Chest detours use actual path length (A*), not Manhattan — prevents far detours
    //   • Clear phase separation prevents premature exit-seeking during exploration
    //   • _lastDfsDir momentum reduces backtracking (direction-aware frontier selection)

    private bool PlanNextTargetEfficient()
    {
        BuildDangerMap();
        float hpRatio = _player.Health / (float)_player.MaxHealth;

        // Phase transition: key collected → switch to SeekExit
        if (_player.HasKey && !_efficientSeeking)
        {
            _efficientSeeking = true;
            _dfsTarget        = null;
        }

        // ── Corridor guard ────────────────────────────────────────────────────
        // If the player is standing on a Path (corridor) tile, retreat is forbidden.
        // Turning back mid-corridor doubles the HP cost while revealing nothing new.
        // Only a life-threatening emergency (HP < 30 %) may override this rule.
        // In all other cases: commit to reaching the next room floor.
        if (_grid.GetTileType(_player.X, _player.Y) == TileType.Path)
        {
            if (hpRatio < 0.3f)
            {
                var chest = FindNearestReachableChest(ChestStepsCritical);
                if (chest.HasValue && TryPathTo(chest.Value, "Chest! [E]"))
                { _dfsTarget = null; return true; }
            }
            // Preserve the committed room target; if none, pick the nearest
            // unvisited room floor so we always move *forward* through the corridor.
            if (!_dfsTarget.HasValue)
                _dfsTarget = FindNearestUnvisitedRoomFloor() ?? FindDeepestFrontier();
            if (_dfsTarget.HasValue && TryPathTo(_dfsTarget.Value, "Cross")) return true;
            _dfsTarget = null;
            return false;
        }

        // ── Room planning (player on Floor tile) ─────────────────────────────
        if (hpRatio < 0.3f)
        {
            var chest = FindNearestReachableChest(ChestStepsCritical);
            if (chest.HasValue && TryPathTo(chest.Value, "Chest! [E]"))
            { _dfsTarget = null; return true; }
        }

        return _efficientSeeking ? PlanSeekExit(hpRatio) : PlanExplore(hpRatio);
    }

    // Phase 1: fog-aware room exploration until key is in hand
    private bool PlanExplore(float hpRatio)
    {
        var key = FindNearestActive(_roomManager.ActiveKeys);
        if (key.HasValue && TryPathTo(key.Value, "Key [E]"))
        { _dfsTarget = null; return true; }

        // Proactive heal: use a tight step budget so the AI never crosses a
        // corridor just to reach a chest in another room.
        if (hpRatio < 0.7f)
        {
            var chest = FindNearestReachableChest(ChestStepsSameRoom);
            if (chest.HasValue && TryPathTo(chest.Value, "Chest [E]")) return true;
        }

        if (hpRatio > 0.5f)
        {
            var coin = FindNearbyCoin(maxDist: 3);
            if (coin.HasValue && TryPathTo(coin.Value, "Coin [E]")) return true;
        }

        return ExploreFrontier();
    }

    // Phase 2: navigate to exit; continue room exploration if exit still hidden
    private bool PlanSeekExit(float hpRatio)
    {
        if (!_exitDoor.IsPlaced) return false;

        if (!_fog.IsRevealed(_exitDoor.ExitX, _exitDoor.ExitY))
        {
            // Still exploring — same tight budget as PlanExplore
            if (hpRatio < 0.7f)
            {
                var chest = FindNearestReachableChest(ChestStepsSameRoom);
                if (chest.HasValue && TryPathTo(chest.Value, "Chest [E]")) return true;
            }
            return ExploreFrontier();
        }

        // Exit revealed: allow a larger detour to top up HP before the final run
        if (hpRatio < 0.65f)
        {
            var chest = FindNearestReachableChest(ChestStepsProactive);
            if (chest.HasValue && TryPathTo(chest.Value, "Chest [E]")) return true;
        }

        if (TryPathTo((_exitDoor.ExitX, _exitDoor.ExitY), "Exit [E]"))
        { _dfsTarget = null; return true; }

        return false;
    }

    // ─── Room-aware exploration (shared by both phases) ──────────────────────
    // Logic:  1. Finish current room  (floor-only BFS → no corridor crossings)
    //         2. Move to nearest unvisited revealed room  (commit via _dfsTarget)
    //         3. Fallback: direction-aware tile DFS  (corridor-only maps / edge cases)
    //
    // "Reduce lingering on Path tiles" is achieved by never targeting a corridor
    // tile during room exploration — the AI only leaves a room when it is fully
    // explored, then crosses the corridor in a single committed A* path.

    private bool ExploreFrontier()
    {
        if (_dfsTarget.HasValue)
        {
            bool arrived = _player.X == _dfsTarget.Value.x && _player.Y == _dfsTarget.Value.y;
            if (arrived)        _dfsTarget = null;
            else if (TryPathTo(_dfsTarget.Value, "Room →")) return true;
            else                _dfsTarget = null;
        }

        _dfsTarget = FindRoomAwareFrontier();
        if (_dfsTarget.HasValue && TryPathTo(_dfsTarget.Value, "Room")) return true;

        _dfsTarget = null;
        return false;
    }

    /// <summary>
    /// Three-tier target selection:
    ///   Tier 1 – Room frontier: BFS through Floor tiles only; returns nearest
    ///            revealed floor tile adjacent to unrevealed terrain.  Never
    ///            crosses corridors, so the AI stays in the current room.
    ///   Tier 2 – Next room: find the nearest unvisited chunk that has revealed
    ///            floor tiles.  The player will cross a corridor (Path tiles)
    ///            exactly once to reach it, then re-enter room exploration.
    ///   Tier 3 – Fallback tile DFS: used on corridor-only maps or when all
    ///            visible room chunks are visited but unrevealed tiles remain.
    /// </summary>
    private (int x, int y)? FindRoomAwareFrontier()
    {
        // Track visits only while standing on a Floor tile (inside a room)
        if (_grid.GetTileType(_player.X, _player.Y) == TileType.Floor)
            _visitedChunks.Add((_player.X / ChunkW, _player.Y / ChunkH));

        var roomFrontier = FindFrontierInCurrentRoom();
        if (roomFrontier.HasValue) return roomFrontier;

        var nextRoom = FindNearestUnvisitedRoomFloor();
        if (nextRoom.HasValue) return nextRoom;

        return FindDeepestFrontier();
    }

    /// <summary>
    /// BFS through connected Floor tiles from the player's position.
    /// Returns the nearest floor tile that borders unrevealed terrain.
    /// Does NOT expand through Path (corridor) tiles, so exploration
    /// stays inside the current room until it is fully revealed.
    /// </summary>
    private (int x, int y)? FindFrontierInCurrentRoom()
    {
        var queue   = new Queue<(int, int)>();
        var visited = new HashSet<(int, int)>();
        queue.Enqueue((_player.X, _player.Y));
        visited.Add((_player.X, _player.Y));

        while (queue.Count > 0)
        {
            var (cx, cy) = queue.Dequeue();
            bool isPlayer = cx == _player.X && cy == _player.Y;

            // Only consider floor tiles as potential frontier targets
            if (!isPlayer && _grid.GetTileType(cx, cy) == TileType.Floor)
            {
                for (int d = 0; d < 4; d++)
                {
                    int nx = cx + Dx[d], ny = cy + Dy[d];
                    if (_grid.InBounds(nx, ny) && !_fog.IsRevealed(nx, ny))
                        return (cx, cy);
                }
            }

            // Expand only through Floor tiles — Path tiles are corridor boundaries
            for (int d = 0; d < 4; d++)
            {
                int nx = cx + Dx[d], ny = cy + Dy[d];
                if (!_grid.InBounds(nx, ny)) continue;
                if (_grid.GetTileType(nx, ny) != TileType.Floor) continue;
                if (!_fog.IsRevealed(nx, ny)) continue;
                if (!visited.Add((nx, ny))) continue;
                queue.Enqueue((nx, ny));
            }
        }
        return null;
    }

    /// <summary>
    /// Scans all map chunks for an unvisited chunk that has revealed floor tiles.
    /// Returns the floor tile closest to that chunk's centroid (a safe A* target).
    /// The player will navigate through whatever corridor connects the rooms.
    /// </summary>
    private (int x, int y)? FindNearestUnvisitedRoomFloor()
    {
        int colsMax = _grid.Width  / ChunkW + 1;
        int rowsMax = _grid.Height / ChunkH + 1;

        float bestDist = float.MaxValue;
        (int x, int y)? best = null;

        for (int cx = 0; cx < colsMax; cx++)
        for (int cy = 0; cy < rowsMax; cy++)
        {
            if (_visitedChunks.Contains((cx, cy))) continue;
            var floor = FindRevealedFloorInChunk(cx, cy);
            if (!floor.HasValue) continue;
            float d = Mathf.Abs(floor.Value.x - _player.X) +
                      Mathf.Abs(floor.Value.y - _player.Y);
            if (d < bestDist) { bestDist = d; best = floor; }
        }
        return best;
    }

    /// <summary>
    /// Returns the revealed floor tile in a chunk that is closest to the chunk's
    /// centroid — a stable, reachable waypoint for room-to-room navigation.
    /// Returns null when no floor tiles are yet revealed in this chunk.
    /// </summary>
    private (int x, int y)? FindRevealedFloorInChunk(int cx, int cy)
    {
        int x0 = cx * ChunkW,                          y0 = cy * ChunkH;
        int x1 = Mathf.Min(x0 + ChunkW, _grid.Width),  y1 = Mathf.Min(y0 + ChunkH, _grid.Height);

        int sumX = 0, sumY = 0, count = 0;
        for (int x = x0; x < x1; x++)
        for (int y = y0; y < y1; y++)
        {
            if (_grid.GetTileType(x, y) == TileType.Floor && _fog.IsRevealed(x, y))
            { sumX += x; sumY += y; count++; }
        }
        if (count == 0) return null;

        int centX = sumX / count, centY = sumY / count;
        float minD = float.MaxValue;
        (int x, int y)? best = null;
        for (int x = x0; x < x1; x++)
        for (int y = y0; y < y1; y++)
        {
            if (_grid.GetTileType(x, y) != TileType.Floor || !_fog.IsRevealed(x, y)) continue;
            float d = Mathf.Abs(x - centX) + Mathf.Abs(y - centY);
            if (d < minD) { minD = d; best = (x, y); }
        }
        return best;
    }

    /// <summary>
    /// Direction-aware frontier selection combining DFS depth with minimal backtracking.
    ///
    /// Two-phase strategy:
    ///   Phase 1 — "Keep going": if any frontier lies in the same general direction as
    ///             _lastDfsDir (positive dot-product), pick the DEEPEST one among them.
    ///             This commits the AI to one corridor/branch before turning around.
    ///   Phase 2 — "Backtrack minimally": when Phase 1 finds nothing (direction exhausted),
    ///             pick the NEAREST frontier overall (minimum hops), not the farthest.
    ///             Nearest minimises the backtrack walk, unlike the old max-hops strategy
    ///             that would jump to the opposite end of the explored area.
    ///
    /// After picking a target, _lastDfsDir is updated toward it so the next call
    /// continues in the same corridor. Reset to zero when exploration restarts.
    /// </summary>
    private (int x, int y)? FindDeepestFrontier()
    {
        var queue   = new Queue<(int x, int y, int hops)>();
        var visited = new HashSet<(int, int)>();
        queue.Enqueue((_player.X, _player.Y, 0));
        visited.Add((_player.X, _player.Y));

        var frontiers = new List<(int x, int y, int hops)>();

        while (queue.Count > 0)
        {
            var (cx, cy, hops) = queue.Dequeue();
            bool isPlayer = cx == _player.X && cy == _player.Y;

            if (!isPlayer)
            {
                for (int d = 0; d < 4; d++)
                {
                    int nx = cx + Dx[d], ny = cy + Dy[d];
                    if (_grid.InBounds(nx, ny) && !_fog.IsRevealed(nx, ny))
                    { frontiers.Add((cx, cy, hops)); break; }
                }
            }

            for (int d = 0; d < 4; d++)
            {
                int nx = cx + Dx[d], ny = cy + Dy[d];
                if (!_grid.InBounds(nx, ny)) continue;
                if (_grid.GetTileType(nx, ny) == TileType.Wall) continue;
                if (!_fog.IsRevealed(nx, ny)) continue;
                if (!visited.Add((nx, ny))) continue;
                queue.Enqueue((nx, ny, hops + 1));
            }
        }

        if (frontiers.Count == 0) return null;

        bool hasDir = _lastDfsDir.sqrMagnitude > 0.01f;

        // Phase 1: deepest aligned frontier (continue in current direction)
        (int x, int y)? bestAligned  = null;
        int             maxAlignHops = -1;

        // Phase 2: nearest frontier (minimal backtrack when direction is exhausted)
        (int x, int y)? bestNearest  = null;
        int             minHops      = int.MaxValue;

        foreach (var (fx, fy, hops) in frontiers)
        {
            if (hasDir)
            {
                float dx  = fx - _player.X, dy = fy - _player.Y;
                float len = Mathf.Sqrt(dx * dx + dy * dy);
                float alignment = len > 0.5f
                    ? (dx * _lastDfsDir.x + dy * _lastDfsDir.y) / len
                    : 0f;

                if (alignment > 0f && hops > maxAlignHops)
                { maxAlignHops = hops; bestAligned = (fx, fy); }
            }

            if (hops < minHops)
            { minHops = hops; bestNearest = (fx, fy); }
        }

        var result = bestAligned ?? bestNearest;

        if (result.HasValue)
        {
            // Update direction ONLY when actively committing to a direction:
            //   • bestAligned chosen → we found a frontier aligned with current direction → update
            //   • !hasDir → first pick ever → bootstrap direction from bestNearest
            // When bestNearest is the fallback (forced backtrack), do NOT change _lastDfsDir.
            // Updating on forced backtrack causes direction to flip repeatedly (oscillation).
            bool shouldUpdateDir = bestAligned.HasValue || !hasDir;
            if (shouldUpdateDir)
            {
                float dx  = result.Value.x - _player.X;
                float dy  = result.Value.y - _player.Y;
                float len = Mathf.Sqrt(dx * dx + dy * dy);
                if (len > 0.5f)
                    _lastDfsDir = new Vector2(dx / len, dy / len);
            }
        }

        return result;
    }

    /// <summary>
    /// Nearest revealed chest reachable within maxSteps A* steps.
    /// Uses actual path length (like MapTraversal.FindChestDetour) rather than
    /// Manhattan distance — prevents committing to a chest behind a long detour.
    /// </summary>
    private (int, int)? FindNearestReachableChest(int maxSteps)
    {
        int bestLen = int.MaxValue;
        (int, int)? best = null;
        foreach (var c in _roomManager.ActiveChests)
        {
            if (c == null || !c.IsActive) continue;
            if (!_fog.IsRevealed(c.TileX, c.TileY)) continue;
            var path = AStar(_player.X, _player.Y, c.TileX, c.TileY);
            if (path.Count < 2 || path.Count - 1 > maxSteps) continue;
            if (path.Count < bestLen) { bestLen = path.Count; best = (c.TileX, c.TileY); }
        }
        return best;
    }

    /// <summary>Nearest revealed coin within maxDist Manhattan tiles.</summary>
    private (int, int)? FindNearbyCoin(int maxDist)
    {
        int bestDist = int.MaxValue;
        (int, int)? best = null;
        foreach (var coin in _roomManager.ActiveCoins)
        {
            if (coin == null || !coin.IsActive) continue;
            if (!_fog.IsRevealed(coin.TileX, coin.TileY)) continue;
            int d = Mathf.Abs(coin.TileX - _player.X) + Mathf.Abs(coin.TileY - _player.Y);
            if (d > maxDist || d >= bestDist) continue;
            bestDist = d;
            best = (coin.TileX, coin.TileY);
        }
        return best;
    }

    /// <summary>
    /// Find the nearest coin that is currently revealed by fog (in Omni mode
    /// all coins are "revealed") and within CoinPickupRange manhattan tiles.
    /// </summary>
    private (int, int)? FindNearbyRevealedCoin()
    {
        int bestDist = int.MaxValue;
        (int, int)? best = null;
        foreach (var coin in _roomManager.ActiveCoins)
        {
            if (coin == null || !coin.IsActive) continue;
            if (FairMode && _fog != null && !_fog.IsRevealed(coin.TileX, coin.TileY)) continue;
            int d = Mathf.Abs(coin.TileX - _player.X) + Mathf.Abs(coin.TileY - _player.Y);
            if (d > CoinPickupRange) continue;
            if (d < bestDist) { bestDist = d; best = (coin.TileX, coin.TileY); }
        }
        return best;
    }

    /// <summary>
    /// Fair-mode exploration: BFS through revealed walkable tiles from the
    /// player, returning the FIRST revealed tile that borders unrevealed
    /// terrain. Guaranteed reachable (unlike a Manhattan-nearest frontier
    /// which may be blocked by walls).
    /// </summary>
    private (int, int)? FindFrontierTile()
    {
        var queue   = new Queue<(int, int)>();
        var visited = new HashSet<(int, int)>();
        queue.Enqueue((_player.X, _player.Y));
        visited.Add((_player.X, _player.Y));

        while (queue.Count > 0)
        {
            var (cx, cy) = queue.Dequeue();

            // Is this a frontier? Skip player's own tile (TryPathTo distance-0 fails)
            bool atPlayer = cx == _player.X && cy == _player.Y;
            if (!atPlayer)
            {
                for (int d = 0; d < 4; d++)
                {
                    int nx = cx + Dx[d], ny = cy + Dy[d];
                    if (!_grid.InBounds(nx, ny)) continue;
                    if (!_fog.IsRevealed(nx, ny))
                        return (cx, cy);
                }
            }

            // Expand to revealed walkable neighbours
            for (int d = 0; d < 4; d++)
            {
                int nx = cx + Dx[d], ny = cy + Dy[d];
                if (!_grid.InBounds(nx, ny)) continue;
                if (_grid.GetTileType(nx, ny) == TileType.Wall) continue;
                if (!_fog.IsRevealed(nx, ny)) continue;
                if (!visited.Add((nx, ny))) continue;
                queue.Enqueue((nx, ny));
            }
        }

        return null;
    }

    private bool TryPathTo((int x, int y) target, string label)
    {
        var path = AStar(_player.X, _player.Y, target.x, target.y);
        if (path.Count <= 1) return false;

        _path = new Queue<(int, int)>();
        for (int i = 1; i < path.Count; i++) _path.Enqueue(path[i]);
        CurrentTarget = label;
        return true;
    }

    /// <summary>Generic nearest-active finder for both Chests and KeyItems.</summary>
    private (int x, int y)? FindNearestActive<T>(System.Collections.Generic.IReadOnlyList<T> entities)
        where T : MonoBehaviour
    {
        float minD = float.MaxValue;
        (int x, int y)? best = null;
        foreach (var e in entities)
        {
            // Each entity type exposes TileX, TileY, IsActive via duck-typed reflection?
            // Cleaner: use concrete types
            int ex, ey; bool active;
            switch (e)
            {
                case Chest c:   ex = c.TileX; ey = c.TileY; active = c.IsActive; break;
                case KeyItem k: ex = k.TileX; ey = k.TileY; active = k.IsActive; break;
                default: continue;
            }
            if (!active) continue;
            if (FairMode && !_fog.IsRevealed(ex, ey)) continue;
            int d = Mathf.Abs(ex - _player.X) + Mathf.Abs(ey - _player.Y);
            if (d < minD) { minD = d; best = (ex, ey); }
        }
        return best;
    }

    // ─── A* Pathfinding ──────────────────────────────────────────────────────

    private List<(int, int)> AStar(int sx, int sy, int tx, int ty)
    {
        if (sx == tx && sy == ty) return new List<(int, int)> { (sx, sy) };

        var gScore = new Dictionary<(int, int), float>();
        var parent = new Dictionary<(int, int), (int, int)>();
        var open   = new List<(float f, int x, int y)>();

        gScore[(sx, sy)] = 0;
        open.Add((Heuristic(sx, sy, tx, ty), sx, sy));

        while (open.Count > 0)
        {
            // Pop minimum f (linear scan — adequate for 64x64 maps)
            int minIdx = 0;
            for (int i = 1; i < open.Count; i++)
                if (open[i].f < open[minIdx].f) minIdx = i;
            var cur = open[minIdx];
            open.RemoveAt(minIdx);

            if (cur.x == tx && cur.y == ty)
                return Reconstruct(parent, sx, sy, tx, ty);

            for (int d = 0; d < 4; d++)
            {
                int nx = cur.x + Dx[d], ny = cur.y + Dy[d];
                if (!_grid.InBounds(nx, ny)) continue;
                if (_grid.GetTileType(nx, ny) == TileType.Wall) continue;
                // Fair mode: only walk on tiles we've actually seen
                if (FairMode && !_fog.IsRevealed(nx, ny) &&
                    !(nx == tx && ny == ty))  // target itself may be the unrevealed frontier
                    continue;

                float tentativeG = gScore[(cur.x, cur.y)] + TileCost(nx, ny);

                if (!gScore.TryGetValue((nx, ny), out float g) || tentativeG < g)
                {
                    gScore[(nx, ny)] = tentativeG;
                    parent[(nx, ny)] = (cur.x, cur.y);
                    open.Add((tentativeG + Heuristic(nx, ny, tx, ty), nx, ny));
                }
            }
        }

        return new List<(int, int)>(); // unreachable
    }

    private static float Heuristic(int x, int y, int tx, int ty)
        => Mathf.Abs(x - tx) + Mathf.Abs(y - ty);

    /// <summary>
    /// HP-aware cost with coin attraction.
    ///   fear       = 2 when HP = 0, 1 when HP = full — scales danger/path penalties
    ///   coinBonus  = CoinAttraction × hpRatio — vanishes as HP drops
    /// The two curves together mean AI will brave Path tiles for coins only
    /// when it has HP to spare; when low on HP it skips them entirely.
    /// </summary>
    private float TileCost(int x, int y)
    {
        float hpRatio = _player.Health / (float)_player.MaxHealth;

        // fear scales danger / path penalties with HP loss
        float fear = EfficientMode && hpRatio < 0.4f
            ? 5f - hpRatio * 4f   // critical → near-impassable Path tiles
            : 2f - hpRatio;       // normal: 1.0 (full HP) → 2.0 (0 HP)

        // Coin attraction (Fair/Omni only; Efficient uses FindNearbyCoin)
        float coinBonus = (!EfficientMode && hpRatio >= 0.3f)
            ? CoinAttraction * (hpRatio - 0.3f) / 0.7f
            : 0f;

        float cost = 1f + _dangerMap[x, y] * fear;

        if (_grid.GetTileType(x, y) == TileType.Path)
        {
            // Efficient: extra surcharge so A* commits to the shortest corridor
            // crossing and never wanders along corridor tiles unnecessarily.
            float corridorMult = EfficientMode ? 2f : 1f;
            cost += PathPenalty * fear * corridorMult;
        }

        if (_coinTiles != null && _coinTiles.Contains((x, y)))
            cost -= coinBonus;

        return Mathf.Max(cost, 0.1f);
    }

    private void BuildDangerMap()
    {
        _dangerMap = new float[_grid.Width, _grid.Height];
        const int radius = 2;

        foreach (var m in _roomManager.LiveMonsters)
        {
            // if (!m.IsAlive) continue;
            if (!m.IsActive) continue;
            for (int dx = -radius; dx <= radius; dx++)
            for (int dy = -radius; dy <= radius; dy++)
            {
                int x = m.TileX + dx, y = m.TileY + dy;
                if (!_grid.InBounds(x, y)) continue;
                float dist = Mathf.Sqrt(dx * dx + dy * dy);
                if (dist > radius) continue;
                float penalty = (radius - dist + 1) * 12f;
                if (penalty > _dangerMap[x, y]) _dangerMap[x, y] = penalty;
            }
        }

        // Rebuild coin lookup (entities can be collected/removed between plans)
        _coinTiles = new HashSet<(int, int)>();
        foreach (var coin in _roomManager.ActiveCoins)
        {
            if (coin != null && coin.IsActive)
                _coinTiles.Add((coin.TileX, coin.TileY));
        }
    }

    private static List<(int, int)> Reconstruct(
        Dictionary<(int, int), (int, int)> parent,
        int sx, int sy, int tx, int ty)
    {
        var path = new List<(int, int)>();
        var cur  = (tx, ty);
        while (cur != (sx, sy))
        {
            path.Add(cur);
            if (!parent.TryGetValue(cur, out cur)) return new List<(int, int)>();
        }
        path.Add((sx, sy));
        path.Reverse();
        return path;
    }
}
