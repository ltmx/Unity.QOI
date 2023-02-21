using System;
using System.IO;
using UnityEditor;
using UnityEngine;
using UnityEditor.AssetImporters;
using static UnityEditor.EditorUtility;
using static UnityEngine.TextureFormat;


[ScriptedImporter(1, "qoi")]
public class QoiImporter : ScriptedImporter
{

    public bool Crunch;
    public bool NormalMap;
    public TextureCompressionQuality CompressionQuality = TextureCompressionQuality.Best;
    private TextureFormat textureFormat = DXT1Crunched;
    
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
            _ => throw new ArgumentOutOfRangeException(nameof(img.Channels), img.Channels,
                $"Unhandled QOI channel format '{img.Channels}'!")
        };

        bool isLinear = img.ColorSpace ==  ColorSpace.Linear;
        Texture2D tex = new(img.Width, img.Height, format, true, isLinear);

        int slashIdx = ctx.assetPath.LastIndexOf('/');
        if (slashIdx < 0) slashIdx = 0;
        int dotIdx = ctx.assetPath.LastIndexOf('.');
            
        Debug.Assert(dotIdx >= 0);

        tex.name = ctx.assetPath.Substring(slashIdx, dotIdx - slashIdx);

        tex.SetPixelData(img.Data.ToArray(), 0);

        bool hasAlpha = format == RGBA32;

        if (hasAlpha) textureFormat = Crunch ? DXT5Crunched : DXT5;
        else textureFormat = Crunch ? DXT1Crunched : DXT1;
        
        tex.alphaIsTransparency = hasAlpha;

        tex.Apply();
        
        CompressTexture(tex, textureFormat, TextureCompressionQuality.Best);
        tex.Compress(false);

        stream.Close();

            
        ctx.AddObjectToAsset(tex.name, tex, tex);
        ctx.SetMainObject(tex);
        // AssetDatabase.Refresh();
    }
}