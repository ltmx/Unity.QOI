using Unity.Burst;
using static LTMX.QOI.QoiCodec;

namespace LTMX.QOI
{
    /// <summary>
    /// Burst compiled hot loops of <see cref="QoiCodec"/>. Called directly from managed code: Burst swaps in the native
    /// version, and the same pointer code runs as plain C# when Burst compilation is disabled.
    /// <para>
    /// Callers validate every size beforehand. The decoder still bounds checks its reads, since the data may be corrupted.
    /// Multi byte loads and stores assume a little endian CPU, like every platform Unity supports.
    /// </para>
    /// </summary>
    [BurstCompile]
    internal static unsafe class QoiBurst
    {
        internal struct EncodeArgs
        {
            public int Width, Height, SourceChannels, BytesPerSample;
            public int SeekChunkPixels, ChunkCount;
            public QoiChannels Channels;
            public QoiSampleFormat SampleFormat;
            public byte FlipY;
        }

        internal struct DecodeArgs
        {
            public int Width, Height, BytesPerSample, OutputChannels;
            public QoiChannels Channels;
            public QoiSampleFormat SampleFormat;
            public byte FlipY;
        }

        internal const int DecodeOk = 0;
        internal const int DecodeTruncated = 1;
        /// <c>DecodeInvalidOp | op</c>: an op that single channel files don't use
        internal const int DecodeInvalidOp = 0x100;

