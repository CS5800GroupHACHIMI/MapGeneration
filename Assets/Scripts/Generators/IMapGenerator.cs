using Model;
using UnityEngine;

namespace Generators
{
    public interface IMapGenerator
    {
        string Name { get; }
        
        void Generate(MapGrid grid, MapConfig config);
        
        Vector2Int GetStartPosition(MapGrid grid) =>
            new Vector2Int(grid.Width / 2, grid.Height / 2);
    }
}