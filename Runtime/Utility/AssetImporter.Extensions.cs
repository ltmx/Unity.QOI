using System.IO;
using UnityEditor;
using UnityEngine;

public static class AssetImporterExtensions {

    public static TextureImporter SetReadWrite(this TextureImporter t, bool state) {
        t.isReadable = state;
        return t;
    }

    public static TextureImporter SetCrunchCompression(this TextureImporter t, bool state) {
        t.crunchedCompression = state;
        return t;
    }

    public static TextureImporter SetAlphaIsTransparency(this TextureImporter t, bool state) {
        t.alphaIsTransparency = state;
        return t;
    }

    public static TextureImporter SetAsNormalMap(this TextureImporter t) {
        t.textureType = TextureImporterType.NormalMap;
        return t;
    }

    public static TextureImporter SetType(this TextureImporter t, TextureImporterType type) {
        t.textureType = type;
        return t;
    }

    public static Texture2D SetReadWriteAndRefresh(this Texture2D texture, bool state) {
        texture?.GetImporter()?.SetReadWrite(state).SaveAndReimport();
        return texture;
    }
    
    public static Texture2D SetReadWrite(this Texture2D texture, bool state) {
        texture?.GetImporter()?.SetReadWrite(state);
        return texture;
    }
    
    public static Texture2D SetCrunchCompression(this Texture2D texture, bool state) {
        texture?.GetImporter()?.SetCrunchCompression(state).SaveAndReimport();
        return texture;
    }
    
    public static Texture2D SetAlphaIsTransparency(this Texture2D texture, bool state) {
        texture?.GetImporter()?.SetAlphaIsTransparency(state).SaveAndReimport();
        return texture;
    }

    public static Texture2D SetAsNormalMap(this Texture2D texture) {
        texture?.GetImporter()?.SetType(TextureImporterType.NormalMap).SaveAndReimport();
        return texture;
    }
    
    public static void SetImporterType(this Texture2D texture, TextureImporterType type) {
        texture?.GetImporter()?.SetType(type).SaveAndReimport();
    }

    public static TextureImporter GetImporter(this Texture2D t) => AssetImporter.GetAtPath(t.GetPath()) as TextureImporter;
    // public static string GetAssetPath(this Object o) => AssetDatabase.GetAssetPath(o);
    public static string GetPath(this Texture2D t) => AssetDatabase.GetAssetPath(t);

    public static void Import(this Texture2D t) => AssetDatabase.ImportAsset(t.GetPath());

    public static void Reimport(this AssetImporter t) => AssetDatabase.ImportAsset(t.assetPath);
    public static void ReimportAndRefresh(this AssetImporter t)
    {
        AssetDatabase.ImportAsset(t.assetPath);
        AssetDatabase.Refresh();
    }
    
    public static T LoadAtPath<T>(string path) where T : Object => AssetDatabase.LoadAssetAtPath<T>(path);
    
    public static string GetRelativeAssetPath(this Object asset) => Application.dataPath + GetAssetPathWithoutAssetFolderAndAssetName(asset);
    public static string GetAssetPathWithoutAssetFolderAndAssetName(this Object asset) => AssetDatabase.GetAssetPath(asset).RemoveAssetFolderFromPath(asset).RemoveAssetNameFromPath(asset);
    public static string GetAssetPathWithoutAssetName(this Object asset) => AssetDatabase.GetAssetPath(asset).RemoveAssetNameFromPath(asset);
    public static string RemoveAssetNameFromPath(this string path, Object asset) => path.Replace("/" + asset.name + ".asset", "");
    public static string RemoveAssetFolderFromPath(this string path, Object asset) => path.Replace("Assets", "");
    
    public static void CreateDirectoryIfVoid(this string path)
    {
        if (!Directory.Exists(path)) Directory.CreateDirectory(path);
    }

    
}