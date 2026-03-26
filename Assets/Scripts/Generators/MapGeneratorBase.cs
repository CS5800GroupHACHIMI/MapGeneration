using Model;
using UnityEngine;

namespace Generators
{
    /// <summary>
    /// Inherit from this class to implement map generation algorithms.
    ///
    /// What we can use:
    ///   grid   — Use grid.Set(x, y, TileType.X) to place tiles
    ///   config — Map size, seed, default tile type
    ///
    /// What we need to do:
    ///   Set Name.
    ///   Implement Generate().
    ///   Assign _startPosition for the player spawn point.
    ///
    /// To add a new generator:
    ///   1. Create a class that extends MapGeneratorBase
    ///   2. Add [CreateAssetMenu] attribute
    ///   3. Create the asset in the Editor and assign it in GameLifetimeScope
    ///   — No other files need to change.
    /// </summary>
    public abstract class MapGeneratorBase : ScriptableObject, IMapGenerator
    {
        public abstract string Name { get; }

        protected Vector2Int _startPosition;

        public abstract void Generate(MapGrid grid, MapConfig config);

        public virtual Vector2Int GetStartPosition(MapGrid grid) => _startPosition;
    }
}