        /// <summary>
        /// Encodes stream pixels [<paramref name="start"/>, <paramref name="end"/>) at <c>output + p</c> and returns the end of
        /// the written ops. For seekable files, <paramref name="start"/> must be a chunk boundary: ranges starting after the first
        /// chunk are self-contained, so they can be encoded in parallel and concatenated into the exact sequential bytes.
        /// The offsets of the chunks starting in the range are written to <paramref name="chunkOffsets"/>.
        /// <para>
        /// Plain files can be split too: <paramref name="state"/> (64 index entries) holds the encoder's index
        /// at <paramref name="start"/>, which must not continue a run (pixel <c>start</c> differs from <c>start - 1</c>).
        /// </para>
        /// <para>
        /// Ops may be written as 2 or 4 byte stores, up to 3 bytes past the returned end but never past the range's worst case
        /// size (<c>pixels * (channels + 1) + chunks</c>).
        /// </para>
        /// </summary>
        [BurstCompile(CompileSynchronously = true)]
        public static int EncodeRange(byte* pixels, byte* output, int p, int* chunkOffsets, uint* state, ref EncodeArgs args,
            int start, int end)
        {
            int width = args.Width, height = args.Height, bytesPerSample = args.BytesPerSample;
            int sourceChannels = args.SourceChannels, seekChunkPixels = args.SeekChunkPixels;
            bool flipY = args.FlipY != 0;
            int pixelCount = width * height;
            int streamPixels = pixelCount * bytesPerSample;

            // The first chunk is encoded like a plain stream, the others start with a literal
            int nextChunkStart = int.MaxValue;
            if (args.ChunkCount > 0)
            {
                if (start == 0)
                {
                    chunkOffsets[0] = p;
                    if (seekChunkPixels < streamPixels) nextChunkStart = seekChunkPixels;
                }
                else nextChunkStart = start;
            }

            uint* index = stackalloc uint[64];
            for (int k = 0; k < 64; k++) index[k] = 0;
            // Bit set of index entries the decoder is guaranteed to share with us. A plain QOI decoder starts with a
            // zeroed index, so every entry is valid. A seekable chunk only trusts entries written inside the chunk.
            ulong valid = ulong.MaxValue;
            bool single = args.Channels == QoiChannels.R;
            bool readAlpha = args.Channels == QoiChannels.Rgba && sourceChannels == 4;
            // 8 bit RGBA sources: one 4 byte load per pixel
            bool quad = sourceChannels == 4 && bytesPerSample == 1 && !single;
            int stride = sourceChannels * bytesPerSample;
            int gOffset = sourceChannels == 1 ? 0 : bytesPerSample;
            int bOffset = sourceChannels == 1 ? 0 : 2 * bytesPerSample;
            int aOffset = 3 * bytesPerSample;
            uint one = OneBits(args.SampleFormat);
            uint prev = OpaqueBlack;
            if (state != null)
            {
                for (int k = 0; k < 64; k++) index[k] = state[k];
                prev = LoadPixel(pixels, ref args, start - 1);
            }
            int run = 0;
            int i = start;

            int plane = start / pixelCount;
            int rest = start - plane * pixelCount;
            int y = rest / width;
            int x0 = rest - y * width;

            for (; plane < bytesPerSample && i < end; plane++, y = 0)
            {
                // Most significant byte first, samples are little endian
                int byteInSample = bytesPerSample - 1 - plane;
                // Alpha byte when the source has none: opaque in RGBA files, 255 in RGB streams
                uint alpha = (args.Channels == QoiChannels.Rgba ? one >> (byteInSample * 8) & 0xFF : 255u) << 24;
                uint keepMask = readAlpha ? 0xFFFFFFFF : 0x00FFFFFF;
                uint addAlpha = readAlpha ? 0 : alpha;

                for (; y < height && i < end; y++, x0 = 0)
                {
                    byte* s = pixels + ((long)(flipY ? height - 1 - y : y) * width + x0) * stride + byteInSample;
                    int xEnd = end - i < width - x0 ? x0 + (end - i) : width;

                    for (int x = x0; x < xEnd; x++, i++, s += stride)
                    {
                        uint px;
                        if (quad) px = (*(uint*)s & keepMask) | addAlpha;
                        else if (single) px = s[0] | OpaqueBlack;
                        else px = s[0] | (uint)s[gOffset] << 8 | (uint)s[bOffset] << 16 | (readAlpha ? (uint)s[aOffset] << 24 : alpha);

                        if (i == nextChunkStart)
                        {
                            // Seekable chunk boundary: close the run and restart from a self-contained literal
                            if (run > 0)
                            {
                                output[p++] = (byte)(OpRun | (run - 1));
                                run = 0;
                            }
                            chunkOffsets[i / seekChunkPixels] = p;
                            nextChunkStart = streamPixels - i > seekChunkPixels ? i + seekChunkPixels : int.MaxValue;

                            if (single)
                            {
                                output[p] = OpRgb;
                                output[p + 1] = (byte)px;
                                p += 2;
                            }
                            else
                            {
                                output[p] = OpRgba;
                                *(uint*)(output + p + 1) = px;
                                p += 5;
                            }
                            int h0 = Hash(px);
                            index[h0] = px;
                            valid = 1UL << h0;
                            prev = px;
                            continue;
                        }

                        if (px == prev)
                        {
                            if (++run == 62)
                            {
                                output[p++] = OpRun | 61;
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
                                bool diff = (uint)(d + 32) < 64;
                                // Stored as 2 bytes, the second one is ignored by a diff
                                *(ushort*)(output + p) = (ushort)(diff ? OpDiff | (d + 32) : OpRgb | (int)(px & 0xFF) << 8);
                                p += diff ? 1 : 2;
                            }
                            else if ((px ^ prev) >> 24 == 0)
                            {
                                // Differences wrap around like the reference implementation's signed chars
                                int vr = (sbyte)(px - prev);
                                int vg = (sbyte)((px >> 8) - (prev >> 8));
                                int vb = (sbyte)((px >> 16) - (prev >> 16));
                                int vgr = (sbyte)(vr - vg);
                                int vgb = (sbyte)(vb - vg);

                                // Branchless choice between diff, luma and rgb: the 3 candidates are built, the shortest valid
                                // one is stored as 4 bytes and p only moves by its length. These mispredict a lot, while index
                                // hits and alpha changes are rarer and predict well (a fully branchless version is slower).
                                bool diff = (uint)(vr + 2) < 4 && (uint)(vg + 2) < 4 && (uint)(vb + 2) < 4;
                                bool luma = (uint)(vgr + 8) < 16 && (uint)(vg + 32) < 64 && (uint)(vgb + 8) < 16;
                                uint diffOp = (uint)(OpDiff | (vr + 2) << 4 | (vg + 2) << 2 | (vb + 2));
                                uint lumaOp = (uint)(OpLuma | (vg + 32) | ((vgr + 8) << 4 | (vgb + 8)) << 8);
                                uint rgbOp = OpRgb | (px & 0x00FFFFFF) << 8;
                                *(uint*)(output + p) = diff ? diffOp : luma ? lumaOp : rgbOp;
                                p += diff ? 1 : luma ? 2 : 4;
                            }
                            else
                            {
                                output[p] = OpRgba;
                                *(uint*)(output + p + 1) = px;
                                p += 5;
                            }
                        }

                        prev = px;
                    }
                }
            }

            // End of the stream, or of a range: the next range starts with a chunk boundary, which closes the run too
            if (run > 0) output[p++] = (byte)(OpRun | (run - 1));
            return p;
        }

