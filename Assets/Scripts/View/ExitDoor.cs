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
    [SerializeField][Obsolete] private Color doorColor   = new Color(0.2f, 0.6f, 1f, 1f);
    [SerializeField] private int   sortOrder   = 5;

    private MapGrid  _grid;
    private Player   _player;
    private Tilemap  _tilemap;

    [Obsolete] private SpriteRenderer _spriteRenderer;
    private int _exitX, _exitY;
    private bool _active;

    public event Action OnPlayerReachedExit;
    public int  ExitX     => _exitX;
    public int  ExitY     => _exitY;
    public bool IsPlaced  => _active;

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
            // if (_spriteRenderer != null) _spriteRenderer.enabled = false;
            return;
        }

        var adj       = BuildAdjacency(rooms);
        int startRoom = FindNearestRoom(rooms, playerStart);
        int farRoom   = FindFarthestRoom(adj, startRoom, rooms.Count);

        // Fallback: BFS couldn't reach any other room (disconnected chunk graph).
        // Pick the room with the greatest Euclidean distance from start instead.
        if (farRoom == -1)
        {
            float maxDist = -1f;
            for (int i = 0; i < rooms.Count; i++)
            {
                if (i == startRoom) continue;
                float d = Vector2Int.Distance(rooms[i].center, playerStart);
                if (d > maxDist) { maxDist = d; farRoom = i; }
            }
        }

        // Truly only one room in the entire map — cannot place exit
        if (farRoom == -1)
        {
            _active = false;
            // if (_spriteRenderer != null) _spriteRenderer.enabled = false;
            return;
        }

        _exitX = rooms[farRoom].center.x;
        _exitY = rooms[farRoom].center.y;

        // CreateOrUpdateVisual();
        
        var worldPos = _tilemap.CellToWorld(new Vector3Int(_exitX, _exitY, 0)) + _tilemap.cellSize * 0.5f;
        worldPos.z = -0.5f;
        transform.position = worldPos;
        
        _active = true;

        _player.OnMoved      -= OnPlayerMoved;
        _player.OnTeleported -= OnPlayerMoved;
        
        _player.OnMoved      += OnPlayerMoved;
        _player.OnTeleported += OnPlayerMoved;
    }

    private void OnPlayerMoved(int x, int y)
    {
        if (!_active) return;
        if (x == _exitX && y == _exitY && _player.HasKey)
        {
            _active = false;
            OnPlayerReachedExit?.Invoke();
        }
    }

    [Obsolete]
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
            int x0 = cx * ChunkW, y0 = cy * ChunkH;
            int x1 = Mathf.Min(x0 + ChunkW, _grid.Width);
            int y1 = Mathf.Min(y0 + ChunkH, _grid.Height);

            var floorSet = new HashSet<(int, int)>();
            for (int x = x0; x < x1; x++)
            for (int y = y0; y < y1; y++)
                if (_grid.GetTileType(x, y) == TileType.Floor)
                    floorSet.Add((x, y));

            if (floorSet.Count == 0) continue;

            var component = LargestComponent(floorSet);

            int minX = int.MaxValue, maxX = int.MinValue;
            int minY = int.MaxValue, maxY = int.MinValue;
            foreach (var (x, y) in component)
            {
                if (x < minX) minX = x; if (x > maxX) maxX = x;
                if (y < minY) minY = y; if (y > maxY) maxY = y;
            }
            // Filter out corridors / small fragments
            // Real rooms: >= 4 tiles in each dimension AND >= 16 total tiles
            // Corridors: typically 1-3 tiles wide OR < 15 tiles total
            if (maxX - minX < 4 || maxY - minY < 4 || component.Count < 16) continue;

            // Also require reasonable "fill ratio" — corridors snake around and
            // don't fill their bounding box well, while rooms do
            int bboxArea = (maxX - minX + 1) * (maxY - minY + 1);
            if ((float)component.Count / bboxArea < 0.45f) continue;

            int sumX = 0, sumY = 0;
            foreach (var (x, y) in component) { sumX += x; sumY += y; }
            int ccx = sumX / component.Count, ccy = sumY / component.Count;

            // Find the "most interior" tile: must have ALL 4 cardinal neighbors be Floor
            // (corridor tiles have walls on sides → can't satisfy this).
            // Break ties by closest to centroid.
            bool foundInterior = false;
            Vector2Int best = new Vector2Int(component[0].Item1, component[0].Item2);
            float minD = float.MaxValue;
            foreach (var (x, y) in component)
            {
                bool allFloor =
                    _grid.InBounds(x + 1, y) && _grid.GetTileType(x + 1, y) == TileType.Floor &&
                    _grid.InBounds(x - 1, y) && _grid.GetTileType(x - 1, y) == TileType.Floor &&
                    _grid.InBounds(x, y + 1) && _grid.GetTileType(x, y + 1) == TileType.Floor &&
                    _grid.InBounds(x, y - 1) && _grid.GetTileType(x, y - 1) == TileType.Floor;
                if (!allFloor) continue;

                float d = Mathf.Abs(x - ccx) + Mathf.Abs(y - ccy);
                if (d < minD) { minD = d; best = new Vector2Int(x, y); foundInterior = true; }
            }

            // No interior tile found → this chunk is just a corridor, skip it
            if (!foundInterior) continue;

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

    /// <summary>
    /// BFS from start room. Returns the room with maximum hop distance that is
    /// NOT the start room itself. Returns -1 if no other room is reachable.
    /// </summary>
    private int FindFarthestRoom(Dictionary<int, List<int>> adj, int start, int count)
    {
        var dist  = new Dictionary<int, int> { [start] = 0 };
        var queue = new Queue<int>();
        queue.Enqueue(start);

        int farthest = -1;
        int maxDist  = -1;

        while (queue.Count > 0)
        {
            int cur = queue.Dequeue();
            int d   = dist[cur];

            if (cur != start && d > maxDist)
            {
                maxDist  = d;
                farthest = cur;
            }

            foreach (int nb in adj[cur])
            {
                if (!dist.ContainsKey(nb))
                {
                    dist[nb] = d + 1;
                    queue.Enqueue(nb);
                }
            }
        }
        return farthest; // -1 if start is the only reachable room
    }

    private static List<(int, int)> LargestComponent(HashSet<(int, int)> tiles)
    {
        var visited = new HashSet<(int, int)>();
        var best    = new List<(int, int)>();

        foreach (var start in tiles)
        {
            if (!visited.Add(start)) continue;

            var comp  = new List<(int, int)>();
            var queue = new Queue<(int, int)>();
            queue.Enqueue(start);
            comp.Add(start);

            while (queue.Count > 0)
            {
                var (x, y) = queue.Dequeue();
                foreach (var nb in new[] { (x+1,y),(x-1,y),(x,y+1),(x,y-1) })
                {
                    if (tiles.Contains(nb) && visited.Add(nb))
                    { comp.Add(nb); queue.Enqueue(nb); }
                }
            }

            if (comp.Count > best.Count) best = comp;
        }
        return best;
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
