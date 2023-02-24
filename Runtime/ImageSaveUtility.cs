using System;
using System.IO;
using UnityEditor;
using UnityEngine;
using static ImageSaveUtility.TextureEncodingFormat;

public static class ImageSaveUtility
{
    // Opens The Save File Panel and saves the texture to the selected path
    public static void SaveToFile(this Texture2D texture, TextureEncodingFormat format)
    {
        var path = EditorUtility.SaveFilePanel("Save Texture", "", texture.name, format.ToString().ToLower());
        if (!string.IsNullOrEmpty(path)) texture.EncodeTo(format).WriteAllBytes(path);
    }
    
    private static readonly string outputFolder = "QOI_Output";
    
    /// Saves the texture to a file in the project's RenderOutput folder
    public static void SaveToQOIFile(this Texture2D t, TextureEncodingFormat format)
    {
        var dirPath = Application.dataPath + "/" + outputFolder;
        dirPath.CreateDirectoryIfVoid();

        var fileName = "/QOI_" + t.name + ImageProcessing.FileExtensions.QOI;
        var filePath = dirPath + fileName;

        var bytes = t.EncodeTo(format);
        var byteCount = bytes.WriteAllBytes(filePath);
        
        #if UNITY_EDITOR
        Debug.Log(byteCount / 1024 + "Kb was saved as: " + filePath);
        #endif
        
        // Focuses the file in the project window and highlights it
        Selection.activeObject = AssetImporterExtensions.LoadAtPath<Texture2D>("Assets/" + outputFolder + fileName);
        EditorGUIUtility.PingObject( Selection.activeObject );
        
        // Refresh the project window
        AssetDatabase.SaveAssets();
        #if UNITY_EDITOR
        AssetDatabase.Refresh();
        #endif
    }
    
    
    public static byte[] EncodeTo(this Texture2D t, TextureEncodingFormat format)
    {
        return format switch
        {
            QOI => t.EncodeToQOI(),
            PNG => t.EncodeToPNG(),
            JPG => t.EncodeToJPG(),
            EXR => t.EncodeToEXR(),
            TGA => t.EncodeToTGA(),
            // TODO: Should implement the rest of the formats which currently dont have any encoders/decoders
            BMP => t.EncodeToBMP(),
            // HDR => t.EncodeToHDR(),
            // TIFF => t.EncodeToTIFF(),
            // GIF => t.EncodeToGIF(),
            // HEIF => t.EncodeToHEIF(),
            _ => t.EncodeToPNG()
        };
    }

    public static byte[] EncodeToBMP(this Texture2D t)
    {

        return Array.Empty<byte>();
    }
    
    // public static byte[] EncodeBitmap(Bitmap bitmap)
    // {
    //     using MemoryStream stream = new MemoryStream();
    //     bitmap.Save(stream, ImageFormat.Bmp);
    //     return stream.ToArray();
    // }

    public enum TextureEncodingFormat
    {
        QOI, PNG, JPG, EXR, TGA, BMP, HDR, TIFF, GIF, HEIF
    }
}