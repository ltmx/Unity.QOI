using UnityEditor;
using UnityEngine;
using static LTMX.QOI.Editor.ImageSaveUtility;

namespace LTMX.QOI.Editor
{
    public static class ImageExporter
    {
        // Context menu actions -----------------------------------------------------
        [MenuItem("Assets/Export to.../QOI", false, 5)]
        private static void ExportToQOI() => (Selection.activeObject as Texture2D).SaveToFile(TextureEncodingFormat.QOI);
        [MenuItem("Assets/Export to.../QOI", true)]
        private static bool ExportToQOIValidation() => Selection.activeObject is Texture2D;

        [MenuItem("Assets/Export to.../PNG", false, 5)]
        private static void ExportToPNG() => (Selection.activeObject as Texture2D).SaveToFile(TextureEncodingFormat.PNG);
        [MenuItem("Assets/Export to.../PNG", true)]
        private static bool ExportToPNGValidation() => Selection.activeObject is Texture2D;

        [MenuItem("Assets/Export to.../JPG", false, 5)]
        private static void ExportToJPG() => (Selection.activeObject as Texture2D).SaveToFile(TextureEncodingFormat.JPG);
        [MenuItem("Assets/Export to.../JPG", true)]
        private static bool ExportToJPGValidation() => Selection.activeObject is Texture2D;
    }
}
