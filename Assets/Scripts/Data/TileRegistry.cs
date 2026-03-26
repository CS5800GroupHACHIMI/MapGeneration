using System;
using System.Collections.Generic;
using Data;
using UnityEngine;
using UnityEngine.Tilemaps;

[CreateAssetMenu(fileName = "TileRegistry", menuName = "Scriptable Objects/TileRegistry")]
public class TileRegistry : ScriptableObject
{
    [Serializable]
    private struct Entry
    {
        public TileType type;
        public TileBase data; // Tile / RuleTile / AnimatedTile
    }

    [SerializeField] private Entry[] entries;

    private Dictionary<TileType, TileBase> map;

    private void OnEnable()
    {
        map = new Dictionary<TileType, TileBase>();
        foreach (var e in entries)
            map[e.type] = e.data;
    }

    public TileBase Get(TileType type)
    {
        if (map.TryGetValue(type, out var data)) return data;
        Debug.LogError($"[TileRegistry] TileType.{type} not registered.");
        return null;
    }

    // For Special Tile
    public bool TryGetAs<T>(TileType type, out T tile) where T : TileBase
    {
        if (map.TryGetValue(type, out var data) && data is T cast)
        {
            tile = cast;
            return true;
        }
        tile = null;
        return false;
    }
}