        /// Reads stream pixels exactly as <see cref="EncodeRange"/> does, for the scans of the parallel plain encoder
        private struct Loader
        {
            private readonly byte* pixels;
            private readonly int width, height, pixelCount, bytesPerSample, g, b, a;
            private readonly bool flipY, single, readAlpha, rgba;
            private readonly uint one;
            public readonly int Stride;

            public Loader(byte* pixels, ref EncodeArgs args)
            {
                this.pixels = pixels;
                width = args.Width;
                height = args.Height;
                pixelCount = width * height;
                bytesPerSample = args.BytesPerSample;
                Stride = args.SourceChannels * bytesPerSample;
                g = args.SourceChannels == 1 ? 0 : bytesPerSample;
                b = args.SourceChannels == 1 ? 0 : 2 * bytesPerSample;
                a = 3 * bytesPerSample;
                flipY = args.FlipY != 0;
                single = args.Channels == QoiChannels.R;
                rgba = args.Channels == QoiChannels.Rgba;
                readAlpha = rgba && args.SourceChannels == 4;
                one = OneBits(args.SampleFormat);
            }

            /// Source of stream pixel <paramref name="i"/>, its column, and the alpha of its plane when the source has none
            public byte* At(int i, out int x, out uint alpha)
            {
                int plane = i / pixelCount;
                int rest = i - plane * pixelCount;
                int y = rest / width;
                x = rest - y * width;
                int byteInSample = bytesPerSample - 1 - plane;
                alpha = (rgba ? one >> (byteInSample * 8) & 0xFF : 255u) << 24;
                return pixels + ((long)(flipY ? height - 1 - y : y) * width + x) * Stride + byteInSample;
            }

            public uint Load(byte* s, uint alpha) => single
                ? s[0] | OpaqueBlack
                : s[0] | (uint)s[g] << 8 | (uint)s[b] << 16 | (readAlpha ? (uint)s[a] << 24 : alpha);

            public int Width => width;
        }

        private static uint LoadPixel(byte* pixels, ref EncodeArgs args, int i)
        {
            var loader = new Loader(pixels, ref args);
            byte* s = loader.At(i, out _, out uint alpha);
            return loader.Load(s, alpha);
        }

        /// Stream pixel <paramref name="i"/> as the encoder sees it
        [BurstCompile(CompileSynchronously = true)]
        public static uint PixelAt(byte* pixels, ref EncodeArgs args, int i) => LoadPixel(pixels, ref args, i);

        /// First stream pixel in [<paramref name="from"/>, <paramref name="to"/>) that differs from <paramref name="value"/>, or -1
        [BurstCompile(CompileSynchronously = true)]
        public static int FindDifferent(byte* pixels, ref EncodeArgs args, int from, int to, uint value)
        {
            var loader = new Loader(pixels, ref args);
            int stride = loader.Stride;
            int i = from;
            while (i < to)
            {
                // One row at a time
                byte* s = loader.At(i, out int x, out uint alpha);
                int rowEnd = to - i < loader.Width - x ? to : i + loader.Width - x;
                for (; i < rowEnd; i++, s += stride)
                    if (loader.Load(s, alpha) != value) return i;
            }
            return -1;
        }

