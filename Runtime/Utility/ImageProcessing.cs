using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using QoiSharp;
using QoiSharp.Codec;
using Unity.Mathematics;
using UnityEngine;

namespace Utility
{
    public static class ImageProcessing
    {


        public static string SaveToFile(this Texture2D tex, string _name)
        {
            var bytes = tex.EncodeToPNG();
            string path = Application.dataPath + "/" + _name + ".png";
            File.WriteAllBytes(path, bytes);
            return "Assets/" + _name + ".png";
        }

        public static string SaveToFile(this Texture2D tex, string _name, string path)
        {
            var bytes = tex.EncodeToPNG();
            string _path = path + "/" + _name + ".png";
            File.WriteAllBytes(_path, bytes);
            return "path/"; // testing
        }



        public static float2 MinMaxTextureValue(this Texture2D tex)
        {
            float2 minMax = new float2(1, 0);
            foreach (var p in tex.GetPixels()) {
                float now = p.r;
                if (now > minMax.y) minMax.y = now;
                if (now < minMax.x) minMax.x = now;
            }
            return minMax;
        }
    
        // Color Modification Functions
        public static Color RemapMinMax(this Texture2D t, Color c) => c.unlerp(t.MinMaxTextureValue());
        public static Color FlipNormalY(this Texture2D tex, Color c) => new(c.r, 1 - c.g, c.b, c.a);
        public static Color PackNormalAndHeight(Texture2D tex, Color c, Color d) => new(c.r, c.g, c.b, d.r);
        public static Color PackAlbedoAndAlpha(Texture2D tex, Color c, Color d) => new(c.r, c.g, c.b, d.r);
    
        // Texture Modification Functions
        public static Texture2D RemapMinMax(this Texture2D tex) => tex.ApplyModification(RemapMinMax);
        public static Texture2D FlipNormalY(this Texture2D tex) => ApplyModification(tex, FlipNormalY);
        public static Texture2D PackNormalAndHeight(Texture2D normal, Texture2D height) => normal.ApplyModification(height, PackNormalAndHeight);
        public static Texture2D PackAlbedoAndAlpha(this Texture2D albedo, Texture2D alpha) => albedo.ApplyModification(alpha, PackAlbedoAndAlpha);
    
        // Texture Modification Iterator Functions
        public static Texture2D ApplyModification(this Texture2D tex, Func<Texture2D, Color, Color> function)
        {
            var c = tex.GetPixels();
            for (int i = 0; i < c.Length; i++)
                c[i] = function(tex, c[i]);

            return tex.BlankCopy().SetPixelsChain(c);
        }
    
        public static Texture2D ApplyModification(this Texture2D tex, Texture2D tex2, Func<Texture2D, Color, Color, Color> function)
        {
            var c = tex.GetPixels();
            var d = tex2.GetPixels();
            for (int i = 0; i < c.Length; i++)
                c[i] = function(tex, c[i], d[i]);

            return tex.BlankCopy().SetPixelsChain(c);
        }
    
        public static Texture2D PackHeights(List<Texture2D> h)
        {
            var H1 = h[0].GetPixels();
            var H2 = h[1].GetPixels();
            var H3 = h[2].GetPixels();
            var H4 = h[3].GetPixels();
            for (int i = 0; i < H1.Length; i++)
            {
                H1[i] = new Color(H1[i].r, H2[i].r, H3[i].r, H4[i].r);
            }
            return h[0].BlankCopy().SetPixelsChain(H1);
        }

