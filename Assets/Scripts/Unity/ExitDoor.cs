using System;
using System.Collections.Generic;
using Data;
using Model;
using UnityEngine;
using UnityEngine.Tilemaps;
using VContainer;

/// <summary>
/// Exit door placed in the farthest room from the player start.
/// When the player steps on it, triggers the next level.
/// Creates its own sprite at runtime.
/// </summary>
public class ExitDoor : MonoBehaviour
{
    [Header("Visual")]
    [SerializeField] private Color doorColor   = new Color(0.2f, 0.6f, 1f, 1f);
    [SerializeField] private int   sortOrder   = 5;

    private MapGrid  _grid;
    private Player   _player;
    private Tilemap  _tilemap;

    private SpriteRenderer _spriteRenderer;
    private int _exitX, _exitY;
    private bool _active;

    public event Action OnPlayerReachedExit;

    [Inject]
    public void Construct(MapGrid grid, Player player, Tilemap tilemap)
    {
        _grid    = grid;
        _player  = player;
        _tilemap = tilemap;
    }

    /// <summary>
    /// Place the exit door at the farthest room from the player start.
    /// Call after map generation.
    /// </summary>
    public void PlaceAtFarthestRoom(Vector2Int playerStart)
    {
        var rooms = DetectRooms();
        if (rooms.Count < 2)
        {
            _active = false;
            if (_spriteRenderer != null) _spriteRenderer.enabled = false;
            return;
        }

        var adj       = BuildAdjacency(rooms);
        int startRoom = FindNearestRoom(rooms, playerStart);
        int farRoom   = FindFarthestRoom(adj, startRoom, rooms.Count);

        _exitX = rooms[farRoom].center.x;
        _exitY = rooms[farRoom].center.y;

        CreateOrUpdateVisual();
        _active = true;

        _player.OnMoved      -= OnPlayerMoved;
        _player.OnTeleported -= OnPlayerMoved;
        _player.OnMoved      += OnPlayerMoved;
        _player.OnTeleported += OnPlayerMoved;
    }

    private void OnPlayerMoved(int x, int y)
    {
        if (!_active) return;
        if (x == _exitX && y == _exitY)
        {
            _active = false;
            OnPlayerReachedExit?.Invoke();
        }
    }

    private void CreateOrUpdateVisual()
    {
        if (_spriteRenderer == null)
        {
            var go = new GameObject("ExitDoorSprite");
            go.transform.SetParent(transform, false);
            _spriteRenderer = go.AddComponent<SpriteRenderer>();
            _spriteRenderer.sortingOrder = sortOrder;

            // Create a simple door sprite (16x16 with an arch shape)
            const int size = 16;
            var tex = new Texture2D(size, size);
            tex.filterMode = FilterMode.Point;
            var pixels = new Color[size * size];

            for (int py = 0; py < size; py++)
            for (int px = 0; px < size; px++)
            {
                // Door frame
                bool isFrame = px <= 1 || px >= size - 2 || py <= 1;
                // Arch top
                float cx = size / 2f, cy = size - 1f;
                float dist = Mathf.Sqrt((px - cx) * (px - cx) + (py - cy) * (py - cy));
                bool isArch = py >= size - 5 && dist <= size / 2f + 1 && dist >= size / 2f - 2;
                // Door interior
                bool isInterior = px > 2 && px < size - 3 && py > 2 && py < size - 3;

                if (isArch || isFrame)
                    pixels[py * size + px] = doorColor;
                else if (isInterior)
                    pixels[py * size + px] = new Color(doorColor.r * 0.3f, doorColor.g * 0.3f, doorColor.b * 0.5f, 0.9f);
                else
                    pixels[py * size + px] = Color.clear;
            }

            tex.SetPixels(pixels);
            tex.Apply();
            _spriteRenderer.sprite = Sprite.Create(tex, new Rect(0, 0, size, size), new Vector2(0.5f, 0.5f), size);
        }

        // Position at exit tile center
        var worldPos = _tilemap.CellToWorld(new Vector3Int(_exitX, _exitY, 0)) + _tilemap.cellSize * 0.5f;
        worldPos.z = -0.5f;
        _spriteRenderer.transform.position = worldPos;
        _spriteRenderer.enabled = true;
    }

