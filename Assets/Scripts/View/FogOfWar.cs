using Model;
using UnityEngine;
using UnityEngine.Tilemaps;
using VContainer;

/// <summary>
/// Smooth fog of war using a single Texture2D + SpriteRenderer.
/// No grid artifacts — bilinear filtering + smooth circle painting.
/// </summary>
public class FogOfWar : MonoBehaviour
{
    [Header("Settings")]
    [SerializeField] private int   revealRadius  = 5;
    [SerializeField] private int   softEdge      = 3;
    [SerializeField] private Color fogColor      = new Color(0.08f, 0.08f, 0.12f, 0.95f);
    [SerializeField] private int   fogSortOrder  = 10;
    [SerializeField] private int   pixelsPerTile = 4;  // higher = smoother circles

    private MapGrid  _grid;
    private Player   _player;
    private Tilemap  _parentTilemap;

    private SpriteRenderer _fogRenderer;
    private Texture2D      _fogTex;
    private float[,]       _mask;       // 0 = clear, 1 = fogged
    private bool[,]        _revealed;   // for minimap queries
    private int            _texW, _texH;
    private bool           _dirty;

    public bool IsRevealed(int x, int y)
    {
        if (x < 0 || x >= _grid.Width || y < 0 || y >= _grid.Height) return false;
        return _revealed != null && _revealed[x, y];
    }

    /// <summary>Returns fog opacity for a tile: 0 = fully clear, 1 = fully fogged.</summary>
    public float GetFogAlpha(int tileX, int tileY)
    {
        if (_mask == null) return 1f;
        int px = Mathf.Clamp(tileX * pixelsPerTile + pixelsPerTile / 2, 0, _texW - 1);
        int py = Mathf.Clamp(tileY * pixelsPerTile + pixelsPerTile / 2, 0, _texH - 1);
        return _mask[px, py];
    }

    /// <summary>Returns fog opacity at sub-pixel precision.</summary>
    public float GetFogAlphaAtPixel(int px, int py)
    {
        if (_mask == null) return 1f;
        if (px < 0 || px >= _texW || py < 0 || py >= _texH) return 1f;
        return _mask[px, py];
    }

    public int PixPerTile => pixelsPerTile;

    /// <summary>Remove all fog instantly (call before regenerating map).</summary>
    public void Clear()
    {
        if (_fogRenderer != null)
            _fogRenderer.enabled = false;
    }

    [Inject]
    public void Construct(MapGrid grid, Player player, Tilemap parentTilemap)
    {
        _grid          = grid;
        _player        = player;
        _parentTilemap = parentTilemap;
    }

    public void Initialize()
    {
        CreateFogLayer();

        _revealed = new bool[_grid.Width, _grid.Height];

        // Fill mask to fully fogged
        for (int x = 0; x < _texW; x++)
        for (int y = 0; y < _texH; y++)
            _mask[x, y] = 1f;

        UploadTexture();

        _player.OnMoved      -= OnPlayerMoved;
        _player.OnTeleported -= OnPlayerMoved;
        _player.OnMoved      += OnPlayerMoved;
        _player.OnTeleported += OnPlayerMoved;

        Reveal(_player.X, _player.Y);
    }

    private void OnPlayerMoved(int x, int y)
    {
        Reveal(x, y);
    }

    private void LateUpdate()
    {
        if (_dirty)
        {
            UploadTexture();
            _dirty = false;
        }
    }

    // ─── Reveal ──────────────────────────────────────────────────────────────

    private void Reveal(int tileX, int tileY)
    {
        if (_mask == null) return;

        // Work in pixel coordinates
        float cx = (tileX + 0.5f) * pixelsPerTile;
        float cy = (tileY + 0.5f) * pixelsPerTile;
        float hardR = revealRadius * pixelsPerTile;
        float softR = softEdge * pixelsPerTile;
        float outerR = hardR + softR;
        int   range  = Mathf.CeilToInt(outerR);

        for (int dx = -range; dx <= range; dx++)
        for (int dy = -range; dy <= range; dy++)
        {
            int px = Mathf.RoundToInt(cx) + dx;
            int py = Mathf.RoundToInt(cy) + dy;
            if (px < 0 || px >= _texW || py < 0 || py >= _texH) continue;

            float dist = Mathf.Sqrt(dx * dx + dy * dy);
            if (dist > outerR) continue;

            float targetAlpha;
            if (dist <= hardR)
                targetAlpha = 0f;
            else
                targetAlpha = Mathf.SmoothStep(0f, 1f, (dist - hardR) / softR);

            if (targetAlpha >= _mask[px, py]) continue;
            _mask[px, py] = targetAlpha;
        }

        // Update tile-level revealed array for minimap
        int r = revealRadius;
        for (int ddx = -r; ddx <= r; ddx++)
        for (int ddy = -r; ddy <= r; ddy++)
        {
            if (ddx * ddx + ddy * ddy > r * r) continue;
            int tx = tileX + ddx, ty = tileY + ddy;
            if (tx >= 0 && tx < _grid.Width && ty >= 0 && ty < _grid.Height)
                _revealed[tx, ty] = true;
        }

        _dirty = true;
    }

    // ─── Texture ─────────────────────────────────────────────────────────────

    private void UploadTexture()
    {
        var pixels = new Color[_texW * _texH];
        for (int y = 0; y < _texH; y++)
        for (int x = 0; x < _texW; x++)
        {
            float a = _mask[x, y];
            pixels[y * _texW + x] = new Color(fogColor.r, fogColor.g, fogColor.b, a * fogColor.a);
        }

        _fogTex.SetPixels(pixels);
        _fogTex.Apply();
    }

    // ─── Setup ───────────────────────────────────────────────────────────────

    private void CreateFogLayer()
    {
        // Clean up previous
        if (_fogRenderer != null)
            Destroy(_fogRenderer.gameObject);

        _texW = _grid.Width  * pixelsPerTile;
        _texH = _grid.Height * pixelsPerTile;
        _mask = new float[_texW, _texH];

        _fogTex = new Texture2D(_texW, _texH, TextureFormat.RGBA32, false)
        {
            filterMode = FilterMode.Bilinear,
            wrapMode   = TextureWrapMode.Clamp
        };

        // Sprite covering the entire map
        Vector3 cellSize = _parentTilemap.cellSize;
        float worldW = _grid.Width  * cellSize.x;
        float worldH = _grid.Height * cellSize.y;
        float ppu    = _texW / worldW;

        var sprite = Sprite.Create(
            _fogTex,
            new Rect(0, 0, _texW, _texH),
            new Vector2(0, 0),
            ppu);

        var fogGO = new GameObject("FogOfWar");
        var grid  = _parentTilemap.layoutGrid;
        fogGO.transform.SetParent(grid.transform, false);
        fogGO.transform.localPosition = _parentTilemap.CellToLocal(Vector3Int.zero);

        _fogRenderer = fogGO.AddComponent<SpriteRenderer>();
        _fogRenderer.sprite       = sprite;
        _fogRenderer.sortingOrder = fogSortOrder;
        _fogRenderer.material     = new Material(Shader.Find("Sprites/Default"));
    }

    private void OnDestroy()
    {
        if (_player != null)
        {
            _player.OnMoved      -= OnPlayerMoved;
            _player.OnTeleported -= OnPlayerMoved;
        }
        if (_fogTex != null) Destroy(_fogTex);
    }
}
