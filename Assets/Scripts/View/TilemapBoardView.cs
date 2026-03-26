using System;
using Model;
using UnityEngine;
using UnityEngine.Tilemaps;
using VContainer;

public class TilemapBoardView : MonoBehaviour
{
    [SerializeField]private Tilemap tileMap;

    private MapGrid _grid;

    [Inject]
    public void Construct(MapGrid grid)
    {
        _grid = grid;
    }

    public void Initialize()
    {
        tileMap.ClearAllTiles();
        _grid.OnTileChanged += OnTileChanged;
    }

    private void OnTileChanged(int x, int y, TileBase data)
    {
        tileMap.SetTile(new Vector3Int(x, y, 0), data);
    }

    private void OnDestroy()
    {
        if (_grid != null) _grid.OnTileChanged -= OnTileChanged;
    }
}