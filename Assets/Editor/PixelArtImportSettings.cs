using UnityEditor;
using UnityEngine;

public class PixelArtImportSettings : AssetPostprocessor
{
    // ── CONFIG ──────────────────────────────────────────────
    static readonly string[] PIXEL_ART_PATHS =
    {
        "Assets/Resources/Desert Shooter Bundle (Sprites Only)/Tiles/Tiles",
        "Assets/Resources/Desert Shooter Bundle (Sprites Only)/Tiles/Tilemap",
        "Assets/Resources/"
    };

    const int    PPU           = 16;   // Pixels Per Unit
    const bool   GENERATE_MIPS = false;
    // ────────────────────────────────────────────────────────

    void OnPreprocessTexture()
    {
        if (!IsPixelArtPath(assetPath)) return;

        var importer = (TextureImporter)assetImporter;

        importer.textureType         = TextureImporterType.Sprite;
        importer.spritePixelsPerUnit = PPU;
        importer.filterMode          = FilterMode.Point;
        importer.mipmapEnabled       = GENERATE_MIPS;
        importer.isReadable          = true;

        var settings = importer.GetDefaultPlatformTextureSettings();
        settings.format     = TextureImporterFormat.RGBA32;
        settings.textureCompression = TextureImporterCompression.Uncompressed;
        importer.SetPlatformTextureSettings(settings);

        Debug.Log($"[PixelArtImport] Auto-applied PPU={PPU}: {assetPath}");
    }

    static bool IsPixelArtPath(string path)
    {
        foreach (var p in PIXEL_ART_PATHS)
            if (path.StartsWith(p)) return true;
        return false;
    }
}