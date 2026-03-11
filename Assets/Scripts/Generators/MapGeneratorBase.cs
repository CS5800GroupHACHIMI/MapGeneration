using Data;
using Model;
using UnityEngine;

namespace Generators
{
    /// <summary>
    /// Inherit from this class to implement map generation algorithms.
    /// 
    /// What we can use：
    ///   grid     — Use grid.Set(x, y, Tile(TileType.X)) to set the tiles
    ///   config   — Configs included map size, seed, default tile.
    /// 
    /// What we need to do：
    ///   Config basic information: Name.
    ///   Implement Generate() function.
    ///   Use grid.Set() fill the map.
    ///   Assign variable _startPosition for start point.
    /// </summary>
    public abstract class MapGeneratorBase : IMapGenerator
    {
        private readonly TileRegistry _registry;

        public abstract string Name { get; }
        
        protected Vector2Int _startPosition;

        protected MapGeneratorBase(TileRegistry registry)
        {
            _registry = registry;
        }

        public abstract void Generate(MapGrid grid, MapConfig config);
    }
}