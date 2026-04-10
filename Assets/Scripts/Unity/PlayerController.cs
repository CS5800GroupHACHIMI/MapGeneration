using Data;
using Model;
using UnityEngine;
using UnityEngine.InputSystem;
using VContainer.Unity;

public class PlayerController : ITickable
{
    private readonly Player        _player;
    private readonly MapGrid       _grid;
    private readonly PlayerInput   _input;
    private readonly PlayerView    _view;
    private readonly MapTraversal  _traversal;

    private float _damageAccumulator;
    private const float DamagePerSecond = 10f;

    public PlayerController(Player player, MapGrid grid, PlayerInput input, PlayerView view, MapTraversal traversal)
    {
        _player    = player;
        _grid      = grid;
        _input     = input;
        _view      = view;
        _traversal = traversal;
        _input.Player.Enable();
    }

    public void Tick()
    {
        // ── Tile damage (Path = purple = 10 DPS) ─────────────────────────────
        if (!_player.IsDead &&
            _grid.InBounds(_player.X, _player.Y) &&
            _grid.GetTileType(_player.X, _player.Y) == TileType.Path)
        {
            _damageAccumulator += DamagePerSecond * Time.deltaTime;
            if (_damageAccumulator >= 1f)
            {
                int dmg = (int)_damageAccumulator;
                _damageAccumulator -= dmg;
                _player.TakeDamage(dmg);
            }
        }
        else
        {
            _damageAccumulator = 0f;
        }

        // ── Movement ─────────────────────────────────────────────────────────
        if (_player.IsDead) return;
        if (_traversal.IsAutoWalking) return;
        if (_view.IsAnimating) return;

        var input = _input.Player.Move.ReadValue<Vector2>();
        if (input == Vector2.zero) return;

        int dx = input.x >  0.5f ?  1 : input.x < -0.5f ? -1 : 0;
        int dy = input.y >  0.5f ?  1 : input.y < -0.5f ? -1 : 0;

        if (dx == 0 && dy == 0) return;

        int nx = _player.X + dx;
        int ny = _player.Y + dy;

        if (dx != 0 && dy != 0)
        {
            int hx = _player.X + dx, hy = _player.Y;
            int vx = _player.X,      vy = _player.Y + dy;
            bool hOpen = _grid.InBounds(hx, hy) && _grid.GetTileType(hx, hy) != TileType.Wall;
            bool vOpen = _grid.InBounds(vx, vy) && _grid.GetTileType(vx, vy) != TileType.Wall;
            bool dOpen = _grid.InBounds(nx, ny) && _grid.GetTileType(nx, ny) != TileType.Wall;

            if (hOpen && vOpen && dOpen)
            { }
            else if (hOpen)
                { nx = hx; ny = hy; }
            else if (vOpen)
                { nx = vx; ny = vy; }
            else return;
        }
        else if (!_grid.InBounds(nx, ny) || _grid.GetTileType(nx, ny) == TileType.Wall)
        {
            return;
        }

        _player.MoveTo(nx, ny);
    }
}
