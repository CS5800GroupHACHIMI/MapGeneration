using UnityEngine;
using VContainer;

namespace Unity
{
    public class ItemFactories
    { 
        private Chest _prefabChest;
        private KeyItem _prefabKey;
        private ExitDoor _prefabExit;
        private MonsterEntity _prefabEnemy;
        private Transform _itemRoot;
        
        private readonly IObjectResolver _resolver;

        public ItemFactories(
            Chest prefabChest,
            KeyItem prefabKey,
            ExitDoor prefabExit,
            MonsterEntity prefabEnemy,
            [Key("ItemRoot")] Transform itemRoot,
            IObjectResolver resolver
            )
        {
            _prefabChest = prefabChest;
            _prefabKey = prefabKey;
            _prefabExit = prefabExit;
            _prefabEnemy = prefabEnemy;
            _itemRoot = itemRoot;
            _resolver = resolver;
        }

        public Chest CreateChest(int x, int y)
        {
            var chest = Object.Instantiate(_prefabChest, _itemRoot);
            _resolver.Inject(chest);
            chest.Place(x, y);
            return chest;
        }

        public KeyItem CreateKey(int x, int y)
        {
            var key = Object.Instantiate(_prefabKey, _itemRoot);
            _resolver.Inject(key);
            key.Place(x, y);
            return key;
        }

        public ExitDoor CreateExit(Vector2Int playerStart)
        {
            var exit = Object.Instantiate(_prefabExit, _itemRoot);
            _resolver.Inject(exit);
            exit.PlaceAtFarthestRoom(playerStart);
            return exit;
        }
        
        public MonsterEntity CreateEnemy(int x, int y, int chunkX, int chunkY)
        {
            var entity = Object.Instantiate(_prefabEnemy, _itemRoot);
            _resolver.Inject(entity);
            entity.Place(x, y, chunkX, chunkY);
            return entity;
        }
    }
}