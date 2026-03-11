using System;
using Data;
using UnityEngine.Tilemaps;

namespace Model
{
    public class MapGrid
    {
        public int Width  { get; }
        public int Height { get; }

        private readonly Tile[,] _tiles;
        private readonly TileRegistry _registry;

        public event Action<int, int, Tile> OnTileChanged;

        public MapGrid(MapConfig config, TileRegistry registry)
        {
            Width  = config.width;
            Height = config.height;
            _tiles = new Tile[Width, Height];
            _registry = registry;
        }

        public Tile Get(int x, int y) => _tiles[x, y];

        public void Set(int x, int y, TileType type)
        {
            _tiles[x, y] = _registry.Get(type);
            OnTileChanged?.Invoke(x, y, _tiles[x, y]);
        }

        public void Reset(TileType defaultData)
        {
            for (int x = 0; x < Width;  x++)
            for (int y = 0; y < Height; y++)
                Set(x, y, defaultData);
        }

        public bool InBounds(int x, int y) =>
            x >= 0 && x < Width && y >= 0 && y < Height;
    }
}