        /// <summary>
        /// Last pixel of every hash in [<paramref name="from"/>, <paramref name="to"/>): the index entries this range leaves
        /// behind. Returns the bit set of the entries found.
        /// </summary>
        [BurstCompile(CompileSynchronously = true)]
        public static ulong CollectIndex(byte* pixels, ref EncodeArgs args, int from, int to, uint* table)
        {
            var loader = new Loader(pixels, ref args);
            int stride = loader.Stride;
            ulong found = 0;
            int i = to - 1;
            while (i >= from && found != ulong.MaxValue)
            {
                // One row at a time, backwards
                byte* s = loader.At(i, out int x, out uint alpha);
                int rowStart = i - x < from ? from : i - x;
                for (; i >= rowStart && found != ulong.MaxValue; i--, s -= stride)
                {
                    uint px = loader.Load(s, alpha);
                    int h = Hash(px);
                    if ((found >> h & 1) == 0)
                    {
                        table[h] = px;
                        found |= 1UL << h;
                    }
                }
            }
            return found;
        }

        /// <summary>
        /// Decodes <paramref name="count"/> stream pixels starting at stream pixel <paramref name="start"/>, reading ops from
        /// <paramref name="p"/>. An op may start before <paramref name="opsEnd"/> and never reads past <paramref name="dataLength"/>.
        /// Returns <see cref="DecodeOk"/> or an error code.
        /// </summary>
        [BurstCompile(CompileSynchronously = true)]
        public static int DecodeRange(byte* data, int dataLength, int p, int opsEnd, byte* output, ref DecodeArgs args,
            int start, int count)
        {
            int ignored;
            return DecodeSegment(data, dataLength, p, opsEnd, int.MaxValue, output, ref args, start, count, null, &ignored, &ignored);
        }

        /// <summary>
        /// <see cref="DecodeRange"/> that may also stop at the first op starting at or after <paramref name="stopAt"/>, and
        /// start from a decoder state: <paramref name="state"/> (64 index entries then the pixel) is read, then receives the
        /// state at the end. Writes the number of decoded pixels and the end of the decoded ops.
        /// </summary>
        [BurstCompile(CompileSynchronously = true)]
        public static int DecodeSegment(byte* data, int dataLength, int p, int opsEnd, int stopAt, byte* output, ref DecodeArgs args,
            int start, int count, uint* state, int* decoded, int* end)
        {
            int width = args.Width, height = args.Height, bytesPerSample = args.BytesPerSample;
            int outputChannels = args.OutputChannels;
            int pixelCount = width * height;
            bool flipY = args.FlipY != 0;
            bool single = args.Channels == QoiChannels.R;
            bool fileAlpha = args.Channels == QoiChannels.Rgba;
            uint one = OneBits(args.SampleFormat);
            // 8 bit RGBA output of a color file: one 4 byte store per pixel
            bool quad = outputChannels == 4 && bytesPerSample == 1 && !single;
            uint alphaBits = fileAlpha ? 0 : 0xFF000000;

            int stride = outputChannels * bytesPerSample;
            long rowBytes = (long)width * stride;
            // Added after the last pixel of a row, the offset already moved one row forward
            long rowJump = flipY ? -2 * rowBytes : 0;
            int g = bytesPerSample, b = 2 * bytesPerSample, a = 3 * bytesPerSample;

            int plane = start / pixelCount;
            int pixel = start - plane * pixelCount;
            int x = pixel % width;
            int row = flipY ? height - 1 - pixel / width : pixel / width;
            int byteInSample = bytesPerSample - 1 - plane;
            byte* o = output + row * rowBytes + (long)x * stride + byteInSample;
            byte alpha = (byte)(one >> (byteInSample * 8));
            if (opsEnd > dataLength) opsEnd = dataLength;

            uint* index = stackalloc uint[64];
            uint px = OpaqueBlack;
            for (int k = 0; k < 64; k++) index[k] = 0;
            if (state != null)
            {
                for (int k = 0; k < 64; k++) index[k] = state[k];
                px = state[64];
            }
            int n = 0;
            *decoded = 0;
            *end = p;

            while (n < count && p < stopAt)
            {
                if (p >= opsEnd) return DecodeTruncated;
                int b1 = data[p++];
                int repeat = 1;

                if (single)
                {
                    if (b1 == OpRgb)
                    {
                        if (p >= dataLength) return DecodeTruncated;
                        px = data[p++] | OpaqueBlack;
                    }
                    else if (b1 == OpRgba || (b1 & Mask2) == OpLuma)
                        return DecodeInvalidOp | b1;
                    else if ((b1 & Mask2) == OpIndex)
                        px = index[b1];
                    else if ((b1 & Mask2) == OpDiff)
                        px = (px + (uint)(b1 & 0x3F) - 32) & 0xFF | OpaqueBlack;
                    else
                        repeat = (b1 & 0x3F) + 1;
                }
                else if (b1 == OpRgb)
                {
                    if (p + 3 > dataLength) return DecodeTruncated;
                    px = (px & 0xFF000000) | data[p] | (uint)data[p + 1] << 8 | (uint)data[p + 2] << 16;
                    p += 3;
                }
                else if (b1 == OpRgba)
                {
                    if (p + 4 > dataLength) return DecodeTruncated;
                    px = *(uint*)(data + p);
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
                            if (p >= dataLength) return DecodeTruncated;
                            int b2 = data[p++];
                            int vg = (b1 & 0x3F) - 32;
                            px = Add(px, vg - 8 + (b2 >> 4 & 0x0F), vg, vg - 8 + (b2 & 0x0F));
                            break;
                        default:
                            repeat = (b1 & 0x3F) + 1;
                            break;
                    }
                }

                index[Hash(px)] = px;

                if (repeat == 1 && quad && x + 1 < width)
                {
                    // Most common case: one RGBA pixel that doesn't end a row
                    *(uint*)o = px | alphaBits;
                    o += 4;
                    x++;
                    pixel++;
                    n++;
                    continue;
                }

                if (repeat > count - n) repeat = count - n;
                n += repeat;

                // Writes the pixel repeat times, one row segment at a time
                while (repeat > 0)
                {
                    int segment = repeat < width - x ? repeat : width - x;
                    if (quad)
                    {
                        uint v = px | alphaBits;
                        for (int k = 0; k < segment; k++) ((uint*)o)[k] = v;
                        o += segment * 4;
                    }
                    else
                    {
                        byte r = (byte)px, gr = (byte)(px >> 8), bl = (byte)(px >> 16);
                        byte al = fileAlpha ? (byte)(px >> 24) : alpha;
                        if (single) gr = bl = r;
                        for (int k = 0; k < segment; k++, o += stride)
                        {
                            o[0] = r;
                            if (outputChannels >= 3)
                            {
                                o[g] = gr;
                                o[b] = bl;
                                if (outputChannels == 4) o[a] = single ? alpha : al;
                            }
                        }
                    }

                    repeat -= segment;
                    pixel += segment;
                    x += segment;
                    if (x == width)
                    {
                        x = 0;
                        o += rowJump;
                    }

                    if (pixel == pixelCount && ++plane < bytesPerSample)
                    {
                        // Next byte plane, back to the first pixel
                        pixel = 0;
                        byteInSample--;
                        o = output + (flipY ? (height - 1) * rowBytes : 0) + byteInSample;
                        alpha = (byte)(one >> (byteInSample * 8));
                    }
                }
            }

