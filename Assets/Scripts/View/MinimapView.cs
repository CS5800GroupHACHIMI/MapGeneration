using System.Collections.Generic;
using Data;
using Model;
using UnityEngine;
using UnityEngine.Tilemaps;
using UnityEngine.UI;
using VContainer;

/// <summary>
/// Renders the map as a Texture2D (1 pixel = 1 tile) displayed on a RawImage.
/// Player marker and camera viewport are drawn by a custom shader using screen-space
/// derivatives, so they remain exactly _lineWidth screen pixels wide at any scale.
/// The base texture is uploaded once after Rebuild(); per-frame work is only updating
/// three shader uniforms.
/// </summary>
public class MinimapView : MonoBehaviour
{
    [Header("UI References")]
    [SerializeField] private GameObject panel;
    [SerializeField] private RawImage   mapImage;
    [SerializeField] private Material   minimapMaterial;

    [Header("Overlay")]
    [SerializeField] private Color colorPlayer     = new Color(1f, 0.2f, 0.2f);
    [SerializeField] private Color colorCamera     = new Color(1f, 1f,  0.3f);
    [SerializeField] private float lineWidthPx     = 3f;
    [SerializeField] private float crossArmTiles   = 1.5f;   // arm half-length in tile units

    [Header("Fallback Tile Colors (used when sprite is not CPU-readable)")]
    [SerializeField] private Color colorAir   = new Color(0.05f, 0.05f, 0.05f);
    [SerializeField] private Color colorWall  = new Color(0.25f, 0.25f, 0.25f);
    [SerializeField] private Color colorFloor = new Color(0.75f, 0.75f, 0.75f);
    [SerializeField] private Color colorPath  = new Color(0.80f, 0.70f, 0.35f);

    // Shader property IDs (cached to avoid string lookups per frame)
    private static readonly int PropPlayerPos   = Shader.PropertyToID("_PlayerPos");
    private static readonly int PropCrossArm    = Shader.PropertyToID("_CrossArm");
    private static readonly int PropPlayerColor = Shader.PropertyToID("_PlayerColor");
    private static readonly int PropCamRect     = Shader.PropertyToID("_CamRect");
    private static readonly int PropCamColor    = Shader.PropertyToID("_CamColor");
    private static readonly int PropHalfLineUV  = Shader.PropertyToID("_HalfLineUV");

    private MapGrid      _grid;
    private Player       _player;
    private PlayerInput  _input;
    private Tilemap      _tilemap;
    private TileRegistry _registry;

    private Texture2D                     _texture;
    private Material                      _matInstance;
    private Dictionary<TileType, Color32> _tileColors;
    private RectTransform                 _imageRT;
    private Canvas                        _rootCanvas;

    // ── DI ───────────────────────────────────────────────────────────────────

    [Inject]
    public void Construct(MapGrid grid, Player player, PlayerInput input,
                          Tilemap tilemap, TileRegistry registry)
    {
        _grid     = grid;
        _player   = player;
        _input    = input;
        _tilemap  = tilemap;
        _registry = registry;

        _input.UI.Enable();
        _input.UI.ToggleMinimap.performed += OnToggle;

        _player.OnMoved      += OnPlayerMoved;
        _player.OnTeleported += OnPlayerMoved;
    }

    // ── Public API ────────────────────────────────────────────────────────────

