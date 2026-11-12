using Unity.Burst;
using Unity.Mathematics;

namespace LTMX.QOI.Editor
{
    /// <summary>
    /// Burst compiled kernels of <see cref="QoiFastCompressor"/>: mipmap downsampling and BC1 / BC3 / BC4 / BC5 / BC7 block
    /// encoding of RGBA32 images, a range of rows per call so callers can spread an image over every core.
    /// <para>
    /// BC1 colors: principal axis of the block, then least squares refinement of the endpoints. BC4 channels: least squares
    /// descents from the block's range; high error blocks also try the min and max on every pair of steps (endpoints past
    /// the range, so steps land on clustered values) and the 6 value mode (exact 0 and 255).
    /// </para>
    /// </summary>
    [BurstCompile]
    internal static unsafe class QoiBlockEncoder
    {
        internal const int BC1 = 0, BC3 = 1, BC4 = 2, BC5 = 3, BC7 = 4;
        internal const int MipSrgb = 0, MipLinear = 1, MipNormal = 2;

        internal static int BlockBytes(int format) => format is BC1 or BC4 ? 8 : 16;

        /// <summary>
        /// Encodes block rows [<paramref name="rowStart"/>, <paramref name="rowEnd"/>) of an RGBA32 image. Blocks past the
        /// image's edge repeat its last row and column. BC4 encodes red, BC5 red and green.
        /// </summary>
        [BurstCompile(CompileSynchronously = true, FloatMode = FloatMode.Fast)]
        public static void EncodeBlockRows(byte* rgba, int width, int height, int format, byte* output, int rowStart, int rowEnd)
        {
            if (format == BC7)
            {
                // Several blocks at once, one per SIMD lane
                for (int by = rowStart; by < rowEnd; by++) QoiBc7Encoder.EncodeRow(rgba, width, height, by, output);
                return;
            }

            int blocksX = (width + 3) >> 2;
            int blockBytes = format == BC1 || format == BC4 ? 8 : 16;
            uint* block = stackalloc uint[16];
            int* values = stackalloc int[16];
            for (int by = rowStart; by < rowEnd; by++)
            for (int bx = 0; bx < blocksX; bx++)
            {
                for (int j = 0; j < 4; j++)
                {
                    uint* row = (uint*)rgba + (long)math.min(by * 4 + j, height - 1) * width;
                    for (int i = 0; i < 4; i++)
                        block[j * 4 + i] = row[math.min(bx * 4 + i, width - 1)];
                }

                byte* dst = output + ((long)by * blocksX + bx) * blockBytes;
                switch (format)
                {
                    case BC1:
                        EncodeColor(block, dst);
                        break;
                    case BC3:
                        EncodeChannel(block, 24, values, dst);
                        EncodeColor(block, dst + 8);
                        break;
                    case BC4:
                        EncodeChannel(block, 0, values, dst);
                        break;
                    default:
                        EncodeChannel(block, 0, values, dst);
                        EncodeChannel(block, 8, values, dst + 8);
                        break;
                }
            }
        }

        // Smallest range of a BC4 block whose values are tried on every pair of steps
        private const int ExtrapolateRange = 16;

        // Smallest squared error (sum over the block) for which those trials and the 6 value mode are worth it
        private const int ExtrapolateError = 64;

