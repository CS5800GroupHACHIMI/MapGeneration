using System.Collections;
using System.Collections.Generic;
using Data;
using Generators;
using Model;
using Unity.Cinemachine;
using UnityEngine;
using VContainer;

public class MapGeneratorRunner : MonoBehaviour
{
    [Header("Animation")]
    [SerializeField] private bool  animateGeneration;
    [SerializeField] private float roomDelay        = 0.3f;
    [SerializeField] private float zoomBackDuration = 1f;

    private MapConfig        _config;
    private MapGrid          _grid;
    private TilemapBoardView _boardView;
    private MinimapView      _minimap;
    private IMapGenerator    _generator;
    private Player           _player;
    private MapTraversal     _traversal;
    private FogOfWar         _fog;
    private ExitDoor         _exitDoor;
    private RoomManager      _roomManager;

    private int _level = 1;

    [Inject]
    public void Construct(
        MapConfig        config,
        MapGrid          grid,
        TilemapBoardView boardView,
        MinimapView      minimap,
        IMapGenerator    generator,
        Player           player,
        MapTraversal     traversal,
        FogOfWar         fog,
        ExitDoor         exitDoor,
        RoomManager     roomManager)
    {
        _config      = config;
        _grid        = grid;
        _boardView   = boardView;
        _minimap     = minimap;
        _generator   = generator;
        _player      = player;
        _traversal   = traversal;
        _fog         = fog;
        _exitDoor    = exitDoor;
        _roomManager = roomManager;

        _exitDoor.OnPlayerReachedExit += NextLevel;
        _player.OnDied += OnPlayerDied;
    }

    private void OnPlayerDied()
    {
        // Restart current level (same seed)
        _config.randomSeed = false;
        _traversal.Stop();
        _player.ResetHealth();
        Run();
        _config.randomSeed = true;
    }

    private void Start() => Run();

    public void Run()
    {
        _fog.Clear();
        _roomManager.Clear();
        _minimap.ClearIcons();

        if (_config.randomSeed)
            _config.seed = UnityEngine.Random.Range(int.MinValue, int.MaxValue);

        if (animateGeneration)
            RunAnimated();
        else
            RunImmediate();
    }

    private void NextLevel()
    {
        _level++;
        _traversal.Stop();
        _player.ResetHealth();
        Run();
    }

    private void RunImmediate()
    {
        _grid.Reset(_config.defaultMapTileData);
        _boardView.Initialize();
        _generator.Generate(_grid, _config);

        var start = _generator.GetStartPosition(_grid);
        _player.TeleportTo(start.x, start.y);

        _fog.Initialize();
        _exitDoor.PlaceAtFarthestRoom(start);
        _minimap.RegisterIcon(_exitDoor.ExitX, _exitDoor.ExitY, new Color32(50, 200, 255, 255));
        _roomManager.PlaceEntities(start, _exitDoor.ExitX, _exitDoor.ExitY);
        _minimap.Rebuild();
    }

    // ─── Animated Generation ─────────────────────────────────────────────────

    private void RunAnimated()
    {
        // Step 1-2: Fill background with Wall (always visible)
        _boardView.Initialize();
        _grid.Reset(TileType.Wall);

        // Step 3: Record generation (events suppressed → tilemap stays as Wall)
        _grid.BeginRecording();
        _generator.Generate(_grid, _config);
        var changes = _grid.EndRecording();

        // Step 4-5: Filter non-Wall entries, group by adjacency
        var rooms = GroupByAdjacency(changes);

        // Step 6: Reset model back to Wall (sync with tilemap)
        _grid.SilentReset(TileType.Wall);

        // Step 7: Animate
        StartCoroutine(AnimateRooms(rooms));
    }

