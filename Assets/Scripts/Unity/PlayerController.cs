using Data;
using Model;
using UnityEngine;
using UnityEngine.InputSystem;
using VContainer.Unity;

public class PlayerController : ITickable
{
    private readonly Player _player;
    private readonly MapGrid _grid;
    private readonly PlayerInput _input;

    private float       _cooldown;
    private const float MoveDelay = 0.12f;

    public PlayerController(Player player, MapGrid grid, PlayerInput input)
    {
        _player = player;
        _grid   = grid;
        _input  = input;
        _input.Player.Enable();
    }

    public void Tick()
    {
        _cooldown -= Time.deltaTime;
        if (_cooldown > 0f) return;

        var input = _input.Player.Move.ReadValue<Vector2>();
        if (input == Vector2.zero) return;

        int dx = 0, dy = 0;
        if      (input.x >  0.5f) dx =  1;
        else if (input.x < -0.5f) dx = -1;
        else if (input.y >  0.5f) dy =  1;
        else if (input.y < -0.5f) dy = -1;

        int nx = _player.X + dx;
        int ny = _player.Y + dy;

        if (!_grid.InBounds(nx, ny)) return;
        if (_grid.GetTileType(nx, ny) == TileType.Wall) return;

        _player.MoveTo(nx, ny);
        _cooldown = MoveDelay;
    }
}