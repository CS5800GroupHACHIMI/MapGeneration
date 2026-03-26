using System;
using Data;
using UnityEngine.Tilemaps;

namespace Model
{
    public class MapGrid
    {
        public int Width  { get; }
        public int Height { get; }

        private readonly TileBase[,]     _tiles;
        private readonly TileType[,] _types;
        private readonly TileRegistry _registry;

        public event Action<int, int, TileBase> OnTileChanged;

        public MapGrid(MapConfig config, TileRegistry registry)
        {
            Width  = config.width;
            Height = config.height;
            _tiles    = new TileBase[Width, Height];
            _types    = new TileType[Width, Height];
            _registry = registry;
        }

        public TileBase Get(int x, int y) => _tiles[x, y];

        public TileType GetTileType(int x, int y) => _types[x, y];

        public void Set(int x, int y, TileType type)
        {
            _types[x, y] = type;
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