    /// <summary>
    /// Keeps only non-Wall entries, groups consecutive tiles by 8-connectivity.
    /// Small groups (doors, short corridors) are merged into the next group.
    /// </summary>
    private static List<List<(int x, int y, TileType type)>> GroupByAdjacency(
        List<(int x, int y, TileType type)> changes)
    {
        var groups  = new List<List<(int x, int y, TileType type)>>();
        var current = new List<(int x, int y, TileType type)>();
        var curSet  = new HashSet<(int, int)>();

        foreach (var c in changes)
        {
            if (c.type == TileType.Wall) continue;

            bool adjacent = false;
            if (curSet.Count > 0)
            {
                for (int dx = -1; dx <= 1 && !adjacent; dx++)
                for (int dy = -1; dy <= 1 && !adjacent; dy++)
                    if (curSet.Contains((c.x + dx, c.y + dy)))
                        adjacent = true;
            }

            if (!adjacent && current.Count > 0)
            {
                groups.Add(current);
                current = new List<(int x, int y, TileType type)>();
                curSet.Clear();
            }

            current.Add(c);
            curSet.Add((c.x, c.y));
        }
        if (current.Count > 0)
            groups.Add(current);

        // Merge small groups (doors/corridors) into the next room
        for (int i = groups.Count - 2; i >= 0; i--)
        {
            if (groups[i].Count < 8)
            {
                groups[i + 1].InsertRange(0, groups[i]);
                groups.RemoveAt(i);
            }
        }

        return groups;
    }

    private IEnumerator AnimateRooms(List<List<(int x, int y, TileType type)>> rooms)
    {
        var cam = Camera.main;
        if (cam == null) yield break;

        // ── Hide minimap & disable Cinemachine ──
        _minimap.gameObject.SetActive(false);
        var brain = cam.GetComponent<CinemachineBrain>();
        if (brain != null) brain.enabled = false;

        float   originalSize = cam.orthographicSize;
        Vector3 originalPos  = cam.transform.position;

        // ── Overview camera ──
        float mapCenterX   = _grid.Width  * 0.5f;
        float mapCenterY   = _grid.Height * 0.5f;
        float overviewSize = Mathf.Max(_grid.Width / cam.aspect, _grid.Height) * 0.55f;

        cam.transform.position = new Vector3(mapCenterX, mapCenterY, originalPos.z);
        cam.orthographicSize   = overviewSize;

        // ── Carve rooms one by one using recorded types ──
        foreach (var room in rooms)
        {
            foreach (var (x, y, type) in room)
                _grid.Set(x, y, type);
            _boardView.RefreshAll();
            yield return new WaitForSeconds(roomDelay);
        }

        // ── Spawn player ──
        var start = _generator.GetStartPosition(_grid);
        _player.TeleportTo(start.x, start.y);
        _fog.Initialize();
        _exitDoor.PlaceAtFarthestRoom(start);
        _minimap.RegisterIcon(_exitDoor.ExitX, _exitDoor.ExitY, new Color32(50, 200, 255, 255));
        _roomManager.PlaceEntities(start, _exitDoor.ExitX, _exitDoor.ExitY);
        _minimap.Rebuild();

        // ── Smooth zoom back to player ──
        Vector3 targetPos = new Vector3(start.x + 0.5f, start.y + 0.5f, originalPos.z);
        float   elapsed   = 0f;
        Vector3 fromPos   = cam.transform.position;
        float   fromSize  = cam.orthographicSize;

        while (elapsed < zoomBackDuration)
        {
            elapsed += Time.deltaTime;
            float t = Mathf.SmoothStep(0f, 1f, elapsed / zoomBackDuration);
            cam.transform.position = Vector3.Lerp(fromPos, targetPos, t);
            cam.orthographicSize   = Mathf.Lerp(fromSize, originalSize, t);
            yield return null;
        }

        cam.transform.position = targetPos;
        cam.orthographicSize   = originalSize;

        // ── Re-enable Cinemachine & minimap ──
        if (brain != null) brain.enabled = true;
        _minimap.gameObject.SetActive(true);
        _minimap.Rebuild();
    }

    // ─── Traversal HUD ──────────────────────────────────────────────────────

    private GUIStyle _boxStyle;
    private GUIStyle _labelStyle;

