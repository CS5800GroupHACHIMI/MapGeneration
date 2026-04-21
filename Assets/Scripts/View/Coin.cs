using System;
using Model;
using UnityEngine;
using UnityEngine.Tilemaps;
using VContainer;

/// <summary>
/// Collectible coin placed on a Path (purple, damaging) tile.
/// Picking it up grants +2 score. Player must brave the damage to earn it.
/// </summary>
public class Coin : MonoBehaviour
{
    private Player  _player;
    private Tilemap _tilemap;

    [Obsolete] private SpriteRenderer _sr;
    private int  _x, _y;
    private bool _active;

    public int  TileX    => _x;
    public int  TileY    => _y;
    public bool IsActive => _active;

    private const int ScoreAmount = 2;

    [Inject]
    public void Construct(Player player, Tilemap tilemap)
    {
        _player  = player;
        _tilemap = tilemap;
    }

    public void Place(int x, int y)
    {
        _x = x;
        _y = y;
        // CreateSprite();
        
        var world = _tilemap.CellToWorld(new Vector3Int(_x, _y, 0)) + _tilemap.cellSize * 0.5f;
        world.z = -0.3f;
        transform.position = world;
        
        _player.OnMoved      += OnPlayerMoved;
        _player.OnTeleported += OnPlayerMoved;
        _active = true;
    }

    public void Remove()
    {
        if (_player != null)
        {
            _player.OnMoved      -= OnPlayerMoved;
            _player.OnTeleported -= OnPlayerMoved;
        }
        _active = false;
        // if (_sr != null) _sr.enabled = false;
        Destroy(gameObject);
    }

    private void OnPlayerMoved(int x, int y)
    {
        if (!_active) return;
        if (x != _x || y != _y) return;

        _player.AddScore(ScoreAmount);
        _active     = false;
        // _sr.enabled = false;
        _player.OnMoved      -= OnPlayerMoved;
        _player.OnTeleported -= OnPlayerMoved;
        
        Destroy(gameObject);
    }

    [Obsolete]
    private void CreateSprite()
    {
        if (_sr != null) { _sr.enabled = true; return; }

        const int S = 16;
        var tex    = new Texture2D(S, S) { filterMode = FilterMode.Point };
        var pixels = new Color[S * S];

        // Simple gold coin: bright yellow disc with darker edge + highlight
        var gold      = new Color(1.00f, 0.85f, 0.10f);
        var goldDark  = new Color(0.60f, 0.45f, 0.05f);
        var highlight = new Color(1.00f, 1.00f, 0.70f);

        float cx = (S - 1) * 0.5f;
        float cy = (S - 1) * 0.5f;
        for (int py = 0; py < S; py++)
        for (int px = 0; px < S; px++)
        {
            float dist = Mathf.Sqrt((px - cx) * (px - cx) + (py - cy) * (py - cy));

            Color c;
            if      (dist < 3.0f) c = highlight;
            else if (dist < 5.0f) c = gold;
            else if (dist < 6.0f) c = goldDark;
            else                  c = Color.clear;

            pixels[py * S + px] = c;
        }

        tex.SetPixels(pixels);
        tex.Apply();

        var go = new GameObject("CoinSprite");
        go.transform.SetParent(transform, false);
        _sr              = go.AddComponent<SpriteRenderer>();
        _sr.sprite       = Sprite.Create(tex, new Rect(0, 0, S, S), new Vector2(0.5f, 0.5f), S);
        _sr.sortingOrder = 4;

        var world = _tilemap.CellToWorld(new Vector3Int(_x, _y, 0)) + _tilemap.cellSize * 0.5f;
        world.z = -0.3f;
        go.transform.position = world;
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
