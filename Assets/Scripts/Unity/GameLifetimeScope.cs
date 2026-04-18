using Generators;
using Model;
using Unity;
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
    
    [Header("Items")]
    [SerializeField] private Chest              chestView;
    [SerializeField] private KeyItem            keyItemView;
    [SerializeField] private MonsterEntity      monsterEntityView;

    [Header("Minimap")]
    [SerializeField] private MinimapView        minimapView;

    [Header("Fog")]
    [SerializeField] private FogOfWar           fogOfWar;

    [Header("Exit")]
    [SerializeField] private ExitDoor           exitDoor;

    [Header("Rooms")]
    [SerializeField] private RoomManager roomManager;

    protected override void Configure(IContainerBuilder builder)
    {
        builder.RegisterInstance(mapConfig);
        builder.RegisterInstance(tileRegistry);
        builder.RegisterInstance(tilemap);
        
        builder.RegisterInstance(chestView);
        builder.RegisterInstance(keyItemView);
        builder.RegisterInstance(exitDoor);
        builder.RegisterInstance(monsterEntityView);
        
        builder.RegisterInstance(new PlayerInput());

        builder.Register<MapGrid>(Lifetime.Singleton);
        builder.Register<Player>(Lifetime.Singleton);
        builder.Register<ItemFactories>(Lifetime.Singleton);

        builder.RegisterInstance(generator).AsImplementedInterfaces();
        builder.RegisterInstance(roomManager.ItemRoot).Keyed("ItemRoot");

        builder.RegisterComponent(boardView);
        builder.RegisterComponent(runner);
        builder.RegisterComponent(playerView);
        builder.RegisterComponent(minimapView);
        builder.RegisterComponent(fogOfWar);
        builder.RegisterComponent(roomManager);

        builder.RegisterEntryPoint<MapTraversal>().AsSelf();
        builder.RegisterEntryPoint<PlayerController>();
    }
}