using UnityEngine;
using UnityEditor;
using static ImageSaveUtility.TextureEncodingFormat;

public static class QOIExporter
{
    // Context menu actions -----------------------------------------------------
    [MenuItem("Assets/Export to.../QOI", false, 5)]
    private static void ExportToQOI() {
        var texture = Selection.activeObject as Texture2D;
        texture.SaveToFile(QOI);
    }
    [MenuItem("Assets/Export to.../QOI", true)]
    private static bool ExportToQOIValidation() => Selection.activeObject is Texture2D;
    
    
    [MenuItem("Assets/Export to.../PNG", false, 5)]
    private static void ExportToPNG() {
        var texture = Selection.activeObject as Texture2D;
        texture.SaveToFile(PNG);
    }
    [MenuItem("Assets/Export to.../PNG", true)]
    private static bool ExportToPNGValidation() => Selection.activeObject is Texture2D;
    
    
    [MenuItem("Assets/Export to.../JPG", false, 5)]
    private static void ExportToJPG() {
        var texture = Selection.activeObject as Texture2D;
        texture.SaveToFile(JPG);
    }
    [MenuItem("Assets/Export to.../JPG", true)]
    private static bool ExportToJPGValidation() => Selection.activeObject is Texture2D;
}