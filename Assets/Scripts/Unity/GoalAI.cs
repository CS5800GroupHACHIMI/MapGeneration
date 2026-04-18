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

    // Risk/reward tuning constants
    private const float PathPenalty       = 10f; // base cost for stepping on a Path (damaging) tile
    private const float CoinAttraction    = 10f; // negative cost bonus for tiles with a coin
    private const int   CoinPickupRange   = 8;   // target revealed coins within this tile distance

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
                if (IsRunning && !FairMode) Stop();
                else                        Start(fair: false);
            }
            if (Keyboard.current[Key.H].wasPressedThisFrame)
            {
                if (IsRunning && FairMode) Stop();
                else                       Start(fair: true);
            }
        }

        if (!IsRunning) return;
        if (_player.IsDead) { Stop(); return; }

        _moveTimer -= Time.deltaTime;
        if (_moveTimer > 0f) return;

        if (_path == null || _path.Count == 0)
        {
            if (!PlanNextTarget())
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

    public void Start(bool fair = false)
    {
        _traversal.Stop();
        FairMode   = fair;
        _path      = new Queue<(int, int)>();
        _moveTimer = 0f;
        IsRunning  = true;
        Debug.Log($"[GoalAI] Started ({(fair ? "Fair" : "Omniscient")})");
    }

    public void Stop()
    {
        IsRunning     = false;
        _path?.Clear();
        CurrentTarget = "None";
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

            // Is this a frontier? (adjacent to at least one unrevealed tile)
            for (int d = 0; d < 4; d++)
            {
                int nx = cx + Dx[d], ny = cy + Dy[d];
                if (!_grid.InBounds(nx, ny)) continue;
                if (!_fog.IsRevealed(nx, ny))
                    return (cx, cy);
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
        float fear    = 2f - hpRatio;   // low HP → higher Path/danger penalties

        // Coin attraction:
        //   HP < 30% → 0 (survival instinct, ignore coins entirely)
        //   HP ≥ 30% → scales linearly from 0 (at 30%) up to full (at 100%)
        float coinBonus = hpRatio < 0.3f
            ? 0f
            : CoinAttraction * (hpRatio - 0.3f) / 0.7f;

        float cost = 1f + _dangerMap[x, y] * fear;
        if (_grid.GetTileType(x, y) == TileType.Path)
            cost += PathPenalty * fear;

        if (_coinTiles != null && _coinTiles.Contains((x, y)))
            cost -= coinBonus;

        return Mathf.Max(cost, 0.1f);   // A* requires non-negative weights
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