        /// Packs AO, Metallic & Smoothness into a single texture
        public static Texture2D PackHOSM(Texture2D height, Texture2D occlusion, Texture2D smoothness, Texture2D metalness)
        {
            var Height = height ? height.Px() : Array.Empty<Color>();
            var Occlusion = occlusion ? occlusion.GetPixels() : Array.Empty<Color>();
            var Smoothness = smoothness ? smoothness.GetPixels() : Array.Empty<Color>();
            var Metalness = metalness ? metalness.GetPixels() : Array.Empty<Color>();
            for (int i = 0; i < Height.Length; i++)
            {
                Height[i] = new Color(height ? Height[i].r : 1, occlusion ? Occlusion[i].r : 0, smoothness ? Smoothness[i].r : 0, metalness ? Metalness[i].r : 0);
            }

            return height.BlankCopy().SetPixelsChain(Height);
        }
    
    
        public static byte[] EncodeToQOI(this Texture2D t)
        {
            var copy = t.CopySafe();
        
            bool hasAlpha = t.GetImporter().DoesSourceTextureHaveAlpha();
            Channels channels = hasAlpha ? Channels.RgbWithAlpha : Channels.Rgb;
        
            byte[] data = hasAlpha? copy.GetByteArray32() : copy.GetByteArray24();
            var qoiImage = new QoiImage(data, t.width, t.height, channels); // Should be Integrated into a constructor
            return QoiEncoder.Encode(qoiImage);
        }
    
        public static byte[] EncodeToEXR(this Texture2D t)
        {
            var copy = t.CopySafe();
            return copy.GetByteArray32();
        }

        /// Short for File.WriteAllBytes
        /// <returns>Returns the length of written file in bytes</returns>
        public static int WriteAllBytes(this byte[] bytes, string path)
        {
            File.WriteAllBytes(path, bytes);
            return path.Length;
        }
    

        public static class FileExtensions
        {
            public const string QOI = ".qoi";
            public const string PNG = ".png";
            public const string JPG = ".jpg";
            public const string EXR = ".exr";
            public const string TGA = ".tga";
            public const string ShaderGraph = ".shadergraph";
            public const string ShaderSubGraph =".shadersubgraph";
            public const string VFXGraph =".visualeffect";
            public const string ASSET =".asset";
        }
    
        /// <summary>
        /// Copy a given <see cref="RenderTexture"/> to a <see cref="Texture2D"/>.
        /// This method assumes that both textures exist and are the same size.
        /// </summary>
        /// <param name="renderTexture">The source <see cref="RenderTexture" />.</param>
        /// <param name="texture">The destination <see cref="Texture2D" />.</param>
        public static void ToTexture2D(this RenderTexture renderTexture, Texture2D texture)
        {
            RenderTexture.active = renderTexture;
            texture.ReadPixels(new Rect(0, 0, texture.width, texture.height), 0, 0);
            texture.Apply();
        }
    
        public static Texture2D ToTexture2D(this RenderTexture renderTexture)
        {
            var tex = new Texture2D(renderTexture.width, renderTexture.height, TextureFormat.ARGB32, true);
            renderTexture.ToTexture2D(tex);
            return tex;
        }
    
        // public static void ToRenderTexture(this Texture2D texture, RenderTexture renderTexture) => Graphics.CopyTexture(texture, renderTexture);

        // public static RenderTexture ToRenderTexture(this Texture2D texture)
        // {
        //     var rt = new RenderTexture(texture.width, texture.height, 0, RenderTextureFormat.ARGB32);
        //     rt.useMipMap = true;
        //     rt.autoGenerateMips = true;
        //     rt.Create();
        //     Graphics.CopyTexture(texture, rt);
        //     rt.GenerateMips();
        //     // rt.Release();
        //     return rt;
        // }
        [Serializable]
        public enum MaxTextureSize
        {
            [Description("16")] x16 = 16,
            [Description("32")] x32 = 32,
            [Description("64")] x64 = 64,
            [Description("128")] x128 = 128,
            [Description("256")] x256 = 256,
            [Description("512")] x512 = 512,
            [Description("1024")] x1024 = 1024,
            [Description("2048")] x2048 = 2048,
            [Description("4096")] x4096 = 4096,
            [Description("8192")] x8192 = 8192,
            [Description("16384")] x16384 = 16384
        }

    }
}