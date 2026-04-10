using System.Collections.Generic;
using Data;
using Model;
using UnityEngine;
using UnityEngine.Tilemaps;
using VContainer;

/// <summary>
/// Assigns room roles after map generation and places entities.
/// One key room (bright yellow icon), one treasure room (gold icon),
/// remaining rooms get monsters. Start and exit rooms are excluded.
/// </summary>
public class RoomManager : MonoBehaviour
{
    private MapGrid     _grid;
    private Player      _player;
    private Tilemap     _tilemap;
    private MinimapView _minimap;

    private readonly List<Chest>         _chests   = new();
    private readonly List<KeyItem>       _keys     = new();
    private readonly List<MonsterEntity> _monsters = new();

    [Inject]
    public void Construct(MapGrid grid, Player player, Tilemap tilemap, MinimapView minimap)
    {
        _grid    = grid;
        _player  = player;
        _tilemap = tilemap;
        _minimap = minimap;
    }

    public void PlaceEntities(Vector2Int startPos, int exitX, int exitY)
    {
        Clear();

        var rooms = DetectRooms();

        int startChunkX = startPos.x / ChunkW;
        int startChunkY = startPos.y / ChunkH;
        int exitChunkX  = exitX      / ChunkW;
        int exitChunkY  = exitY      / ChunkH;
        // Also exclude by exact position to handle edge cases where start == exit
        bool hasValidExit = (exitX != startPos.x || exitY != startPos.y);

        var available = new List<int>();
        for (int i = 0; i < rooms.Count; i++)
        {
            // Never place key/chest/monster in the player's starting room
            if (rooms[i].chunkX == startChunkX && rooms[i].chunkY == startChunkY) continue;
            // Never place key/chest/monster in the exit room (if exit is valid)
            if (hasValidExit && rooms[i].chunkX == exitChunkX && rooms[i].chunkY == exitChunkY) continue;
            available.Add(i);
        }

        if (available.Count == 0) return;

        Shuffle(available);

        int total = available.Count;
        int idx   = 0;

        // Key room (exactly 1) — bright yellow icon
        {
            var room = rooms[available[idx++]];
            var go   = new GameObject("KeyItem");
            go.transform.SetParent(transform, false);
            var key = go.AddComponent<KeyItem>();
            key.Initialize(_player, _tilemap, _minimap);
            key.Place(room.center.x, room.center.y);
            _keys.Add(key);
            _minimap?.RegisterIcon(room.center.x, room.center.y, new Color32(255, 255, 80, 255));
        }

        // Remaining rooms: 20% chests, 40% monsters, rest empty
        int remaining    = total - 1;
        int chestCount   = Mathf.Max(1, Mathf.RoundToInt(remaining * 0.20f));
        int monsterCount = Mathf.RoundToInt(remaining * 0.40f);
        // Clamp so we don't exceed available slots
        chestCount   = Mathf.Min(chestCount,   total - idx);
        monsterCount = Mathf.Min(monsterCount, total - idx - chestCount);

        for (int i = 0; i < chestCount && idx < total; i++)
        {
            var room = rooms[available[idx++]];
            var go   = new GameObject("Chest");
            go.transform.SetParent(transform, false);
            var chest = go.AddComponent<Chest>();
            chest.Initialize(_player, _tilemap, _minimap);
            chest.Place(room.center.x, room.center.y);
            _chests.Add(chest);
            // Chest hidden by fog — no minimap icon
        }

        for (int i = 0; i < monsterCount && idx < total; i++)
        {
            var room    = rooms[available[idx++]];
            var go      = new GameObject("Monster");
            go.transform.SetParent(transform, false);
            var monster = go.AddComponent<MonsterEntity>();
            monster.Initialize(_player, _tilemap);
            monster.Place(room.center.x, room.center.y, room.chunkX, room.chunkY);
            _monsters.Add(monster);
        }
        // remaining rooms (idx..total-1) are intentionally left empty
    }

    public void Clear()
    {
        foreach (var c in _chests)   if (c != null) { c.Remove(); Destroy(c.gameObject); }
        foreach (var k in _keys)     if (k != null) { k.Remove(); Destroy(k.gameObject); }
        foreach (var m in _monsters) if (m != null) Destroy(m.gameObject);
        _chests.Clear();
        _keys.Clear();
        _monsters.Clear();
    }

    // ─── Room detection ───────────────────────────────────────────────────────

    private const int ChunkW = 10, ChunkH = 8;

    private struct RoomInfo
    {
        public int chunkX, chunkY;
        public Vector2Int center;
    }

    private List<RoomInfo> DetectRooms()
    {
        int cols  = Mathf.Max(1, _grid.Width  / ChunkW);
        int rows  = Mathf.Max(1, _grid.Height / ChunkH);
        var rooms = new List<RoomInfo>();

        for (int cx = 0; cx < cols; cx++)
        for (int cy = 0; cy < rows; cy++)
        {
            int x0 = cx * ChunkW, y0 = cy * ChunkH;
            int x1 = Mathf.Min(x0 + ChunkW, _grid.Width);
            int y1 = Mathf.Min(y0 + ChunkH, _grid.Height);

            // Collect Floor tiles in this chunk
            var floorSet = new HashSet<(int, int)>();
            for (int x = x0; x < x1; x++)
            for (int y = y0; y < y1; y++)
                if (_grid.GetTileType(x, y) == TileType.Floor)
                    floorSet.Add((x, y));

            if (floorSet.Count == 0) continue;

            // Find largest connected Floor component (4-connected BFS)
            var component = LargestComponent(floorSet);

            // Bounding-box check: corridors are ≤2 tiles in one dimension,
            // rooms are at least 4×4. Reject narrow/thin regions.
            int minX = int.MaxValue, maxX = int.MinValue;
            int minY = int.MaxValue, maxY = int.MinValue;
            foreach (var (x, y) in component)
            {
                if (x < minX) minX = x; if (x > maxX) maxX = x;
                if (y < minY) minY = y; if (y > maxY) maxY = y;
            }
            if (maxX - minX < 3 || maxY - minY < 3) continue; // corridor

            // Center tile = component tile closest to centroid
            int sumX = 0, sumY = 0;
            foreach (var (x, y) in component) { sumX += x; sumY += y; }
            int ccx = sumX / component.Count, ccy = sumY / component.Count;

            float minD = float.MaxValue;
            var   best = component[0];
            foreach (var (x, y) in component)
            {
                float d = Mathf.Abs(x - ccx) + Mathf.Abs(y - ccy);
                if (d < minD) { minD = d; best = (x, y); }
            }

            rooms.Add(new RoomInfo { chunkX = cx, chunkY = cy,
                                     center = new Vector2Int(best.Item1, best.Item2) });
        }
        return rooms;
    }

    /// <summary>BFS flood-fill within a set; returns the largest 4-connected component.</summary>
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

    private static void Shuffle(List<int> list)
    {
        for (int i = list.Count - 1; i > 0; i--)
        {
            int j = Random.Range(0, i + 1);
            (list[i], list[j]) = (list[j], list[i]);
        }
    }
}