        // BC4 block of one channel (bit shift 0 = red … 24 = alpha), also the alpha block of BC3. 8 value mode: e0 > e1,
        // 8 evenly spaced steps from e0 to e1. A flat block uses step 0 (e0) only. 6 value mode (e0 < e1, 6 steps plus 0
        // and 255) when it does better.
        private static void EncodeChannel(uint* block, int shift, int* v, byte* dst)
        {
            int lo = 255, hi = 0;
            for (int k = 0; k < 16; k++)
            {
                v[k] = (int)(block[k] >> shift) & 255;
                lo = math.min(lo, v[k]);
                hi = math.max(hi, v[k]);
            }

            int e0 = hi, e1 = lo;
            bool six = false;
            if (hi > lo)
            {
                // Least squares descents from the block's range, then (high contrast blocks hold most of the error) from
                // an inset and an outset range
                int best = int.MaxValue;
                Descend(v, hi, lo, ref best, ref e0, ref e1);
                int r8 = (hi - lo) >> 3;
                if (r8 > 0 && best > 0)
                {
                    Descend(v, hi - r8, lo + r8, ref best, ref e0, ref e1);
                    Descend(v, math.min(255, hi + r8), math.max(0, lo - r8), ref best, ref e0, ref e1);
                }

                // Clustered values: the max and min on any two steps, endpoints extrapolated past them
                if (hi - lo >= ExtrapolateRange && best > ExtrapolateError)
                {
                    int start = best;
                    for (int i = 0; i < 7; i++)
                    for (int j = i + 1; j < 8; j++)
                    {
                        if (i == 0 && j == 7) continue;
                        float stepSize = (hi - lo) / (float)(j - i);
                        int r0 = math.min(255, (int)math.round(hi + stepSize * i));
                        int r1 = math.max(0, (int)math.round(lo - stepSize * (7 - j)));
                        TryPair(v, r0, r1, ref best, ref e0, ref e1);
                    }
                    if (best < start) Descend(v, e0, e1, ref best, ref e0, ref e1);
                }

                // Spans that are multiples of 7 put every step on an integer: a range up to 7 encodes exactly, up to 14
                // within 1. Tried from both ends of the range, at the smallest such span and the next one.
                int span = (hi - lo + 6) / 7 * 7;
                for (int i = 0; i < 4 && best > 0; i++)
                {
                    int s = span + (i >> 1) * 7;
                    int r0 = (i & 1) == 0 ? hi : lo + s, r1 = r0 - s;
                    if (r0 <= 255 && r1 >= 0) TryPair(v, r0, r1, ref best, ref e0, ref e1);
                }

                // Endpoints are integers: the neighbors of the best pair may round better
                int c0 = e0, c1 = e1;
                for (int d = 0; d < 9 && best > 0; d++)
                {
                    int r0 = c0 + d / 3 - 1, r1 = c1 + d % 3 - 1;
                    if (d != 4 && r0 <= 255 && r1 >= 0 && r0 > r1) TryPair(v, r0, r1, ref best, ref e0, ref e1);
                }

                // 6 value mode: the values strictly inside (0, 255) on any two of its steps
                if (hi - lo >= ExtrapolateRange && best > ExtrapolateError)
                {
                    int lo6 = 255, hi6 = 0;
                    for (int k = 0; k < 16; k++)
                        if (v[k] > 0 && v[k] < 255)
                        {
                            lo6 = math.min(lo6, v[k]);
                            hi6 = math.max(hi6, v[k]);
                        }
                    int best6 = int.MaxValue, f0 = 0, f1 = 0;
                    if (hi6 > lo6)
                    {
                        for (int i = 0; i < 5; i++)
                        for (int j = i + 1; j < 6; j++)
                        {
                            float stepSize = (hi6 - lo6) / (float)(j - i);
                            int r0 = math.max(0, (int)math.round(lo6 - stepSize * i));
                            int r1 = math.min(255, (int)math.round(hi6 + stepSize * (5 - j)));
                            TryPair6(v, r0, r1, ref best6, ref f0, ref f1);
                        }
                        int g0 = f0, g1 = f1;
                        for (int d = 0; d < 9 && best6 > 0; d++)
                        {
                            int r0 = g0 + d / 3 - 1, r1 = g1 + d % 3 - 1;
                            if (d != 4 && r0 >= 0 && r1 <= 255 && r0 < r1) TryPair6(v, r0, r1, ref best6, ref f0, ref f1);
                        }
                    }
                    if (best6 < best)
                    {
                        six = true;
                        e0 = f0;
                        e1 = f1;
                    }
                }
            }

            // Indices: step 0 = index 0 (e0), step 7 = index 1 (e1), steps 1…6 = indices 2…7
            ulong bits = 0;
            if (six)
                for (int k = 0; k < 16; k++)
                    bits |= (ulong)Index6(v[k], e0, e1, out _) << (3 * k);
            else if (e0 > e1)
                for (int k = 0; k < 16; k++)
                {
                    int step = Step(v[k], e0, e1, 7f / (e0 - e1), out _);
                    bits |= (ulong)(step == 0 ? 0 : step == 7 ? 1 : step + 1) << (3 * k);
                }
            dst[0] = (byte)e0;
            dst[1] = (byte)e1;
            for (int b = 0; b < 6; b++) dst[2 + b] = (byte)(bits >> (8 * b));
        }

