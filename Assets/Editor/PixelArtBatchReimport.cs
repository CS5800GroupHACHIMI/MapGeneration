using UnityEditor;
using UnityEngine;

public class PixelArtBatchReimport
{
    [MenuItem("Tools/Pixel Art/Reimport All Pixel Art Textures")]
    static void ReimportAll()
    {
        string[] guids = AssetDatabase.FindAssets(
            "t:Texture2D",
            new[] { "Assets/Resources/Desert Shooter Bundle (Sprites Only)/Tiles/Tiles" } 
        );

        int count = 0;
        foreach (var guid in guids)
        {
            string path = AssetDatabase.GUIDToAssetPath(guid);
            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);
            count++;
        }

        AssetDatabase.Refresh();
        Debug.Log($"[PixelArtImport] Reimported {count} textures.");
    }
}