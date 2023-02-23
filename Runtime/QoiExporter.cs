using UnityEngine;
using UnityEditor;
using static ImageSaveUtility.TextureEncodingFormat;

public static class QOIExporter
{
    // Context menu actions -----------------------------------------------------
    [MenuItem("Assets/Export To QOI", false, 5)]
    private static void ExportToQOI() {
        var texture = Selection.activeObject as Texture2D;
        texture.SaveToFile(QOI);
    }

    [MenuItem("Assets/Export To QOI", true)]
    private static bool ExportToQOIValidation() => Selection.activeObject is Texture2D;
}