        // Nearest step of v. Decoders round ((7 - step) * e0 + step * e1) / 7 to an integer, so the nearest decoded value
        // may be a neighbor of the nearest exact one.
        private static int Step(int v, int e0, int e1, float scale, out int error)
        {
            int step = math.clamp((int)((e0 - v) * scale + 0.5f), 0, 7);
            int d = v - ((7 - step) * e0 + step * e1 + 3) / 7;
            if (step > 0)
            {
                int dn = v - ((8 - step) * e0 + (step - 1) * e1 + 3) / 7;
                if (dn * dn < d * d) { d = dn; step--; }
            }
            if (step < 7)
            {
                int dn = v - ((6 - step) * e0 + (step + 1) * e1 + 3) / 7;
                if (dn * dn < d * d) { d = dn; step++; }
            }
            error = d * d;
            return step;
        }

        // 6 value mode index of v (e0 < e1): steps 0…5 from e0 to e1 = indices 0, 2…5, 1; 6 = 0, 7 = 255
        private static int Index6(int v, int e0, int e1, out int error)
        {
            int step = math.clamp((int)((v - e0) * 5f / (e1 - e0) + 0.5f), 0, 5);
            int d = v - ((5 - step) * e0 + step * e1 + 2) / 5;
            if (step > 0)
            {
                int dn = v - ((6 - step) * e0 + (step - 1) * e1 + 2) / 5;
                if (dn * dn < d * d) { d = dn; step--; }
            }
            if (step < 5)
            {
                int dn = v - ((4 - step) * e0 + (step + 1) * e1 + 2) / 5;
                if (dn * dn < d * d) { d = dn; step++; }
            }
            int index = step == 0 ? 0 : step == 5 ? 1 : step + 1;
            if (v * v < d * d)
            {
                d = v;
                index = 6;
            }
            if ((255 - v) * (255 - v) < d * d)
            {
                d = 255 - v;
                index = 7;
            }
            error = d * d;
            return index;
        }

        private static void TryPair6(int* v, int a, int b, ref int best, ref int e0, ref int e1)
        {
            // Index6 on 4 values at a time (x * 13108 >> 16 = x / 5 up to 16383)
            float scale = 5f / (b - a);
            int4 sum = 0;
            for (int k = 0; k < 16; k += 4)
            {
                int4 x = *(int4*)(v + k);
                int4 step = math.clamp((int4)((float4)(x - a) * scale + 0.5f), 0, 5);
                int4 d = x - (((5 - step) * a + step * b + 2) * 13108 >> 16);
                int4 down = x - (((6 - step) * a + (step - 1) * b + 2) * 13108 >> 16);
                int4 up = x - (((4 - step) * a + (step + 1) * b + 2) * 13108 >> 16);
                int4 e = math.min(d * d, math.min(math.select(int.MaxValue, down * down, step > 0),
                    math.select(int.MaxValue, up * up, step < 5)));
                sum += math.min(e, math.min(x * x, (255 - x) * (255 - x)));
            }
            int error = math.csum(sum);
            if (error >= best) return;
            best = error;
            e0 = a;
            e1 = b;
        }

        // Keeps (a, b) when it beats the best pair
        private static void TryPair(int* v, int a, int b, ref int best, ref int e0, ref int e1)
        {
            // Step on 4 values at a time (x * 9363 >> 16 = x / 7 up to 13109)
            float scale = 7f / (a - b);
            int4 sum = 0;
            for (int k = 0; k < 16; k += 4)
            {
                int4 x = *(int4*)(v + k);
                int4 step = math.clamp((int4)((float4)(a - x) * scale + 0.5f), 0, 7);
                int4 d = x - (((7 - step) * a + step * b + 3) * 9363 >> 16);
                int4 down = x - (((8 - step) * a + (step - 1) * b + 3) * 9363 >> 16);
                int4 up = x - (((6 - step) * a + (step + 1) * b + 3) * 9363 >> 16);
                sum += math.min(d * d, math.min(math.select(int.MaxValue, down * down, step > 0),
                    math.select(int.MaxValue, up * up, step < 7)));
            }
            int error = math.csum(sum);
            if (error >= best) return;
            best = error;
            e0 = a;
            e1 = b;
        }

