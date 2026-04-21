using Data;
using Model;
using UnityEngine;
using UnityEngine.Tilemaps;
using VContainer;

/// <summary>
/// Monster that wanders randomly within wanderRadius of its spawn point.
/// Alternates between idle pauses and walking to a random target tile.
/// Deals 8 DPS when the player is within 1 tile (Chebyshev) in the same chunk.
/// </summary>
public class MonsterEntity : MonoBehaviour
{
    private Player   _player;
    private Tilemap  _tilemap;
    private MapGrid  _grid;
    private Animator       _animator;
    private SpriteRenderer _sr;

    private int  _x, _y;
    private int  _spawnX, _spawnY;

    public int  TileX    => _x;
    public int  TileY    => _y;
    public bool IsActive { get; private set; }

    private float _damageAccumulator;
    private const float DamagePerSecond = 8f;
    private const int   DamageRange     = 1;
    private const int   ChunkW          = 10;
    private const int   ChunkH          = 8;

    [SerializeField] private RuntimeAnimatorController[] variants;

    // ── Inspector ─────────────────────────────────────────────────────────────
    [SerializeField] private bool  enableWander = true;
    [SerializeField] private float idleTimeMin  = 1.0f;
    [SerializeField] private float idleTimeMax  = 3.0f;
    [SerializeField] private int   wanderRadius = 4;
    [SerializeField] private float stepDuration = 0.18f;

    // ── Walk state ────────────────────────────────────────────────────────────
    private enum State { Idle, Moving }
    private State _state = State.Idle;
    private float _idleTimer;
    private int   _targetX, _targetY;

    // lerp between tiles
    private Vector3 _fromPos, _toPos;
    private float   _moveTimer;
    private bool    _isLerping;

    private static readonly int WalkStateHash = Animator.StringToHash("Walk");

    private void Awake()
    {
        _animator = GetComponent<Animator>();
        _sr       = GetComponent<SpriteRenderer>();

        if (variants.Length > 0)
        {
            var v = variants[Random.Range(0, variants.Length)];
            if (v != null) _animator.runtimeAnimatorController = v;
        }
        SetWalking(false);
    }

    [Inject]
    public void Construct(Player player, Tilemap tilemap, MapGrid grid)
    {
        _player  = player;
        _tilemap = tilemap;
        _grid    = grid;
    }

    public void Place(int x, int y, int chunkX, int chunkY)
    {
        _x = _spawnX = x;
        _y = _spawnY = y;
        IsActive  = true;

        var world = _tilemap.CellToWorld(new Vector3Int(x, y, 0)) + _tilemap.cellSize * 0.5f;
        world.z = -0.4f;
        transform.position = world;

        _idleTimer = Random.Range(idleTimeMin, idleTimeMax);
        _state     = State.Idle;
    }

    public void Remove()
    {
        IsActive = false;
        Destroy(gameObject);
    }

    private void Update()
    {
        if (_player == null || !IsActive) return;

        HandleDamage();
        if (!_player.IsDead && enableWander) HandleMovement();
    }

    // ── Damage ────────────────────────────────────────────────────────────────

    private void HandleDamage()
    {
        if (_player.IsDead) { _damageAccumulator = 0f; return; }

        int mcx = _x / ChunkW, mcy = _y / ChunkH;
        int pcx = _player.X / ChunkW, pcy = _player.Y / ChunkH;
        if (pcx != mcx || pcy != mcy) { _damageAccumulator = 0f; return; }

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

    // ── Movement state machine ────────────────────────────────────────────────

    private void HandleMovement()
    {
        if (_grid == null) return;

        // finish current lerp first
        if (_isLerping)
        {
            _moveTimer += Time.deltaTime;
            float t = Mathf.Clamp01(_moveTimer / stepDuration);
            transform.position = Vector3.Lerp(_fromPos, _toPos, t);
            if (t >= 1f) { transform.position = _toPos; _isLerping = false; }
            return;
        }

        switch (_state)
        {
            case State.Idle:
                _idleTimer -= Time.deltaTime;
                if (_idleTimer <= 0f)
                {
                    if (TryPickTarget())
                    {
                        _state = State.Moving;
                        SetWalking(true);
                    }
                    else
                    {
                        _idleTimer = Random.Range(idleTimeMin, idleTimeMax);
                    }
                }
                break;

            case State.Moving:
                if (_x == _targetX && _y == _targetY)
                {
                    _state     = State.Idle;
                    _idleTimer = Random.Range(idleTimeMin, idleTimeMax);
                    SetWalking(false);
                    break;
                }
                StepTowardTarget();
                break;
        }
    }

    private void StepTowardTarget()
    {
        int stepX = _targetX > _x ? 1 : _targetX < _x ? -1 : 0;
        int stepY = _targetY > _y ? 1 : _targetY < _y ? -1 : 0;

        // try horizontal first, fall back to vertical
        if (stepX != 0 && TryStep(_x + stepX, _y)) return;
        if (stepY != 0 && TryStep(_x, _y + stepY)) return;

        // blocked — give up
        _state     = State.Idle;
        _idleTimer = Random.Range(idleTimeMin, idleTimeMax);
        SetWalking(false);
    }

    private bool TryStep(int nx, int ny)
    {
        if (!_grid.InBounds(nx, ny)) return false;
        if (_grid.GetTileType(nx, ny) == TileType.Wall) return false;

        if (_sr != null)
        {
            if      (nx > _x) _sr.flipX = false;
            else if (nx < _x) _sr.flipX = true;
        }

        _fromPos  = transform.position;
        _toPos    = _tilemap.CellToWorld(new Vector3Int(nx, ny, 0)) + _tilemap.cellSize * 0.5f;
        _toPos.z  = -0.4f;
        _x        = nx;
        _y        = ny;
        _moveTimer = 0f;
        _isLerping = true;
        return true;
    }

    private bool TryPickTarget()
    {
        for (int i = 0; i < 10; i++)
        {
            int tx = _spawnX + Random.Range(-wanderRadius, wanderRadius + 1);
            int ty = _spawnY + Random.Range(-wanderRadius, wanderRadius + 1);
            if (!_grid.InBounds(tx, ty)) continue;
            if (_grid.GetTileType(tx, ty) == TileType.Wall) continue;
            if (tx == _x && ty == _y) continue;
            _targetX = tx;
            _targetY = ty;
            return true;
        }
        return false;
    }

    private void SetWalking(bool walking)
    {
        if (_animator == null) return;
        if (walking)
        {
            _animator.speed = 1f;
        }
        else
        {
            _animator.speed = 0f;
            _animator.Play(WalkStateHash, 0, 0f);
            _animator.Update(0.001f); // non-zero required to force Play to take effect
        }
    }
}
