using Model;
using UnityEngine;
using UnityEngine.Tilemaps;
using VContainer;

public class PlayerView : MonoBehaviour
{
    [SerializeField] private SpriteRenderer spriteRenderer;
    [SerializeField] private int sortingOrder = 1;
    // [SerializeField] private float moveSpeed = 10f;
    [SerializeField] private float moveDuration = 0.12f;
    
    private Player _model;
    private Tilemap _tilemap;
    
    private Vector3 _fromPosition;
    private Vector3 _toPosition;
    
    private float   _moveTime;
    private bool    _isMoving;
    
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
        // If still animating, snap to the previous tile's exact position
        // so the next animation always starts from a clean grid-aligned point
        _fromPosition = _isMoving ? _toPosition : transform.position;
        _toPosition   = GridToWorld(x, y);
        _moveTime     = 0f;
        _isMoving     = true;

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
        if (!_isMoving) return;

        _moveTime += Time.deltaTime;
        float t    = Mathf.Clamp01(_moveTime / moveDuration);

        transform.position = Vector3.Lerp(_fromPosition, _toPosition, Mathf.SmoothStep(0f, 1f, t));

        if (t >= 1f)
        {
            transform.position = _toPosition;
            _isMoving          = false;
        }
    }
    
    private void OnDestroy()
    {
        if (_model == null) return;
        _model.OnMoved      -= OnMoved;
        _model.OnTeleported -= OnTeleported;
    }
}