        // Alternates nearest steps and least squares endpoints from (a, b), keeping the best pair found
        private static void Descend(int* v, int a, int b, ref int best, ref int e0, ref int e1)
        {
            for (int iteration = 0; iteration <= 8; iteration++)
            {
                float scale = 7f / (a - b);
                float aa = 0, bb = 0, ab = 0, ax = 0, bx = 0;
                int error = 0;
                for (int k = 0; k < 16; k++)
                {
                    float t = Step(v[k], a, b, scale, out int e) * (1f / 7), s = 1 - t;
                    error += e;
                    aa += s * s;
                    bb += t * t;
                    ab += s * t;
                    ax += s * v[k];
                    bx += t * v[k];
                }
                if (error < best)
                {
                    best = error;
                    e0 = a;
                    e1 = b;
                }

                // Least squares endpoints for these steps, unless every value uses the same step
                float det = aa * bb - ab * ab;
                if (best == 0 || math.abs(det) < 1e-6f) return;
                float inv = 1 / det;
                int r0 = (int)math.clamp((ax * bb - bx * ab) * inv + 0.5f, 0, 255);
                int r1 = (int)math.clamp((bx * aa - ax * ab) * inv + 0.5f, 0, 255);
                if (r0 <= r1 || (r0 == a && r1 == b)) return;
                a = r0;
                b = r1;
            }
        }

        // BC1 color block, always in 4 color mode (BC3 color blocks have no other mode)
        private static void EncodeColor(uint* block, byte* dst)
        {
            float3 mean = 0, lo = 255, hi = 0;
            for (int k = 0; k < 16; k++)
            {
                float3 c = Color(block[k]);
                mean += c;
                lo = math.min(lo, c);
                hi = math.max(hi, c);
            }
            mean *= 1f / 16;

            ushort c0, c1;
            uint indices;
            if (math.all(lo == hi))
            {
                c0 = c1 = Quantize(mean);
                indices = 0;
            }
            else
            {
                // Principal axis: power iterations on the covariance, from the bounding box diagonal
                float xx = 0, xy = 0, xz = 0, yy = 0, yz = 0, zz = 0;
                for (int k = 0; k < 16; k++)
                {
                    float3 d = Color(block[k]) - mean;
                    xx += d.x * d.x; xy += d.x * d.y; xz += d.x * d.z;
                    yy += d.y * d.y; yz += d.y * d.z; zz += d.z * d.z;
                }
                float3 axis = hi - lo;
                for (int i = 0; i < 4; i++)
                {
                    axis = new float3(xx * axis.x + xy * axis.y + xz * axis.z,
                        xy * axis.x + yy * axis.y + yz * axis.z,
                        xz * axis.x + yz * axis.y + zz * axis.z);
                    float m = math.cmax(math.abs(axis));
                    if (m < 1e-6f) break;
                    axis /= m;
                }
                if (math.lengthsq(axis) < 1e-12f) axis = hi - lo;
                axis = math.normalize(axis);

                float tMin = float.MaxValue, tMax = float.MinValue;
                for (int k = 0; k < 16; k++)
                {
                    float t = math.dot(Color(block[k]) - mean, axis);
                    tMin = math.min(tMin, t);
                    tMax = math.max(tMax, t);
                }
                float3 e0 = mean + axis * tMax, e1 = mean + axis * tMin;

                // Quantize, pick indices, refit the endpoints to those indices, keep the best
                c0 = c1 = 0;
                indices = 0;
                float best = float.MaxValue;
                for (int iteration = 0; iteration < 2; iteration++)
                {
                    ushort q0 = Quantize(e0), q1 = Quantize(e1);
                    uint candidate = PickIndices(block, q0, q1, out float error);
                    if (error < best)
                    {
                        best = error;
                        c0 = q0;
                        c1 = q1;
                        indices = candidate;
                    }
                    if (error == 0 || !Refit(block, candidate, ref e0, ref e1)) break;
                }

                // 4 color mode needs c0 > c1: swapping the endpoints swaps indices 0 / 1 and 2 / 3
                if (c0 < c1)
                {
                    (c0, c1) = (c1, c0);
                    indices ^= 0x55555555;
                }
                else if (c0 == c1) indices = 0;
            }

            dst[0] = (byte)c0;
            dst[1] = (byte)(c0 >> 8);
            dst[2] = (byte)c1;
            dst[3] = (byte)(c1 >> 8);
            dst[4] = (byte)indices;
            dst[5] = (byte)(indices >> 8);
            dst[6] = (byte)(indices >> 16);
            dst[7] = (byte)(indices >> 24);
        }

