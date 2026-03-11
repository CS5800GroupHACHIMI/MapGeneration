using Data;
using Model;
using UnityEngine;

namespace Generators
{
    public class SingleRoomGenerator : MapGeneratorBase
    {
        public override string Name => "Single Room";

        public SingleRoomGenerator(TileRegistry registry) : base(registry) { }

        public override void Generate(MapGrid grid, MapConfig config)
        {
            int margin = 2;

            for (int x = 0; x < grid.Width;  x++)
            for (int y = 0; y < grid.Height; y++)
            {
                bool isAir = x < margin || x >= grid.Width  - margin
                                         || y < margin || y >= grid.Height - margin;
                
                bool isWall = x == margin || x == grid.Width  - margin - 1
                                        || y == margin || y == grid.Height - margin - 1;
                if (isAir)
                    grid.Set(x, y, TileType.Air);
                else if (isWall)
                    grid.Set(x, y, TileType.Wall);
                else
                    grid.Set(x, y, TileType.Floor);
            }

            _startPosition = new Vector2Int(grid.Width / 2, grid.Height / 2);
        }
    }
}