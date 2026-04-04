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
        if (_traversal.IsAutoWalking) return;
        if (_view.IsAnimating) return;

        var input = _input.Player.Move.ReadValue<Vector2>();
        if (input == Vector2.zero) return;

        // Both axes read independently — holding A/D no longer blocks W/S.
        int dx = input.x >  0.5f ?  1 : input.x < -0.5f ? -1 : 0;
        int dy = input.y >  0.5f ?  1 : input.y < -0.5f ? -1 : 0;

        if (dx == 0 && dy == 0) return;

        int nx = _player.X + dx;
        int ny = _player.Y + dy;

        // For diagonals, fall back to cardinal if the diagonal tile is blocked.
        if (!_grid.InBounds(nx, ny) || _grid.GetTileType(nx, ny) == TileType.Wall)
        {
            // Try horizontal only
            if (dx != 0 && dy != 0)
            {
                int hx = _player.X + dx, hy = _player.Y;
                int vx = _player.X,      vy = _player.Y + dy;

                if (_grid.InBounds(hx, hy) && _grid.GetTileType(hx, hy) != TileType.Wall)
                    { nx = hx; ny = hy; }
                else if (_grid.InBounds(vx, vy) && _grid.GetTileType(vx, vy) != TileType.Wall)
                    { nx = vx; ny = vy; }
                else return;
            }
            else return;
        }

        _player.MoveTo(nx, ny);
    }
}