    public void Rebuild()
    {
        if (_texture != null) Destroy(_texture);

        _texture = new Texture2D(_grid.Width, _grid.Height, TextureFormat.RGBA32, false)
        {
            filterMode = FilterMode.Point,
            wrapMode   = TextureWrapMode.Clamp,
        };

        // Build and upload base map pixels once
        _tileColors = BuildTileColors();
        var pixels  = new Color32[_grid.Width * _grid.Height];
        for (int y = 0; y < _grid.Height; y++)
        for (int x = 0; x < _grid.Width;  x++)
            pixels[y * _grid.Width + x] = _tileColors[_grid.GetTileType(x, y)];

        _texture.SetPixels32(pixels);
        _texture.Apply();

        // Create a per-instance material so multiple minimaps don't share state
        if (_matInstance != null) Destroy(_matInstance);
        _matInstance = new Material(minimapMaterial);

        mapImage.material = _matInstance;
        mapImage.texture  = _texture;

        // Push static color properties once (colors don't change per frame)
        _matInstance.SetColor(PropPlayerColor, colorPlayer);
        _matInstance.SetColor(PropCamColor,    colorCamera);

        // Cache layout references for per-frame pixel-size calculation
        _imageRT    = mapImage.GetComponent<RectTransform>();
        _rootCanvas = mapImage.canvas.rootCanvas;

        var fitter = mapImage.GetComponent<AspectRatioFitter>();
        if (fitter != null)
        {
            fitter.aspectRatio = (float)_grid.Width / _grid.Height;
            fitter.aspectMode  = AspectRatioFitter.AspectMode.FitInParent;
        }

        panel.SetActive(false);
    }

    // ── Unity ─────────────────────────────────────────────────────────────────

    private void Update()
    {
        if (!panel.activeSelf || _matInstance == null) return;

        // ── Half line width in UV space ───────────────────────────────────────
        // RectTransform.rect gives canvas-unit size; multiply by rootCanvas.scaleFactor
        // to get actual screen pixels, then invert to get UV-per-pixel.
        float displayW = _imageRT.rect.width  * _rootCanvas.scaleFactor;
        float displayH = _imageRT.rect.height * _rootCanvas.scaleFactor;
        float hlx = lineWidthPx * 0.5f / Mathf.Max(displayW, 1f);
        float hly = lineWidthPx * 0.5f / Mathf.Max(displayH, 1f);
        _matInstance.SetVector(PropHalfLineUV, new Vector4(hlx, hly, 0, 0));

        // Cross arm length in UV space (tiles → UV)
        _matInstance.SetVector(PropCrossArm, new Vector4(
            crossArmTiles / _grid.Width,
            crossArmTiles / _grid.Height, 0, 0));

        // ── Player position in UV space ───────────────────────────────────────
        float px = (_player.X + 0.5f) / _grid.Width;
        float py = (_player.Y + 0.5f) / _grid.Height;
        _matInstance.SetVector(PropPlayerPos, new Vector4(px, py, 0, 0));

        // ── Camera viewport in UV space ───────────────────────────────────────
        var cam = Camera.main;
        if (cam != null)
        {
            float halfH = cam.orthographicSize;
            float halfW = halfH * cam.aspect;
            Vector3 pos = cam.transform.position;

            Vector3Int bl = _tilemap.WorldToCell(new Vector3(pos.x - halfW, pos.y - halfH));
            Vector3Int tr = _tilemap.WorldToCell(new Vector3(pos.x + halfW, pos.y + halfH));

            float nx0 = (float) bl.x       / _grid.Width;
            float ny0 = (float) bl.y       / _grid.Height;
            float nx1 = (float)(tr.x + 1)  / _grid.Width;
            float ny1 = (float)(tr.y + 1)  / _grid.Height;

            _matInstance.SetVector(PropCamRect, new Vector4(nx0, ny0, nx1, ny1));
        }
    }

    private void OnDestroy()
    {
        if (_input  != null) _input.UI.ToggleMinimap.performed -= OnToggle;
        if (_player != null)
        {
            _player.OnMoved      -= OnPlayerMoved;
            _player.OnTeleported -= OnPlayerMoved;
        }
        if (_texture     != null) Destroy(_texture);
        if (_matInstance != null) Destroy(_matInstance);
    }

    // ── Private ───────────────────────────────────────────────────────────────

    private void OnToggle(UnityEngine.InputSystem.InputAction.CallbackContext _)
    {
        panel.SetActive(!panel.activeSelf);
    }

    private void OnPlayerMoved(int x, int y) { } // shader reads _player directly in Update

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
