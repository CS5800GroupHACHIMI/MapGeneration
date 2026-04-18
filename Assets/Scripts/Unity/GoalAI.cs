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

        // 2. Low HP → nearest chest
        if (_player.Health * 2 < _player.MaxHealth)
        {
            var chest = FindNearestActive(_roomManager.ActiveChests);
            if (chest.HasValue && TryPathTo(chest.Value, "Chest (Heal)")) return true;
        }

        // 3. Has key → exit (must be revealed in FairMode)
        if (_player.HasKey && _exitDoor.IsPlaced &&
            (!FairMode || _fog.IsRevealed(_exitDoor.ExitX, _exitDoor.ExitY)))
        {
            if (TryPathTo((_exitDoor.ExitX, _exitDoor.ExitY), "Exit")) return true;
        }

        // 4. Fair mode fallback: explore — path to nearest revealed frontier tile
        if (FairMode)
        {
            var frontier = FindFrontierTile();
            if (frontier.HasValue && TryPathTo(frontier.Value, "Explore")) return true;
        }

        return false;
    }

    /// <summary>
    /// Fair-mode exploration: revealed floor tile adjacent to an unrevealed tile.
    /// Walking there lets the fog expand into new territory.
    /// </summary>
    private (int, int)? FindFrontierTile()
    {
        float minDist = float.MaxValue;
        (int, int)? best = null;

        for (int x = 0; x < _grid.Width; x++)
        for (int y = 0; y < _grid.Height; y++)
        {
            if (!_fog.IsRevealed(x, y)) continue;
            if (_grid.GetTileType(x, y) == TileType.Wall) continue;

            // Adjacent to at least one unrevealed tile?
            bool isFrontier = false;
            for (int d = 0; d < 4; d++)
            {
                int nx = x + Dx[d], ny = y + Dy[d];
                if (!_grid.InBounds(nx, ny)) continue;
                if (!_fog.IsRevealed(nx, ny)) { isFrontier = true; break; }
            }
            if (!isFrontier) continue;

            float dist = Mathf.Abs(x - _player.X) + Mathf.Abs(y - _player.Y);
            if (dist < minDist) { minDist = dist; best = (x, y); }
        }

        return best;
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

    private float TileCost(int x, int y)
    {
        float cost = 1f + _dangerMap[x, y];
        if (_grid.GetTileType(x, y) == TileType.Path) cost += 10f;
        return cost;
    }

    private void BuildDangerMap()
    {
        _dangerMap = new float[_grid.Width, _grid.Height];
        const int radius = 2;

        foreach (var m in _roomManager.LiveMonsters)
        {
            if (!m.IsAlive) continue;
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
