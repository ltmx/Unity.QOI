using System;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Unity.Collections.LowLevel.Unsafe;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using static UnityEngine.TextureFormat;

namespace LTMX.QOI.Editor
{
    /// <summary>
    /// The importer's <see cref="QoiCompressor.Fast"/> compressor: mipmaps and block compression of 8-bit images, Burst
    /// compiled (<see cref="QoiBlockEncoder"/>) and spread over every core. Writes the blocks straight into the texture's
    /// memory, without <see cref="UnityEditor.EditorUtility.CompressTexture(Texture2D, TextureFormat, TextureCompressionQuality)"/>.
    /// </summary>
    internal static unsafe class QoiFastCompressor
    {
        // Source rows per parallel band (1 << BandShift): every level down to BandShift - 2 is built and encoded in the
        // band's task. Small enough to balance cores and stay in cache, large enough to keep small levels in bands.
        private const int BandShift = 5, BandRows = 1 << BandShift;

        // Block rows per task of the levels encoded after the bands
        private const int TailRowsPerTask = 4;

        // Pixels per task of the RGBA32 expansion
        private const int ExpandPixelsPerTask = 1 << 16;

        private static ushort[] _toLinear;
        private static byte[] _toSrgb;

        /// <summary>Formats the fast compressor writes. Others fall back to Unity's compressor.</summary>
        public static bool Supports(TextureFormat format) => format is DXT1 or DXT5 or BC4 or BC5 or BC7;

        /// <summary>Full mip chain length, down to 1x1.</summary>
        public static int MipCount(int width, int height) => 1 + (int)Math.Floor(Math.Log(Math.Max(width, height), 2));

        public static Texture2D CreateTexture2D(byte[] pixels, int channels, int width, int height, TextureFormat format,
            bool mipmaps, bool linear, bool normalMap)
        {
            int mipCount = mipmaps ? MipCount(width, height) : 1;
            var tex = new Texture2D(width, height, GetGraphicsFormat(format, linear), mipCount,
                mipCount > 1 ? TextureCreationFlags.MipChain : TextureCreationFlags.None);
            var blocks = new IntPtr[mipCount];
            for (int m = 0; m < mipCount; m++) blocks[m] = TextureMemory(tex.GetPixelData<byte>(m), LevelBytes(width, height, m, Code(format)));
            WithRgba32(pixels, channels, width * height, rgba =>
                Compress(rgba, width, height, Code(format), mipCount, MipMode(linear, normalMap), blocks));
            tex.Apply(false);
            return tex;
        }

        /// <summary><paramref name="faces"/>: the 6 faces in <see cref="CubemapFace"/> order.</summary>
        public static Cubemap CreateCubemap(byte[][] faces, int channels, int size, TextureFormat format, bool mipmaps, bool linear)
        {
            int mipCount = mipmaps ? MipCount(size, size) : 1;
            var cube = new Cubemap(size, GetGraphicsFormat(format, linear),
                mipCount > 1 ? TextureCreationFlags.MipChain : TextureCreationFlags.None, mipCount);
            var blocks = new IntPtr[mipCount];
            for (int f = 0; f < 6; f++)
            {
                for (int m = 0; m < mipCount; m++)
                    blocks[m] = TextureMemory(cube.GetPixelData<byte>(m, (CubemapFace)f), LevelBytes(size, size, m, Code(format)));
                WithRgba32(faces[f], channels, size * size, rgba =>
                    Compress(rgba, size, size, Code(format), mipCount, MipMode(linear, false), blocks));
            }
            cube.Apply(false);
            return cube;
        }

        /// <summary>Block compressed data of every mip, from RGBA32 pixels.</summary>
        public static byte[][] Compress(byte[] rgba, int width, int height, TextureFormat format, bool mipmaps, int mipMode)
        {
            int code = Code(format);
            int mipCount = mipmaps ? MipCount(width, height) : 1;
            var result = new byte[mipCount][];
            var handles = new GCHandle[mipCount + 1];
            try
            {
                var blocks = new IntPtr[mipCount];
                for (int m = 0; m < mipCount; m++)
                {
                    result[m] = new byte[LevelBytes(width, height, m, code)];
                    handles[m] = GCHandle.Alloc(result[m], GCHandleType.Pinned);
                    blocks[m] = handles[m].AddrOfPinnedObject();
                }
                handles[mipCount] = GCHandle.Alloc(rgba, GCHandleType.Pinned);
                Compress(handles[mipCount].AddrOfPinnedObject(), width, height, code, mipCount, mipMode, blocks);
            }
            finally
            {
                foreach (var handle in handles)
                    if (handle.IsAllocated) handle.Free();
            }
            return result;
        }

