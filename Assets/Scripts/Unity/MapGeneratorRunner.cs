using System;
using Generators;
using Model;
using UnityEngine;
using VContainer;

public class MapGeneratorRunner : MonoBehaviour
{
    private MapConfig _config;
    private MapGrid _grid;
    private TilemapBoardView _boardView;
    private IMapGenerator _generator;
    private PlayerSpawner _spawner;

    [Inject]
    public void Construct(
        MapConfig config,
        MapGrid grid,
        TilemapBoardView boardView,
        IMapGenerator generator,
        PlayerSpawner spawner
        )
    {
        _config    = config;
        _grid      = grid;
        _boardView = boardView;
        _generator = generator;
        _spawner   = spawner;
    }

    private void Start() => Run();

    public void Run()
    {
        if (_config.randomSeed)
            _config.seed = UnityEngine.Random.Range(int.MinValue, int.MaxValue);
        
        _grid.Reset(_config.defaultMapTileData);
        _boardView.Initialize();
        _generator.Generate(_grid, _config);
        
        _spawner.Spawn();
    }
}