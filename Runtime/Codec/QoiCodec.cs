using System;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;

namespace LTMX.QOI
{
    /// <summary>
    /// Dependency free QOI encoder / decoder (https://qoiformat.org/qoi-specification.pdf).
    /// <para>
    /// Pixels are packed as <c>r | g &lt;&lt; 8 | b &lt;&lt; 16 | a &lt;&lt; 24</c> internally.
    /// <c>flipY</c> converts between QOI row order (top to bottom) and Unity row order (bottom to top).
    /// </para>
    /// <para>
    /// Seekable files: when encoding with <c>seekChunkPixels &gt; 0</c>, every chunk of N pixels starts with a
    /// QOI_OP_RGBA, never continues a run from the previous chunk and never references index entries written
    /// before the chunk. The op stream is therefore a 100% valid QOI stream that any decoder reads, but each
    /// chunk can also be decoded on its own. A small trailer appended after the end marker stores the byte
    /// offset of every chunk, enabling multithreaded and compute shader decoding:
    /// <code>
    /// header | ops | end marker | u32 offsets[chunkCount] | u32 chunkPixels | u32 chunkCount | "qsek" | end marker
    /// </code>
    /// Decoders that know nothing about the trailer stop after the last pixel and ignore it.
    /// </para>
    /// </summary>
    public static class QoiCodec
    {
        public const int HeaderSize = 14;
        public const int PaddingSize = 8;
        public const int DefaultChunkPixels = 4096;

        /// Guards against files whose worst case encoded size would not fit in a 2GB array.
        public const long MaxPixels = 400_000_000;

        public const uint Magic = 0x716F6966;     // "qoif"
        public const uint SeekMagic = 0x7173656B; // "qsek"

        internal const int OpIndex = 0x00;
        internal const int OpDiff = 0x40;
        internal const int OpLuma = 0x80;
        internal const int OpRun = 0xC0;
        internal const int OpRgb = 0xFE;
        internal const int OpRgba = 0xFF;
        internal const int Mask2 = 0xC0;
        internal const uint OpaqueBlack = 0xFF000000;

        // Seek trailer without the offsets: chunkPixels, chunkCount, magic, end marker
        private const int SeekFooterSize = 12 + PaddingSize;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int Hash(uint px) =>
            (int)(((px & 0xFF) * 3 + (px >> 8 & 0xFF) * 5 + (px >> 16 & 0xFF) * 7 + (px >> 24) * 11) & 63);

        // Header ---------------------------------------------------------------------------------------------

        public static bool TryReadHeader(ReadOnlySpan<byte> data, out QoiHeader header)
        {
            header = default;
            if (data.Length < HeaderSize + PaddingSize || BinaryPrimitives.ReadUInt32BigEndian(data) != Magic)
                return false;

            uint width = BinaryPrimitives.ReadUInt32BigEndian(data.Slice(4));
            uint height = BinaryPrimitives.ReadUInt32BigEndian(data.Slice(8));
            byte channels = data[12];

            if (width == 0 || height == 0 || (ulong)width * height > MaxPixels || channels is not (3 or 4))
                return false;

            // The color space byte is informative only, anything that is not linear is treated as sRGB
            var colorSpace = data[13] == 1 ? QoiColorSpace.Linear : QoiColorSpace.SRgb;
            header = new QoiHeader((int)width, (int)height, (QoiChannels)channels, colorSpace);
            return true;
        }

        public static QoiHeader ReadHeader(ReadOnlySpan<byte> data)
        {
            if (!TryReadHeader(data, out var header))
                throw new QoiException("Invalid QOI header (bad magic, size or channel count).");
            return header;
        }

        // Encoding -------------------------------------------------------------------------------------------

