using System;
using System.Collections.Generic;
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

        private List<(int x, int y, TileType type)> _changeLog;
        private bool _isRecording;
        private List<(int x, int y, TileType type)> _log;
        private bool _isLogging;

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

        public void BeginRecording()
        {
            _isRecording = true;
            _changeLog = new List<(int, int, TileType)>();
        }

        public List<(int x, int y, TileType type)> EndRecording()
        {
            _isRecording = false;
            return _changeLog;
        }

        public void BeginLogging()
        {
            _isLogging = true;
            _log = new List<(int, int, TileType)>();
        }

        public List<(int x, int y, TileType type)> EndLogging()
        {
            _isLogging = false;
            return _log;
        }

        public void NotifyTileChanged(int x, int y)
        {
            OnTileChanged?.Invoke(x, y, _tiles[x, y]);
        }

        public void Set(int x, int y, TileType type)
        {
            _types[x, y] = type;
            _tiles[x, y] = _registry.Get(type);
            if (_isLogging)
                _log.Add((x, y, type));
            if (_isRecording)
                _changeLog.Add((x, y, type));
            else
                OnTileChanged?.Invoke(x, y, _tiles[x, y]);
        }

        public void Reset(TileType defaultData)
        {
            for (int x = 0; x < Width;  x++)
            for (int y = 0; y < Height; y++)
                Set(x, y, defaultData);
        }

        public void SilentReset(TileType defaultData)
        {
            for (int x = 0; x < Width;  x++)
            for (int y = 0; y < Height; y++)
            {
                _types[x, y] = defaultData;
                _tiles[x, y] = _registry.Get(defaultData);
            }
        }

        public bool InBounds(int x, int y) =>
            x >= 0 && x < Width && y >= 0 && y < Height;
    }
}