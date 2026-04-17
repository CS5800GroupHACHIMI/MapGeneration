using Model;
using UnityEngine;
using UnityEngine.Tilemaps;

/// <summary>
/// Key pickup required to open the exit door.
/// Removes its minimap icon when collected.
/// </summary>
public class KeyItem : MonoBehaviour
{
    private Player      _player;
    private Tilemap     _tilemap;
    private MinimapView _minimap;

    private SpriteRenderer _sr;
    private int  _x, _y;
    private bool _active;

    public int  TileX    => _x;
    public int  TileY    => _y;
    public bool IsActive => _active;

    public void Initialize(Player player, Tilemap tilemap, MinimapView minimap)
    {
        _player  = player;
        _tilemap = tilemap;
        _minimap = minimap;
    }

    public void Place(int x, int y)
    {
        _x = x;
        _y = y;
        CreateSprite();
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
        if (_sr != null) _sr.enabled = false;
    }

    private void OnPlayerMoved(int x, int y)
    {
        if (!_active) return;
        if (x != _x || y != _y) return;

        _player.PickupKey();
        _active     = false;
        _sr.enabled = false;
        _player.OnMoved      -= OnPlayerMoved;
        _player.OnTeleported -= OnPlayerMoved;

        _minimap?.UnregisterIcon(_x, _y);
        _minimap?.RefreshTile(_x, _y);
    }

    private void CreateSprite()
    {
        if (_sr != null) { _sr.enabled = true; return; }

        const int S  = 16;
        var tex      = new Texture2D(S, S) { filterMode = FilterMode.Point };
        var pixels   = new Color[S * S];
        var gold     = new Color(1.00f, 0.85f, 0.10f);

        for (int py = 0; py < S; py++)
        for (int px = 0; px < S; px++)
        {
            float cx = 10.5f, cy = 10.5f, outerR = 3.8f, innerR = 2.0f;
            float d     = Mathf.Sqrt((px - cx) * (px - cx) + (py - cy) * (py - cy));
            bool isRing  = d <= outerR && d >= innerR;
            bool isShaft = px >= 3 && px <= 5 && py >= 3 && py <= 10;
            bool isTooth1 = py >= 3 && py <= 5 && px >= 3 && px <= 7;
            bool isTooth2 = py >= 6 && py <= 8 && px >= 3 && px <= 6;

            pixels[py * S + px] = (isRing || isShaft || isTooth1 || isTooth2)
                ? gold : Color.clear;
        }

        tex.SetPixels(pixels);
        tex.Apply();

        var go = new GameObject("KeySprite");
        go.transform.SetParent(transform, false);
        _sr              = go.AddComponent<SpriteRenderer>();
        _sr.sprite       = Sprite.Create(tex, new Rect(0, 0, S, S), new Vector2(0.5f, 0.5f), S);
        _sr.sortingOrder = 5;

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