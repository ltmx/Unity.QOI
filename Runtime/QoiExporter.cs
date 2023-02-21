using UnityEngine;
using UnityEditor;
using static AssetImporterExtensions;
using static ImageProcessing;

public static class QOIExporter
{
    private static readonly string outputFolder = "QOI_Output";
    /// <summary>
    /// Saves the texture to a file in the project's RenderOutput folder
    /// </summary>
    /// <param name="t"></param>
    public static void SaveToFile(this Texture2D t)
    {
        var dirPath = Application.dataPath + "/" + outputFolder;
        dirPath.CreateDirectoryIfVoid();

        var fileName = "/QOI_" + t.name + QOI;
        var filePath = dirPath + fileName;
        var byteCount = t.EncodeToQOI().WriteAllBytes(filePath);
        
        #if UNITY_EDITOR
        Debug.Log(byteCount / 1024 + "Kb was saved as: " + filePath);
        #endif
        
        // Focuses the file in the project window and highlights it
        Selection.activeObject = LoadAtPath<Texture2D>("Assets/" + outputFolder + fileName);
        EditorGUIUtility.PingObject( Selection.activeObject );
        
        // Refresh the project window
        AssetDatabase.SaveAssets();
        #if UNITY_EDITOR
        AssetDatabase.Refresh();
        #endif
    }

    // Context menu actions -----------------------------------------------------
    
    [MenuItem("Assets/Export To QOI", false, 5)]
    private static void ExportToQOI() {
        var texture = Selection.activeObject as Texture2D;
        texture.SaveToFile();
    }

    [MenuItem("Assets/Export To QOI", true)]
    private static bool ExportToQOIValidation() => Selection.activeObject is Texture2D;
}


