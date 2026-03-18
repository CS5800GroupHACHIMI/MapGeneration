using Generators;
using Model;
using UnityEngine;
using VContainer;

public class MapGeneratorRunner : MonoBehaviour
{
    private MapConfig        _config;
    private MapGrid          _grid;
    private TilemapBoardView _boardView;
    private IMapGenerator    _generator;
    private Player           _player;

    [Inject]
    public void Construct(
        MapConfig        config,
        MapGrid          grid,
        TilemapBoardView boardView,
        IMapGenerator    generator,
        Player           player)
    {
        _config    = config;
        _grid      = grid;
        _boardView = boardView;
        _generator = generator;
        _player    = player;
    }

    private void Start() => Run();

    public void Run()
    {
        if (_config.randomSeed)
            _config.seed = UnityEngine.Random.Range(int.MinValue, int.MaxValue);

        _grid.Reset(_config.defaultMapTileData);
        _boardView.Initialize();
        _generator.Generate(_grid, _config);

        var start = _generator.GetStartPosition(_grid);
        _player.TeleportTo(start.x, start.y);
    }
}
