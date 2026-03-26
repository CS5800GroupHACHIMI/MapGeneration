using Data;
using Model;
using UnityEngine;

namespace Generators
{
    [CreateAssetMenu(fileName = "RandomRoomGenerator", menuName = "Generators/Random Room")]
    public class RandomRoomGenerator : MapGeneratorBase
    {
        public override string Name => "Random Room";

        // Probability that an interior cell becomes Floor (vs Wall)
        private const float FloorChance = 0.6f;



        public override void Generate(MapGrid grid, MapConfig config)
        {
            Random.InitState(config.seed);

            int margin = 1;

            for (int x = 0; x < grid.Width; x++)
            for (int y = 0; y < grid.Height; y++)
            {
                bool isBorder = x < margin || x >= grid.Width  - margin
                                           || y < margin || y >= grid.Height - margin;
                if (isBorder)
                {
                    grid.Set(x, y, TileType.Air);
                    continue;
                }

                bool isEdge = x == margin || x == grid.Width  - margin - 1
                                          || y == margin || y == grid.Height - margin - 1;
                if (isEdge)
                {
                    grid.Set(x, y, TileType.Wall);
                    continue;
                }

                grid.Set(x, y, Random.value < FloorChance ? TileType.Floor : TileType.Wall);
            }

            _startPosition = new Vector2Int(grid.Width / 2, grid.Height / 2);
        }
    }
}