            if (state != null)
            {
                for (int k = 0; k < 64; k++) state[k] = index[k];
                state[64] = px;
            }
            *decoded = n;
            *end = p;
            return DecodeOk;
        }

        // Parallel decoding of plain files ---------------------------------------------------------------------------------
        //
        // A plain file's ops can only be decoded in order: each op depends on the previous pixel and on the index. To split
        // the work, the op bytes are cut into segments, then:
        // 1. Every segment is decoded in parallel from a guessed state (the initial one), without writing pixels. The guess is
        //    wrong, and the cut may even fall inside an op, but variable length codes resynchronize, and every literal and
        //    every index write brings the guessed state closer to the true one.
        // 2. In order, the true decoder runs from the true end of the previous segment, in lockstep with the guessed one,
        //    until both agree on the pixel and on every index entry still used later in the segment. From there both decode
        //    the same pixels: the true end state and pixel count follow from step 1. A segment that doesn't converge quickly is
        //    decoded for real, in order. Images made of small differences rarely resynchronize: the first segment is decoded
        //    for real while the second one is scanned as a probe, and when the probe or two segments in a row don't converge,
        //    the rest of the file is decoded in order. Such files cost about the same as a sequential decode.
        // 3. The converged segments are decoded in parallel from their now known start state and pixel.

