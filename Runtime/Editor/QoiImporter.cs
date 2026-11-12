using System;
using System.Collections.Generic;
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
    [ScriptedImporter(6, "qoi")]
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

        // Size and compression: the default settings, used by every platform without an override
        public MaxTextureSize maxTextureSize = MaxTextureSize.x2048;
        public QoiTextureFormat format = QoiTextureFormat.Automatic;
        public TextureImporterCompression compression = TextureImporterCompression.Compressed;
        public TextureCompressionQuality compressionQuality = TextureCompressionQuality.Normal;
        public bool crunchedCompression;
        public QoiCompressor compressor = QoiCompressor.Unity;

        /// <summary>Per platform overrides of the size and compression settings, by build target group name.</summary>
        public List<PlatformSettings> platformSettings = new();

        /// <summary>Size and compression settings of one build target group ("Standalone", "Android"…).</summary>
        [Serializable]
        public class PlatformSettings
        {
            public string buildTarget;
            public bool overridden;
            public MaxTextureSize maxTextureSize = MaxTextureSize.x2048;
            public QoiTextureFormat format = QoiTextureFormat.Automatic;
            public TextureImporterCompression compression = TextureImporterCompression.Compressed;
            public TextureCompressionQuality compressionQuality = TextureCompressionQuality.Normal;
            public bool crunchedCompression;
            public QoiCompressor compressor = QoiCompressor.Unity;
        }

        /// <summary>The settings a build target group imports with: its override, or the default settings.</summary>
        public PlatformSettings GetPlatformSettings(BuildTargetGroup group)
        {
            string name = group.ToString();
            foreach (var p in platformSettings)
                if (p.overridden && p.buildTarget == name)
                    return p;
            return new PlatformSettings
            {
                buildTarget = name, maxTextureSize = maxTextureSize, format = format, compression = compression,
                compressionQuality = compressionQuality, crunchedCompression = crunchedCompression, compressor = compressor
            };
        }

        // Settings of the platform being imported for (set in OnImportAsset)
        private PlatformSettings _settings;
        private bool _mobile;

        public override void OnImportAsset(AssetImportContext ctx)
        {
            // Reading the build target makes the asset reimport when the active platform changes
            var group = BuildPipeline.GetBuildTargetGroup(ctx.selectedBuildTarget);
            _settings = GetPlatformSettings(group);
            _mobile = IsMobile(group);

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

            int maxSize = (int)_settings.maxTextureSize;
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
            bool compressed = TryGetCompressedFormat(img, width, height, out var compressedFormat);
            Texture2D tex;
            if (compressed && UseFastCompressor(img, compressedFormat))
            {
                tex = QoiFastCompressor.CreateTexture2D(pixels, channels, width, height, compressedFormat, mipMapEnabled, linear,
                    textureType == TextureImporterType.NormalMap);
            }
            else
            {
                tex = new Texture2D(width, height, GetFormat(img), mipMapEnabled, linear);
                tex.SetPixelData(pixels, 0);
                tex.Apply(mipMapEnabled);
                if (compressed) EditorUtility.CompressTexture(tex, compressedFormat, _settings.compressionQuality);
            }
            tex.name = Path.GetFileNameWithoutExtension(ctx.assetPath);
            tex.alphaIsTransparency = alphaIsTransparency;
            tex.wrapMode = wrapMode;
            tex.filterMode = filterMode;
            tex.anisoLevel = anisoLevel;

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

        // The fast compressor handles 8-bit images in the formats it supports, crunch excepted
        private bool UseFastCompressor(QoiImage img, TextureFormat compressedFormat) =>
            _settings.compressor == QoiCompressor.Fast && img.SampleFormat == QoiSampleFormat.Byte && QoiFastCompressor.Supports(compressedFormat);

        private static bool IsMobile(BuildTargetGroup group) => group is BuildTargetGroup.Android or BuildTargetGroup.iOS;

        private bool TryGetCompressedFormat(QoiImage img, int width, int height, out TextureFormat result)
        {
            bool hdr = img.SampleFormat != QoiSampleFormat.Byte;
            bool single = img.Channels == QoiChannels.R;
            bool hasAlpha = img.Channels == QoiChannels.Rgba;
            return TryResolveFormat(_settings, _mobile, hdr, single, hasAlpha, textureType == TextureImporterType.NormalMap,
                width, height, out result);
        }

        /// <summary>
        /// The compressed format a texture imports to, or false when it stays uncompressed. Automatic picks:
        /// <list type="bullet">
        /// <item>Desktop: BC6H for HDR (no alpha, so RGBA and single channel HDR stay uncompressed), BC4 for single channel,
        /// BC7 for high quality, else DXT1 / DXT5 (crunched or not).</item>
        /// <item>Mobile: ASTC, 4x4 for high quality and normal maps, 8x8 for low quality, else 6x6. EAC R11 for single
        /// channel. HDR stays uncompressed: pick an ASTC HDR format explicitly when the target GPUs support it.</item>
        /// </list>
        /// BC, ETC and EAC formats need a width and height multiple of 4. ASTC takes any size.
        /// </summary>
        internal static bool TryResolveFormat(PlatformSettings s, bool mobile, bool hdr, bool single, bool hasAlpha, bool normalMap,
            int width, int height, out TextureFormat result)
        {
            result = default;
            var format = s.format;
            if (format == QoiTextureFormat.Uncompressed ||
                (format == QoiTextureFormat.Automatic && s.compression == TextureImporterCompression.Uncompressed))
                return false;

            if (format == QoiTextureFormat.Automatic)
            {
                if (mobile)
                {
                    if (hdr) return false;
                    format = single ? QoiTextureFormat.EAC_R
                        : s.crunchedCompression ? (hasAlpha ? QoiTextureFormat.ETC2_RGBA : QoiTextureFormat.ETC2_RGB)
                        : s.compression == TextureImporterCompression.CompressedHQ || normalMap ? QoiTextureFormat.ASTC_4x4
                        : s.compression == TextureImporterCompression.CompressedLQ ? QoiTextureFormat.ASTC_8x8
                        : QoiTextureFormat.ASTC_6x6;
                }
                else
                {
                    if (hdr && (single || hasAlpha)) return false;
                    format = hdr ? QoiTextureFormat.BC6H
                        : single ? QoiTextureFormat.BC4
                        : s.compression == TextureImporterCompression.CompressedHQ ? QoiTextureFormat.BC7
                        : hasAlpha ? QoiTextureFormat.BC3
                        : QoiTextureFormat.BC1;
                }
            }

            bool crunch = s.crunchedCompression;
            result = format switch
            {
                QoiTextureFormat.BC1 => crunch ? DXT1Crunched : DXT1,
                QoiTextureFormat.BC3 => crunch ? DXT5Crunched : DXT5,
                QoiTextureFormat.BC4 => BC4,
                QoiTextureFormat.BC5 => BC5,
                QoiTextureFormat.BC6H => BC6H,
                QoiTextureFormat.BC7 => BC7,
                QoiTextureFormat.ETC2_RGB => crunch ? ETC_RGB4Crunched : ETC2_RGB,
                QoiTextureFormat.ETC2_RGBA => crunch ? ETC2_RGBA8Crunched : ETC2_RGBA8,
                QoiTextureFormat.EAC_R => EAC_R,
                QoiTextureFormat.EAC_RG => EAC_RG,
                QoiTextureFormat.ASTC_4x4 => ASTC_4x4,
                QoiTextureFormat.ASTC_5x5 => ASTC_5x5,
                QoiTextureFormat.ASTC_6x6 => ASTC_6x6,
                QoiTextureFormat.ASTC_8x8 => ASTC_8x8,
                QoiTextureFormat.ASTC_10x10 => ASTC_10x10,
                QoiTextureFormat.ASTC_12x12 => ASTC_12x12,
                QoiTextureFormat.ASTC_HDR_4x4 => ASTC_HDR_4x4,
                QoiTextureFormat.ASTC_HDR_5x5 => ASTC_HDR_5x5,
                QoiTextureFormat.ASTC_HDR_6x6 => ASTC_HDR_6x6,
                QoiTextureFormat.ASTC_HDR_8x8 => ASTC_HDR_8x8,
                QoiTextureFormat.ASTC_HDR_10x10 => ASTC_HDR_10x10,
                QoiTextureFormat.ASTC_HDR_12x12 => ASTC_HDR_12x12,
                _ => throw new ArgumentOutOfRangeException(nameof(s), format, null)
            };
            bool astc = format >= QoiTextureFormat.ASTC_4x4;
            return astc || (width % 4 == 0 && height % 4 == 0);
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
            faceSize = Math.Max(1, Math.Min(faceSize, (int)_settings.maxTextureSize));

            bool compressed = TryGetCompressedFormat(img, faceSize, faceSize, out var compressedFormat);
            if (compressed && UseFastCompressor(img, compressedFormat))
            {
                var faces = new byte[6][];
                for (int f = 0; f < 6; f++) faces[f] = BuildFace(img, layout, f, faceSize);
                var fast = QoiFastCompressor.CreateCubemap(faces, img.PixelChannels == 3 ? 4 : img.PixelChannels, faceSize,
                    compressedFormat, mipMapEnabled, IsLinear(img));
                fast.name = Path.GetFileNameWithoutExtension(ctx.assetPath);
                fast.wrapMode = TextureWrapMode.Clamp;
                fast.filterMode = filterMode;
                fast.anisoLevel = anisoLevel;
                ctx.AddObjectToAsset("texture", fast);
                ctx.SetMainObject(fast);
                return;
            }

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

            if (compressed)
                EditorUtility.CompressCubemapTexture(cube, compressedFormat, _settings.compressionQuality);

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

    /// <summary>Block compressor of a QOI import.</summary>
    public enum QoiCompressor
    {
        /// <summary>Unity's compressor, <see cref="EditorUtility.CompressTexture(Texture2D, TextureFormat, TextureCompressionQuality)"/>.</summary>
        Unity = 0,
        /// <summary>
        /// The package's Burst compressor, multithreaded, with its own mipmaps. Formats it doesn't support yet fall back to
        /// Unity's (see <see cref="QoiFastCompressor.Supports"/>).
        /// </summary>
        Fast = 1
    }

    /// <summary>
    /// Texture format of a QOI import. Automatic follows the Compression setting and the platform: BC on desktop, ASTC on
    /// mobile (see <see cref="QoiImporter.TryResolveFormat"/>). Values are serialized: never renumber them.
    /// </summary>
    public enum QoiTextureFormat
    {
        Automatic = 0,
        Uncompressed = 1,

        [InspectorName("BC (Desktop)/BC1 · RGB")] BC1 = 10,
        [InspectorName("BC (Desktop)/BC3 · RGBA")] BC3 = 11,
        [InspectorName("BC (Desktop)/BC4 · R")] BC4 = 12,
        [InspectorName("BC (Desktop)/BC5 · RG, normal maps")] BC5 = 13,
        [InspectorName("BC (Desktop)/BC6H · RGB HDR")] BC6H = 14,
        [InspectorName("BC (Desktop)/BC7 · RGBA, high quality")] BC7 = 15,

        [InspectorName("ETC2 (Mobile)/ETC2 · RGB")] ETC2_RGB = 20,
        [InspectorName("ETC2 (Mobile)/ETC2 · RGBA")] ETC2_RGBA = 21,
        [InspectorName("ETC2 (Mobile)/EAC · R")] EAC_R = 22,
        [InspectorName("ETC2 (Mobile)/EAC · RG")] EAC_RG = 23,

        // ASTC formats must stay last: TryResolveFormat tests format >= ASTC_4x4
        [InspectorName("ASTC (Mobile)/4x4 · 8 bpp")] ASTC_4x4 = 30,
        [InspectorName("ASTC (Mobile)/5x5 · 5.12 bpp")] ASTC_5x5 = 31,
        [InspectorName("ASTC (Mobile)/6x6 · 3.56 bpp")] ASTC_6x6 = 32,
        [InspectorName("ASTC (Mobile)/8x8 · 2 bpp")] ASTC_8x8 = 33,
        [InspectorName("ASTC (Mobile)/10x10 · 1.28 bpp")] ASTC_10x10 = 34,
        [InspectorName("ASTC (Mobile)/12x12 · 0.89 bpp")] ASTC_12x12 = 35,

        [InspectorName("ASTC HDR (Mobile)/4x4 · 8 bpp")] ASTC_HDR_4x4 = 40,
        [InspectorName("ASTC HDR (Mobile)/5x5 · 5.12 bpp")] ASTC_HDR_5x5 = 41,
        [InspectorName("ASTC HDR (Mobile)/6x6 · 3.56 bpp")] ASTC_HDR_6x6 = 42,
        [InspectorName("ASTC HDR (Mobile)/8x8 · 2 bpp")] ASTC_HDR_8x8 = 43,
        [InspectorName("ASTC HDR (Mobile)/10x10 · 1.28 bpp")] ASTC_HDR_10x10 = 44,
        [InspectorName("ASTC HDR (Mobile)/12x12 · 0.89 bpp")] ASTC_HDR_12x12 = 45
    }
}
