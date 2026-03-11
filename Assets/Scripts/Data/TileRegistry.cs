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
        public Tile data;
    }
    
    [SerializeField] private Entry[] entries;
    
    private Dictionary<TileType, Tile> map;
    
    private void OnEnable()
    {
        map = new Dictionary<TileType, Tile>();
        foreach (var e in entries)
            map[e.type] = e.data;
    }
    
    public Tile Get(TileType type)
    {
        if (map.TryGetValue(type, out var data)) return data;
        Debug.LogError($"[TileRegistry] TileType.{type} not registered.");
        return null;
    }
}