    private void OnGUI()
    {
        if (_traversal == null) return;

        if (_boxStyle == null)
        {
            _boxStyle = new GUIStyle(GUI.skin.box);
            _boxStyle.normal.background = MakeTex(1, 1, new Color(0f, 0f, 0f, 0.6f));

            _labelStyle = new GUIStyle(GUI.skin.label);
            _labelStyle.fontSize  = 18;
            _labelStyle.fontStyle = FontStyle.Bold;
            _labelStyle.normal.textColor = Color.white;
            _labelStyle.alignment = TextAnchor.MiddleLeft;
            _labelStyle.padding   = new RectOffset(10, 10, 5, 5);
        }

        float w = 280, h = 60;
        var rect = new Rect(Screen.width - w - 10, 10, w, h);

        GUI.Box(rect, GUIContent.none, _boxStyle);

        string algo   = _traversal.Algorithm.ToString();
        string status = _traversal.IsAutoWalking ? "  Running..." : "";
        GUI.Label(new Rect(rect.x, rect.y + 2, w, 28),
            $"Level {_level}  |  {algo}{status}", _labelStyle);

        _labelStyle.fontSize  = 13;
        _labelStyle.fontStyle = FontStyle.Normal;
        _labelStyle.normal.textColor = new Color(0.8f, 0.8f, 0.8f);
        GUI.Label(new Rect(rect.x, rect.y + 28, w, 24),
            "[T] Start / Stop    [Y] Switch", _labelStyle);

        // Reset style for next frame
        _labelStyle.fontSize  = 18;
        _labelStyle.fontStyle = FontStyle.Bold;
        _labelStyle.normal.textColor = Color.white;

        // ── HP Bar (bottom-left) ─────────────────────────────────────────────
        if (_player != null)
        {
            float barW = 200, barH = 20, barX = 10, barY = Screen.height - 35;
            float hpRatio = (float)_player.Health / _player.MaxHealth;

            // Background
            GUI.DrawTexture(new Rect(barX, barY, barW, barH), Texture2D.whiteTexture,
                ScaleMode.StretchToFill, false, 0, new Color(0.2f, 0.2f, 0.2f, 0.8f), 0, 0);

            // HP fill
            Color hpColor = hpRatio > 0.5f ? Color.green :
                            hpRatio > 0.25f ? Color.yellow : Color.red;
            GUI.DrawTexture(new Rect(barX, barY, barW * hpRatio, barH), Texture2D.whiteTexture,
                ScaleMode.StretchToFill, false, 0, hpColor, 0, 0);

            // Text
            _labelStyle.fontSize = 14;
            _labelStyle.alignment = TextAnchor.MiddleCenter;
            _labelStyle.normal.textColor = Color.white;
            GUI.Label(new Rect(barX, barY, barW, barH),
                $"HP {_player.Health}/{_player.MaxHealth}", _labelStyle);

            // Reset
            _labelStyle.fontSize = 18;
            _labelStyle.alignment = TextAnchor.MiddleLeft;

            // ── Key indicator ────────────────────────────────────────────────────
            string keyText  = _player.HasKey ? "KEY  [READY]" : "KEY  [NEEDED]";
            Color  keyColor = _player.HasKey ? Color.green : new Color(1f, 0.6f, 0.2f);
            _labelStyle.fontSize  = 13;
            _labelStyle.alignment = TextAnchor.MiddleLeft;
            _labelStyle.normal.textColor = keyColor;
            GUI.Label(new Rect(10, Screen.height - 58, 200, 22), keyText, _labelStyle);
            _labelStyle.fontSize  = 18;
            _labelStyle.normal.textColor = Color.white;
        }
    }

    private static Texture2D MakeTex(int w, int h, Color col)
    {
        var pixels = new Color[w * h];
        for (int i = 0; i < pixels.Length; i++) pixels[i] = col;
        var tex = new Texture2D(w, h);
        tex.SetPixels(pixels);
        tex.Apply();
        return tex;
    }
}
