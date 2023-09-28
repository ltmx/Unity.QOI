using System;
using System.IO;
using QoiSharp;
using QoiSharp.Codec;
using UnityEditor;
using UnityEngine;
using UnityEditor.AssetImporters;
using static Utility.ImageProcessing;
using static UnityEditor.EditorUtility;
using static UnityEngine.TextureFormat;
using ColorSpace = QoiSharp.Codec.ColorSpace;

/// <summary>
/// Qoi Importer
/// </summary>
[ScriptedImporter(1, "qoi")]
public class QoiImporter : ScriptedImporter
{
// Texture import settings
    public TextureImporterType textureType = TextureImporterType.Default;

    // Todo: Implement grayscaleToAlpha
    // public bool grayscaleToAlpha;
    public bool alphaIsTransparency;
    public bool mipMapEnabled = true;
    //Todo: Implement npotScale clamping
    public TextureImporterNPOTScale npotScale = TextureImporterNPOTScale.None;
    public bool sRGBTexture = true;
    // public bool linearTexture = false;
    
    
    public TextureWrapMode wrapMode = TextureWrapMode.Repeat;
    public FilterMode filterMode = FilterMode.Trilinear;
    [Range(0,16)]
    public int anisoLevel = 1;
    
    // Size and compression
    public MaxTextureSize maxTextureSize = MaxTextureSize.x2048;
    public TextureCompressionQuality compressionQuality;
    public bool crunchedCompression = false;
    
    // Private Fields
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
}