        /// One op of the state only decoder, same semantics as <see cref="DecodeSegment"/>. Returns its pixel count, or 0
        /// for invalid or truncated data. <paramref name="readSlot"/> is the index entry read by an index op, otherwise -1.
        private static int StepOp(byte* data, int dataLength, ref int p, ref uint px, uint* index, bool single, out int readSlot)
        {
            readSlot = -1;
            if (p >= dataLength) return 0;
            int b1 = data[p++];
            int repeat = 1;

            if (single)
            {
                if (b1 == OpRgb)
                {
                    if (p >= dataLength) return 0;
                    px = data[p++] | OpaqueBlack;
                }
                else if (b1 == OpRgba || (b1 & Mask2) == OpLuma)
                    return 0;
                else if ((b1 & Mask2) == OpIndex)
                {
                    px = index[b1];
                    readSlot = b1;
                }
                else if ((b1 & Mask2) == OpDiff)
                    px = (px + (uint)(b1 & 0x3F) - 32) & 0xFF | OpaqueBlack;
                else
                    repeat = (b1 & 0x3F) + 1;
            }
            else if (b1 == OpRgb)
            {
                if (p + 3 > dataLength) return 0;
                px = (px & 0xFF000000) | data[p] | (uint)data[p + 1] << 8 | (uint)data[p + 2] << 16;
                p += 3;
            }
            else if (b1 == OpRgba)
            {
                if (p + 4 > dataLength) return 0;
                px = *(uint*)(data + p);
                p += 4;
            }
            else
            {
                switch (b1 & Mask2)
                {
                    case OpIndex:
                        px = index[b1];
                        readSlot = b1;
                        break;
                    case OpDiff:
                        px = Add(px, (b1 >> 4 & 3) - 2, (b1 >> 2 & 3) - 2, (b1 & 3) - 2);
                        break;
                    case OpLuma:
                        if (p >= dataLength) return 0;
                        int b2 = data[p++];
                        int vg = (b1 & 0x3F) - 32;
                        px = Add(px, vg - 8 + (b2 >> 4 & 0x0F), vg, vg - 8 + (b2 & 0x0F));
                        break;
                    default:
                        repeat = (b1 & 0x3F) + 1;
                        break;
                }
            }

            index[Hash(px)] = px;
            return repeat;
        }

        /// <summary>
        /// Step 1: decodes the ops starting in [<paramref name="from"/>, <paramref name="to"/>) from the initial state, without
        /// writing pixels. Writes the end state (64 index entries then the pixel) to <paramref name="state"/>, the start of the
        /// last op that used each index entry to <paramref name="lastUse"/> (-1 if none), the pixel count and the end of the ops.
        /// Returns 0 on invalid data, 1 otherwise.
        /// </summary>
        [BurstCompile(CompileSynchronously = true)]
        public static int ScanSegment(byte* data, int dataLength, int from, int to, ref DecodeArgs args, uint* state, int* lastUse,
            long* pixels, int* end)
        {
            bool single = args.Channels == QoiChannels.R;
            uint px = OpaqueBlack;
            for (int k = 0; k < 64; k++)
            {
                state[k] = 0;
                lastUse[k] = -1;
            }

            long n = 0;
            int p = from;
            while (p < to)
            {
                int op = p;
                int repeat = StepOp(data, dataLength, ref p, ref px, state, single, out int read);
                if (repeat == 0) return 0;
                if (read >= 0) lastUse[read] = op;
                lastUse[Hash(px)] = op;
                n += repeat;
            }

            state[64] = px;
            *pixels = n;
            *end = p;
            return 1;
        }

        internal const int SyncConverged = 0;
        internal const int SyncGaveUp = 1;
        internal const int SyncInvalid = 2;