        private static float3 Color(uint px) => new float3(px & 255, (px >> 8) & 255, (px >> 16) & 255);

        private static ushort Quantize(float3 c)
        {
            c = math.clamp(c, 0, 255);
            int r = (int)(c.x * (31f / 255) + 0.5f), g = (int)(c.y * (63f / 255) + 0.5f), b = (int)(c.z * (31f / 255) + 0.5f);
            return (ushort)((r << 11) | (g << 5) | b);
        }

        private static float3 Expand(ushort c)
        {
            int r = c >> 11, g = (c >> 5) & 63, b = c & 31;
            return new float3((r << 3) | (r >> 2), (g << 2) | (g >> 4), (b << 3) | (b >> 2));
        }

        // Nearest of the 4 palette colors for every pixel: index 0 = c0, 1 = c1, 2 = 2/3 c0 + 1/3 c1, 3 = 1/3 c0 + 2/3 c1
        private static uint PickIndices(uint* block, ushort q0, ushort q1, out float error)
        {
            float3 p0 = Expand(q0), p1 = Expand(q1);
            float3 p2 = (2 * p0 + p1) * (1f / 3), p3 = (p0 + 2 * p1) * (1f / 3);
            uint indices = 0;
            error = 0;
            for (int k = 0; k < 16; k++)
            {
                float3 c = Color(block[k]);
                float d0 = math.distancesq(c, p0), d1 = math.distancesq(c, p1);
                float d2 = math.distancesq(c, p2), d3 = math.distancesq(c, p3);
                uint index = 0;
                float d = d0;
                if (d1 < d) { d = d1; index = 1; }
                if (d2 < d) { d = d2; index = 2; }
                if (d3 < d) { d = d3; index = 3; }
                error += d;
                indices |= index << (2 * k);
            }
            return indices;
        }

        // Least squares endpoints for fixed indices. False when every pixel uses the same weight.
        private static bool Refit(uint* block, uint indices, ref float3 e0, ref float3 e1)
        {
            float aa = 0, bb = 0, ab = 0;
            float3 ax = 0, bx = 0;
            for (int k = 0; k < 16; k++)
            {
                uint index = (indices >> (2 * k)) & 3;
                float t = index == 0 ? 0 : index == 1 ? 1 : index == 2 ? 1f / 3 : 2f / 3;
                float s = 1 - t;
                float3 c = Color(block[k]);
                aa += s * s;
                bb += t * t;
                ab += s * t;
                ax += s * c;
                bx += t * c;
            }
            float det = aa * bb - ab * ab;
            if (math.abs(det) < 1e-6f) return false;
            float inv = 1 / det;
            e0 = math.clamp((ax * bb - bx * ab) * inv, 0, 255);
            e1 = math.clamp((bx * aa - ax * ab) * inv, 0, 255);
            return true;
        }

