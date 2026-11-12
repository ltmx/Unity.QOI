using System;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading.Tasks;

namespace LTMX.QOI
{
    /// <summary>
    /// QOI encoder / decoder (https://qoiformat.org/qoi-specification.pdf).
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
    /// <para>
    /// The encode / decode loops are Burst compiled (<see cref="QoiBurst"/>).
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
            int chunkCount = seekChunkPixels > 0 ? (streamPixels + seekChunkPixels - 1) / seekChunkPixels : 0;
            int[] chunkOffsets = chunkCount > 0 ? new int[chunkCount] : null;
            var args = new QoiBurst.EncodeArgs
            {
                Width = width, Height = height, SourceChannels = sourceChannels, BytesPerSample = bytesPerSample,
                SeekChunkPixels = seekChunkPixels, ChunkCount = chunkCount, Channels = channels, SampleFormat = sampleFormat,
                FlipY = (byte)(flipY ? 1 : 0)
            };

            int p;
            unsafe
            {
                fixed (byte* src = pixels)
                fixed (byte* dst = output)
                fixed (int* offsets = chunkOffsets)
                {
                    p = streamPixels >= MinParallelEncodePixels && Environment.ProcessorCount > 1
                        ? EncodeParallel(src, dst, offsets, args, streamPixels)
                        : QoiBurst.EncodeRange(src, dst, HeaderSize, offsets, null, ref args, 0, streamPixels);
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

        /// Images at least this large (in stream pixels) are encoded on multiple threads
        private const int MinParallelEncodePixels = 1 << 16;

        /// Smallest segment of a plain file encoded on its own thread
        private const int MinSegmentPixels = 1 << 14;

        /// <summary>
        /// Encodes segments of the stream in parallel, each one at its worst case offset, then moves them back to back.
        /// The bytes are exactly those of a sequential encode:
        /// <list type="bullet">
        /// <item>Seekable files: segments are batches of chunks, which are self-contained.</item>
        /// <item>Plain files: segments start where the pixel differs from the previous one, so no run crosses them, with the
        /// encoder's index at that point, rebuilt from the last pixel of every hash before it.</item>
        /// </list>
        /// The worst case layout sums the same per pixel and per chunk bounds as <see cref="MaxEncodedSize"/>.
        /// </summary>
        private static unsafe int EncodeParallel(byte* src, byte* dst, int* offsets, QoiBurst.EncodeArgs args,
            int streamPixels)
        {
            int chunkPixels = args.SeekChunkPixels;
            bool seekable = args.ChunkCount > 0;
            int targetSegments = Environment.ProcessorCount * 4;
            var source = (IntPtr)src;

            int[] bounds;
            uint[] states = null;
            if (seekable)
            {
                int chunksPerSegment = Math.Max(1, (args.ChunkCount + targetSegments - 1) / targetSegments);
                int segmentCount = (args.ChunkCount + chunksPerSegment - 1) / chunksPerSegment;
                bounds = new int[segmentCount + 1];
                for (int s = 0; s < segmentCount; s++) bounds[s] = s * chunksPerSegment * chunkPixels;
                bounds[segmentCount] = streamPixels;
            }
            else
            {
                int rangePixels = Math.Max(MinSegmentPixels, (streamPixels + targetSegments - 1) / targetSegments);
                int rangeCount = (streamPixels + rangePixels - 1) / rangePixels;
                var splits = new int[rangeCount];
                var firstNotBlack = new int[rangeCount];

                // Pass 1: where each range can start (its first pixel that doesn't continue a run), and the end of the opaque
                // black run that may start the stream: the encoder's run covers it, so these pixels never enter the index
                Parallel.For(0, rangeCount, r =>
                {
                    var a = args;
                    int from = r * rangePixels, to = Math.Min(from + rangePixels, streamPixels);
                    uint previous = r == 0 ? OpaqueBlack : QoiBurst.PixelAt((byte*)source, ref a, from - 1);
                    splits[r] = r == 0 ? 0 : QoiBurst.FindDifferent((byte*)source, ref a, from, to, previous);
                    firstNotBlack[r] = r > 0 && previous == OpaqueBlack
                        ? splits[r]
                        : QoiBurst.FindDifferent((byte*)source, ref a, from, to, OpaqueBlack);
                });
                int indexStart = streamPixels;
                for (int r = 0; r < rangeCount; r++)
                {
                    if (firstNotBlack[r] < 0) continue;
                    indexStart = firstNotBlack[r];
                    break;
                }

                // Pass 2: index entries left behind by each range
                var tables = new uint[rangeCount * 64];
                var found = new ulong[rangeCount];
                fixed (uint* tablePointer = tables)
                {
                    var tableBase = (IntPtr)tablePointer;
                    Parallel.For(0, rangeCount, r =>
                    {
                        var a = args;
                        int from = Math.Max(r * rangePixels, indexStart), to = Math.Min(r * rangePixels + rangePixels, streamPixels);
                        if (from < to) found[r] = QoiBurst.CollectIndex((byte*)source, ref a, from, to, (uint*)tableBase + r * 64);
                    });
                }

                // The index at a split merges the entries of every range before it. Pixels between a range start and its split
                // repeat the last pixel of the previous range, which is already one of its entries.
                var segmentStarts = new System.Collections.Generic.List<int> { 0 };
                var segmentStates = new System.Collections.Generic.List<uint>(rangeCount * 64);
                var index = new uint[64];
                segmentStates.AddRange(index);
                for (int r = 0; r < rangeCount; r++)
                {
                    if (r > 0 && splits[r] >= 0)
                    {
                        segmentStarts.Add(splits[r]);
                        segmentStates.AddRange(index);
                    }
                    for (int h = 0; h < 64; h++)
                        if ((found[r] >> h & 1) != 0) index[h] = tables[r * 64 + h];
                }
                segmentStarts.Add(streamPixels);
                bounds = segmentStarts.ToArray();
                states = segmentStates.ToArray();
            }

            int count = bounds.Length - 1;
            if (count == 1) return QoiBurst.EncodeRange(src, dst, HeaderSize, offsets, null, ref args, 0, streamPixels);

            long worstPerPixel = (int)args.Channels + 1;
            var regions = new int[count];
            long position = HeaderSize;
            for (int s = 0; s < count; s++)
            {
                regions[s] = (int)position;
                int pixels = bounds[s + 1] - bounds[s];
                position += pixels * worstPerPixel + (seekable ? (pixels + chunkPixels - 1) / chunkPixels : 0);
            }

            var ends = new int[count];
            var output = (IntPtr)dst;
            var chunkOffsets = (IntPtr)offsets;
            fixed (uint* statePointer = states)
            {
                var stateBase = (IntPtr)statePointer;
                Parallel.For(0, count, s =>
                {
                    var a = args;
                    uint* state = seekable || s == 0 ? null : (uint*)stateBase + s * 64;
                    ends[s] = QoiBurst.EncodeRange((byte*)source, (byte*)output, regions[s], (int*)chunkOffsets, state, ref a,
                        bounds[s], bounds[s + 1]);
                });
            }

            // Segments only move towards the start of the buffer: memmove semantics handle the overlap
            int p = ends[0];
            for (int s = 1; s < count; s++)
            {
                int length = ends[s] - regions[s];
                int shift = regions[s] - p;
                if (shift != 0)
                {
                    Buffer.MemoryCopy(dst + regions[s], dst + p, length, length);
                    if (seekable)
                    {
                        int last = (bounds[s + 1] + chunkPixels - 1) / chunkPixels;
                        for (int c = bounds[s] / chunkPixels; c < last; c++) offsets[c] -= shift;
                    }
                }
                p += length;
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
            else if (!parallel || !TryDecodePlainParallel(data, pixels, header, outputChannels, flipY))
            {
                DecodeRange(data, HeaderSize, data.Length, pixels, header, outputChannels, flipY, 0, header.StreamPixelCount);
            }

            return new QoiImage(header, pixels, outputChannels);
        }

        /// Plain files with at least this many op bytes are decoded on multiple threads
        private const int MinParallelDecodeBytes = 1 << 18;

        /// Op bytes of a segment of a plain file decoded on its own thread
        private const int MinDecodeSegmentBytes = 1 << 16;

        /// Ops the true and guessed decoders may run in lockstep before a segment is decoded in order instead
        private const int MaxSyncOps = 1 << 12;

        /// <summary>
        /// Decodes a plain file on multiple threads (see <see cref="QoiBurst"/>, parallel decoding of plain files).
        /// Returns false, possibly after writing some pixels, when the data looks corrupted or isn't worth splitting:
        /// the caller then decodes it in order, which reports errors exactly.
        /// </summary>
        private static unsafe bool TryDecodePlainParallel(byte[] data, byte[] pixels, QoiHeader header, int outputChannels, bool flipY)
        {
            int opsStart = HeaderSize, opsEnd = data.Length - PaddingSize;
            int segmentCount = Math.Min(Environment.ProcessorCount * 2, (opsEnd - opsStart) / MinDecodeSegmentBytes);
            if (Environment.ProcessorCount < 2 || opsEnd - opsStart < MinParallelDecodeBytes || segmentCount < 3)
                return false;

            var args = new QoiBurst.DecodeArgs
            {
                Width = header.Width, Height = header.Height, BytesPerSample = header.BytesPerSample, OutputChannels = outputChannels,
                Channels = header.Channels, SampleFormat = header.SampleFormat, FlipY = (byte)(flipY ? 1 : 0)
            };
            long total = header.StreamPixelCount;

            var bounds = new int[segmentCount + 1];
            for (int s = 0; s <= segmentCount; s++) bounds[s] = opsStart + (int)((long)(opsEnd - opsStart) * s / segmentCount);

            // States hold 65 entries per segment: the index then the pixel
            var scanStates = new uint[segmentCount * 65];
            var lastUses = new int[segmentCount * 64];
            var scanPixels = new long[segmentCount];
            var scanEnds = new int[segmentCount];
            var scanned = new bool[segmentCount];
            var states = new uint[segmentCount * 65];
            var starts = new int[segmentCount];
            var firstPixels = new long[segmentCount + 1];
            var decodedInOrder = new bool[segmentCount];

            fixed (byte* dataPointer = data)
            fixed (byte* outputPointer = pixels)
            fixed (uint* scanStatePointer = scanStates)
            fixed (int* lastUsePointer = lastUses)
            fixed (uint* statePointer = states)
            {
                var source = (IntPtr)dataPointer;
                var output = (IntPtr)outputPointer;
                var scanStateBase = (IntPtr)scanStatePointer;
                var lastUseBase = (IntPtr)lastUsePointer;
                var stateBase = (IntPtr)statePointer;
                int length = data.Length;

                // 1. Guessed runs of segments [from, to), from the initial state
                void Scan(int from, int to) => Parallel.For(from, to, s =>
                {
                    var a = args;
                    long n;
                    int end;
                    scanned[s] = 1 == QoiBurst.ScanSegment((byte*)source, length, bounds[s], bounds[s + 1], ref a,
                        (uint*)scanStateBase + s * 65, (int*)lastUseBase + s * 64, &n, &end);
                    scanPixels[s] = n;
                    scanEnds[s] = end;
                });

                // The first segment is decoded for real, while the second one is scanned. It's a probe: files made of small
                // differences rarely resynchronize, and are then decoded in order without wasting work.
                for (int k = 0; k < 64; k++) statePointer[65 + k] = 0;
                statePointer[65 + 64] = OpaqueBlack;
                bool firstOk = false;
                int firstDecoded = 0, firstEnd = 0;
                Parallel.Invoke(() =>
                {
                    var a = args;
                    int decoded, end;
                    firstOk = QoiBurst.DecodeSegment((byte*)source, length, opsStart, length, bounds[1], (byte*)output, ref a, 0,
                        (int)total, (uint*)stateBase + 65, &decoded, &end) == QoiBurst.DecodeOk;
                    firstDecoded = decoded;
                    firstEnd = end;
                }, () => Scan(1, 2));
                if (!firstOk) return false;

                // 2. True start state and pixel of every segment, in order
                decodedInOrder[0] = true;
                starts[0] = opsStart;
                starts[1] = firstEnd;
                firstPixels[1] = firstDecoded;

                int failures = 0;
                for (int s = 1; s < segmentCount - 1; s++)
                {
                    if (firstPixels[s] >= total) return false;
                    uint* current = statePointer + (s + 1) * 65;
                    Buffer.MemoryCopy(statePointer + s * 65, current, 65 * sizeof(uint), 65 * sizeof(uint));

                    long n = 0;
                    int end = 0;
                    int result = scanned[s]
                        ? QoiBurst.SyncSegment(dataPointer, length, bounds[s], bounds[s + 1], starts[s], ref args, current,
                            scanStatePointer + s * 65, lastUsePointer + s * 64, scanPixels[s], scanEnds[s], MaxSyncOps, &n, &end)
                        : QoiBurst.SyncGaveUp;
                    if (result == QoiBurst.SyncInvalid) return false;

                    if (result == QoiBurst.SyncGaveUp)
                    {
                        Buffer.MemoryCopy(statePointer + s * 65, current, 65 * sizeof(uint), 65 * sizeof(uint));
                        int decoded;
                        if (s == 1 || ++failures == 2)
                        {
                            // This file doesn't resynchronize: the following segments are decoded in order too
                            if (QoiBurst.DecodeSegment(dataPointer, length, starts[s], length, int.MaxValue, outputPointer, ref args,
                                    (int)firstPixels[s], (int)(total - firstPixels[s]), current, &decoded, &end) != QoiBurst.DecodeOk ||
                                decoded != total - firstPixels[s])
                                return false;
                            for (int t = s; t < segmentCount; t++) decodedInOrder[t] = true;
                            break;
                        }

                        // Decoded for real, in order, from the true start state
                        if (QoiBurst.DecodeSegment(dataPointer, length, starts[s], length, bounds[s + 1], outputPointer, ref args,
                                (int)firstPixels[s], (int)(total - firstPixels[s]), current, &decoded, &end) != QoiBurst.DecodeOk)
                            return false;
                        n = decoded;
                        decodedInOrder[s] = true;
                    }
                    else failures = 0;

                    if (s == 1) Scan(2, segmentCount - 1);
                    starts[s + 1] = end;
                    firstPixels[s + 1] = firstPixels[s] + n;
                }
                firstPixels[segmentCount] = total;
                if (!decodedInOrder[segmentCount - 1] && firstPixels[segmentCount - 1] >= total) return false;

                // 3. Every other segment, in parallel from its true start
                bool ok = true;
                Parallel.For(0, segmentCount, s =>
                {
                    if (decodedInOrder[s]) return;
                    var a = args;
                    int count = (int)(firstPixels[s + 1] - firstPixels[s]);
                    int decoded, end;
                    int result = QoiBurst.DecodeSegment((byte*)source, length, starts[s], length, int.MaxValue, (byte*)output, ref a,
                        (int)firstPixels[s], count, (uint*)stateBase + s * 65, &decoded, &end);
                    // Every segment must end exactly where the next one starts
                    if (result != QoiBurst.DecodeOk || decoded != count || (s < segmentCount - 1 && end != starts[s + 1]))
                        ok = false;
                });
                return ok;
            }
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
        private static unsafe void DecodeRange(ReadOnlySpan<byte> data, int p, int opsEnd, Span<byte> output, in QoiHeader header,
            int outputChannels, bool flipY, int start, int count)
        {
            // The Burst kernel trusts the output size, it only bounds checks reads of the (possibly corrupted) data
            if (output.Length < (long)header.PixelCount * outputChannels * header.BytesPerSample ||
                start < 0 || count < 0 || (long)start + count > header.StreamPixelCount)
                throw new ArgumentOutOfRangeException(nameof(output));

            var args = new QoiBurst.DecodeArgs
            {
                Width = header.Width, Height = header.Height, BytesPerSample = header.BytesPerSample, OutputChannels = outputChannels,
                Channels = header.Channels, SampleFormat = header.SampleFormat, FlipY = (byte)(flipY ? 1 : 0)
            };

            int result;
            fixed (byte* src = data)
            fixed (byte* dst = output)
                result = QoiBurst.DecodeRange(src, data.Length, p, opsEnd, dst, ref args, start, count);

            if (result == QoiBurst.DecodeTruncated)
                throw new QoiException("Unexpected end of QOI data.");
            if (result != QoiBurst.DecodeOk)
                throw new QoiException($"Invalid op 0x{result & 0xFF:X2} in a single channel QOI file.");
        }
    }
}
