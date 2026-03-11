using Data;
using UnityEngine;
using UnityEngine.Serialization;
using UnityEngine.Tilemaps;

[CreateAssetMenu(fileName = "MapConfig", menuName = "Scriptable Objects/MapConfig")]
public class MapConfig : ScriptableObject
{
    public int width = 32;
    public int height = 32;
    
    public int seed = 0;
    public bool randomSeed = true;
    
    public TileType defaultMapTileData;
}