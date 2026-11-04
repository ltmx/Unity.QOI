using System;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
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
    /// <para>
    /// Extensions (not readable by standard QOI decoders, which reject them because of the channel byte):
    /// <list type="bullet">
    /// <item>The header channel byte is <c>channelCount | sampleFormat &lt;&lt; 4</c>. Standard files have a sample format of 0.</item>
    /// <item>Single channel files (<see cref="QoiChannels.R"/>): pixels are <c>v | 0xFF000000</c>. QOI_OP_INDEX and QOI_OP_RUN are
    /// unchanged, <c>0x40 | (d + 32)</c> stores a wrapping difference d in [-32, 31], <c>0xFE v</c> stores a literal value
    /// (and starts seekable chunks). QOI_OP_LUMA and QOI_OP_RGBA are not used.</item>
    /// <item>HDR files (<see cref="QoiSampleFormat.Half"/>, <see cref="QoiSampleFormat.Float"/>): samples are split into byte planes,
    /// most significant byte first. The op stream encodes plane 0 of every pixel, then plane 1 of every pixel..., as if it was
    /// an 8 bit image <c>bytesPerSample</c> times taller. Seekable chunks and their pixel counts refer to this stream.
    /// The alpha byte of RGB streams is always 255.</item>
    /// </list>
    /// </para>
    /// </summary>
    public static class QoiCodec
    {
        public const int HeaderSize = 14;
        public const int PaddingSize = 8;
        public const int DefaultChunkPixels = 4096;

        /// Guards against files whose worst case encoded size would not fit in a 2GB array (counted in stream pixels).
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

        /// Bytes per channel of a sample format, also the number of byte planes of its op stream
        public static int BytesPerSample(QoiSampleFormat format) => format switch
        {
            QoiSampleFormat.Byte => 1,
            QoiSampleFormat.Half => 2,
            QoiSampleFormat.Float => 4,
            _ => throw new QoiException($"Invalid sample format: {format}.")
        };

        /// Bits of the value 1 (opaque alpha) in a sample format
        internal static uint OneBits(QoiSampleFormat format) => format switch
        {
            QoiSampleFormat.Half => 0x3C00,
            QoiSampleFormat.Float => 0x3F800000,
            _ => 0xFF
        };

        // Header ---------------------------------------------------------------------------------------------

        public static bool TryReadHeader(ReadOnlySpan<byte> data, out QoiHeader header)
        {
            header = default;
            if (data.Length < HeaderSize + PaddingSize || BinaryPrimitives.ReadUInt32BigEndian(data) != Magic)
                return false;

            uint width = BinaryPrimitives.ReadUInt32BigEndian(data.Slice(4));
            uint height = BinaryPrimitives.ReadUInt32BigEndian(data.Slice(8));
            int channels = data[12] & 0x0F;
            int format = data[12] >> 4;

            if (width == 0 || height == 0 || channels is not (1 or 3 or 4) || format > (int)QoiSampleFormat.Float)
                return false;
            if ((ulong)width * height * (ulong)BytesPerSample((QoiSampleFormat)format) > MaxPixels)
                return false;

            // The color space byte is informative only, anything that is not linear is treated as sRGB
            var colorSpace = data[13] == 1 ? QoiColorSpace.Linear : QoiColorSpace.SRgb;
            header = new QoiHeader((int)width, (int)height, (QoiChannels)channels, colorSpace, (QoiSampleFormat)format);
            return true;
        }

        public static QoiHeader ReadHeader(ReadOnlySpan<byte> data)
        {
            if (!TryReadHeader(data, out var header))
                throw new QoiException("Invalid QOI header (bad magic, size, channel count or sample format).");
            return header;
        }

        public static void WriteHeader(Span<byte> output, in QoiHeader header)
        {
            BinaryPrimitives.WriteUInt32BigEndian(output, Magic);
            BinaryPrimitives.WriteUInt32BigEndian(output.Slice(4), (uint)header.Width);
            BinaryPrimitives.WriteUInt32BigEndian(output.Slice(8), (uint)header.Height);
            output[12] = (byte)((int)header.Channels | (int)header.SampleFormat << 4);
            output[13] = (byte)header.ColorSpace;
        }

        // Encoding -------------------------------------------------------------------------------------------

        /// <summary>
        /// Upper bound of the encoded size, use it to allocate a buffer for <see cref="Encode(ReadOnlySpan{byte},Span{byte},int,int,int,QoiChannels,QoiColorSpace,bool,int,QoiSampleFormat)"/>.
        /// </summary>
        public static int MaxEncodedSize(int width, int height, QoiChannels channels, int seekChunkPixels = 0,
            QoiSampleFormat sampleFormat = QoiSampleFormat.Byte)
        {
            long streamPixels = (long)width * height * BytesPerSample(sampleFormat);
            long chunkCount = seekChunkPixels > 0 ? (streamPixels + seekChunkPixels - 1) / seekChunkPixels : 0;
            // RGBA ops can only appear when alpha changes, so RGB files are bounded by 4 bytes per pixel,
            // plus one extra byte for every seekable chunk that starts with a 5 byte QOI_OP_RGBA.
            // Single channel files are bounded by 2 bytes per pixel.
            long size = HeaderSize + streamPixels * ((int)channels + 1) + chunkCount + PaddingSize;
            if (chunkCount > 0) size += chunkCount * 4 + SeekFooterSize;
            return size > int.MaxValue ? throw new QoiException("Image too large to encode.") : (int)size;
        }

        /// <summary>
        /// Encodes raw pixels into a new QOI file.
        /// </summary>
        /// <param name="pixels">Tightly packed source pixels, <paramref name="sourceChannels"/> samples per pixel (R, RGB or RGBA order),
        /// each sample in <paramref name="sampleFormat"/> (little endian bits for HDR formats).</param>
        /// <param name="sourceChannels">1, 3 or 4 samples per source pixel.</param>
        /// <param name="channels">Channels written in the file. Missing source channels are filled (single channel sources
        /// are replicated as gray, alpha is opaque), extra source channels are ignored.</param>
        /// <param name="flipY">True when the source rows are stored bottom to top (Unity textures).</param>
        /// <param name="seekChunkPixels">0 for a plain QOI file, otherwise the chunk size of a seekable file.</param>
        /// <param name="sampleFormat">Format of the source samples and of the file. Anything but <see cref="QoiSampleFormat.Byte"/> is an extension.</param>
        public static byte[] Encode(ReadOnlySpan<byte> pixels, int width, int height, int sourceChannels, QoiChannels channels,
            QoiColorSpace colorSpace = QoiColorSpace.SRgb, bool flipY = false, int seekChunkPixels = 0,
            QoiSampleFormat sampleFormat = QoiSampleFormat.Byte)
        {
            var buffer = new byte[MaxEncodedSize(width, height, channels, seekChunkPixels, sampleFormat)];
            int size = Encode(pixels, buffer, width, height, sourceChannels, channels, colorSpace, flipY, seekChunkPixels, sampleFormat);
            Array.Resize(ref buffer, size);
            return buffer;
        }

        /// <summary>
        /// Encodes half float pixels (raw IEEE 754 binary16 bits) into a new HDR QOI file.
        /// </summary>
        public static byte[] Encode(ReadOnlySpan<ushort> halfPixels, int width, int height, int sourceChannels, QoiChannels channels,
            bool flipY = false, int seekChunkPixels = 0) =>
            Encode(MemoryMarshal.AsBytes(halfPixels), width, height, sourceChannels, channels, QoiColorSpace.Linear, flipY,
                seekChunkPixels, QoiSampleFormat.Half);

        /// <summary>
        /// Encodes float pixels into a new HDR QOI file.
        /// </summary>
        public static byte[] Encode(ReadOnlySpan<float> floatPixels, int width, int height, int sourceChannels, QoiChannels channels,
            bool flipY = false, int seekChunkPixels = 0) =>
            Encode(MemoryMarshal.AsBytes(floatPixels), width, height, sourceChannels, channels, QoiColorSpace.Linear, flipY,
                seekChunkPixels, QoiSampleFormat.Float);

        /// <summary>
        /// Encodes raw pixels into <paramref name="output"/> and returns the number of bytes written.
        /// <paramref name="output"/> must be at least <see cref="MaxEncodedSize"/> bytes long.
        /// </summary>
        public static int Encode(ReadOnlySpan<byte> pixels, Span<byte> output, int width, int height, int sourceChannels, QoiChannels channels,
            QoiColorSpace colorSpace = QoiColorSpace.SRgb, bool flipY = false, int seekChunkPixels = 0,
            QoiSampleFormat sampleFormat = QoiSampleFormat.Byte)
        {
            int bytesPerSample = BytesPerSample(sampleFormat);
            if (width <= 0 || height <= 0 || (long)width * height * bytesPerSample > MaxPixels)
                throw new QoiException($"Invalid image size: {width}x{height}.");
            if (sourceChannels is not (1 or 3 or 4))
                throw new QoiException($"Invalid source channel count: {sourceChannels}.");
            if (channels is not (QoiChannels.R or QoiChannels.Rgb or QoiChannels.Rgba))
                throw new QoiException($"Invalid channel count: {channels}.");
            if (pixels.Length < (long)width * height * sourceChannels * bytesPerSample)
                throw new QoiException($"Pixel buffer too small: {pixels.Length} bytes for {width}x{height}x{sourceChannels} {sampleFormat}.");
            if (seekChunkPixels < 0)
                throw new QoiException($"Invalid seek chunk size: {seekChunkPixels}.");
            if (output.Length < MaxEncodedSize(width, height, channels, seekChunkPixels, sampleFormat))
                throw new QoiException("Output buffer too small, use MaxEncodedSize.");

            WriteHeader(output, new QoiHeader(width, height, channels, colorSpace, sampleFormat));

            int streamPixels = width * height * bytesPerSample;
            int lastPixel = streamPixels - 1;
            int chunkCount = seekChunkPixels > 0 ? (streamPixels + seekChunkPixels - 1) / seekChunkPixels : 0;
            int[] chunkOffsets = chunkCount > 0 ? new int[chunkCount] : null;
            int nextChunkStart = chunkCount > 1 ? seekChunkPixels : int.MaxValue;
            int chunk = 1;
            if (chunkOffsets != null) chunkOffsets[0] = HeaderSize;

            Span<uint> index = stackalloc uint[64];
            // Bit set of index entries the decoder is guaranteed to share with us. A plain QOI decoder starts with a
            // zeroed index, so every entry is valid. A seekable chunk only trusts entries written inside the chunk.
            ulong valid = ulong.MaxValue;
            bool single = channels == QoiChannels.R;
            bool readAlpha = channels == QoiChannels.Rgba && sourceChannels == 4;
            int stride = sourceChannels * bytesPerSample;
            int gOffset = sourceChannels == 1 ? 0 : bytesPerSample;
            int bOffset = sourceChannels == 1 ? 0 : 2 * bytesPerSample;
            int aOffset = 3 * bytesPerSample;
            uint one = OneBits(sampleFormat);
            uint prev = OpaqueBlack;
            int run = 0;
            int p = HeaderSize;
            int i = 0;

            for (int plane = 0; plane < bytesPerSample; plane++)
            {
                // Most significant byte first, samples are little endian
                int byteInSample = bytesPerSample - 1 - plane;
                // Alpha byte when the source has none: opaque in RGBA files, 255 in RGB streams
                uint alpha = (channels == QoiChannels.Rgba ? one >> (byteInSample * 8) & 0xFF : 255u) << 24;

                for (int y = 0; y < height; y++)
                {
                    int s = (flipY ? height - 1 - y : y) * width * stride + byteInSample;
                    for (int x = 0; x < width; x++, i++, s += stride)
                    {
                        uint px = single
                            ? pixels[s] | OpaqueBlack
                            : pixels[s] | (uint)pixels[s + gOffset] << 8 | (uint)pixels[s + bOffset] << 16 |
                              (readAlpha ? (uint)pixels[s + aOffset] << 24 : alpha);

                        if (i == nextChunkStart)
                        {
                            // Seekable chunk boundary: close the run and restart from a self-contained literal
                            if (run > 0)
                            {
                                output[p++] = (byte)(OpRun | (run - 1));
                                run = 0;
                            }
                            chunkOffsets[chunk++] = p;
                            nextChunkStart = chunk < chunkCount ? nextChunkStart + seekChunkPixels : int.MaxValue;

                            if (single)
                            {
                                output[p++] = OpRgb;
                                output[p++] = (byte)px;
                            }
                            else
                            {
                                output[p++] = OpRgba;
                                output[p++] = (byte)px;
                                output[p++] = (byte)(px >> 8);
                                output[p++] = (byte)(px >> 16);
                                output[p++] = (byte)(px >> 24);
                            }
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

                            if (single)
                            {
                                int d = (sbyte)(px - prev);
                                if (d is >= -32 and < 32)
                                {
                                    output[p++] = (byte)(OpDiff | (d + 32));
                                }
                                else
                                {
                                    output[p++] = OpRgb;
                                    output[p++] = (byte)px;
                                }
                            }
                            else if ((px ^ prev) >> 24 == 0)
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
        /// Chunks count stream pixels (<see cref="QoiHeader.StreamPixelCount"/>).
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
            long pixelCount = header.StreamPixelCount;
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
        /// Decodes a QOI file. Samples keep the file's <see cref="QoiSampleFormat"/>.
        /// </summary>
        /// <param name="outputChannels">Samples per output pixel: 1 (single channel files only), 3, 4, or 0 to use the file's
        /// channel count. Single channel files are replicated as gray, missing alpha is opaque.</param>
        /// <param name="flipY">True to output rows bottom to top (Unity textures).</param>
        /// <param name="parallel">Decode chunks of seekable files on multiple threads.</param>
        public static QoiImage Decode(byte[] data, int outputChannels = 0, bool flipY = false, bool parallel = true)
        {
            var header = ReadHeader(data);
            if (outputChannels == 0) outputChannels = (int)header.Channels;
            ValidateOutputChannels(header, outputChannels);
            var pixels = new byte[header.PixelCount * outputChannels * header.BytesPerSample];

            if (parallel && TryReadSeekTable(data, header, out int chunkPixels, out int[] offsets) && offsets.Length > 2)
            {
                try
                {
                    Parallel.For(0, offsets.Length - 1, c =>
                    {
                        int start = c * chunkPixels;
                        int count = Math.Min(chunkPixels, header.StreamPixelCount - start);
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
                DecodeRange(data, HeaderSize, data.Length, pixels, header, outputChannels, flipY, 0, header.StreamPixelCount);
            }

            return new QoiImage(header, pixels, outputChannels);
        }

        /// <summary>
        /// Decodes a QOI file into <paramref name="output"/> on the calling thread and returns its header.
        /// <paramref name="output"/> receives <c>PixelCount * outputChannels * BytesPerSample</c> bytes.
        /// </summary>
        public static QoiHeader DecodeInto(ReadOnlySpan<byte> data, Span<byte> output, int outputChannels = 4, bool flipY = false)
        {
            var header = ReadHeader(data);
            if (outputChannels == 0) outputChannels = (int)header.Channels;
            ValidateOutputChannels(header, outputChannels);
            if (output.Length < (long)header.PixelCount * outputChannels * header.BytesPerSample)
                throw new QoiException("Output buffer too small.");

            DecodeRange(data, HeaderSize, data.Length, output, header, outputChannels, flipY, 0, header.StreamPixelCount);
            return header;
        }

        private static void ValidateOutputChannels(in QoiHeader header, int outputChannels)
        {
            if (outputChannels is not (1 or 3 or 4) || (outputChannels == 1 && header.Channels != QoiChannels.R))
                throw new QoiException($"Invalid output channel count {outputChannels} for a {header.Channels} file.");
        }

        /// Decodes <paramref name="count"/> stream pixels starting at stream pixel <paramref name="start"/>
        private static void DecodeRange(ReadOnlySpan<byte> data, int p, int opsEnd, Span<byte> output, in QoiHeader header,
            int outputChannels, bool flipY, int start, int count)
        {
            int width = header.Width;
            int height = header.Height;
            int pixelCount = header.PixelCount;
            int bytesPerSample = header.BytesPerSample;
            bool single = header.Channels == QoiChannels.R;
            bool fileAlpha = header.Channels == QoiChannels.Rgba;
            uint one = OneBits(header.SampleFormat);

            int stride = outputChannels * bytesPerSample;
            int rowBytes = width * stride;
            // Added after the last pixel of a row, the offset already moved one row forward
            int rowJump = flipY ? -2 * rowBytes : 0;
            int g = bytesPerSample, b = 2 * bytesPerSample, a = 3 * bytesPerSample;

            int plane = start / pixelCount;
            int pixel = start - plane * pixelCount;
            int x = pixel % width;
            int row = flipY ? height - 1 - pixel / width : pixel / width;
            int byteInSample = bytesPerSample - 1 - plane;
            int o = row * rowBytes + x * stride + byteInSample;
            byte alpha = (byte)(one >> (byteInSample * 8));

            Span<uint> index = stackalloc uint[64];
            uint px = OpaqueBlack;
            int run = 0;

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

                        if (single)
                        {
                            if (b1 == OpRgb)
                                px = data[p++] | OpaqueBlack;
                            else if (b1 == OpRgba || (b1 & Mask2) == OpLuma)
                                throw new QoiException($"Invalid op 0x{b1:X2} in a single channel QOI file.");
                            else if ((b1 & Mask2) == OpIndex)
                                px = index[b1];
                            else if ((b1 & Mask2) == OpDiff)
                                px = (px + (uint)(b1 & 0x3F) - 32) & 0xFF | OpaqueBlack;
                            else
                                run = b1 & 0x3F;
                        }
                        else if (b1 == OpRgb)
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

                    if (single)
                    {
                        output[o] = (byte)px;
                        if (outputChannels >= 3)
                        {
                            output[o + g] = (byte)px;
                            output[o + b] = (byte)px;
                            if (outputChannels == 4) output[o + a] = alpha;
                        }
                    }
                    else
                    {
                        output[o] = (byte)px;
                        output[o + g] = (byte)(px >> 8);
                        output[o + b] = (byte)(px >> 16);
                        if (outputChannels == 4) output[o + a] = fileAlpha ? (byte)(px >> 24) : alpha;
                    }
                    o += stride;

                    if (++x == width)
                    {
                        x = 0;
                        o += rowJump;
                    }

                    if (++pixel == pixelCount && ++plane < bytesPerSample)
                    {
                        // Next byte plane, back to the first pixel
                        pixel = 0;
                        byteInSample--;
                        o = (flipY ? (height - 1) * rowBytes : 0) + byteInSample;
                        alpha = (byte)(one >> (byteInSample * 8));
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
