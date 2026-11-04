using System;
using System.Diagnostics;
using System.IO;
using UnityEditor;
using UnityEngine;
using Debug = UnityEngine.Debug;
using static LTMX.QOI.Editor.ImageSaveUtility.TextureEncodingFormat;

namespace LTMX.QOI.Editor
{
    public static class ImageSaveUtility
    {
        private const string OutputFolder = "QOI_Output";

        /// Opens The Save File Panel and saves the texture to the selected path.
        /// Returns a summary of the export (also logged), or null when cancelled.
        /// <param name="sampleFormat">QOI only, null to pick it from the texture format (half / float for HDR textures).</param>
        /// <param name="channels">QOI only, null to pick them from the texture format and content.</param>
        public static string SaveToFile(this Texture2D texture, TextureEncodingFormat format, QoiEncoder encoder = QoiEncoder.Cpu,
            QoiSampleFormat? sampleFormat = null, QoiChannels? channels = null)
        {
            var extension = format.ToString().ToLowerInvariant();
            var path = EditorUtility.SaveFilePanel("Save Texture", "", texture.name, extension);
            if (string.IsNullOrEmpty(path)) return null;

            var stopwatch = Stopwatch.StartNew();
            // Not the bare QOI: inside LTMX.QOI.Editor that name is the LTMX.QOI namespace, which wins over the using static
            bool qoi = format == TextureEncodingFormat.QOI;
            var bytes = qoi ? texture.EncodeToQOI(encoder, sampleFormat, channels) : texture.EncodeTo(format);
            double ms = stopwatch.Elapsed.TotalMilliseconds;
            File.WriteAllBytes(path, bytes);

            string by = "";
            long raw = 4L * texture.width * texture.height;
            if (qoi)
            {
                var header = QoiCodec.ReadHeader(bytes);
                by = $" ({header.Channels} {header.SampleFormat}) on the {(encoder == QoiEncoder.Gpu && QoiGpu.IsSupported ? "GPU" : "CPU")}";
                raw = (long)header.PixelCount * (int)header.Channels * header.BytesPerSample;
            }
            string summary = $"Encoded '{texture.name}' ({texture.width}x{texture.height}) to {format}{by} in {ms:F1} ms: " +
                             $"{bytes.Length / 1024f:F0} KB ({100.0 * bytes.Length / raw:F0}% of raw)";
            Debug.Log($"{summary}, saved as {path}");
            ImportIfInProject(path);
            return summary;
        }

        /// <summary>
        /// Encodes mip 0 to QOI on the CPU (plain QOI file) or with the compute shader (seekable QOI file, see <see cref="QoiGpu"/>).
        /// Falls back to the CPU when compute shaders are not supported.
        /// </summary>
        public static byte[] EncodeToQOI(this Texture2D texture, QoiEncoder encoder, QoiSampleFormat? sampleFormat = null, QoiChannels? channels = null)
        {
            if (encoder == QoiEncoder.Gpu && QoiGpu.IsSupported) return QoiGpu.Encode(texture, QoiGpu.DefaultChunkPixels, channels, sampleFormat);
            if (encoder == QoiEncoder.Gpu) Debug.LogWarning("Compute shaders are not supported, encoding QOI on the CPU.");
            // Not texture.EncodeToQOI(0, ...): the literal 0 converts to QoiEncoder and would call this method again
            return QoiTexture.EncodeToQOI(texture, 0, sampleFormat, channels);
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

        public enum QoiEncoder
        {
            Cpu,
            Gpu
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