        /// <summary>
        /// Step 2 for the segment [<paramref name="from"/>, <paramref name="to"/>), whose first true op starts at
        /// <paramref name="trueStart"/> with the state in <paramref name="state"/>. The guessed run is the one of
        /// <see cref="ScanSegment"/> (<paramref name="scanState"/>, <paramref name="scanLastUse"/>, <paramref name="scanPixels"/>,
        /// <paramref name="scanEnd"/>). On <see cref="SyncConverged"/>, <paramref name="state"/> receives the true end state and
        /// the pixel count and end of the segment are written. Gives up after <paramref name="maxOps"/> ops.
        /// </summary>
        [BurstCompile(CompileSynchronously = true)]
        public static int SyncSegment(byte* data, int dataLength, int from, int to, int trueStart, ref DecodeArgs args, uint* state,
            uint* scanState, int* scanLastUse, long scanPixels, int scanEnd, int maxOps, long* pixels, int* end)
        {
            bool single = args.Channels == QoiChannels.R;

            uint* guessIndex = stackalloc uint[64];
            uint* trueIndex = stackalloc uint[64];
            for (int k = 0; k < 64; k++)
            {
                guessIndex[k] = 0;
                trueIndex[k] = state[k];
            }
            uint guessPx = OpaqueBlack, truePx = state[64];
            int guessP = from, trueP = trueStart;
            long guessPixels = 0, truePixels = 0;
            bool guessAlive = true, aligned = false;

            // Index entries by last use, to track the ones still used at or after the current op
            int* order = stackalloc int[64];
            for (int k = 0; k < 64; k++)
            {
                int j = k;
                while (j > 0 && scanLastUse[order[j - 1]] > scanLastUse[k])
                {
                    order[j] = order[j - 1];
                    j--;
                }
                order[j] = k;
            }
            int expired = 0;
            ulong used = ulong.MaxValue;
            ulong differ = 0;

            for (int ops = 0; trueP < to; ops++)
            {
                if (ops > maxOps) return SyncGaveUp;

                if (!aligned)
                {
                    if (guessAlive && guessP < trueP)
                    {
                        // Move the guessed run forward until it starts an op where the true one does, or jumps past it
                        int repeat = guessP < to ? StepOp(data, dataLength, ref guessP, ref guessPx, guessIndex, single, out _) : 0;
                        if (repeat == 0) guessAlive = false;
                        guessPixels += repeat;
                        continue;
                    }
                    if (guessAlive && guessP == trueP)
                    {
                        aligned = true;
                        for (int k = 0; k < 64; k++)
                            if (guessIndex[k] != trueIndex[k]) differ |= 1UL << k;
                    }
                }

                if (aligned)
                {
                    while (expired < 64 && scanLastUse[order[expired]] < trueP)
                    {
                        used &= ~(1UL << order[expired]);
                        expired++;
                    }

                    if (guessPx == truePx && (differ & used) == 0)
                    {
                        // Same ops and same state from here: the guessed run's end is the true one. Entries never used again
                        // keep their true value.
                        for (int k = 0; k < 64; k++)
                            state[k] = (used >> k & 1) != 0 ? scanState[k] : trueIndex[k];
                        state[64] = scanState[64];
                        *pixels = truePixels + (scanPixels - guessPixels);
                        *end = scanEnd;
                        return SyncConverged;
                    }

                    int t = StepOp(data, dataLength, ref trueP, ref truePx, trueIndex, single, out _);
                    int g = StepOp(data, dataLength, ref guessP, ref guessPx, guessIndex, single, out _);
                    if (t == 0 || g == 0) return SyncInvalid;
                    truePixels += t;
                    guessPixels += g;
                    int ht = Hash(truePx), hg = Hash(guessPx);
                    differ = guessIndex[ht] != trueIndex[ht] ? differ | 1UL << ht : differ & ~(1UL << ht);
                    differ = guessIndex[hg] != trueIndex[hg] ? differ | 1UL << hg : differ & ~(1UL << hg);
                }
                else
                {
                    int t = StepOp(data, dataLength, ref trueP, ref truePx, trueIndex, single, out _);
                    if (t == 0) return SyncInvalid;
                    truePixels += t;
                }
            }

            // The true run reached the end of the segment first
            for (int k = 0; k < 64; k++) state[k] = trueIndex[k];
            state[64] = truePx;
            *pixels = truePixels;
            *end = trueP;
            return SyncConverged;
        }

        private static uint Add(uint px, int dr, int dg, int db) =>
            (px & 0xFF000000) |
            (uint)((int)(px & 0xFF) + dr) & 0xFF |
            ((uint)((int)(px >> 8 & 0xFF) + dg) & 0xFF) << 8 |
            ((uint)((int)(px >> 16 & 0xFF) + db) & 0xFF) << 16;
    }
}
