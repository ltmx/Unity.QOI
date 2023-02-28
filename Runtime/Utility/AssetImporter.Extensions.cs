using UnityEditor;
using UnityEngine;

public static class AssetImporterExtensions
{
    // Asset Importer Stuff
    public static void Import(this Texture2D t) => AssetDatabase.ImportAsset(t.GetPath());
    public static void Import<T>(this T t) where T : Object => AssetDatabase.ImportAsset(t.GetPath());
    public static void Import(this AssetImporter t) => AssetDatabase.ImportAsset(t.assetPath);
    public static void ImportAndRefresh(this AssetImporter t) {
        AssetDatabase.ImportAsset(t.assetPath);
        AssetDatabase.Refresh();
    }
}

public static class AssetManagementExtensions
{
    /// <inheritdoc cref="AssetDatabase.GetAssetPath(Object)" />
    public static string GetPath<T>(this T o) where T : Object => AssetDatabase.GetAssetPath(o);
    /// <inheritdoc cref="AssetDatabase.LoadAssetAtPath(string, System.Type)" />
    public static T LoadAtPath<T>(string path) where T : Object => AssetDatabase.LoadAssetAtPath<T>(path);

    public static string GetRelativeAssetPath(this Object asset) => Application.dataPath + GetAssetPathWithoutAssetFolderAndAssetName(asset);
    public static string GetAssetPathWithoutAssetFolderAndAssetName(this Object asset) => AssetDatabase.GetAssetPath(asset).RemoveAssetFolderFromPath(asset).RemoveAssetNameFromPath(asset);
    public static string GetAssetPathWithoutAssetName(this Object asset) => AssetDatabase.GetAssetPath(asset).RemoveAssetNameFromPath(asset);
    public static string RemoveAssetNameFromPath(this string path, Object asset) => path.Replace("/" + asset.name + ".asset", "");
    public static string RemoveAssetFolderFromPath(this string path, Object asset) => path.Replace("Assets", "");

    public static void CreateDirectoryIfVoid(this string path) => path.DirectoryExists()?.CreateDirectory();

    /// Replaces an asset at the desired path. If nothing can be found, the asset it simply created
    public static T CreateOrReplaceAsset<T>(this T asset, string path) where T : Object => CreateAssetIfNull(asset, path).CopySerialized(asset);

    /// <inheritdoc cref="AssetDatabase.CreateAsset" />
    public static T CreateAsset<T>(this T asset, string path) where T : Object {
        AssetDatabase.CreateAsset(asset, path);
        return asset;
    }

    /// <inheritdoc cref="EditorUtility.CopySerialized" />
    public static T CopySerialized<T>(this T destination, T source) where T : Object {
        EditorUtility.CopySerialized(source, destination);
        return destination;
    }
    /// Creates an asset if no asset is found at the desired path
    public static T CreateAssetIfNull<T>(this T asset, string path) where T : Object {
        var existingAsset = LoadAtPath<T>(path);
        return existingAsset != null ? existingAsset : asset.CreateAsset(path);
    }
}