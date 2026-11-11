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
    [ScriptedImporter(5, "qoi")]
    public class QoiImporter : ScriptedImporter
    {
        // Texture import settings
        public TextureImporterType textureType = TextureImporterType.Default;

        /// <summary>Texture2D or Cube (Texture2DArray / 3D are not supported).</summary>
        public TextureImporterShape textureShape = TextureImporterShape.Texture2D;
        public CubemapLayout cubemapLayout = CubemapLayout.Auto;

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

            if (textureShape == TextureImporterShape.TextureCube)
            {
                ImportCubemap(ctx, img);
                return;
            }

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

            bool linear = IsLinear(img);
            var format = GetFormat(img);

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

            if (TryGetCompressedFormat(img, width, height, out var compressedFormat))
                EditorUtility.CompressTexture(tex, compressedFormat, compressionQuality);

            ctx.AddObjectToAsset("texture", tex);
            ctx.SetMainObject(tex);

            if (textureType == TextureImporterType.Sprite)
            {
                var sprite = Sprite.Create(tex, new Rect(0, 0, width, height), new Vector2(0.5f, 0.5f), 100);
                sprite.name = tex.name;
                ctx.AddObjectToAsset("sprite", sprite);
            }
        }

        // HDR and single channel data is linear
        private bool IsLinear(QoiImage img) =>
            !sRGBTexture || textureType == TextureImporterType.NormalMap || img.SampleFormat != QoiSampleFormat.Byte || img.Channels == QoiChannels.R;

        private static TextureFormat GetFormat(QoiImage img)
        {
            bool single = img.Channels == QoiChannels.R;
            return img.SampleFormat switch
            {
                QoiSampleFormat.Half => single ? RHalf : RGBAHalf,
                QoiSampleFormat.Float => single ? RFloat : RGBAFloat,
                _ => single ? R8 : img.Channels == QoiChannels.Rgba ? RGBA32 : RGB24
            };
        }

        // Block compression needs dimensions that are multiples of 4.
        // HDR: BC6H (no alpha, so RGBA and single channel HDR stay uncompressed). Single channel: BC4.
        private bool TryGetCompressedFormat(QoiImage img, int width, int height, out TextureFormat format)
        {
            bool hdr = img.SampleFormat != QoiSampleFormat.Byte;
            bool single = img.Channels == QoiChannels.R;
            bool hasAlpha = img.Channels == QoiChannels.Rgba;
            format = compression switch
            {
                _ when hdr => BC6H,
                _ when single => BC4,
                TextureImporterCompression.CompressedHQ => BC7,
                _ when hasAlpha => crunchedCompression ? DXT5Crunched : DXT5,
                _ => crunchedCompression ? DXT1Crunched : DXT1
            };
            bool canCompress = !hdr || (!single && !hasAlpha);
            return compression != TextureImporterCompression.Uncompressed && canCompress && width % 4 == 0 && height % 4 == 0;
        }

        // -------------------------------------------------------------------------------------------------------------
        // Cubemaps

        /// <summary>Source layout of a cubemap import.</summary>
        public enum CubemapLayout
        {
            /// <summary>2:1 = latitude-longitude, 6:1 = horizontal strip, 1:6 = vertical strip.</summary>
            Auto,
            /// <summary>Equirectangular panorama. Same convention as Skybox/Panoramic: image centre = +X, top = +Y.</summary>
            LatitudeLongitude,
            /// <summary>Six square faces side by side: +X, -X, +Y, -Y, +Z, -Z.</summary>
            HorizontalStrip,
            /// <summary>Six square faces stacked from the top: +X, -X, +Y, -Y, +Z, -Z.</summary>
            VerticalStrip
        }

        private void ImportCubemap(AssetImportContext ctx, QoiImage img)
        {
            int w = img.Width, h = img.Height;
            var layout = cubemapLayout;
            if (layout == CubemapLayout.Auto)
            {
                if (w == 2 * h) layout = CubemapLayout.LatitudeLongitude;
                else if (w == 6 * h) layout = CubemapLayout.HorizontalStrip;
                else if (h == 6 * w) layout = CubemapLayout.VerticalStrip;
                else
                {
                    ctx.LogImportError($"Cubemap '{ctx.assetPath}': cannot detect the layout of a {w}x{h} image " +
                                       "(expected 2:1 lat-long, 6:1 or 1:6 strip). Pick a layout explicitly.");
                    return;
                }
            }

            int faceSize = layout switch
            {
                CubemapLayout.LatitudeLongitude => w / 4, // matches the equator texel density
                CubemapLayout.HorizontalStrip => w / 6,
                _ => h / 6
            };
            faceSize = Math.Max(1, Math.Min(faceSize, (int)maxTextureSize));

            // 3 channel 8-bit faces are expanded to RGBA: there is no 24-bit cubemap graphics format.
            var format = GetFormat(img);
            if (format == RGB24) format = RGBA32;
            var graphicsFormat = UnityEngine.Experimental.Rendering.GraphicsFormatUtility.GetGraphicsFormat(format, !IsLinear(img));
            var flags = mipMapEnabled
                ? UnityEngine.Experimental.Rendering.TextureCreationFlags.MipChain
                : UnityEngine.Experimental.Rendering.TextureCreationFlags.None;
            var cube = new Cubemap(faceSize, graphicsFormat, flags)
            {
                name = Path.GetFileNameWithoutExtension(ctx.assetPath),
                wrapMode = TextureWrapMode.Clamp,
                filterMode = filterMode,
                anisoLevel = anisoLevel
            };

            for (int f = 0; f < 6; f++)
                cube.SetPixelData(BuildFace(img, layout, f, faceSize), 0, (CubemapFace)f);
            cube.Apply(mipMapEnabled);

            if (TryGetCompressedFormat(img, faceSize, faceSize, out var compressedFormat))
                EditorUtility.CompressCubemapTexture(cube, compressedFormat, compressionQuality);

            ctx.AddObjectToAsset("texture", cube);
            ctx.SetMainObject(cube);
        }

        /// One cubemap face (Unity / D3D face orientation, first row = top), bilinear sampled from the source image.
        private static byte[] BuildFace(QoiImage img, CubemapLayout layout, int face, int n)
        {
            int w = img.Width, h = img.Height, ch = img.PixelChannels;
            var fmt = img.SampleFormat;
            byte[] src = img.Pixels;
            int bps = QoiCodec.BytesPerSample(fmt);
            int dch = ch == 3 ? 4 : ch; // 8-bit RGB -> RGBA (see ImportCubemap)
            var dst = new byte[n * n * dch * bps];

            System.Threading.Tasks.Parallel.For(0, n, j =>
            {
                var srcHalfs = MemoryMarshal.Cast<byte, ushort>(src.AsSpan());
                var srcFloats = MemoryMarshal.Cast<byte, float>(src.AsSpan());
                var dstHalfs = MemoryMarshal.Cast<byte, ushort>(dst.AsSpan());
                var dstFloats = MemoryMarshal.Cast<byte, float>(dst.AsSpan());
                Span<float> acc = stackalloc float[4];

                for (int i = 0; i < n; i++)
                {
                    float a = 2f * (i + 0.5f) / n - 1f;   // s, left -> right
                    float b = 2f * (j + 0.5f) / n - 1f;   // t, top -> bottom

                    // Source pixel position (x right, y up from the bottom row, as decoded with flipY)
                    float x, y;
                    bool wrapX = false;
                    float minX = 0, maxX = w - 1, minY = 0, maxY = h - 1;
                    if (layout == CubemapLayout.LatitudeLongitude)
                    {
                        Vector3 d = face switch
                        {
                            0 => new Vector3(1f, -b, -a),
                            1 => new Vector3(-1f, -b, a),
                            2 => new Vector3(a, 1f, b),
                            3 => new Vector3(a, -1f, -b),
                            4 => new Vector3(a, -b, 1f),
                            _ => new Vector3(-a, -b, -1f)
                        };
                        d.Normalize();
                        float u = 0.5f - Mathf.Atan2(d.z, d.x) / (2f * Mathf.PI);
                        float v = 1f - Mathf.Acos(Mathf.Clamp(d.y, -1f, 1f)) / Mathf.PI;
                        x = u * w - 0.5f;
                        y = v * h - 0.5f;
                        wrapX = true;
                    }
                    else
                    {
                        // Face-local pixel, then offset into the strip. Strip rows are counted from the top.
                        float fx = (a + 1f) * 0.5f * (layout == CubemapLayout.HorizontalStrip ? w / 6 : w) - 0.5f;
                        float fyTop = (b + 1f) * 0.5f * (layout == CubemapLayout.HorizontalStrip ? h : h / 6) - 0.5f;
                        if (layout == CubemapLayout.HorizontalStrip)
                        {
                            int fw = w / 6;
                            x = face * fw + fx;
                            y = h - 1 - fyTop;
                            minX = face * fw; maxX = minX + fw - 1;
                        }
                        else
                        {
                            int fh = h / 6;
                            x = fx;
                            float yTop = face * fh + fyTop;
                            y = h - 1 - yTop;
                            maxY = h - 1 - face * fh; minY = maxY - fh + 1;
                        }
                    }

                    int x0 = (int)Math.Floor(x), y0 = (int)Math.Floor(y);
                    float tx = x - x0, ty = y - y0;
                    acc.Clear();
                    for (int k = 0; k < 4; k++)
                    {
                        int sx = x0 + (k & 1), sy = y0 + (k >> 1);
                        float wgt = ((k & 1) != 0 ? tx : 1f - tx) * ((k >> 1) != 0 ? ty : 1f - ty);
                        if (wrapX) sx = ((sx % w) + w) % w;
                        else sx = (int)Math.Clamp(sx, minX, maxX);
                        sy = (int)Math.Clamp(sy, minY, maxY);
                        int si = (sy * w + sx) * ch;
                        for (int c = 0; c < ch; c++)
                        {
                            float val = fmt switch
                            {
                                QoiSampleFormat.Half => Mathf.HalfToFloat(srcHalfs[si + c]),
                                QoiSampleFormat.Float => srcFloats[si + c],
                                _ => src[si + c]
                            };
                            acc[c] += val * wgt;
                        }
                    }

                    int di = (j * n + i) * dch;
                    if (dch != ch) acc[3] = 255f;
                    for (int c = 0; c < dch; c++)
                    {
                        switch (fmt)
                        {
                            case QoiSampleFormat.Half: dstHalfs[di + c] = Mathf.FloatToHalf(acc[c]); break;
                            case QoiSampleFormat.Float: dstFloats[di + c] = acc[c]; break;
                            default: dst[di + c] = (byte)Math.Clamp((int)(acc[c] + 0.5f), 0, 255); break;
                        }
                    }
                }
            });
            return dst;
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
