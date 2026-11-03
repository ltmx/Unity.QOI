using System;
using System.IO;
using UnityEditor;
using UnityEngine;
using static LTMX.QOI.Editor.ImageSaveUtility.TextureEncodingFormat;

namespace LTMX.QOI.Editor
{
    public static class ImageSaveUtility
    {
        private const string OutputFolder = "QOI_Output";

        // Opens The Save File Panel and saves the texture to the selected path
        public static void SaveToFile(this Texture2D texture, TextureEncodingFormat format)
        {
            var extension = format.ToString().ToLowerInvariant();
            var path = EditorUtility.SaveFilePanel("Save Texture", "", texture.name, extension);
            if (string.IsNullOrEmpty(path)) return;

            File.WriteAllBytes(path, texture.EncodeTo(format));
            ImportIfInProject(path);
        }

        /// Saves the texture to a file in the project's QOI_Output folder and selects it
        public static void SaveToQOIFile(this Texture2D texture)
        {
            var directory = Path.Combine(Application.dataPath, OutputFolder);
            Directory.CreateDirectory(directory);

            var fileName = "QOI_" + texture.name + ".qoi";
            var bytes = texture.EncodeToQOI();
            File.WriteAllBytes(Path.Combine(directory, fileName), bytes);
            Debug.Log($"{bytes.Length / 1024}KB were saved as: Assets/{OutputFolder}/{fileName}");

            var assetPath = $"Assets/{OutputFolder}/{fileName}";
            AssetDatabase.ImportAsset(assetPath);
            Selection.activeObject = AssetDatabase.LoadAssetAtPath<Texture2D>(assetPath);
            EditorGUIUtility.PingObject(Selection.activeObject);
        }

        /// Encodes mip 0 of the texture, works with non readable and compressed textures
        public static byte[] EncodeTo(this Texture2D t, TextureEncodingFormat format)
        {
            if (format == TextureEncodingFormat.QOI) return t.EncodeToQOI();

            if (format == EXR)
                return ReadableCopy(t, RenderTextureFormat.ARGBFloat, TextureFormat.RGBAFloat).EncodeToEXR(Texture2D.EXRFlags.CompressZIP);

            var copy = ReadableCopy(t, RenderTextureFormat.ARGB32, TextureFormat.RGBA32);
            try
            {
                return format switch
                {
                    PNG => copy.EncodeToPNG(),
                    JPG => copy.EncodeToJPG(95),
                    TGA => copy.EncodeToTGA(),
                    _ => throw new ArgumentOutOfRangeException(nameof(format), format, null)
                };
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(copy);
            }
        }

        private static Texture2D ReadableCopy(Texture2D source, RenderTextureFormat rtFormat, TextureFormat format)
        {
            bool linear = !source.isDataSRGB;
            var readWrite = linear ? RenderTextureReadWrite.Linear : RenderTextureReadWrite.sRGB;
            var rt = RenderTexture.GetTemporary(source.width, source.height, 0, rtFormat, readWrite);
            var previous = RenderTexture.active;
            var copy = new Texture2D(source.width, source.height, format, false, linear);
            Graphics.Blit(source, rt);
            RenderTexture.active = rt;
            copy.ReadPixels(new Rect(0, 0, source.width, source.height), 0, 0, false);
            copy.Apply();
            RenderTexture.active = previous;
            RenderTexture.ReleaseTemporary(rt);
            return copy;
        }

        private static void ImportIfInProject(string path)
        {
            var fullPath = Path.GetFullPath(path).Replace('\\', '/');
            var dataPath = Path.GetFullPath(Application.dataPath).Replace('\\', '/');
            if (fullPath.StartsWith(dataPath + "/", StringComparison.OrdinalIgnoreCase))
                AssetDatabase.ImportAsset("Assets" + fullPath.Substring(dataPath.Length));
        }

        public enum TextureEncodingFormat
        {
            QOI,
            PNG,
            JPG,
            EXR,
            TGA
        }
    }
}
