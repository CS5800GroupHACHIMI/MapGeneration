using System;
using Model;
using Unity.VisualScripting;
using UnityEngine;
using UnityEngine.Tilemaps;
using VContainer;

/// <summary>
/// Treasure chest placed in a room center. Heals 30 HP on pickup.
/// Removes its minimap icon when collected.
/// </summary>
public class Chest : MonoBehaviour
{
    private Player      _player;
    private Tilemap     _tilemap;
    private MinimapView _minimap;

    // private SpriteRenderer _sr;
    private int  _x, _y;
    private bool _active;

    public int  TileX    => _x;
    public int  TileY    => _y;
    public bool IsActive => _active;

    private const int HealAmount = 30;

    [Inject]
    public void Construct(Player player, Tilemap tilemap, MinimapView minimap)
    {
        _player  = player;
        _tilemap = tilemap;
        _minimap = minimap;
    }

    public void Place(int x, int y)
    {
        _x = x;
        _y = y;
        // CreateSprite();

        var world = _tilemap.CellToWorld(new Vector3Int(_x, _y, 0)) + _tilemap.cellSize * 0.5f;
        world.z = -0.4f;
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

        _player.Heal(HealAmount);
        _active     = false;
        // _sr.enabled = false;
        _player.OnMoved      -= OnPlayerMoved;
        _player.OnTeleported -= OnPlayerMoved;

        _minimap?.UnregisterIcon(_x, _y);
        _minimap?.RefreshTile(_x, _y);
        
        Destroy(gameObject);
    }

    [Obsolete]
    private void CreateSprite()
    {
        // if (_sr != null) { _sr.enabled = true; return; }

        const int S = 16;
        var tex     = new Texture2D(S, S) { filterMode = FilterMode.Point };
        var pixels  = new Color[S * S];

        // Classic treasure chest: dark border, two-tone body/lid, gold lock
        var border  = new Color(0.22f, 0.10f, 0.01f);
        var lidCol  = new Color(0.52f, 0.28f, 0.04f);
        var bodyCol = new Color(0.76f, 0.46f, 0.08f);
        var divCol  = new Color(0.18f, 0.08f, 0.01f);
        var gold    = new Color(1.00f, 0.85f, 0.10f);
        var goldDrk = new Color(0.55f, 0.40f, 0.00f);

        for (int py = 0; py < S; py++)
        for (int px = 0; px < S; px++)
        {
            bool inChest  = px >= 1 && px <= 14 && py >= 2 && py <= 13;
            bool isBorder = inChest && (px == 1 || px == 14 || py == 2 || py == 13);
            bool isDivide = inChest && py == 8 && !isBorder;
            bool isLid    = inChest && py >= 9  && py <= 12 && !isBorder;
            bool isBody   = inChest && py >= 3  && py <= 7  && !isBorder;
            // Circular lock on body center
            float lx = 7.5f, ly = 5f;
            float ld = Mathf.Sqrt((px - lx) * (px - lx) + (py - ly) * (py - ly));
            bool isLockRing = isBody && ld <= 2.2f && ld >= 1.2f;
            bool isLockFill = isBody && ld < 1.2f;

            if      (!inChest)    pixels[py * S + px] = Color.clear;
            else if (isBorder)    pixels[py * S + px] = border;
            else if (isDivide)    pixels[py * S + px] = divCol;
            else if (isLockRing)  pixels[py * S + px] = gold;
            else if (isLockFill)  pixels[py * S + px] = goldDrk;
            else if (isLid)       pixels[py * S + px] = lidCol;
            else if (isBody)      pixels[py * S + px] = bodyCol;
            else                  pixels[py * S + px] = Color.clear;
        }

        tex.SetPixels(pixels);
        tex.Apply();

        var go = new GameObject("ChestSprite");
        go.transform.SetParent(transform, false);
        // _sr              = go.AddComponent<SpriteRenderer>();
        // _sr.sprite       = Sprite.Create(tex, new Rect(0, 0, S, S), new Vector2(0.5f, 0.5f), S);
        // _sr.sortingOrder = 5;

        var world = _tilemap.CellToWorld(new Vector3Int(_x, _y, 0)) + _tilemap.cellSize * 0.5f;
        world.z = -0.4f;
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