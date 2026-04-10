using System.Collections.Generic;
using Data;
using Model;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.Tilemaps;
using UnityEngine.UI;
using VContainer;

public class MinimapView : MonoBehaviour
{
    [Header("UI References")]
    [SerializeField] private GameObject panel;
    [SerializeField] private RawImage   mapImage;

    [Header("Fallback Tile Colors")]
    [SerializeField] private Color colorAir   = new Color(0.05f, 0.05f, 0.05f);
    [SerializeField] private Color colorWall  = new Color(0.25f, 0.25f, 0.25f);
    [SerializeField] private Color colorFloor = new Color(0.75f, 0.75f, 0.75f);
    [SerializeField] private Color colorPath  = new Color(0.80f, 0.70f, 0.35f);

    private const int PixelsPerTile = 4; // minimap resolution multiplier

    private MapGrid      _grid;
    private Player       _player;
    private Tilemap      _tilemap;
    private TileRegistry _registry;
    private FogOfWar     _fog;

    private Texture2D                     _texture;
    private Dictionary<TileType, Color32> _tileColors;
    private RectTransform                 _panelRT;
    private static readonly Color32       FogGray = new Color32(25, 25, 25, 255);
    private Dictionary<Vector2Int, Color32> _icons = new();

    // ── Minimap mode ────────────────────────────────────────────────────────
    private bool _isFullMap;

    private const float SmallSize      = 350f;
    private const float SmallViewRange = 0.3f;
    private const float FullSize       = 700f;

    // ── DI ───────────────────────────────────────────────────────────────────

    [Inject]
    public void Construct(MapGrid grid, Player player,
                          Tilemap tilemap, TileRegistry registry, FogOfWar fog)
    {
        _grid     = grid;
        _player   = player;
        _tilemap  = tilemap;
        _registry = registry;
        _fog      = fog;

        _player.OnMoved      += OnPlayerMoved;
        _player.OnTeleported += OnPlayerMoved;
    }

    // ── Public API ────────────────────────────────────────────────────────────

    public void Rebuild()
    {
        if (_texture != null) Destroy(_texture);

        int texW = _grid.Width  * PixelsPerTile;
        int texH = _grid.Height * PixelsPerTile;

        _texture = new Texture2D(texW, texH, TextureFormat.RGBA32, false)
        {
            filterMode = FilterMode.Bilinear,
            wrapMode   = TextureWrapMode.Clamp,
        };

        _tileColors = BuildTileColors();
        var pixels  = new Color32[texW * texH];
        for (int y = 0; y < texH; y++)
        for (int x = 0; x < texW; x++)
            pixels[y * texW + x] = GetMinimapPixelSub(x, y);

        _texture.SetPixels32(pixels);
        _texture.Apply();

        mapImage.material = null; // default UI shader — no overlay
        mapImage.texture  = _texture;

        _panelRT = panel.GetComponent<RectTransform>();

        var fitter = mapImage.GetComponent<AspectRatioFitter>();
        if (fitter != null)
        {
            fitter.aspectRatio = (float)_grid.Width / _grid.Height;
            fitter.aspectMode  = AspectRatioFitter.AspectMode.FitInParent;
        }

        _isFullMap = false;
        ApplySmallMode();
        panel.SetActive(true);
    }

    public void RegisterIcon(int x, int y, Color32 color)  => _icons[new Vector2Int(x, y)] = color;
    public void UnregisterIcon(int x, int y)               => _icons.Remove(new Vector2Int(x, y));
    public void ClearIcons()                               => _icons.Clear();

    public void RefreshTile(int tileX, int tileY)
    {
        if (_texture == null) return;
        for (int sx = 0; sx < PixelsPerTile; sx++)
        for (int sy = 0; sy < PixelsPerTile; sy++)
        {
            int px = tileX * PixelsPerTile + sx;
            int py = tileY * PixelsPerTile + sy;
            _texture.SetPixel(px, py, GetMinimapPixelSub(px, py));
        }
        _texture.Apply();
    }

    // ── Unity ─────────────────────────────────────────────────────────────────

    private void Update()
    {
        // Tab hold = full map, release = small map
        bool tabHeld = Keyboard.current != null && Keyboard.current[Key.Tab].isPressed;
        if (tabHeld && !_isFullMap)
        {
            _isFullMap = true;
            ApplyFullMode();
        }
        else if (!tabHeld && _isFullMap)
        {
            _isFullMap = false;
            ApplySmallMode();
        }

        if (!_isFullMap)
            UpdateSmallViewport();
    }

    private void OnDestroy()
    {
        if (_player != null)
        {
            _player.OnMoved      -= OnPlayerMoved;
            _player.OnTeleported -= OnPlayerMoved;
        }
        if (_texture != null) Destroy(_texture);
    }

    // ── Mode Switching ───────────────────────────────────────────────────────

    private void ApplySmallMode()
    {
        if (_panelRT == null) return;

        _panelRT.anchorMin = new Vector2(0, 1);
        _panelRT.anchorMax = new Vector2(0, 1);
        _panelRT.pivot     = new Vector2(0, 1);
        _panelRT.sizeDelta = new Vector2(SmallSize, SmallSize);
        _panelRT.anchoredPosition = new Vector2(10, -10);

        UpdateSmallViewport();
        panel.SetActive(true);
    }