        /// <summary>
        /// Rows [<paramref name="rowStart"/>, <paramref name="rowEnd"/>) of the next mip of an RGBA32 image: 2x2 box
        /// filter, the last row or column repeated on odd sizes. sRGB colors are averaged in linear space through integer
        /// lookup tables (<paramref name="toLinear"/>: 256 entries of linear 0…65535, <paramref name="toSrgb"/>: 65536
        /// entries), normals are averaged as vectors then renormalized. Alpha is always averaged as is.
        /// </summary>
        [BurstCompile(CompileSynchronously = true)]
        public static void DownsampleRows(byte* src, int srcWidth, int srcHeight, byte* dst, int dstWidth, int mode,
            ushort* toLinear, byte* toSrgb, int rowStart, int rowEnd)
        {
            uint* s = (uint*)src;
            uint* d = (uint*)dst;
            for (int y = rowStart; y < rowEnd; y++)
            {
                uint* row0 = s + (long)math.min(2 * y, srcHeight - 1) * srcWidth;
                uint* row1 = s + (long)math.min(2 * y + 1, srcHeight - 1) * srcWidth;
                uint* drow = d + (long)y * dstWidth;
                // Full 2x2 quads first (no clamping), then the odd last column
                int full = math.min(dstWidth, srcWidth >> 1);
                if (mode == MipLinear)
                {
                    for (int x = 0; x < full; x++)
                        drow[x] = Average(row0[2 * x], row0[2 * x + 1], row1[2 * x], row1[2 * x + 1]);
                }
                else if (mode == MipSrgb)
                {
                    for (int x = 0; x < full; x++)
                        drow[x] = AverageSrgb(row0[2 * x], row0[2 * x + 1], row1[2 * x], row1[2 * x + 1], toLinear, toSrgb);
                }
                else
                {
                    for (int x = 0; x < full; x++)
                        drow[x] = AverageNormal(row0[2 * x], row0[2 * x + 1], row1[2 * x], row1[2 * x + 1]);
                }

                for (int x = full; x < dstWidth; x++)
                {
                    int x0 = math.min(2 * x, srcWidth - 1), x1 = math.min(2 * x + 1, srcWidth - 1);
                    uint a = row0[x0], b = row0[x1], c = row1[x0], e = row1[x1];
                    drow[x] = mode == MipLinear ? Average(a, b, c, e)
                        : mode == MipSrgb ? AverageSrgb(a, b, c, e, toLinear, toSrgb)
                        : AverageNormal(a, b, c, e);
                }
            }
        }

        /// <summary>
        /// Pixels [<paramref name="start"/>, <paramref name="end"/>) of 1 or 3 channel 8 bit data as RGBA32: RGB gets an
        /// opaque alpha, a single channel is replicated to RGB.
        /// </summary>
        [BurstCompile(CompileSynchronously = true)]
        public static void ExpandToRgba(byte* src, int channels, byte* dst, long start, long end)
        {
            uint* d = (uint*)dst;
            if (channels == 3)
            {
                for (long i = start; i < end; i++)
                {
                    byte* p = src + i * 3;
                    d[i] = p[0] | ((uint)p[1] << 8) | ((uint)p[2] << 16) | 0xFF000000;
                }
            }
            else
            {
                for (long i = start; i < end; i++)
                    d[i] = src[i] * 0x00010101u | 0xFF000000;
            }
        }

        // Rounded average of all 4 channels at once, two 16 bit lanes per word
        private static uint Average(uint a, uint b, uint c, uint e)
        {
            const uint mask = 0x00FF00FF, round = 0x00020002;
            uint lo = (a & mask) + (b & mask) + (c & mask) + (e & mask) + round;
            uint hi = ((a >> 8) & mask) + ((b >> 8) & mask) + ((c >> 8) & mask) + ((e >> 8) & mask) + round;
            return ((lo >> 2) & mask) | (((hi >> 2) & mask) << 8);
        }

        private static uint AverageSrgb(uint a, uint b, uint c, uint e, ushort* toLinear, byte* toSrgb)
        {
            uint rgb = 0;
            for (int ch = 0; ch < 24; ch += 8)
            {
                uint sum = (uint)toLinear[(a >> ch) & 255] + toLinear[(b >> ch) & 255] + toLinear[(c >> ch) & 255] + toLinear[(e >> ch) & 255];
                rgb |= (uint)toSrgb[(sum + 2) >> 2] << ch;
            }
            return rgb | (Average(a, b, c, e) & 0xFF000000);
        }

        private static uint AverageNormal(uint a, uint b, uint c, uint e)
        {
            float3 n = Normal(a) + Normal(b) + Normal(c) + Normal(e);
            float length = math.length(n);
            n = length > 1e-6f ? n / length : new float3(0, 0, 1);
            int3 q = (int3)math.round((n * 0.5f + 0.5f) * 255);
            return (uint)q.x | ((uint)q.y << 8) | ((uint)q.z << 16) | (Average(a, b, c, e) & 0xFF000000);
        }

        private static float3 Normal(uint px) => Color(px) * (2f / 255) - 1;
    }
}
