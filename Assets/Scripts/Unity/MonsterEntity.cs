using Model;
using UnityEngine;
using UnityEngine.Tilemaps;

/// <summary>
/// Stationary monster placed at a room center.
/// Deals 8 DPS when player is in the surrounding 3×3 area (same room chunk only).
/// </summary>
public class MonsterEntity : MonoBehaviour
{
    private Player  _player;
    private Tilemap _tilemap;

    private SpriteRenderer _sr;
    private int   _x, _y;
    private int   _chunkX, _chunkY;

    public int  TileX   => _x;
    public int  TileY   => _y;
    public bool IsAlive => _sr != null && _sr.enabled;
    private float _damageAccumulator;

    private const float DamagePerSecond = 8f;
    private const int   DamageRange     = 1;  // 3×3 area (Chebyshev distance 1)
    private const int   ChunkW          = 10;
    private const int   ChunkH          = 8;

    public void Initialize(Player player, Tilemap tilemap)
    {
        _player  = player;
        _tilemap = tilemap;
    }

    public void Place(int x, int y, int chunkX, int chunkY)
    {
        _x      = x;
        _y      = y;
        _chunkX = chunkX;
        _chunkY = chunkY;
        CreateSprite();
    }

    private void Update()
    {
        if (_player == null || _player.IsDead || _sr == null || !_sr.enabled)
        {
            _damageAccumulator = 0f;
            return;
        }

        // Only deal damage if player is in the same room chunk
        int playerChunkX = _player.X / ChunkW;
        int playerChunkY = _player.Y / ChunkH;
        if (playerChunkX != _chunkX || playerChunkY != _chunkY)
        {
            _damageAccumulator = 0f;
            return;
        }

        int dx = Mathf.Abs(_player.X - _x);
        int dy = Mathf.Abs(_player.Y - _y);

        if (dx <= DamageRange && dy <= DamageRange)
        {
            _damageAccumulator += DamagePerSecond * Time.deltaTime;
            if (_damageAccumulator >= 1f)
            {
                int dmg = (int)_damageAccumulator;
                _damageAccumulator -= dmg;
                _player.TakeDamage(dmg);
            }
        }
        else
        {
            _damageAccumulator = 0f;
        }
    }

    private void CreateSprite()
    {
        if (_sr != null) { _sr.enabled = true; return; }

        const int S = 16;
        var tex     = new Texture2D(S, S) { filterMode = FilterMode.Point };
        var pixels  = new Color[S * S];
        var bodyCol = new Color(0.7f, 0.1f, 0.1f);
        var eyeCol  = new Color(1.0f, 0.9f, 0.0f);

        for (int py = 0; py < S; py++)
        for (int px = 0; px < S; px++)
        {
            float cx = 7.5f, cy = 7.5f;
            float d     = Mathf.Sqrt((px - cx) * (px - cx) + (py - cy) * (py - cy));
            bool isBody = d <= 5.5f;
            bool isEyeL = Mathf.Sqrt((px - 5f)  * (px - 5f)  + (py - 9f) * (py - 9f)) <= 1.3f;
            bool isEyeR = Mathf.Sqrt((px - 10f) * (px - 10f) + (py - 9f) * (py - 9f)) <= 1.3f;

            if      (isEyeL || isEyeR) pixels[py * S + px] = eyeCol;
            else if (isBody)            pixels[py * S + px] = bodyCol;
            else                        pixels[py * S + px] = Color.clear;
        }

        tex.SetPixels(pixels);
        tex.Apply();

        var go = new GameObject("MonsterSprite");
        go.transform.SetParent(transform, false);
        _sr              = go.AddComponent<SpriteRenderer>();
        _sr.sprite       = Sprite.Create(tex, new Rect(0, 0, S, S), new Vector2(0.5f, 0.5f), S);
        _sr.sortingOrder = 5;

        var world = _tilemap.CellToWorld(new Vector3Int(_x, _y, 0)) + _tilemap.cellSize * 0.5f;
        world.z = -0.4f;
        go.transform.position = world;
    }
}