        // Writable pointer to a texture level's CPU copy (valid until the texture is applied or changed)
        private static IntPtr TextureMemory(Unity.Collections.NativeArray<byte> data, long bytes)
        {
            if (data.Length != bytes)
                throw new InvalidOperationException($"Texture level holds {data.Length} bytes, the compressor writes {bytes}");
            return (IntPtr)NativeArrayUnsafeUtility.GetUnsafePtr(data);
        }

        private static int Code(TextureFormat format) => format switch
        {
            DXT1 => QoiBlockEncoder.BC1,
            DXT5 => QoiBlockEncoder.BC3,
            BC4 => QoiBlockEncoder.BC4,
            BC5 => QoiBlockEncoder.BC5,
            BC7 => QoiBlockEncoder.BC7,
            _ => throw new ArgumentOutOfRangeException(nameof(format), format, "Not supported by the fast compressor")
        };

        // BC4 and BC5 have no sRGB variant
        private static GraphicsFormat GetGraphicsFormat(TextureFormat format, bool linear) =>
            GraphicsFormatUtility.GetGraphicsFormat(format, !linear && format is DXT1 or DXT5 or BC7);

        private static int MipMode(bool linear, bool normalMap) =>
            normalMap ? QoiBlockEncoder.MipNormal : linear ? QoiBlockEncoder.MipLinear : QoiBlockEncoder.MipSrgb;

        private static long LevelBytes(int width, int height, int level, int code) =>
            (long)((Math.Max(1, width >> level) + 3) >> 2) * ((Math.Max(1, height >> level) + 3) >> 2) * QoiBlockEncoder.BlockBytes(code);

        /// <summary>
        /// Runs <paramref name="body"/> on RGBA32 pixels: <paramref name="pixels"/> itself when it has 4 channels, else a
        /// temporary expansion (RGB gets an opaque alpha, a single channel is replicated to RGB).
        /// </summary>
        private static void WithRgba32(byte[] pixels, int channels, int count, Action<IntPtr> body)
        {
            fixed (byte* src = pixels)
            {
                if (channels == 4)
                {
                    body((IntPtr)src);
                    return;
                }

                IntPtr rgba = Marshal.AllocHGlobal((IntPtr)((long)count * 4));
                try
                {
                    var s = (IntPtr)src;
                    int tasks = (count + ExpandPixelsPerTask - 1) / ExpandPixelsPerTask;
                    Parallel.For(0, tasks, t => QoiBlockEncoder.ExpandToRgba((byte*)s, channels, (byte*)rgba,
                        (long)t * ExpandPixelsPerTask, Math.Min(count, (long)(t + 1) * ExpandPixelsPerTask)));
                    body(rgba);
                }
                finally
                {
                    Marshal.FreeHGlobal(rgba);
                }
            }
        }

