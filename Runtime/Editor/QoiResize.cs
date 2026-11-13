using System;
using System.Threading.Tasks;
using Unity.Burst;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Mathematics;

namespace LTMX.QOI.Editor
{
    /// <summary>
    /// Image resize of the QOI importer, Burst, multithreaded: separable tent filter, widened when shrinking so every
    /// source pixel contributes. Samples can change format on the way (HDR source to bytes, [0, 1] clamped).
    /// </summary>
    [BurstCompile]
    internal static unsafe class QoiResize
    {
        private const int Byte = 0, Half = 1, Float = 2;

        private static int Code(QoiSampleFormat f) => f switch { QoiSampleFormat.Half => Half, QoiSampleFormat.Float => Float, _ => Byte };

        /// <summary>Resized samples of <paramref name="src"/> in <paramref name="outFormat"/> (src itself when nothing changes).</summary>
        public static byte[] Resize(byte[] src, int srcWidth, int srcHeight, int dstWidth, int dstHeight, int channels,
            QoiSampleFormat format, QoiSampleFormat outFormat)
        {
            if (srcWidth == dstWidth && srcHeight == dstHeight && format == outFormat) return src;

            var (xIndex, xWeight, xTaps) = Taps(srcWidth, dstWidth);
            var (yIndex, yWeight, yTaps) = Taps(srcHeight, dstHeight);
            int rowH = dstWidth * channels;
            var dst = new byte[(long)rowH * dstHeight * QoiCodec.BytesPerSample(outFormat)];
            int inF = Code(format), outF = Code(outFormat);
            float scale = inF != Byte && outF == Byte ? 255 : inF == Byte && outF != Byte ? 1 / 255f : 1;

            fixed (byte* s = src, d = dst)
            fixed (float* xw = xWeight, yw = yWeight)
            fixed (int* xi = xIndex, yi = yIndex)
            {
                // Pointers as longs: lambdas can't capture them
                long sp = (long)s, dp = (long)d, xwp = (long)xw, ywp = (long)yw, xip = (long)xi, yip = (long)yi;
                int bands = Math.Max(1, Math.Min(dstHeight, Environment.ProcessorCount * 4));
                Parallel.For(0, bands, b => Band((byte*)sp, inF, srcWidth, channels, (byte*)dp, outF, scale, dstWidth,
                    (int*)xip, (float*)xwp, xTaps, (int*)yip, (float*)ywp, yTaps,
                    (int)((long)dstHeight * b / bands), (int)((long)dstHeight * (b + 1) / bands)));
            }
            return dst;
        }

        // Per output position: source indices (clamped) and normalized weights of its taps, a fixed count of taps each
        private static (int[] index, float[] weight, int taps) Taps(int srcSize, int dstSize)
        {
            float scale = (float)srcSize / dstSize;
            float support = Math.Max(1f, scale);
            int taps = 1;
            for (int i = 0; i < dstSize; i++)
            {
                float center = (i + 0.5f) * scale - 0.5f;
                taps = Math.Max(taps, (int)Math.Ceiling(center + support) - (int)Math.Floor(center - support) - 1);
            }
            var index = new int[dstSize * taps];
            var weight = new float[dstSize * taps];
            for (int i = 0; i < dstSize; i++)
            {
                float center = (i + 0.5f) * scale - 0.5f;
                int first = (int)Math.Floor(center - support) + 1, last = (int)Math.Ceiling(center + support) - 1;
                float total = 0;
                for (int k = first; k <= last; k++) total += Math.Max(0f, 1f - Math.Abs(k - center) / support);
                for (int k = first; k <= last; k++)
                {
                    index[i * taps + k - first] = Math.Clamp(k, 0, srcSize - 1);
                    weight[i * taps + k - first] = Math.Max(0f, 1f - Math.Abs(k - center) / support) / total;
                }
                for (int k = last - first + 1; k < taps; k++) index[i * taps + k] = index[i * taps];
            }
            return (index, weight, taps);
        }