        /// <summary>
        /// Upper bound of the encoded size, use it to allocate a buffer for <see cref="Encode(ReadOnlySpan{byte},Span{byte},int,int,int,QoiChannels,QoiColorSpace,bool,int)"/>.
        /// </summary>
        public static int MaxEncodedSize(int width, int height, QoiChannels channels, int seekChunkPixels = 0)
        {
            long pixelCount = (long)width * height;
            long chunkCount = seekChunkPixels > 0 ? (pixelCount + seekChunkPixels - 1) / seekChunkPixels : 0;
            // RGBA ops can only appear when alpha changes, so RGB files are bounded by 4 bytes per pixel,
            // plus one extra byte for every seekable chunk that starts with a 5 byte QOI_OP_RGBA.
            long size = HeaderSize + pixelCount * ((int)channels + 1) + chunkCount + PaddingSize;
            if (chunkCount > 0) size += chunkCount * 4 + SeekFooterSize;
            return size > int.MaxValue ? throw new QoiException("Image too large to encode.") : (int)size;
        }

        /// <summary>
        /// Encodes raw pixels into a new QOI file.
        /// </summary>
        /// <param name="pixels">Tightly packed source pixels, <paramref name="sourceChannels"/> bytes per pixel (RGB or RGBA order).</param>
        /// <param name="sourceChannels">3 or 4 bytes per source pixel.</param>
        /// <param name="channels">Channels written in the header. When <see cref="QoiChannels.Rgb"/>, source alpha is ignored.</param>
        /// <param name="flipY">True when the source rows are stored bottom to top (Unity textures).</param>
        /// <param name="seekChunkPixels">0 for a plain QOI file, otherwise the chunk size of a seekable file.</param>
        public static byte[] Encode(ReadOnlySpan<byte> pixels, int width, int height, int sourceChannels, QoiChannels channels,
            QoiColorSpace colorSpace = QoiColorSpace.SRgb, bool flipY = false, int seekChunkPixels = 0)
        {
            var buffer = new byte[MaxEncodedSize(width, height, channels, seekChunkPixels)];
            int size = Encode(pixels, buffer, width, height, sourceChannels, channels, colorSpace, flipY, seekChunkPixels);
            Array.Resize(ref buffer, size);
            return buffer;
        }