    // ─── Room detection (same logic as MapTraversal) ─────────────────────────

    private const int ChunkW = 10, ChunkH = 8;

    private struct Room
    {
        public int chunkX, chunkY;
        public Vector2Int center;
    }

    private List<Room> DetectRooms()
    {
        int cols = Mathf.Max(1, _grid.Width  / ChunkW);
        int rows = Mathf.Max(1, _grid.Height / ChunkH);
        var rooms = new List<Room>();

        for (int cx = 0; cx < cols; cx++)
        for (int cy = 0; cy < rows; cy++)
        {
            var tiles = new List<(int x, int y)>();
            for (int x = cx * ChunkW; x < (cx + 1) * ChunkW && x < _grid.Width; x++)
            for (int y = cy * ChunkH; y < (cy + 1) * ChunkH && y < _grid.Height; y++)
            {
                var t = _grid.GetTileType(x, y);
                if (t != TileType.Wall && t != TileType.Air)
                    tiles.Add((x, y));
            }
            if (tiles.Count < 6) continue;

            int sumX = 0, sumY = 0;
            foreach (var (x, y) in tiles) { sumX += x; sumY += y; }
            int ccx = sumX / tiles.Count, ccy = sumY / tiles.Count;
            float minD = float.MaxValue;
            var best = new Vector2Int(tiles[0].x, tiles[0].y);
            foreach (var (x, y) in tiles)
            {
                float d = Mathf.Abs(x - ccx) + Mathf.Abs(y - ccy);
                if (d < minD) { minD = d; best = new Vector2Int(x, y); }
            }

            rooms.Add(new Room { chunkX = cx, chunkY = cy, center = best });
        }
        return rooms;
    }

    private Dictionary<int, List<int>> BuildAdjacency(List<Room> rooms)
    {
        var chunkToRoom = new Dictionary<(int, int), int>();
        for (int i = 0; i < rooms.Count; i++)
            chunkToRoom[(rooms[i].chunkX, rooms[i].chunkY)] = i;

        var adj = new Dictionary<int, List<int>>();
        for (int i = 0; i < rooms.Count; i++)
            adj[i] = new List<int>();

        int[] dx = { 1, 0, -1, 0 };
        int[] dy = { 0, 1, 0, -1 };

        for (int i = 0; i < rooms.Count; i++)
        for (int d = 0; d < 4; d++)
        {
            var key = (rooms[i].chunkX + dx[d], rooms[i].chunkY + dy[d]);
            if (chunkToRoom.TryGetValue(key, out int j) && !adj[i].Contains(j))
            {
                adj[i].Add(j);
                adj[j].Add(i);
            }
        }
        return adj;
    }

    private int FindNearestRoom(List<Room> rooms, Vector2Int pos)
    {
        float minD = float.MaxValue;
        int best = 0;
        for (int i = 0; i < rooms.Count; i++)
        {
            float d = Vector2Int.Distance(rooms[i].center, pos);
            if (d < minD) { minD = d; best = i; }
        }
        return best;
    }

    /// <summary>BFS from start room, return the room with maximum hop distance.</summary>
    private int FindFarthestRoom(Dictionary<int, List<int>> adj, int start, int count)
    {
        var visited = new HashSet<int> { start };
        var queue   = new Queue<int>();
        queue.Enqueue(start);
        int farthest = start;

        while (queue.Count > 0)
        {
            int cur = queue.Dequeue();
            farthest = cur;
            foreach (int nb in adj[cur])
            {
                if (visited.Add(nb))
                    queue.Enqueue(nb);
            }
        }
        return farthest;
    }

    private void OnDestroy()
    {
        if (_player != null)
        {
            _player.OnMoved      -= OnPlayerMoved;
            _player.OnTeleported -= OnPlayerMoved;
        }
    }
}