    private void ApplyFullMode()
    {
        if (_panelRT == null) return;

        _panelRT.anchorMin = new Vector2(0.5f, 0.5f);
        _panelRT.anchorMax = new Vector2(0.5f, 0.5f);
        _panelRT.pivot     = new Vector2(0.5f, 0.5f);
        _panelRT.sizeDelta = new Vector2(FullSize, FullSize);
        _panelRT.anchoredPosition = Vector2.zero;

        mapImage.uvRect = new Rect(0, 0, 1, 1);
        panel.SetActive(true);
    }

    private void UpdateSmallViewport()
    {
        if (_grid == null) return;

        float px = (_player.X + 0.5f) / _grid.Width;
        float py = (_player.Y + 0.5f) / _grid.Height;

        float halfRange = SmallViewRange * 0.5f;
        float x0 = Mathf.Clamp(px - halfRange, 0f, 1f - SmallViewRange);
        float y0 = Mathf.Clamp(py - halfRange, 0f, 1f - SmallViewRange);

        mapImage.uvRect = new Rect(x0, y0, SmallViewRange, SmallViewRange);
    }

    // ── Fog Reveal ───────────────────────────────────────────────────────────

    private void OnPlayerMoved(int x, int y)
    {
        if (_texture == null || _fog == null || _tileColors == null) return;

        int range = 8;
        bool changed = false;
        for (int dtx = -range; dtx <= range; dtx++)
        for (int dty = -range; dty <= range; dty++)
        {
            int tx = x + dtx, ty = y + dty;
            if (tx < 0 || tx >= _grid.Width || ty < 0 || ty >= _grid.Height) continue;

            for (int sx = 0; sx < PixelsPerTile; sx++)
            for (int sy = 0; sy < PixelsPerTile; sy++)
            {
                int px = tx * PixelsPerTile + sx;
                int py = ty * PixelsPerTile + sy;
                _texture.SetPixel(px, py, GetMinimapPixelSub(px, py));
            }
            changed = true;
        }

        if (changed) _texture.Apply();
    }

    /// <summary>
    /// Sample fog alpha at sub-pixel precision — each pixel gets its own
    /// smooth alpha value, eliminating grid artifacts at fog boundaries.
    /// </summary>
    private Color32 GetMinimapPixelSub(int px, int py)
    {
        int tx = Mathf.Min(px / PixelsPerTile, _grid.Width  - 1);
        int ty = Mathf.Min(py / PixelsPerTile, _grid.Height - 1);

        // Icons are always visible regardless of fog
        if (_icons.TryGetValue(new Vector2Int(tx, ty), out Color32 iconColor))
            return iconColor;

        Color32 tile = _tileColors[_grid.GetTileType(tx, ty)];

        if (_fog == null) return tile;

        // Map minimap pixel → fog mask pixel (scale if resolutions differ)
        int fogPPT = _fog.PixPerTile;
        int fpx = px * fogPPT / PixelsPerTile;
        int fpy = py * fogPPT / PixelsPerTile;

        float fogAlpha = _fog.GetFogAlphaAtPixel(fpx, fpy);
        if (fogAlpha >= 0.99f) return FogGray;
        if (fogAlpha <= 0.01f) return tile;

        return new Color32(
            (byte)Mathf.Lerp(tile.r, FogGray.r, fogAlpha),
            (byte)Mathf.Lerp(tile.g, FogGray.g, fogAlpha),
            (byte)Mathf.Lerp(tile.b, FogGray.b, fogAlpha),
            255);
    }

    // ── Tile color sampling ───────────────────────────────────────────────────

    private Dictionary<TileType, Color32> BuildTileColors()
    {
        var dict = new Dictionary<TileType, Color32>();
        foreach (TileType type in System.Enum.GetValues(typeof(TileType)))
            dict[type] = ToColor32(SampleSprite(_registry.Get(type), FallbackColor(type)));
        return dict;
    }

    private static Color SampleSprite(TileBase tileBase, Color fallback)
    {
        Sprite sprite = tileBase switch
        {
            Tile t     => t.sprite,
            RuleTile r => r.m_DefaultSprite,
            _          => null,
        };
        if (sprite == null) return fallback;

        var tex = sprite.texture;
        if (!tex.isReadable) return fallback;

        var    rect   = sprite.textureRect;
        Color[] pixels = tex.GetPixels((int)rect.x, (int)rect.y,
                                        (int)rect.width, (int)rect.height);
        Color sum  = Color.clear;
        int   count = 0;
        foreach (var p in pixels)
            if (p.a > 0.1f) { sum += p; count++; }

        return count > 0 ? sum / count : fallback;
    }

    private Color FallbackColor(TileType type) => type switch
    {
        TileType.Wall  => colorWall,
        TileType.Floor => colorFloor,
        TileType.Path  => colorPath,
        _              => colorAir,
    };

    private static Color32 ToColor32(Color c) =>
        new Color32(
            (byte)Mathf.RoundToInt(c.r * 255),
            (byte)Mathf.RoundToInt(c.g * 255),
            (byte)Mathf.RoundToInt(c.b * 255), 255);
}