        /// <summary>
        /// Encodes raw pixels into <paramref name="output"/> and returns the number of bytes written.
        /// <paramref name="output"/> must be at least <see cref="MaxEncodedSize"/> bytes long.
        /// </summary>
        public static int Encode(ReadOnlySpan<byte> pixels, Span<byte> output, int width, int height, int sourceChannels, QoiChannels channels,
            QoiColorSpace colorSpace = QoiColorSpace.SRgb, bool flipY = false, int seekChunkPixels = 0)
        {
            if (width <= 0 || height <= 0 || (long)width * height > MaxPixels)
                throw new QoiException($"Invalid image size: {width}x{height}.");
            if (sourceChannels is not (3 or 4))
                throw new QoiException($"Invalid source channel count: {sourceChannels}.");
            if (channels is not (QoiChannels.Rgb or QoiChannels.Rgba))
                throw new QoiException($"Invalid channel count: {channels}.");
            if (pixels.Length < width * height * sourceChannels)
                throw new QoiException($"Pixel buffer too small: {pixels.Length} bytes for {width}x{height}x{sourceChannels}.");
            if (seekChunkPixels < 0)
                throw new QoiException($"Invalid seek chunk size: {seekChunkPixels}.");
            if (output.Length < MaxEncodedSize(width, height, channels, seekChunkPixels))
                throw new QoiException("Output buffer too small, use MaxEncodedSize.");

            BinaryPrimitives.WriteUInt32BigEndian(output, Magic);
            BinaryPrimitives.WriteUInt32BigEndian(output.Slice(4), (uint)width);
            BinaryPrimitives.WriteUInt32BigEndian(output.Slice(8), (uint)height);
            output[12] = (byte)channels;
            output[13] = (byte)colorSpace;

            int pixelCount = width * height;
            int lastPixel = pixelCount - 1;
            int chunkCount = seekChunkPixels > 0 ? (pixelCount + seekChunkPixels - 1) / seekChunkPixels : 0;
            int[] chunkOffsets = chunkCount > 0 ? new int[chunkCount] : null;
            int nextChunkStart = chunkCount > 1 ? seekChunkPixels : int.MaxValue;
            int chunk = 1;
            if (chunkOffsets != null) chunkOffsets[0] = HeaderSize;

            Span<uint> index = stackalloc uint[64];
            // Bit set of index entries the decoder is guaranteed to share with us. A plain QOI decoder starts with a
            // zeroed index, so every entry is valid. A seekable chunk only trusts entries written inside the chunk.
            ulong valid = ulong.MaxValue;
            bool readAlpha = channels == QoiChannels.Rgba && sourceChannels == 4;
            uint prev = OpaqueBlack;
            int run = 0;
            int p = HeaderSize;
            int i = 0;

            for (int y = 0; y < height; y++)
            {
                int s = (flipY ? height - 1 - y : y) * width * sourceChannels;
                for (int x = 0; x < width; x++, i++, s += sourceChannels)
                {
                    uint px = pixels[s] | (uint)pixels[s + 1] << 8 | (uint)pixels[s + 2] << 16 |
                              (readAlpha ? pixels[s + 3] : 255u) << 24;

                    if (i == nextChunkStart)
                    {
                        // Seekable chunk boundary: close the run and restart from a self-contained QOI_OP_RGBA
                        if (run > 0)
                        {
                            output[p++] = (byte)(OpRun | (run - 1));
                            run = 0;
                        }
                        chunkOffsets[chunk++] = p;
                        nextChunkStart = chunk < chunkCount ? nextChunkStart + seekChunkPixels : int.MaxValue;

                        output[p++] = OpRgba;
                        output[p++] = (byte)px;
                        output[p++] = (byte)(px >> 8);
                        output[p++] = (byte)(px >> 16);
                        output[p++] = (byte)(px >> 24);
                        int h0 = Hash(px);
                        index[h0] = px;
                        valid = 1UL << h0;
                        prev = px;
                        continue;
                    }

                    if (px == prev)
                    {
                        run++;
                        if (run == 62 || i == lastPixel)
                        {
                            output[p++] = (byte)(OpRun | (run - 1));
                            run = 0;
                        }
                        continue;
                    }

                    if (run > 0)
                    {
                        output[p++] = (byte)(OpRun | (run - 1));
                        run = 0;
                    }

                    int h = Hash(px);
                    if (index[h] == px && (valid >> h & 1) != 0)
                    {
                        output[p++] = (byte)(OpIndex | h);
                    }
                    else
                    {
                        index[h] = px;
                        valid |= 1UL << h;

                        if ((px ^ prev) >> 24 == 0)
                        {
                            // Differences wrap around like the reference implementation's signed chars
                            int vr = (sbyte)(px - prev);
                            int vg = (sbyte)((px >> 8) - (prev >> 8));
                            int vb = (sbyte)((px >> 16) - (prev >> 16));
                            int vgr = (sbyte)(vr - vg);
                            int vgb = (sbyte)(vb - vg);

                            if (vr is > -3 and < 2 && vg is > -3 and < 2 && vb is > -3 and < 2)
                            {
                                output[p++] = (byte)(OpDiff | (vr + 2) << 4 | (vg + 2) << 2 | (vb + 2));
                            }
                            else if (vgr is > -9 and < 8 && vg is > -33 and < 32 && vgb is > -9 and < 8)
                            {
                                output[p++] = (byte)(OpLuma | (vg + 32));
                                output[p++] = (byte)((vgr + 8) << 4 | (vgb + 8));
                            }
                            else
                            {
                                output[p++] = OpRgb;
                                output[p++] = (byte)px;
                                output[p++] = (byte)(px >> 8);
                                output[p++] = (byte)(px >> 16);
                            }
                        }
                        else
                        {
                            output[p++] = OpRgba;
                            output[p++] = (byte)px;
                            output[p++] = (byte)(px >> 8);
                            output[p++] = (byte)(px >> 16);
                            output[p++] = (byte)(px >> 24);
                        }
                    }

                    prev = px;
                }
            }

            p = WritePadding(output, p);

            if (chunkOffsets != null)
            {
                foreach (int offset in chunkOffsets)
                {
                    BinaryPrimitives.WriteUInt32BigEndian(output.Slice(p), (uint)offset);
                    p += 4;
                }
                p = WriteSeekFooter(output, p, seekChunkPixels, chunkCount);
            }

            return p;
        }