        // Output rows y0..y1: the source rows they read resized along x into a band local buffer (cache sized, rows at
        // band edges done twice), then blended along y (contiguous, vectorized) and written in the output format
        [BurstCompile(CompileSynchronously = true, FloatMode = FloatMode.Fast)]
        private static void Band(byte* src, int format, int srcWidth, int channels, byte* dst, int outFormat, float scale,
            int dstWidth, int* xIndex, float* xWeight, int xTaps, int* yIndex, float* yWeight, int yTaps, int y0, int y1)
        {
            if (y0 >= y1) return;
            int lo = int.MaxValue, hi = 0;
            for (int i = y0 * yTaps; i < y1 * yTaps; i++)
            {
                lo = math.min(lo, yIndex[i]);
                hi = math.max(hi, yIndex[i]);
            }
            int n = dstWidth * channels, sn = srcWidth * channels;
            var tmp = (float*)UnsafeUtility.Malloc((long)(hi - lo + 1) * n * 4, 16, Allocator.Persistent);
            var row = (float*)UnsafeUtility.Malloc(sn * 4L + 16, 16, Allocator.Persistent);
            var acc = (float*)UnsafeUtility.Malloc(n * 4L, 16, Allocator.Persistent);
            for (int y = lo; y <= hi; y++)
            {
                long o = (long)y * sn;
                float* r = row;
                if (format == Float) r = (float*)src + o;
                else if (format == Half) for (int i = 0; i < sn; i++) row[i] = math.f16tof32(((ushort*)src)[o + i]);
                else for (int i = 0; i < sn; i++) row[i] = src[o + i];
                float* t = tmp + (long)(y - lo) * n;
                switch (channels)
                {
                    case 4: Row4(r, (float4*)t, dstWidth, xIndex, xWeight, xTaps); break;
                    case 3: Row3(r, t, dstWidth, xIndex, xWeight, xTaps); break;
                    default: RowN(r, t, dstWidth, channels, xIndex, xWeight, xTaps); break;
                }
            }
            for (int y = y0; y < y1; y++)
            {
                UnsafeUtility.MemClear(acc, n * 4L);
                for (int k = 0; k < yTaps; k++)
                {
                    float w = yWeight[y * yTaps + k];
                    if (w == 0) continue;
                    float* r = tmp + (long)(yIndex[y * yTaps + k] - lo) * n;
                    for (int i = 0; i < n; i++) acc[i] += r[i] * w;
                }
                long o = (long)y * n;
                if (outFormat == Float) for (int i = 0; i < n; i++) ((float*)dst)[o + i] = acc[i] * scale;
                else if (outFormat == Half) for (int i = 0; i < n; i++) ((ushort*)dst)[o + i] = (ushort)math.f32tof16(acc[i] * scale);
                else for (int i = 0; i < n; i++) dst[o + i] = (byte)math.clamp((int)(acc[i] * scale + 0.5f), 0, 255);
            }
            UnsafeUtility.Free(tmp, Allocator.Persistent);
            UnsafeUtility.Free(row, Allocator.Persistent);
            UnsafeUtility.Free(acc, Allocator.Persistent);
        }

        private static void Row4(float* r, float4* t, int dstWidth, int* index, float* weight, int taps)
        {
            for (int x = 0; x < dstWidth; x++)
            {
                float4 s = 0;
                for (int k = 0; k < taps; k++) s += ((float4*)r)[index[x * taps + k]] * weight[x * taps + k];
                t[x] = s;
            }
        }

        private static void Row3(float* r, float* t, int dstWidth, int* index, float* weight, int taps)
        {
            for (int x = 0; x < dstWidth; x++)
            {
                float3 s = 0;
                for (int k = 0; k < taps; k++) s += *(float3*)(r + index[x * taps + k] * 3) * weight[x * taps + k];
                *(float3*)(t + x * 3) = s;
            }
        }

        private static void RowN(float* r, float* t, int dstWidth, int channels, int* index, float* weight, int taps)
        {
            for (int x = 0; x < dstWidth; x++)
            for (int c = 0; c < channels; c++)
            {
                float s = 0;
                for (int k = 0; k < taps; k++) s += r[index[x * taps + k] * channels + c] * weight[x * taps + k];
                t[x * channels + c] = s;
            }
        }
    }
}
