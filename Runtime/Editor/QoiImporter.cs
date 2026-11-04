using System;
using System.IO;
using System.Runtime.InteropServices;
using UnityEditor;
using UnityEditor.AssetImporters;
using UnityEngine;
using static UnityEngine.TextureFormat;

namespace LTMX.QOI.Editor
{
    /// <summary>
    /// Qoi Importer
    /// </summary>
    [ScriptedImporter(4, "qoi")]
    public class QoiImporter : ScriptedImporter
    {
        // Texture import settings
        public TextureImporterType textureType = TextureImporterType.Default;

        public bool alphaIsTransparency;
        public bool mipMapEnabled = true;
        public TextureImporterNPOTScale npotScale = TextureImporterNPOTScale.None;
        public bool sRGBTexture = true;

        public TextureWrapMode wrapMode = TextureWrapMode.Repeat;
        public FilterMode filterMode = FilterMode.Trilinear;
        [Range(0, 16)]
        public int anisoLevel = 1;

        // Size and compression
        public MaxTextureSize maxTextureSize = MaxTextureSize.x2048;
        public TextureImporterCompression compression = TextureImporterCompression.Compressed;
        public TextureCompressionQuality compressionQuality = TextureCompressionQuality.Normal;
        public bool crunchedCompression;

        public override void OnImportAsset(AssetImportContext ctx)
        {
            QoiImage img;
            try
            {
                var data = File.ReadAllBytes(ctx.assetPath);
                var header = QoiCodec.ReadHeader(data);
                // Unity has no RGB HDR texture format: HDR RGB files are expanded to RGBA. Seekable files are decoded on all cores.
                img = QoiCodec.Decode(data, header.IsHdr && header.Channels == QoiChannels.Rgb ? 4 : 0, flipY: true);
            }
            catch (QoiException e)
            {
                ctx.LogImportError($"Failed to decode QOI image '{ctx.assetPath}': {e.Message}");
                return;
            }

            int width = img.Width;
            int height = img.Height;
            int channels = img.PixelChannels;
            byte[] pixels = img.Pixels;
            var sampleFormat = img.SampleFormat;
            bool hdr = sampleFormat != QoiSampleFormat.Byte;
            bool single = img.Channels == QoiChannels.R;

            // Same order as the TextureImporter: power of two scaling first, then max size. Sprites keep their size.
            int targetWidth = width, targetHeight = height;
            if (textureType != TextureImporterType.Sprite)
            {
                targetWidth = ScaleToPowerOfTwo(width, npotScale);
                targetHeight = ScaleToPowerOfTwo(height, npotScale);
            }

            int maxSize = (int)maxTextureSize;
            if (targetWidth > maxSize || targetHeight > maxSize)
            {
                float scale = (float)maxSize / Math.Max(targetWidth, targetHeight);
                targetWidth = Math.Max(1, Math.Min(maxSize, (int)Math.Round(targetWidth * scale)));
                targetHeight = Math.Max(1, Math.Min(maxSize, (int)Math.Round(targetHeight * scale)));
            }

            pixels = Resize(pixels, width, height, targetWidth, targetHeight, channels, sampleFormat);
            width = targetWidth;
            height = targetHeight;

            bool hasAlpha = img.Channels == QoiChannels.Rgba;
            // HDR and single channel data is linear
            bool linear = !sRGBTexture || textureType == TextureImporterType.NormalMap || hdr || single;
            var format = sampleFormat switch
            {
                QoiSampleFormat.Half => single ? RHalf : RGBAHalf,
                QoiSampleFormat.Float => single ? RFloat : RGBAFloat,
                _ => single ? R8 : hasAlpha ? RGBA32 : RGB24
            };

            var tex = new Texture2D(width, height, format, mipMapEnabled, linear)
            {
                name = Path.GetFileNameWithoutExtension(ctx.assetPath),
                alphaIsTransparency = alphaIsTransparency,
                wrapMode = wrapMode,
                filterMode = filterMode,
                anisoLevel = anisoLevel
            };
            tex.SetPixelData(pixels, 0);
            tex.Apply(mipMapEnabled);

            // Block compression needs dimensions that are multiples of 4.
            // HDR: BC6H (no alpha, so RGBA and single channel HDR stay uncompressed). Single channel: BC4.
            bool canCompress = !hdr || (!single && !hasAlpha);
            if (compression != TextureImporterCompression.Uncompressed && canCompress && width % 4 == 0 && height % 4 == 0)
            {
                var compressedFormat = compression switch
                {
                    _ when hdr => BC6H,
                    _ when single => BC4,
                    TextureImporterCompression.CompressedHQ => BC7,
                    _ when hasAlpha => crunchedCompression ? DXT5Crunched : DXT5,
                    _ => crunchedCompression ? DXT1Crunched : DXT1
                };
                EditorUtility.CompressTexture(tex, compressedFormat, compressionQuality);
            }

            ctx.AddObjectToAsset("texture", tex);
            ctx.SetMainObject(tex);

            if (textureType == TextureImporterType.Sprite)
            {
                var sprite = Sprite.Create(tex, new Rect(0, 0, width, height), new Vector2(0.5f, 0.5f), 100);
                sprite.name = tex.name;
                ctx.AddObjectToAsset("sprite", sprite);
            }
        }