        internal static int WritePadding(Span<byte> output, int p)
        {
            output.Slice(p, PaddingSize - 1).Clear();
            output[p + PaddingSize - 1] = 1;
            return p + PaddingSize;
        }

        internal static int WriteSeekFooter(Span<byte> output, int p, int chunkPixels, int chunkCount)
        {
            BinaryPrimitives.WriteUInt32BigEndian(output.Slice(p), (uint)chunkPixels);
            BinaryPrimitives.WriteUInt32BigEndian(output.Slice(p + 4), (uint)chunkCount);
            BinaryPrimitives.WriteUInt32BigEndian(output.Slice(p + 8), SeekMagic);
            return WritePadding(output, p + 12);
        }

        // Seek table -----------------------------------------------------------------------------------------

        /// <summary>
        /// Reads the seek table of a seekable file. Returns false for plain QOI files.
        /// <paramref name="chunkOffsets"/> has <c>chunkCount + 1</c> entries, the last one being the end of the op stream.
        /// </summary>
        public static bool TryReadSeekTable(ReadOnlySpan<byte> data, in QoiHeader header, out int chunkPixels, out int[] chunkOffsets)
        {
            chunkPixels = 0;
            chunkOffsets = null;

            int footer = data.Length - SeekFooterSize;
            if (footer < HeaderSize + PaddingSize || BinaryPrimitives.ReadUInt32BigEndian(data.Slice(footer + 8)) != SeekMagic)
                return false;

            uint pixels = BinaryPrimitives.ReadUInt32BigEndian(data.Slice(footer));
            uint count = BinaryPrimitives.ReadUInt32BigEndian(data.Slice(footer + 4));
            long pixelCount = header.PixelCount;
            if (pixels == 0 || count == 0 || (count - 1) * (long)pixels >= pixelCount || count * (long)pixels < pixelCount)
                return false;

            long table = footer - count * 4L;
            if (table < HeaderSize + PaddingSize) return false;
            int opsEnd = (int)table - PaddingSize;

            var offsets = new int[count + 1];
            int previous = HeaderSize;
            for (int c = 0; c < count; c++)
            {
                int offset = (int)BinaryPrimitives.ReadUInt32BigEndian(data.Slice((int)table + c * 4));
                if (offset < previous || offset >= opsEnd || (c == 0 && offset != HeaderSize)) return false;
                offsets[c] = previous = offset;
            }
            offsets[count] = opsEnd;

            chunkPixels = (int)pixels;
            chunkOffsets = offsets;
            return true;
        }

        public static bool IsSeekable(ReadOnlySpan<byte> data) =>
            TryReadHeader(data, out var header) && TryReadSeekTable(data, header, out _, out _);

        // Decoding -------------------------------------------------------------------------------------------

        /// <summary>
        /// Decodes a QOI file.
        /// </summary>
        /// <param name="outputChannels">Bytes per output pixel: 3, 4, or 0 to use the file's channel count.</param>
        /// <param name="flipY">True to output rows bottom to top (Unity textures).</param>
        /// <param name="parallel">Decode chunks of seekable files on multiple threads.</param>
        public static QoiImage Decode(byte[] data, int outputChannels = 0, bool flipY = false, bool parallel = true)
        {
            var header = ReadHeader(data);
            if (outputChannels == 0) outputChannels = (int)header.Channels;
            var pixels = new byte[header.PixelCount * outputChannels];

            if (parallel && TryReadSeekTable(data, header, out int chunkPixels, out int[] offsets) && offsets.Length > 2)
            {
                try
                {
                    Parallel.For(0, offsets.Length - 1, c =>
                    {
                        int start = c * chunkPixels;
                        int count = Math.Min(chunkPixels, header.PixelCount - start);
                        DecodeRange(data, offsets[c], offsets[c + 1], pixels, header, outputChannels, flipY, start, count);
                    });
                }
                catch (AggregateException e)
                {
                    throw e.InnerException is QoiException q ? q : new QoiException("Corrupted QOI data.", e.InnerException);
                }
            }
            else
            {
                DecodeInto(data, pixels, outputChannels, flipY);
            }

            return new QoiImage(header, pixels, outputChannels);
        }

