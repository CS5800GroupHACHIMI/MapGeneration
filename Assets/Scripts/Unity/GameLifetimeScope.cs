using Generators;
using Model;
using UnityEngine;
using UnityEngine.Tilemaps;
using VContainer;
using VContainer.Unity;

public class GameLifetimeScope : LifetimeScope
{
    [Header("Map")]
    [SerializeField] private MapConfig          mapConfig;
    [SerializeField] private TileRegistry       tileRegistry;
    [SerializeField] private Tilemap            tilemap;
    [SerializeField] private TilemapBoardView   boardView;
    [SerializeField] private MapGeneratorRunner runner;
    [SerializeField] private MapGeneratorBase   generator;

    [Header("Player")]
    [SerializeField] private PlayerView         playerView;

    [Header("Minimap")]
    [SerializeField] private MinimapView        minimapView;

    protected override void Configure(IContainerBuilder builder)
    {
        builder.RegisterInstance(mapConfig);
        builder.RegisterInstance(tileRegistry);
        builder.RegisterInstance(tilemap);
        builder.RegisterInstance(new PlayerInput());

        builder.Register<MapGrid>(Lifetime.Singleton);
        builder.Register<Player>(Lifetime.Singleton);

        builder.RegisterInstance(generator).AsImplementedInterfaces();

        builder.RegisterComponent(boardView);
        builder.RegisterComponent(runner);
        builder.RegisterComponent(playerView);
        builder.RegisterComponent(minimapView);

        builder.RegisterEntryPoint<MapTraversal>().AsSelf();
        builder.RegisterEntryPoint<PlayerController>();
    }
}