using Model;
using UnityEngine;
using UnityEngine.Tilemaps;
using VContainer;

public class PlayerView : MonoBehaviour
{
    [SerializeField] private SpriteRenderer spriteRenderer;
    [SerializeField] private int sortingOrder = 1;
    [SerializeField] private float moveDuration = 0.12f;
    [SerializeField] private float idleFps = 6f;
    [SerializeField] private float walkFps = 10f;

    private Player _model;
    private Tilemap _tilemap;

    private Vector3 _fromPosition;
    private Vector3 _toPosition;

    private float _moveTime;
    private float _currentDuration;
    private bool  _isMoving;

    private Sprite[] _idleFrames;
    private Sprite[] _walkFrames;
    private float    _animTimer;
    private bool     _wasMoving;

    // Ready for next move when animation is 90% done — eliminates the frame-boundary pause.
    public bool IsAnimating => _isMoving && _moveTime < _currentDuration * 0.9f;

    private void Awake()
    {
        _idleFrames = LoadSortedSprites("idle");
        _walkFrames = LoadSortedSprites("Walk");
    }

    private static Sprite[] LoadSortedSprites(string resourceName)
    {
        var sprites = Resources.LoadAll<Sprite>(resourceName);
        System.Array.Sort(sprites, (a, b) =>
            string.Compare(a.name, b.name, System.StringComparison.Ordinal));
        return sprites;
    }

    [Inject]
    public void Construct(Player model, Tilemap tilemap)
    {
        _model   = model;
        _tilemap = tilemap;

        spriteRenderer.sortingOrder = sortingOrder;
        
        _model.OnMoved += OnMoved;
        _model.OnTeleported += OnTeleported;
    }
    
    private void OnMoved(int x, int y)
    {
        _fromPosition = transform.position;
        _toPosition   = GridToWorld(x, y);

        // Scale duration by actual distance so diagonal moves (√2 tiles)
        // animate at the same pixel-per-second speed as cardinal moves.
        float tileSize = _tilemap.cellSize.x;
        float dist     = Vector3.Distance(_fromPosition, _toPosition);
        _currentDuration = moveDuration * (dist / tileSize);

        _moveTime = 0f;
        _isMoving = true;

        float dx = _toPosition.x - _fromPosition.x;
        if (dx > 0.01f)
            spriteRenderer.flipX = true;
        else if (dx < -0.01f)
            spriteRenderer.flipX = false;
    }

    private void OnTeleported(int x, int y)
    {
        transform.position = GridToWorld(x, y);
        _isMoving          = false;
    }

    private Vector3 GridToWorld(int x, int y)
    {
        var pos = _tilemap.CellToWorld(new Vector3Int(x, y, 0)) + _tilemap.cellSize * 0.5f;
        pos.z = -1f;
        return pos;
    }
    
 
    private void Update()
    {
        // ── Movement lerp ─────────────────────────────────────────────────────
        if (_isMoving)
        {
            _moveTime += Time.deltaTime;
            float t = Mathf.Clamp01(_moveTime / _currentDuration);
            transform.position = Vector3.Lerp(_fromPosition, _toPosition, t);
            if (t >= 1f)
            {
                transform.position = _toPosition;
                _isMoving          = false;
            }
        }

        // ── Frame animation ───────────────────────────────────────────────────
        if (_isMoving != _wasMoving)
        {
            _animTimer = 0f;
            _wasMoving = _isMoving;
        }
        _animTimer += Time.deltaTime;

        var frames = _isMoving ? _walkFrames : _idleFrames;
        var fps    = _isMoving ? walkFps     : idleFps;
        if (frames is { Length: > 0 })
            spriteRenderer.sprite = frames[(int)(_animTimer * fps) % frames.Length];
    }
    
    private void OnDestroy()
    {
        if (_model == null) return;
        _model.OnMoved      -= OnMoved;
        _model.OnTeleported -= OnTeleported;
    }
}