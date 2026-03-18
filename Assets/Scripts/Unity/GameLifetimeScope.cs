using Generators;
using Model;
using UnityEngine;
using UnityEngine.Tilemaps;
using VContainer;
using VContainer.Unity;

public class GameLifetimeScope : LifetimeScope
{
    public enum GeneratorType { SingleRoom, Metroidvania }

    [Header("Map")]
    [SerializeField] private MapConfig          mapConfig;
    [SerializeField] private TileRegistry       tileRegistry;
    [SerializeField] private Tilemap            tilemap;
    [SerializeField] private TilemapBoardView   boardView;
    [SerializeField] private MapGeneratorRunner runner;
    [SerializeField] private GeneratorType      generatorType = GeneratorType.SingleRoom;

    [Header("Player")]
    [SerializeField] private PlayerView         playerView;

    protected override void Configure(IContainerBuilder builder)
    {
        builder.RegisterInstance(mapConfig);
        builder.RegisterInstance(tileRegistry);
        builder.RegisterInstance(tilemap);
        builder.RegisterInstance(new PlayerInput());

        builder.Register<MapGrid>(Lifetime.Singleton);
        builder.Register<Player>(Lifetime.Singleton);

        if (generatorType == GeneratorType.Metroidvania)
            builder.Register<MetroidvaniaGenerator>(Lifetime.Singleton).AsImplementedInterfaces();
        else
            builder.Register<SingleRoomGenerator>(Lifetime.Singleton).AsImplementedInterfaces();

        builder.RegisterComponent(boardView);
        builder.RegisterComponent(runner);
        builder.RegisterComponent(playerView);

        builder.RegisterEntryPoint<PlayerController>();
    }
}