        private static int ScaleToPowerOfTwo(int size, TextureImporterNPOTScale scale)
        {
            if (scale == TextureImporterNPOTScale.None || (size & (size - 1)) == 0) return size;

            int larger = 1;
            while (larger < size) larger <<= 1;
            int smaller = larger >> 1;

            return scale switch
            {
                TextureImporterNPOTScale.ToLarger => larger,
                TextureImporterNPOTScale.ToSmaller => smaller,
                _ => size - smaller < larger - size ? smaller : larger // ToNearest, ties go up
            };
        }

        /// Separable tent filter resize. The filter widens when shrinking so every source pixel contributes.
        private static byte[] Resize(byte[] src, int srcWidth, int srcHeight, int dstWidth, int dstHeight, int channels, QoiSampleFormat format)
        {
            if (srcWidth == dstWidth && srcHeight == dstHeight) return src;

            var (xStart, xWeights) = Weights(srcWidth, dstWidth);
            var (yStart, yWeights) = Weights(srcHeight, dstHeight);
            var halfs = format == QoiSampleFormat.Half ? MemoryMarshal.Cast<byte, ushort>(src.AsSpan()).ToArray() : null;
            var floats = format == QoiSampleFormat.Float ? MemoryMarshal.Cast<byte, float>(src.AsSpan()).ToArray() : null;

            float Sample(int i) => format switch
            {
                QoiSampleFormat.Half => Mathf.HalfToFloat(halfs[i]),
                QoiSampleFormat.Float => floats[i],
                _ => src[i]
            };

            // Horizontal pass into a float buffer of dstWidth x srcHeight
            var tmp = new float[dstWidth * srcHeight * channels];
            for (int y = 0; y < srcHeight; y++)
            for (int x = 0; x < dstWidth; x++)
            {
                var w = xWeights[x];
                int o = (y * dstWidth + x) * channels;
                for (int k = 0; k < w.Length; k++)
                {
                    int s = (y * srcWidth + Math.Clamp(xStart[x] + k, 0, srcWidth - 1)) * channels;
                    for (int c = 0; c < channels; c++) tmp[o + c] += Sample(s + c) * w[k];
                }
            }

            // Vertical pass
            int count = dstWidth * dstHeight * channels;
            var dst = new byte[count * QoiCodec.BytesPerSample(format)];
            var dstHalfs = MemoryMarshal.Cast<byte, ushort>(dst.AsSpan());
            var dstFloats = MemoryMarshal.Cast<byte, float>(dst.AsSpan());
            for (int y = 0; y < dstHeight; y++)
            {
                var w = yWeights[y];
                for (int x = 0; x < dstWidth; x++)
                {
                    int o = (y * dstWidth + x) * channels;
                    for (int c = 0; c < channels; c++)
                    {
                        float sum = 0;
                        for (int k = 0; k < w.Length; k++)
                            sum += tmp[(Math.Clamp(yStart[y] + k, 0, srcHeight - 1) * dstWidth + x) * channels + c] * w[k];
                        switch (format)
                        {
                            case QoiSampleFormat.Half: dstHalfs[o + c] = Mathf.FloatToHalf(sum); break;
                            case QoiSampleFormat.Float: dstFloats[o + c] = sum; break;
                            default: dst[o + c] = (byte)Math.Clamp((int)(sum + 0.5f), 0, 255); break;
                        }
                    }
                }
            }
            return dst;
        }

        private static (int[] start, float[][] weights) Weights(int srcSize, int dstSize)
        {
            float scale = (float)srcSize / dstSize;
            float support = Math.Max(1f, scale);
            var start = new int[dstSize];
            var weights = new float[dstSize][];

            for (int i = 0; i < dstSize; i++)
            {
                float center = (i + 0.5f) * scale - 0.5f;
                int first = (int)Math.Floor(center - support) + 1;
                int last = (int)Math.Ceiling(center + support) - 1;
                var w = new float[last - first + 1];
                float total = 0;
                for (int k = 0; k < w.Length; k++)
                    total += w[k] = Math.Max(0f, 1f - Math.Abs(first + k - center) / support);
                for (int k = 0; k < w.Length; k++) w[k] /= total;
                start[i] = first;
                weights[i] = w;
            }
            return (start, weights);
        }

        [Serializable]
        public enum MaxTextureSize
        {
            x16 = 16,
            x32 = 32,
            x64 = 64,
            x128 = 128,
            x256 = 256,
            x512 = 512,
            x1024 = 1024,
            x2048 = 2048,
            x4096 = 4096,
            x8192 = 8192,
            x16384 = 16384
        }
    }
}