        /// <summary>
        /// Builds the mip chain of RGBA32 pixels and writes every level's blocks to <paramref name="blocks"/>
        /// (<see cref="LevelBytes"/> each).
        /// </summary>
        private static void Compress(IntPtr rgba, int width, int height, int code, int mipCount, int mipMode, IntPtr[] blocks)
        {
            if (mipMode == QoiBlockEncoder.MipSrgb) BuildTables();

            var widths = new int[mipCount];
            var heights = new int[mipCount];
            long pixelBytes = 0;
            for (int m = 0; m < mipCount; m++)
            {
                widths[m] = Math.Max(1, width >> m);
                heights[m] = Math.Max(1, height >> m);
                if (m > 0) pixelBytes += (long)widths[m] * heights[m] * 4;
            }

            // Pixels of every level: the source, then the downsampled levels in one unmanaged buffer
            var pixels = new IntPtr[mipCount];
            pixels[0] = rgba;
            IntPtr mipPixels = pixelBytes > 0 ? Marshal.AllocHGlobal((IntPtr)pixelBytes) : IntPtr.Zero;
            try
            {
                long offset = 0;
                for (int m = 1; m < mipCount; m++)
                {
                    pixels[m] = (IntPtr)((byte*)mipPixels + offset);
                    offset += (long)widths[m] * heights[m] * 4;
                }

                fixed (ushort* toLinear = _toLinear)
                fixed (byte* toSrgb = _toSrgb)
                {
                    var lin = (IntPtr)toLinear;
                    var srgb = (IntPtr)toSrgb;

                    // Bands of BandRows source rows build and encode their part of every level they hold whole rows of,
                    // in one task while the rows are in cache: level m gets BandRows >> m rows of each band. A level
                    // reads only its own band's rows of the level above (2y and 2y + 1, clamped to the last row), so
                    // bands never wait on each other. Levels with less than 4 rows per band are encoded after.
                    int bandLevels = Math.Min(mipCount, BandShift + 1);
                    int encodeLevels = Math.Min(mipCount, BandShift - 1);
                    int bands = (height + BandRows - 1) >> BandShift;
                    void Band(int band)
                    {
                        for (int m = 0; m < bandLevels; m++)
                        {
                            int rowStart = (band << BandShift) >> m;
                            int rowEnd = Math.Min(heights[m], ((band + 1) << BandShift) >> m);
                            if (rowStart >= rowEnd) break;
                            if (m > 0)
                                QoiBlockEncoder.DownsampleRows((byte*)pixels[m - 1], widths[m - 1], heights[m - 1],
                                    (byte*)pixels[m], widths[m], mipMode, (ushort*)lin, (byte*)srgb, rowStart, rowEnd);
                            if (m < encodeLevels)
                                QoiBlockEncoder.EncodeBlockRows((byte*)pixels[m], widths[m], heights[m], code, (byte*)blocks[m],
                                    rowStart >> 2, (rowEnd + 3) >> 2);
                        }
                    }

                    if (bands > 1 && Environment.ProcessorCount > 1) Parallel.For(0, bands, Band);
                    else for (int b = 0; b < bands; b++) Band(b);

                    // Remaining small levels, at most height / BandRows rows each
                    for (int m = encodeLevels; m < mipCount; m++)
                    {
                        if (m >= bandLevels)
                            QoiBlockEncoder.DownsampleRows((byte*)pixels[m - 1], widths[m - 1], heights[m - 1],
                                (byte*)pixels[m], widths[m], mipMode, (ushort*)lin, (byte*)srgb, 0, heights[m]);
                        var p = pixels[m];
                        var b = blocks[m];
                        int lw = widths[m], lh = heights[m];
                        ForRows((lh + 3) >> 2, (from, to) => QoiBlockEncoder.EncodeBlockRows((byte*)p, lw, lh, code, (byte*)b, from, to));
                    }
                }
            }
            finally
            {
                if (mipPixels != IntPtr.Zero) Marshal.FreeHGlobal(mipPixels);
            }
        }

        private static void ForRows(int rows, Action<int, int> body)
        {
            int tasks = (rows + TailRowsPerTask - 1) / TailRowsPerTask;
            if (tasks <= 1 || Environment.ProcessorCount < 2) body(0, rows);
            else Parallel.For(0, tasks, t => body(t * TailRowsPerTask, Math.Min(rows, (t + 1) * TailRowsPerTask)));
        }

        private static void BuildTables()
        {
            if (_toSrgb != null) return;
            var toLinear = new ushort[256];
            for (int i = 0; i < 256; i++) toLinear[i] = (ushort)Math.Round(Mathf.GammaToLinearSpace(i / 255f) * 65535);
            var toSrgb = new byte[65536];
            for (int i = 0; i < 65536; i++)
                toSrgb[i] = (byte)Math.Clamp((int)(Mathf.LinearToGammaSpace(i / 65535f) * 255 + 0.5f), 0, 255);
            _toLinear = toLinear;
            _toSrgb = toSrgb;
        }
    }
}
