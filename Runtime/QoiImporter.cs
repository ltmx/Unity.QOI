using System;
using System.IO;
using UnityEditor;
using UnityEngine;
using UnityEditor.AssetImporters;
using static ImageProcessing;
using static UnityEditor.EditorUtility;
using static UnityEngine.TextureFormat;
// using static MipGenerator; // Todo: Remove this


[ScriptedImporter(1, "qoi")]
public class QoiImporter : ScriptedImporter
{
// Texture import settings
    public TextureImporterType textureType = TextureImporterType.Default;

    // Todo: Implement grayscaleToAlpha
    public bool grayscaleToAlpha;
    public bool alphaIsTransparency;
    public bool mipMapEnabled = true;
    //Todo: Implement npotScale clamping
    public TextureImporterNPOTScale npotScale = TextureImporterNPOTScale.None;
    public bool sRGBTexture = true;
    public bool linearTexture = false;
    
    
    public TextureWrapMode wrapMode = TextureWrapMode.Repeat;
    public FilterMode filterMode = FilterMode.Trilinear;
    public int anisoLevel = 2048;
    
    // Size and compression
    public MaxTextureSize maxTextureSize = MaxTextureSize.x2048;
    public TextureCompressionQuality compressionQuality;
    // public TextureCompressionQuality crunchedCompressionQuality = TextureCompressionQuality.Best;
    public bool crunchedCompression = false;
    
    // ¨rivate Fields
    // private TextureImporterShape textureShape = TextureImporterShape.Texture2D;
    // private TextureDimension dimension = TextureDimension.Tex2D;
    private TextureFormat textureFormat = DXT5;

    public override void OnImportAsset(AssetImportContext ctx)
    {
        if (!ctx.assetPath.EndsWith(".qoi", StringComparison.InvariantCultureIgnoreCase)) return;
        var stream = File.OpenRead(ctx.assetPath);
        var data = new byte[stream.Length];
        stream.Read(data, 0, data.Length);
        QoiImage img = QoiDecoder.Decode(data);

        if (img == null)
        {
            ctx.LogImportError($"Failed to decode QOI image '{ctx.assetPath}'!");
            return;
        }
        
        TextureFormat format = img.Channels switch
        {
            Channels.Rgb => RGB24,
            Channels.RgbWithAlpha => RGBA32,
            // _ => ctx.LogImportError($"Unhandled QOI channel format '{img.Channels}'!");
            _ => throw new ArgumentOutOfRangeException(nameof(img.Channels), img.Channels, $"Unhandled QOI channel format '{img.Channels}'!")
        };
        // format = format == RGBA32 && !alphaIsTransparency ? RGB24 : format;
        Texture2D tex = new(img.Width, img.Height, format, mipMapEnabled, img.ColorSpace == ColorSpace.Linear);
        tex.name = Path.GetFileNameWithoutExtension(ctx.assetPath);
        //property setting;
        tex.alphaIsTransparency = alphaIsTransparency; // Should be able to change it to true;
        tex.wrapMode = wrapMode;
        tex.filterMode = filterMode;
        tex.anisoLevel = (int)anisoLevel; // mipMapBias = mipMapBias,
        // dimension = dimension
        
        tex.SetPixelData(img.Data.ToArray(), 0);
        
        if (format == RGBA32) 
            textureFormat = crunchedCompression ? DXT5Crunched : DXT5; // For RGBA_32, use DXT5
        else textureFormat = crunchedCompression ? DXT1Crunched : DXT1; // For RGB_24, use DXT1

        tex.Apply();
        CompressTexture(tex, textureFormat, compressionQuality);

        stream.Close();
        
        ctx.AddObjectToAsset(tex.name, tex, tex);
        ctx.SetMainObject(tex);
    }

    // private static string GetName(string path)
    // {
    //     int slashIdx = path.LastIndexOf('/');
    //     if (slashIdx < 0) slashIdx = 0;
    //     int dotIdx = path.LastIndexOf('.');
    //     // Debug.Assert(dotIdx >= 0);
    //     return path.Substring(slashIdx, dotIdx - slashIdx);
    // }
}

// public class QOIImageImporter : ScriptedImporter
// {
//     public int mipmapLevels = 3;
//     public TextureWrapMode wrapMode = TextureWrapMode.Repeat;
//     public FilterMode filterMode = FilterMode.Bilinear;
//     public int anisoLevel = 1;
//     public TextureImporterType textureType;
//     public bool mipmapEnabled;
//     public TextureCompressionQuality CompressionQuality = TextureCompressionQuality.Best;
//     private TextureFormat textureFormat = DXT1Crunched;
//     
//     public override void OnImportAsset(AssetImportContext ctx)
//     {
//         // Load the texture data from the file
//         byte[] data = File.ReadAllBytes(ctx.assetPath);
//         Texture2D texture = new Texture2D(2, 2);
//         texture.LoadImage(data);
//
//         // Set the texture import settings
//         this.textureType = TextureImporterType.Default;
//         this.mipmapEnabled = true;
//         this.wrapMode = wrapMode;
//         this.filterMode = filterMode;
//         this.anisoLevel = anisoLevel;
//
//         // Apply the texture to the asset
//         ctx.AddObjectToAsset("QOI Image", texture);
//         ctx.SetMainObject(texture);
//     }
// }