        /// <summary>
        /// Decodes a QOI file into <paramref name="output"/> on the calling thread and returns its header.
        /// </summary>
        public static QoiHeader DecodeInto(ReadOnlySpan<byte> data, Span<byte> output, int outputChannels = 4, bool flipY = false)
        {
            var header = ReadHeader(data);
            if (outputChannels is not (3 or 4))
                throw new QoiException($"Invalid output channel count: {outputChannels}.");
            if (output.Length < header.PixelCount * outputChannels)
                throw new QoiException("Output buffer too small.");

            DecodeRange(data, HeaderSize, data.Length, output, header, outputChannels, flipY, 0, header.PixelCount);
            return header;
        }

        private static void DecodeRange(ReadOnlySpan<byte> data, int p, int opsEnd, Span<byte> output, in QoiHeader header,
            int outputChannels, bool flipY, int start, int count)
        {
            int width = header.Width;
            int height = header.Height;
            Span<uint> index = stackalloc uint[64];
            uint px = OpaqueBlack;
            int run = 0;

            int x = start % width;
            int row = flipY ? height - 1 - start / width : start / width;
            int rowStep = flipY ? -width * outputChannels : width * outputChannels;
            int o = (row * width + x) * outputChannels;

            try
            {
                for (int n = 0; n < count; n++)
                {
                    if (run > 0)
                    {
                        run--;
                    }
                    else
                    {
                        if (p >= opsEnd) throw new QoiException("Unexpected end of QOI data.");
                        int b1 = data[p++];

                        if (b1 == OpRgb)
                        {
                            px = (px & 0xFF000000) | data[p] | (uint)data[p + 1] << 8 | (uint)data[p + 2] << 16;
                            p += 3;
                        }
                        else if (b1 == OpRgba)
                        {
                            px = data[p] | (uint)data[p + 1] << 8 | (uint)data[p + 2] << 16 | (uint)data[p + 3] << 24;
                            p += 4;
                        }
                        else
                        {
                            switch (b1 & Mask2)
                            {
                                case OpIndex:
                                    px = index[b1];
                                    break;
                                case OpDiff:
                                    px = Add(px, (b1 >> 4 & 3) - 2, (b1 >> 2 & 3) - 2, (b1 & 3) - 2);
                                    break;
                                case OpLuma:
                                    int b2 = data[p++];
                                    int vg = (b1 & 0x3F) - 32;
                                    px = Add(px, vg - 8 + (b2 >> 4 & 0x0F), vg, vg - 8 + (b2 & 0x0F));
                                    break;
                                default:
                                    run = b1 & 0x3F;
                                    break;
                            }
                        }

                        index[Hash(px)] = px;
                    }

                    output[o] = (byte)px;
                    output[o + 1] = (byte)(px >> 8);
                    output[o + 2] = (byte)(px >> 16);
                    if (outputChannels == 4) output[o + 3] = (byte)(px >> 24);
                    o += outputChannels;

                    if (++x == width)
                    {
                        x = 0;
                        o += rowStep - width * outputChannels;
                    }
                }
            }
            catch (IndexOutOfRangeException e)
            {
                throw new QoiException("Unexpected end of QOI data.", e);
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static uint Add(uint px, int dr, int dg, int db) =>
            (px & 0xFF000000) |
            (uint)((int)(px & 0xFF) + dr) & 0xFF |
            ((uint)((int)(px >> 8 & 0xFF) + dg) & 0xFF) << 8 |
            ((uint)((int)(px >> 16 & 0xFF) + db) & 0xFF) << 16;
    }
}
