using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Unity.Burst;
using Unity.Burst.Intrinsics;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Mathematics;

namespace LTMX.QOI.Editor
{
    /// <summary>
    /// ASTC LDR encoder of <see cref="QoiFastCompressor"/>, any 2D block size. One partition, one plane, color endpoint
    /// modes luminance (0), luminance + alpha (4), RGB (8) and RGBA (12) picked from the block's content. Each block tries
    /// every weight grid and weight range that fits (largest endpoint range for the bits left): principal axis of the
    /// block, ideal weights decimated to the grid, quantized, then least squares endpoints and weights reprojected on the
    /// quantized endpoints. Constant blocks are void extent blocks.
    /// </summary>
    [BurstCompile]
    internal static unsafe class QoiAstcEncoder
    {
        // Integer sequence encoding ranges by quantization level (weights use levels 0 to 11)
        private static readonly int[] Ranges = { 2, 3, 4, 5, 6, 8, 10, 12, 16, 20, 24, 32, 40, 48, 64, 80, 96, 128, 160, 192, 256 };
        private const int Levels = 21, WeightLevels = 12;

        // Lowest endpoint level a valid block uses (range 6)
        private const int MinColorLevel = 4;

        // Correction steps of the grid weights decimated from texel weights
        private const int DecimateIterations = 2;

        // Relaxation of those steps (1: Jacobi)
        private const float DecimateStep = 1;

        // Refit passes: least squares endpoints, then weights reprojected on the quantized endpoints
        private const int Passes = 2;

        internal struct Grid
        {
            public int X, Y, Count;
            public int Full;
            public int* Index;     // [texels * 4] grid weights of each texel
            public int* Factor;    // [texels * 4] their infill factors, sum 16
            public int* Start;     // [Count + 1] contributions of each grid weight
            public int* Texel;
            public float* Weight;  // contribution factor / sum of the weight's factors
            public float* Step;    // contribution factor / sum of the weight's squared factors (correction steps)
            public float* Raw;     // contribution factor / 16
        }

        internal struct Mode
        {
            public int Grid, Level, Bits, WeightBits, Dual, X, Y;
            public float WeightNoise;         // squared weight quantization step / 12, per unit of line extent
            public fixed float ColorNoise[15]; // endpoint quantization error per texel and channel (estimate)
            // By (partitions - 1) * 5 + endpoint values per partition / 2, -1 when the endpoints don't fit
            public fixed int ColorLevel[15];
        }

        internal struct Tables
        {
            public int Bw, Bh, Texels, GridCount, ModeCount;
            public int OnlyMode; // DEBUG
            public int Skip; // DEBUG
            public double Predicted; // DEBUG
            public fixed long Wins[8]; // DEBUG
            public long* ModeWins; // DEBUG
            public Grid* Grids;
            public Mode* Modes; // grouped by grid
            public int SingleCount, DualCount;
            public int* SingleModes, DualModes;
            public byte* ColorUnquant, ColorQuant;    // [level * 256 + code / value]
            public byte* WeightUnquant, WeightQuant;  // [level * 32 + code], [level * 65 + value]
            public byte* WeightFlip;                  // [level * 32 + code]: code of 64 - weight
            public byte* WeightUp, WeightDown;        // [level * 32 + code]: code of the next higher / lower weight
            public byte* ColorUp, ColorDown;          // [level * 256 + code]: code of the next higher / lower value
            public byte* TritEnc, QuintEnc;
            // The quantization tables above as ints (lane gathers)
            public int* CQ, CU, CUp, CDown, WQ, WU, WUp, WDown;
            public int* Ranges;
            // Distinct partitionings of 2 and 3 partitions (no empty partition): seed, partition of each texel, texel
            // masks of each partition (3 words each)
            public int PartCount2, PartCount3;
            public int* PartSeed2, PartSeed3;
            public byte* PartAssign2, PartAssign3;
            public ulong* PartMask2, PartMask3;
            public fixed byte Trits[Levels], Quints[Levels], Bits[Levels];
            // 1D infill of each axis (0: x, 1: y) and grid size: grid index and 16ths toward the next, per texel
            public fixed byte AxisJ[2 * 13 * 12];
            public fixed float AxisF[2 * 13 * 12];
            public fixed float AxisInv[2 * 13 * 13]; // 1 / summed factors of each grid weight
        }

        internal static string Describe(IntPtr tables, int m)
        {
            var t = (Tables*)tables;
            var mode = t->Modes[m];
            var g = t->Grids[mode.Grid];
            return $"{g.X}x{g.Y}{(mode.Dual != 0 ? " dual" : "")} w{Ranges[mode.Level]} c6:{(mode.ColorLevel[3] < 0 ? 0 : Ranges[mode.ColorLevel[3]])} c8:{(mode.ColorLevel[4] < 0 ? 0 : Ranges[mode.ColorLevel[4]])}";
        }

        private static readonly Dictionary<int, IntPtr> Cache = new Dictionary<int, IntPtr>();

        /// <summary>Tables of a block size, built once and kept for the session.</summary>
        internal static Tables* GetTables(int bw, int bh)
        {
            lock (Cache)
            {
                if (!Cache.TryGetValue(bw * 16 + bh, out var p))
                {
                    p = (IntPtr)Build(bw, bh);
                    Cache[bw * 16 + bh] = p;
                }
                return (Tables*)p;
            }
        }

        // ---- Tables (managed, once per block size) ----

        private static T* Alloc<T>(int count) where T : unmanaged
        {
            var p = (T*)Marshal.AllocHGlobal(Math.Max(1, count) * sizeof(T));
            new Span<T>(p, count).Clear();
            return p;
        }

        private static void Ise(int level, out int trits, out int quints, out int bits)
        {
            int r = Ranges[level];
            trits = r % 3 == 0 ? 1 : 0;
            quints = r % 5 == 0 ? 1 : 0;
            bits = (int)Math.Round(Math.Log(r / (trits == 1 ? 3 : quints == 1 ? 5 : 1), 2));
        }

        internal static int IseBits(int count, int level)
        {
            Ise(level, out int trits, out int quints, out int bits);
            return count * bits + (trits == 1 ? (8 * count + 4) / 5 : 0) + (quints == 1 ? (7 * count + 2) / 3 : 0);
        }

        private static int Replicate(int v, int bits, int to)
        {
            int r = 0, n = 0;
            while (n < to)
            {
                r = (r << bits) | v;
                n += bits;
            }
            return r >> (n - to);
        }

        private static int UnquantColor(int level, int code)
        {
            Ise(level, out int trits, out int quints, out int bits);
            if (trits == 0 && quints == 0) return Replicate(code, bits, 8);
            if (bits == 0) return code * 255 / (Ranges[level] - 1); // ranges 3 and 5: below the endpoint minimum
            int d = code >> bits, m = code & ((1 << bits) - 1);
            int a = m & 1, b = (m >> 1) & 1, c = (m >> 2) & 1, dd = (m >> 3) & 1, e = (m >> 4) & 1, f = (m >> 5) & 1;
            int bb, cc;
            if (trits == 1)
                switch (bits)
                {
                    case 1: bb = 0; cc = 204; break;
                    case 2: bb = b * 0x116; cc = 93; break;
                    case 3: bb = c * 0x10A + b * 0x85; cc = 44; break;
                    case 4: bb = dd * 0x104 + c * 0x82 + b * 0x41; cc = 22; break;
                    case 5: bb = e * 0x102 + dd * 0x81 + c * 0x40 + b * 0x20; cc = 11; break;
                    case 6: bb = f * 0x101 + e * 0x80 + dd * 0x40 + c * 0x20 + b * 0x10; cc = 5; break;
                    default: throw new InvalidOperationException();
                }
            else
                switch (bits)
                {
                    case 1: bb = 0; cc = 113; break;
                    case 2: bb = b * 0x10C; cc = 54; break;
                    case 3: bb = c * 0x105 + b * 0x82; cc = 26; break;
                    case 4: bb = dd * 0x102 + c * 0x81 + b * 0x40; cc = 13; break;
                    case 5: bb = e * 0x101 + dd * 0x80 + c * 0x40 + b * 0x20; cc = 6; break;
                    default: throw new InvalidOperationException();
                }
            int aa = a == 1 ? 0x1FF : 0;
            int t = (d * cc + bb) ^ aa;
            return (aa & 0x80) | (t >> 2);
        }

        private static int UnquantWeight(int level, int code)
        {
            Ise(level, out int trits, out int quints, out int bits);
            int t;
            if (trits == 0 && quints == 0) t = Replicate(code, bits, 6);
            else if (bits == 0) t = trits == 1 ? new[] { 0, 32, 63 }[code] : new[] { 0, 16, 32, 47, 63 }[code];
            else
            {
                int d = code >> bits, m = code & ((1 << bits) - 1);
                int a = m & 1, b = (m >> 1) & 1, c = (m >> 2) & 1;
                int bb, cc;
                if (trits == 1)
                    switch (bits)
                    {
                        case 1: bb = 0; cc = 50; break;
                        case 2: bb = b * 0x44; cc = 23; break;
                        case 3: bb = c * 0x42 + b * 0x21; cc = 11; break;
                        default: throw new InvalidOperationException();
                    }
                else
                    switch (bits)
                    {
                        case 1: bb = 0; cc = 28; break;
                        case 2: bb = b * 0x42; cc = 13; break;
                        default: throw new InvalidOperationException();
                    }
                int aa = a == 1 ? 0x7F : 0;
                int v = (d * cc + bb) ^ aa;
                t = (aa & 0x20) | (v >> 2);
            }
            return t > 32 ? t + 1 : t;
        }

        private static void DecodeTrits(int T, int* t)
        {
            int bit(int i) => (T >> i) & 1;
            int c;
            if (((T >> 2) & 7) == 7)
            {
                c = (((T >> 5) & 7) << 2) | (T & 3);
                t[4] = 2;
                t[3] = 2;
            }
            else
            {
                c = T & 31;
                if (((T >> 5) & 3) == 3)
                {
                    t[4] = 2;
                    t[3] = bit(7);
                }
                else
                {
                    t[4] = bit(7);
                    t[3] = (T >> 5) & 3;
                }
            }
            int cb(int i) => (c >> i) & 1;
            if ((c & 3) == 3)
            {
                t[2] = 2;
                t[1] = cb(4);
                t[0] = (cb(3) << 1) | (cb(2) & ~cb(3) & 1);
            }
            else if (((c >> 2) & 3) == 3)
            {
                t[2] = 2;
                t[1] = 2;
                t[0] = c & 3;
            }
            else
            {
                t[2] = cb(4);
                t[1] = (c >> 2) & 3;
                t[0] = (cb(1) << 1) | (cb(0) & ~cb(1) & 1);
            }
        }

        private static void DecodeQuints(int Q, int* q)
        {
            int bit(int i) => (Q >> i) & 1;
            if (((Q >> 1) & 3) == 3 && ((Q >> 5) & 3) == 0)
            {
                q[2] = (bit(0) << 2) | ((bit(4) & ~bit(0) & 1) << 1) | (bit(3) & ~bit(0) & 1);
                q[1] = 4;
                q[0] = 4;
                return;
            }
            int c;
            if (((Q >> 1) & 3) == 3)
            {
                q[2] = 4;
                c = (((Q >> 3) & 3) << 3) | ((~(Q >> 5) & 3) << 1) | bit(0);
            }
            else
            {
                q[2] = (Q >> 5) & 3;
                c = Q & 31;
            }
            if ((c & 7) == 5)
            {
                q[1] = 4;
                q[0] = (c >> 3) & 3;
            }
            else
            {
                q[1] = (c >> 3) & 3;
                q[0] = c & 7;
            }
        }

        // Grid size, dual plane and weight level of a 2D block mode (false: reserved or out of the weight limits)
        private static bool DecodeBlockMode(int mode, out int x, out int y, out bool dual, out int level)
        {
            int baseLevel = (mode >> 4) & 1;
            int h = (mode >> 9) & 1, d = (mode >> 10) & 1, a = (mode >> 5) & 3;
            x = y = 0;
            dual = false;
            level = 0;
            if ((mode & 3) != 0)
            {
                baseLevel |= (mode & 3) << 1;
                int b = (mode >> 7) & 3;
                switch ((mode >> 2) & 3)
                {
                    case 0: x = b + 4; y = a + 2; break;
                    case 1: x = b + 8; y = a + 2; break;
                    case 2: x = a + 2; y = b + 8; break;
                    default:
                        b &= 1;
                        if ((mode & 0x100) != 0) { x = b + 2; y = a + 2; }
                        else { x = a + 2; y = b + 6; }
                        break;
                }
            }
            else
            {
                baseLevel |= ((mode >> 2) & 3) << 1;
                if (((mode >> 2) & 3) == 0) return false;
                int b = (mode >> 9) & 3;
                switch ((mode >> 7) & 3)
                {
                    case 0: x = 12; y = a + 2; break;
                    case 1: x = a + 2; y = 12; break;
                    case 2: x = a + 6; y = b + 6; d = 0; h = 0; break;
                    default:
                        switch ((mode >> 5) & 3)
                        {
                            case 0: x = 6; y = 10; break;
                            case 1: x = 10; y = 6; break;
                            default: return false;
                        }
                        break;
                }
            }
            int count = x * y * (d + 1);
            level = baseLevel - 2 + 6 * h;
            dual = d != 0;
            if (level < 0 || level >= WeightLevels) return false;
            int bits = IseBits(count, level);
            return count <= 64 && bits >= 24 && bits <= 96;
        }

        private static Tables* Build(int bw, int bh)
        {
            var t = Alloc<Tables>(1);
            t->OnlyMode = -1;
            t->Bw = bw;
            t->Bh = bh;
            int texels = t->Texels = bw * bh;
            for (int l = 0; l < Levels; l++)
            {
                Ise(l, out int trits, out int quints, out int bits);
                t->Trits[l] = (byte)trits;
                t->Quints[l] = (byte)quints;
                t->Bits[l] = (byte)bits;
            }

            t->ColorUnquant = Alloc<byte>(Levels * 256);
            t->ColorQuant = Alloc<byte>(Levels * 256);
            for (int l = 0; l < Levels; l++)
            {
                int range = Ranges[l];
                for (int c = 0; c < range; c++) t->ColorUnquant[l * 256 + c] = (byte)UnquantColor(l, c);
                for (int v = 0; v < 256; v++)
                {
                    int best = 0, bestError = int.MaxValue;
                    for (int c = 0; c < range; c++)
                    {
                        int e = Math.Abs(t->ColorUnquant[l * 256 + c] - v);
                        if (e < bestError) { bestError = e; best = c; }
                    }
                    t->ColorQuant[l * 256 + v] = (byte)best;
                }
            }

            t->WeightUnquant = Alloc<byte>(WeightLevels * 32);
            t->WeightQuant = Alloc<byte>(WeightLevels * 65);
            t->WeightFlip = Alloc<byte>(WeightLevels * 32);
            for (int l = 0; l < WeightLevels; l++)
            {
                int range = Ranges[l];
                for (int c = 0; c < range; c++) t->WeightUnquant[l * 32 + c] = (byte)UnquantWeight(l, c);
                for (int v = 0; v <= 64; v++)
                {
                    int best = 0, bestError = int.MaxValue;
                    for (int c = 0; c < range; c++)
                    {
                        int e = Math.Abs(t->WeightUnquant[l * 32 + c] - v);
                        if (e < bestError) { bestError = e; best = c; }
                    }
                    t->WeightQuant[l * 65 + v] = (byte)best;
                }
                for (int c = 0; c < range; c++)
                {
                    int flipped = t->WeightQuant[l * 65 + 64 - t->WeightUnquant[l * 32 + c]];
                    if (t->WeightUnquant[l * 32 + flipped] != 64 - t->WeightUnquant[l * 32 + c])
                        throw new InvalidOperationException($"ASTC weight level {l} is not symmetric");
                    t->WeightFlip[l * 32 + c] = (byte)flipped;
                }
            }

            t->WeightUp = Alloc<byte>(WeightLevels * 32);
            t->WeightDown = Alloc<byte>(WeightLevels * 32);
            for (int l = 0; l < WeightLevels; l++)
            for (int c = 0; c < Ranges[l]; c++)
            {
                int v = t->WeightUnquant[l * 32 + c], up = c, down = c;
                for (int o = 0; o < Ranges[l]; o++)
                {
                    int u = t->WeightUnquant[l * 32 + o];
                    if (u > v && (up == c || u < t->WeightUnquant[l * 32 + up])) up = o;
                    if (u < v && (down == c || u > t->WeightUnquant[l * 32 + down])) down = o;
                }
                t->WeightUp[l * 32 + c] = (byte)up;
                t->WeightDown[l * 32 + c] = (byte)down;
            }

            t->TritEnc = Alloc<byte>(243);
            t->QuintEnc = Alloc<byte>(125);
            var seen = new bool[243];
            int* d5 = stackalloc int[5];
            for (int T = 0; T < 256; T++)
            {
                DecodeTrits(T, d5);
                int i = d5[0] + 3 * d5[1] + 9 * d5[2] + 27 * d5[3] + 81 * d5[4];
                if (!seen[i]) { seen[i] = true; t->TritEnc[i] = (byte)T; }
            }
            var seenQ = new bool[125];
            for (int Q = 0; Q < 128; Q++)
            {
                DecodeQuints(Q, d5);
                int i = d5[0] + 5 * d5[1] + 25 * d5[2];
                if (!seenQ[i]) { seenQ[i] = true; t->QuintEnc[i] = (byte)Q; }
            }
            if (Array.IndexOf(seen, false) >= 0 || Array.IndexOf(seenQ, false) >= 0)
                throw new InvalidOperationException("ASTC trit / quint tables incomplete");

            // Modes that fit the block, grouped by grid
            var grids = new List<(int x, int y)>();
            var modes = new List<(int grid, int level, int bits, int weightBits, int dual)>();
            for (int m = 0; m < 2048; m++)
            {
                if (!DecodeBlockMode(m, out int x, out int y, out bool dual, out int level) || x > bw || y > bh) continue;
                int g = grids.IndexOf((x, y));
                if (g < 0) { g = grids.Count; grids.Add((x, y)); }
                int d = dual ? 1 : 0;
                if (modes.Exists(e => e.grid == g && e.level == level && e.dual == d)) continue;
                modes.Add((g, level, m, IseBits(x * y * (d + 1), level), d));
            }
            modes.Sort((a, b) => a.grid != b.grid ? a.grid.CompareTo(b.grid) : a.dual != b.dual ? a.dual.CompareTo(b.dual) : a.level.CompareTo(b.level));

            t->ModeCount = modes.Count;
            t->Modes = Alloc<Mode>(modes.Count);
            t->ModeWins = Alloc<long>(modes.Count * 4);
            t->SingleModes = Alloc<int>(modes.Count);
            t->DualModes = Alloc<int>(modes.Count);
            for (int i = 0; i < modes.Count; i++)
                if (modes[i].dual != 0) t->DualModes[t->DualCount++] = i;
                else t->SingleModes[t->SingleCount++] = i;
            for (int i = 0; i < modes.Count; i++)
            {
                var m = modes[i];
                ref var mode = ref t->Modes[i];
                mode.Grid = m.grid;
                mode.Level = m.level;
                mode.Bits = m.bits;
                mode.WeightBits = m.weightBits;
                mode.Dual = m.dual;
                mode.X = grids[m.grid].x;
                mode.Y = grids[m.grid].y;
                float wr = Ranges[m.level] - 1;
                mode.WeightNoise = 1 / (12 * wr * wr);
                for (int p = 1; p <= 3; p++)
                {
                    int colorBits = 128 - (p == 1 ? 17 : 29) - m.weightBits - 2 * m.dual;
                    mode.ColorLevel[(p - 1) * 5] = -1;
                    for (int v = 1; v <= 4; v++)
                    {
                        int best = -1;
                        for (int l = 0; l < Levels; l++)
                            if (IseBits(2 * v * p, l) <= colorBits) best = l;
                        mode.ColorLevel[(p - 1) * 5 + v] = best >= MinColorLevel && 2 * v * p <= 18 ? best : -1;
                        float cr = best >= 0 ? 255f / (Ranges[best] - 1) : 0;
                        mode.ColorNoise[(p - 1) * 5 + v] = texels * cr * cr / 18;
                    }
                }
            }

            t->Ranges = Alloc<int>(Levels);
            for (int l = 0; l < Levels; l++) t->Ranges[l] = Ranges[l];

            t->ColorUp = Alloc<byte>(Levels * 256);
            t->ColorDown = Alloc<byte>(Levels * 256);
            for (int l = 0; l < Levels; l++)
            for (int c = 0; c < Ranges[l]; c++)
            {
                int v = t->ColorUnquant[l * 256 + c], up = c, down = c;
                for (int o = 0; o < Ranges[l]; o++)
                {
                    int u = t->ColorUnquant[l * 256 + o];
                    if (u > v && (up == c || u < t->ColorUnquant[l * 256 + up])) up = o;
                    if (u < v && (down == c || u > t->ColorUnquant[l * 256 + down])) down = o;
                }
                t->ColorUp[l * 256 + c] = (byte)up;
                t->ColorDown[l * 256 + c] = (byte)down;
            }

            t->CQ = ToInt(t->ColorQuant, Levels * 256);
            t->CU = ToInt(t->ColorUnquant, Levels * 256);
            t->CUp = ToInt(t->ColorUp, Levels * 256);
            t->CDown = ToInt(t->ColorDown, Levels * 256);
            t->WQ = ToInt(t->WeightQuant, WeightLevels * 65);
            t->WU = ToInt(t->WeightUnquant, WeightLevels * 32);
            t->WUp = ToInt(t->WeightUp, WeightLevels * 32);
            t->WDown = ToInt(t->WeightDown, WeightLevels * 32);

            BuildPartitions(bw, bh, 2, out t->PartCount2, out t->PartSeed2, out t->PartAssign2, out t->PartMask2);
            BuildPartitions(bw, bh, 3, out t->PartCount3, out t->PartSeed3, out t->PartAssign3, out t->PartMask3);

            for (int axis = 0; axis < 2; axis++)
            {
                int len = axis == 0 ? bw : bh, dd = (1024 + len / 2) / (len - 1);
                for (int g = 2; g <= len; g++)
                for (int x = 0; x < len; x++)
                {
                    int gs = (dd * x * (g - 1) + 32) >> 6;
                    t->AxisJ[(axis * 13 + g) * 12 + x] = (byte)(gs >> 4);
                    t->AxisF[(axis * 13 + g) * 12 + x] = (gs & 15) / 16f;
                }
                for (int g = 2; g <= len; g++)
                {
                    var den = new float[g + 1];
                    for (int x = 0; x < len; x++)
                    {
                        int j = t->AxisJ[(axis * 13 + g) * 12 + x];
                        float f = t->AxisF[(axis * 13 + g) * 12 + x];
                        den[j] += 1 - f;
                        den[j + 1] += f;
                    }
                    for (int j = 0; j <= g; j++) t->AxisInv[(axis * 13 + g) * 13 + j] = den[j] > 0 ? 1 / den[j] : 0;
                }
            }

            t->GridCount = grids.Count;
            t->Grids = Alloc<Grid>(grids.Count);
            for (int g = 0; g < grids.Count; g++) BuildGrid(ref t->Grids[g], bw, bh, grids[g].x, grids[g].y);
            return t;
        }

        private static int* ToInt(byte* b, int count)
        {
            int* r = Alloc<int>(count);
            for (int i = 0; i < count; i++) r[i] = b[i];
            return r;
        }

        private static uint Hash52(uint p)
        {
            p ^= p >> 15;
            p *= 0xEEDE0891;
            p ^= p >> 5;
            p += p << 16;
            p ^= p >> 7;
            p ^= p >> 3;
            p ^= p << 6;
            p ^= p >> 17;
            return p;
        }

        // Partition of a texel, as the spec's hash
        private static int SelectPartition(int seed, int x, int y, int z, int count, bool small)
        {
            if (small)
            {
                x <<= 1;
                y <<= 1;
                z <<= 1;
            }
            seed += (count - 1) * 1024;
            uint rnum = Hash52((uint)seed);
            int s1 = (int)(rnum & 0xF), s2 = (int)((rnum >> 4) & 0xF), s3 = (int)((rnum >> 8) & 0xF), s4 = (int)((rnum >> 12) & 0xF);
            int s5 = (int)((rnum >> 16) & 0xF), s6 = (int)((rnum >> 20) & 0xF), s7 = (int)((rnum >> 24) & 0xF), s8 = (int)((rnum >> 28) & 0xF);
            int s9 = (int)((rnum >> 18) & 0xF), s10 = (int)((rnum >> 22) & 0xF), s11 = (int)((rnum >> 26) & 0xF);
            int s12 = (int)(((rnum >> 30) | (rnum << 2)) & 0xF);
            s1 *= s1; s2 *= s2; s3 *= s3; s4 *= s4; s5 *= s5; s6 *= s6;
            s7 *= s7; s8 *= s8; s9 *= s9; s10 *= s10; s11 *= s11; s12 *= s12;
            int sh1, sh2;
            if ((seed & 1) != 0)
            {
                sh1 = (seed & 2) != 0 ? 4 : 5;
                sh2 = count == 3 ? 6 : 5;
            }
            else
            {
                sh1 = count == 3 ? 6 : 5;
                sh2 = (seed & 2) != 0 ? 4 : 5;
            }
            int sh3 = (seed & 0x10) != 0 ? sh1 : sh2;
            s1 >>= sh1; s2 >>= sh2; s3 >>= sh1; s4 >>= sh2; s5 >>= sh1; s6 >>= sh2; s7 >>= sh1; s8 >>= sh2;
            s9 >>= sh3; s10 >>= sh3; s11 >>= sh3; s12 >>= sh3;
            int a = (int)((s1 * x + s2 * y + s11 * z + (rnum >> 14)) & 0x3F);
            int b = (int)((s3 * x + s4 * y + s12 * z + (rnum >> 10)) & 0x3F);
            int c = (int)((s5 * x + s6 * y + s9 * z + (rnum >> 6)) & 0x3F);
            int d = (int)((s7 * x + s8 * y + s10 * z + (rnum >> 2)) & 0x3F);
            if (count < 4) d = 0;
            if (count < 3) c = 0;
            if (a >= b && a >= c && a >= d) return 0;
            if (b >= c && b >= d) return 1;
            if (c >= d) return 2;
            return 3;
        }

        private static void BuildPartitions(int bw, int bh, int count, out int found, out int* seeds, out byte* assign, out ulong* masks)
        {
            int texels = bw * bh;
            bool small = texels < 31;
            var keys = new HashSet<string>();
            var list = new List<(int seed, byte[] part)>();
            for (int seed = 0; seed < 1024; seed++)
            {
                var part = new byte[texels];
                var used = new bool[count];
                for (int y = 0; y < bh; y++)
                for (int x = 0; x < bw; x++)
                {
                    int p = SelectPartition(seed, x, y, 0, count, small);
                    part[y * bw + x] = (byte)p;
                    if (p < count) used[p] = true;
                }
                if (Array.IndexOf(used, false) >= 0) continue;
                // Same partitioning up to the partition order
                var order = new int[count];
                for (int k = 0; k < count; k++) order[k] = -1;
                int next = 0;
                var key = new char[texels];
                for (int i = 0; i < texels; i++)
                {
                    if (order[part[i]] < 0) order[part[i]] = next++;
                    key[i] = (char)('0' + order[part[i]]);
                }
                if (!keys.Add(new string(key))) continue;
                list.Add((seed, part));
            }
            found = list.Count;
            seeds = Alloc<int>(found);
            assign = Alloc<byte>(found * texels);
            masks = Alloc<ulong>(found * count * 3);
            for (int j = 0; j < found; j++)
            {
                seeds[j] = list[j].seed;
                for (int i = 0; i < texels; i++)
                {
                    int p = list[j].part[i];
                    assign[j * texels + i] = (byte)p;
                    masks[(j * count + p) * 3 + (i >> 6)] |= 1UL << (i & 63);
                }
            }
        }

        // Weight infill of the spec: 4 grid weights per texel, factors summing to 16
        private static void BuildGrid(ref Grid grid, int bw, int bh, int x, int y)
        {
            int texels = bw * bh, count = x * y;
            grid.X = x;
            grid.Y = y;
            grid.Count = count;
            grid.Full = x == bw && y == bh ? 1 : 0;
            grid.Index = Alloc<int>(texels * 4);
            grid.Factor = Alloc<int>(texels * 4);
            int ds = (1024 + bw / 2) / (bw - 1), dt = (1024 + bh / 2) / (bh - 1);
            for (int ty = 0; ty < bh; ty++)
            for (int tx = 0; tx < bw; tx++)
            {
                int gs = (ds * tx * (x - 1) + 32) >> 6, gt = (dt * ty * (y - 1) + 32) >> 6;
                int js = gs >> 4, fs = gs & 15, jt = gt >> 4, ft = gt & 15;
                int v0 = js + jt * x;
                int w11 = (fs * ft + 8) >> 4, w10 = ft - w11, w01 = fs - w11, w00 = 16 - fs - ft + w11;
                int i = (ty * bw + tx) * 4;
                grid.Index[i] = v0;
                grid.Index[i + 1] = Math.Min(v0 + 1, count - 1);
                grid.Index[i + 2] = Math.Min(v0 + x, count - 1);
                grid.Index[i + 3] = Math.Min(v0 + x + 1, count - 1);
                grid.Factor[i] = w00;
                grid.Factor[i + 1] = w01;
                grid.Factor[i + 2] = w10;
                grid.Factor[i + 3] = w11;
            }

            var lists = new List<(int texel, int factor)>[count];
            for (int g = 0; g < count; g++) lists[g] = new List<(int, int)>();
            for (int tx = 0; tx < texels; tx++)
            for (int k = 0; k < 4; k++)
                if (grid.Factor[tx * 4 + k] > 0) lists[grid.Index[tx * 4 + k]].Add((tx, grid.Factor[tx * 4 + k]));
            int total = 0;
            foreach (var l in lists) total += l.Count;
            grid.Start = Alloc<int>(count + 1);
            grid.Texel = Alloc<int>(total);
            grid.Weight = Alloc<float>(total);
            grid.Step = Alloc<float>(total);
            grid.Raw = Alloc<float>(total);
            int n = 0;
            for (int g = 0; g < count; g++)
            {
                grid.Start[g] = n;
                float sum = 0, sum2 = 0;
                foreach (var c in lists[g])
                {
                    sum += c.factor;
                    sum2 += c.factor * c.factor / 16f;
                }
                foreach (var c in lists[g])
                {
                    grid.Texel[n] = c.texel;
                    grid.Weight[n] = c.factor / sum;
                    grid.Step[n] = c.factor * DecimateStep / sum2;
                    grid.Raw[n] = c.factor / 16f;
                    n++;
                }
            }
            grid.Start[count] = n;
        }

        // ---- Encoder (Burst) ----

        // Modes ranked by estimate, then fitted, per partitioning / plane choice
        private const int Candidates = 3;

        // Squared error per texel (channel sum) of the best one plane fit under which nothing else is tried
        private const float EarlyOut = 3;

        // Color channels tried on plane 2 (opaque blocks), worst fitted by the line first
        private const int DualTries = 3;

        // Modes fitted by the searches after the first (dual plane, partitions)
        private const int OtherCandidates = 3;

        // Partitionings fitted, best k-means matches first: two and three partitions
        private const int PartitionCandidates = 2, Partition3Candidates = 2;

        // Blocks searched together. Their fits are queued, then run 8 at a time (one per SIMD lane) per weight grid.
        private const int GroupBlocks = 64;
        private const int MaxConfigs = GroupBlocks * 4, MaxJobs = MaxConfigs * 3;

        // One search: partitioning, plane 2 channel, ideal texel weights of its lines
        private struct Block
        {
            public int N, P, Plane2, Cem, Values, Seed;
            public float4 Mask, M1, M2;     // used channels, those on plane 1, the one on plane 2 (zero: one plane)
            public byte* Part;              // partition of each texel
            public fixed float W1[144];
            public fixed float W2[144];
            public fixed float Length[3];   // squared extent of each partition's lines
        }

        private struct Result
        {
            public float Error;
            public int Mode, ColorLevel, P, Plane2, Seed, Cem;
            public float4 ChannelError;
            public fixed int Color[24];      // endpoint codes, partition after partition
            public fixed byte Weights[64];   // plane 1 at 0, plane 2 at 32
        }

        private struct Scratch
        {
            public fixed byte Zero[144];
            public fixed byte Cluster[144];
            public fixed int Mode[16];
            public fixed float Estimate[16];
            public fixed int Partition[16];
            public fixed int Mismatch[16];
        }

        // Block of the group
        private struct Info
        {
            public int Active, Gray, Alpha, Cem, Values;
            public float4 Mask, Min, Max;
            public byte* Dst;
        }

        // A search queued for fitting: its block, partitioning, plane 2 channel and ideal texel weights
        private struct Config
        {
            public int Block, P, Seed, Plane2, Cem, Values;
            public float4 Mask, M1, M2;
            public byte* Part;
            public float* W1, W2;
        }

        private struct Job
        {
            public int Config, Mode, Key;
        }

        private struct State
        {
            public int N, ConfigCount, JobCount;
            public float4* Px;     // [GroupBlocks * N]
            public Info* Info;
            public Result* Best;
            public Config* Configs;
            public float* Pool;    // ideal weights of the configs
            public Job* Jobs, Sorted;
            public int* KeyStart;  // [keys + 1], then cursors [keys + 1]
            // Lanes: texel i of channel c at c * N + i
            public LaneF* C, W1, W2, Tw1, Tw2, Infill, Res, D, Gw1, Gw2;
            public LaneI* Part, Q1, Q2, Qv;
        }

        private static byte* Carve(ref byte* p, long bytes)
        {
            byte* r = p;
            p += (bytes + 63) & ~63L;
            return r;
        }

        private static byte* Allocate(State* st, Tables* t)
        {
            int n = st->N = t->Texels;
            int keys = t->GridCount * 2 + 1;
            long lane = sizeof(LaneF);
            long size = GroupBlocks * n * 16L + GroupBlocks * (long)sizeof(Info) + GroupBlocks * (long)sizeof(Result) +
                        MaxConfigs * (long)sizeof(Config) + MaxConfigs * 2L * n * 4 + 2L * MaxJobs * sizeof(Job) + keys * 8L +
                        (18L * n + 320) * lane + 64 * 32;
            byte* mem = (byte*)UnsafeUtility.Malloc(size, 64, Allocator.Persistent);
            byte* p = mem;
            st->Px = (float4*)Carve(ref p, GroupBlocks * n * 16L);
            st->Info = (Info*)Carve(ref p, GroupBlocks * (long)sizeof(Info));
            st->Best = (Result*)Carve(ref p, GroupBlocks * (long)sizeof(Result));
            st->Configs = (Config*)Carve(ref p, MaxConfigs * (long)sizeof(Config));
            st->Pool = (float*)Carve(ref p, MaxConfigs * 2L * n * 4);
            st->Jobs = (Job*)Carve(ref p, MaxJobs * (long)sizeof(Job));
            st->Sorted = (Job*)Carve(ref p, MaxJobs * (long)sizeof(Job));
            st->KeyStart = (int*)Carve(ref p, keys * 8L);
            st->C = (LaneF*)Carve(ref p, 4 * n * lane);
            st->Res = (LaneF*)Carve(ref p, 4 * n * lane);
            st->D = (LaneF*)Carve(ref p, 4 * n * lane);
            st->W1 = (LaneF*)Carve(ref p, n * lane);
            st->W2 = (LaneF*)Carve(ref p, n * lane);
            st->Tw1 = (LaneF*)Carve(ref p, n * lane);
            st->Tw2 = (LaneF*)Carve(ref p, n * lane);
            st->Infill = (LaneF*)Carve(ref p, n * lane);
            st->Part = (LaneI*)Carve(ref p, n * lane);
            st->Gw1 = (LaneF*)Carve(ref p, 64 * lane);
            st->Gw2 = (LaneF*)Carve(ref p, 64 * lane);
            st->Q1 = (LaneI*)Carve(ref p, 64 * lane);
            st->Q2 = (LaneI*)Carve(ref p, 64 * lane);
            st->Qv = (LaneI*)Carve(ref p, 64 * lane);
            st->ConfigCount = st->JobCount = 0;
            return mem;
        }

        /// <summary>
        /// Encodes block rows [<paramref name="rowStart"/>, <paramref name="rowEnd"/>) of an RGBA32 image. Blocks past the
        /// image's edge repeat its last row and column.
        /// </summary>
        [BurstCompile(CompileSynchronously = true, FloatMode = FloatMode.Fast)]
        public static void EncodeRows(byte* rgba, int width, int height, IntPtr tables, byte* output, int rowStart, int rowEnd)
        {
            var t = (Tables*)tables;
            int blocksX = (width + t->Bw - 1) / t->Bw;
            Scratch scratch;
            Scratch* s = &scratch;
            for (int i = 0; i < 144; i++) s->Zero[i] = 0;
            State state;
            State* st = &state;
            byte* mem = Allocate(st, t);
            int total = (rowEnd - rowStart) * blocksX;
            for (int first = 0; first < total; first += GroupBlocks)
                EncodeGroup(rgba, width, height, t, s, st, output, rowStart, blocksX, first, math.min(GroupBlocks, total - first));
            UnsafeUtility.Free(mem, Allocator.Persistent);
        }

        // Blocks [first, first + count) of the rows: one plane fits, then dual plane fits and partitioned fits for the
        // blocks one plane fits poorly
        private static void EncodeGroup(byte* rgba, int width, int height, Tables* t, Scratch* s, State* st, byte* output,
            int rowStart, int blocksX, int first, int count)
        {
            int bw = t->Bw, bh = t->Bh, n = t->Texels;
            Block block;
            Block* b = &block;
            b->N = n;
            for (int j = 0; j < count; j++)
            {
                int index = first + j, by = rowStart + index / blocksX, bx = index % blocksX;
                float4* px = st->Px + j * n;
                for (int y = 0; y < bh; y++)
                {
                    uint* row = (uint*)rgba + (long)math.min(by * bh + y, height - 1) * width;
                    for (int x = 0; x < bw; x++)
                    {
                        uint p = row[math.min(bx * bw + x, width - 1)];
                        px[y * bw + x] = new float4(p & 255, (p >> 8) & 255, (p >> 16) & 255, p >> 24);
                    }
                }
                Info* info = st->Info + j;
                info->Dst = output + ((long)by * blocksX + bx) * 16;
                float4 mn = px[0], mx = px[0];
                bool gray = true;
                for (int i = 0; i < n; i++)
                {
                    mn = math.min(mn, px[i]);
                    mx = math.max(mx, px[i]);
                    gray &= px[i].x == px[i].y & px[i].y == px[i].z;
                }
                info->Min = mn;
                info->Max = mx;
                if (math.all(mn == mx))
                {
                    VoidExtent(px[0], info->Dst);
                    info->Active = 0;
                    continue;
                }
                bool alpha = mn.w < 255;
                info->Active = 1;
                info->Gray = gray ? 1 : 0;
                info->Alpha = alpha ? 1 : 0;
                info->Cem = gray ? (alpha ? 4 : 0) : (alpha ? 12 : 8);
                info->Values = gray ? (alpha ? 4 : 2) : (alpha ? 8 : 6);
                info->Mask = alpha ? 1 : new float4(1, 1, 1, 0);
                st->Best[j].Error = float.MaxValue;

                // One partition, one plane
                Begin(b, info);
                Setup(b, px, 1, 0, s->Zero, -1);
                if ((t->Skip & 1) == 0) Search(b, t, s, st, j, Candidates);
            }
            Flush(t, st);

            // One partition, a channel on its own plane: alpha, or the color channels the line fits worst
            for (int j = 0; j < count && (t->Skip & 2) == 0; j++)
            {
                Info* info = st->Info + j;
                Result* best = st->Best + j;
                if (info->Active == 0 || best->Error <= EarlyOut * n) continue;
                float4* px = st->Px + j * n;
                Begin(b, info);
                if (info->Gray != 0)
                {
                    if (info->Alpha != 0)
                    {
                        Setup(b, px, 1, 0, s->Zero, 3);
                        Search(b, t, s, st, j, OtherCandidates);
                    }
                    continue;
                }
                if (info->Alpha != 0)
                {
                    Setup(b, px, 1, 0, s->Zero, 3);
                    Search(b, t, s, st, j, OtherCandidates);
                    continue;
                }
                float4 lineError = best->ChannelError;
                for (int tries = 0; tries < DualTries; tries++)
                {
                    int c = lineError.x >= lineError.y && lineError.x >= lineError.z ? 0 : lineError.y >= lineError.z ? 1 : 2;
                    Setup(b, px, 1, 0, s->Zero, c);
                    Search(b, t, s, st, j, OtherCandidates);
                    lineError[c] = -1;
                }
            }
            Flush(t, st);

            // Two and three partitions, closest to a k-means clustering
            for (int j = 0; j < count; j++)
            {
                Info* info = st->Info + j;
                if (info->Active == 0 || st->Best[j].Error <= EarlyOut * n) continue;
                float4* px = st->Px + j * n;
                Begin(b, info);
                for (int p = 2; p <= 3; p++)
                {
                    if (p * info->Values > 18) break;
                    if ((t->Skip & (p == 2 ? 4 : 8)) != 0) continue;
                    int found = FindPartitions(px, t, s, p, info->Mask, p == 2 ? PartitionCandidates : Partition3Candidates);
                    for (int k = 0; k < found; k++)
                    {
                        int index = s->Partition[k];
                        int seed = p == 2 ? t->PartSeed2[index] : t->PartSeed3[index];
                        byte* part = (p == 2 ? t->PartAssign2 : t->PartAssign3) + index * n;
                        Setup(b, px, p, seed, part, -1);
                        Search(b, t, s, st, j, OtherCandidates);
                    }
                }
            }
            Flush(t, st);

            for (int j = 0; j < count; j++)
            {
                Info* info = st->Info + j;
                if (info->Active == 0) continue;
                Result* best = st->Best + j;
                if (best->Error == float.MaxValue)
                {
                    VoidExtent(math.round((info->Min + info->Max) * 0.5f), info->Dst);
                    continue;
                }
                t->Predicted += best->Error;
                t->Wins[best->P == 1 ? (best->Plane2 >= 0 ? 1 : 0) : best->P] += 1;
                t->ModeWins[best->Mode * 4 + best->P] += 1;
                Pack(t, best, best->Cem, info->Values, info->Dst);
            }
        }

        private static void Begin(Block* b, Info* info)
        {
            b->Cem = info->Cem;
            b->Values = info->Values;
            b->Mask = info->Mask;
        }

        // Lines of each partition: principal axis of the plane 1 channels, extent of the plane 2 channel
        private static void Setup(Block* b, float4* px, int partitions, int seed, byte* part, int plane2)
        {
            int n = b->N;
            b->P = partitions;
            b->Seed = seed;
            b->Part = part;
            b->Plane2 = plane2;
            b->M2 = 0;
            if (plane2 >= 0) b->M2[plane2] = 1;
            b->M1 = b->Mask * (1 - b->M2);
            for (int k = 0; k < partitions; k++)
            {
                float4 mean = 0, mn = float.MaxValue, mx = float.MinValue;
                int count = 0;
                for (int i = 0; i < n; i++)
                {
                    if (part[i] != k) continue;
                    float4 p = px[i] * b->M1;
                    mean += p;
                    mn = math.min(mn, p);
                    mx = math.max(mx, p);
                    count++;
                }
                mean /= math.max(count, 1);
                float4x4 cov = 0;
                for (int i = 0; i < n; i++)
                {
                    if (part[i] != k) continue;
                    float4 d = px[i] * b->M1 - mean;
                    cov.c0 += d * d.x;
                    cov.c1 += d * d.y;
                    cov.c2 += d * d.z;
                    cov.c3 += d * d.w;
                }
                float4 axis = mx - mn;
                for (int it = 0; it < 4; it++)
                {
                    axis = math.mul(cov, axis);
                    float m = math.cmax(math.abs(axis));
                    if (m > 0) axis /= m;
                }
                if (math.dot(axis, axis) < 1e-12f) axis = mx - mn;
                if (math.dot(axis, axis) < 1e-12f) axis = b->M1;
                axis = math.normalize(axis);
                if (axis.x + axis.y + axis.z < 0) axis = -axis;

                float tmin = float.MaxValue, tmax = float.MinValue;
                for (int i = 0; i < n; i++)
                {
                    if (part[i] != k) continue;
                    float d = math.dot(px[i] * b->M1 - mean, axis);
                    b->W1[i] = d;
                    tmin = math.min(tmin, d);
                    tmax = math.max(tmax, d);
                }
                float scale = tmax > tmin ? 1 / (tmax - tmin) : 0;
                for (int i = 0; i < n; i++)
                    if (part[i] == k) b->W1[i] = (b->W1[i] - tmin) * scale;
                b->Length[k] = (tmax - tmin) * (tmax - tmin);

                if (plane2 >= 0)
                {
                    float lo = float.MaxValue, hi = float.MinValue;
                    for (int i = 0; i < n; i++)
                        if (part[i] == k)
                        {
                            lo = math.min(lo, px[i][plane2]);
                            hi = math.max(hi, px[i][plane2]);
                        }
                    float inv = hi > lo ? 1 / (hi - lo) : 0;
                    for (int i = 0; i < n; i++)
                        if (part[i] == k) b->W2[i] = (px[i][plane2] - lo) * inv;
                    b->Length[k] += (hi - lo) * (hi - lo);
                }
            }
        }

        // Modes ranked by an error estimate (weight decimation, weight and endpoint quantization), the best queued
        private static void Search(Block* b, Tables* t, Scratch* s, State* st, int block, int candidates)
        {
            int n = b->N;
            bool dual = b->Plane2 >= 0;
            int valueIndex = (b->P - 1) * 5 + b->Values / 2;
            float length = 0;
            for (int i = 0; i < n; i++) length += b->Length[b->Part[i]];
            float channels = math.csum(b->Mask);
            int found = 0;
            // Decimation error estimates, separable: each axis decimated alone, errors added
            float* ex = stackalloc float[13];
            float* ey = stackalloc float[13];
            for (int g = 0; g < 13; g++) ex[g] = ey[g] = 0;
            AxisErrors(b, t, b->W1, ex, ey);
            if (dual) AxisErrors(b, t, b->W2, ex, ey);

            ex[t->Bw] = 0;
            ey[t->Bh] = 0;
            int* list = dual ? t->DualModes : t->SingleModes;
            int count = dual ? t->DualCount : t->SingleCount;
            for (int i = 0; i < count; i++)
            {
                int m = list[i];
                Mode* mode = t->Modes + m;
                if (mode->ColorLevel[valueIndex] < 0 || (t->OnlyMode >= 0 && m != t->OnlyMode)) continue;
                float estimate = length * mode->WeightNoise + channels * mode->ColorNoise[valueIndex] + ex[mode->X] + ey[mode->Y];
                if (found < candidates || estimate < s->Estimate[found - 1])
                {
                    int k = found < candidates ? found++ : found - 1;
                    while (k > 0 && s->Estimate[k - 1] > estimate)
                    {
                        s->Estimate[k] = s->Estimate[k - 1];
                        s->Mode[k] = s->Mode[k - 1];
                        k--;
                    }
                    s->Estimate[k] = estimate;
                    s->Mode[k] = m;
                }
            }
            if (found == 0) return;

            int ci = st->ConfigCount++;
            Config* cfg = st->Configs + ci;
            cfg->Block = block;
            cfg->P = b->P;
            cfg->Seed = b->Seed;
            cfg->Plane2 = b->Plane2;
            cfg->Cem = b->Cem;
            cfg->Values = b->Values;
            cfg->Mask = b->Mask;
            cfg->M1 = b->M1;
            cfg->M2 = b->M2;
            cfg->Part = b->Part;
            cfg->W1 = st->Pool + ci * 2 * n;
            cfg->W2 = cfg->W1 + n;
            for (int i = 0; i < n; i++)
            {
                cfg->W1[i] = b->W1[i];
                cfg->W2[i] = dual ? b->W2[i] : 0;
            }
            for (int k = 0; k < found; k++)
            {
                Job* job = st->Jobs + st->JobCount++;
                job->Config = ci;
                job->Mode = s->Mode[k];
                job->Key = t->Modes[s->Mode[k]].Grid * 2 + t->Modes[s->Mode[k]].Dual;
            }
        }

        // Fits the queued jobs, 8 at a time per weight grid, and keeps each block's best
        private static void Flush(Tables* t, State* st)
        {
            int keys = t->GridCount * 2, jobs = st->JobCount;
            int* start = st->KeyStart;
            int* cursor = start + keys + 1;
            for (int k = 0; k <= keys; k++) start[k] = 0;
            for (int j = 0; j < jobs; j++) start[st->Jobs[j].Key + 1]++;
            for (int k = 0; k < keys; k++) start[k + 1] += start[k];
            for (int k = 0; k <= keys; k++) cursor[k] = start[k];
            for (int j = 0; j < jobs; j++) st->Sorted[cursor[st->Jobs[j].Key]++] = st->Jobs[j];
            for (int k = 0; k < keys; k++)
                for (int i = start[k]; i < start[k + 1]; i += LaneF.W)
                    FitLanes(t, st, st->Sorted + i, math.min(LaneF.W, start[k + 1] - i));
            st->JobCount = 0;
            st->ConfigCount = 0;
        }

        // Squared error of the texel weights decimated to g weights along one axis (each row or column alone), scaled by
        // the lines' extents
        // 8 floats from p, any alignment
        private static LaneF Load8(float* p) =>
            X86.Avx.IsAvxSupported ? LaneF.Of(X86.Avx.mm256_loadu_ps(p)) : LaneF.Of(*(float4*)p, *(float4*)(p + 4));

        // Adds to ex[g] / ey[g] the error of the texel weights decimated to g weights along x / y alone, every grid
        // size, scaled by the lines' extents. The rows (or columns) of the block are the SIMD lanes: 16 lines at most,
        // missing lines are zero.
        private static void AxisErrors(Block* b, Tables* t, float* w, float* ex, float* ey)
        {
            int bw = t->Bw, bh = t->Bh;
            float* lw = stackalloc float[12 * 16];
            float* ll = stackalloc float[12 * 16];
            LaneF* num = stackalloc LaneF[13];
            LaneF* gw = stackalloc LaneF[13];
            for (int axis = 0; axis < 2; axis++)
            {
                int len = axis == 0 ? bw : bh, lines = axis == 0 ? bh : bw;
                // lw[x * 16 + line]: texel x of each line
                for (int i = 0; i < 12 * 16; i++) lw[i] = ll[i] = 0;
                for (int y = 0; y < bh; y++)
                for (int x = 0; x < bw; x++)
                {
                    int i = y * bw + x, o = axis == 0 ? x * 16 + y : y * 16 + x;
                    lw[o] = w[i];
                    ll[o] = b->Length[b->Part[i]];
                }
                float* e = axis == 0 ? ex : ey;
                for (int g = 2; g < len; g++)
                {
                    byte* jt = t->AxisJ + (axis * 13 + g) * 12;
                    float* ft = t->AxisF + (axis * 13 + g) * 12;
                    float* inv = t->AxisInv + (axis * 13 + g) * 13;
                    LaneF total = 0;
                    for (int half = 0; half < (lines > 8 ? 2 : 1); half++)
                    {
                        for (int j = 0; j <= g; j++) num[j] = 0;
                        for (int x = 0; x < len; x++)
                        {
                            int j = jt[x];
                            LaneF v = Load8(lw + x * 16 + half * 8), fv = v * ft[x];
                            num[j] += v - fv;
                            num[j + 1] += fv;
                        }
                        for (int j = 0; j < g; j++) gw[j] = num[j] * inv[j];
                        gw[g] = gw[g - 1];
                        for (int j = 0; j <= g; j++) num[j] = 0;
                        for (int x = 0; x < len; x++)
                        {
                            int j = jt[x];
                            LaneF rr = Load8(lw + x * 16 + half * 8) - (gw[j] + (gw[j + 1] - gw[j]) * ft[x]), fr = rr * ft[x];
                            num[j] += rr - fr;
                            num[j + 1] += fr;
                        }
                        for (int j = 0; j < g; j++) gw[j] = vmath.clamp(gw[j] + num[j] * inv[j], 0, 1);
                        gw[g] = gw[g - 1];
                        for (int x = 0; x < len; x++)
                        {
                            int j = jt[x];
                            LaneF d = gw[j] + (gw[j + 1] - gw[j]) * ft[x] - Load8(lw + x * 16 + half * 8);
                            total += d * d * Load8(ll + x * 16 + half * 8);
                        }
                    }
                    float4 sum = total.Lo + total.Hi;
                    e[g] += sum.x + sum.y + sum.z + sum.w;
                }
            }
        }

        // ---- Fits, 8 jobs per lane set (same weight grid and plane count) ----

        // Value of a texel's partition
        private static LaneF Sel(LaneF x0, LaneF x1, LaneF x2, LaneI part, int maxP)
        {
            if (maxP == 1) return x0;
            LaneF x = vmath.select(x0, x1, part == 1);
            return maxP == 2 ? x : vmath.select(x, x2, part == 2);
        }

        private static void FitLanes(Tables* t, State* st, Job* jobs, int count)
        {
            int n = st->N;
            Mode* firstMode = t->Modes + jobs[0].Mode;
            Grid* grid = t->Grids + firstMode->Grid;
            bool dual = firstMode->Dual != 0;
            LaneI level = 0, colorLevel = 0, cem = 0, parts = 0;
            LaneF4 m1 = default, m2 = default, mask = default;
            int maxP = 1;
            bool alpha = false;
            LaneF* C = st->C;
            for (int l = 0; l < LaneF.W; l++)
            {
                Job* job = jobs + math.min(l, count - 1);
                Config* cfg = st->Configs + job->Config;
                Mode* mode = t->Modes + job->Mode;
                level[l] = mode->Level;
                colorLevel[l] = mode->ColorLevel[(cfg->P - 1) * 5 + cfg->Values / 2];
                cem[l] = cfg->Cem;
                parts[l] = cfg->P;
                maxP = math.max(maxP, cfg->P);
                alpha |= cfg->Mask.w > 0;
                float4 cm1 = cfg->M1, cm2 = cfg->M2, cmask = cfg->Mask;
                m1.C0[l] = cm1.x; m1.C1[l] = cm1.y; m1.C2[l] = cm1.z; m1.C3[l] = cm1.w;
                m2.C0[l] = cm2.x; m2.C1[l] = cm2.y; m2.C2[l] = cm2.z; m2.C3[l] = cm2.w;
                mask.C0[l] = cmask.x; mask.C1[l] = cmask.y; mask.C2[l] = cmask.z; mask.C3[l] = cmask.w;
                float4* px = st->Px + cfg->Block * n;
                float* w1 = cfg->W1;
                float* w2 = cfg->W2;
                byte* part = cfg->Part;
                for (int i = 0; i < n; i++)
                {
                    float4 p = px[i] * cmask;
                    C[i][l] = p.x;
                    C[n + i][l] = p.y;
                    C[2 * n + i][l] = p.z;
                    C[3 * n + i][l] = p.w;
                    st->W1[i][l] = w1[i];
                    st->W2[i][l] = w2[i];
                    st->Part[i][l] = part[i];
                }
            }
            int channels = alpha ? 4 : 3;
            LaneI wb65 = level * 65, wb32 = level * 32, cb = colorLevel * 256;
            LaneB lum = cem < 8, colorCem = cem >= 8;
            LaneB order = (parts > 1) & colorCem;
            LaneI* pk = st->Part;

            DecimateL(grid, st->W1, st->Gw1, n, st->Infill);
            QuantizeWeightsL(t, grid, wb65, wb32, st->Gw1, st->Q1, st->Tw1, n, st->Qv);
            if (dual)
            {
                DecimateL(grid, st->W2, st->Gw2, n, st->Infill);
                QuantizeWeightsL(t, grid, wb65, wb32, st->Gw2, st->Q2, st->Tw2, n, st->Qv);
            }
            else
                for (int i = 0; i < n; i++) st->Tw2[i] = 0;

            LaneF4* e0 = stackalloc LaneF4[3];
            LaneF4* e1 = stackalloc LaneF4[3];
            LaneF4* fa = stackalloc LaneF4[3];
            LaneF4* fb = stackalloc LaneF4[3];
            LaneF4* oe0 = stackalloc LaneF4[3];
            LaneF4* oe1 = stackalloc LaneF4[3];
            LaneI4* k0 = stackalloc LaneI4[3];
            LaneI4* k1 = stackalloc LaneI4[3];
            LaneI4* ok0 = stackalloc LaneI4[3];
            LaneI4* ok1 = stackalloc LaneI4[3];
            LaneF4* sc = stackalloc LaneF4[3];
            LaneF* inv2 = stackalloc LaneF[3];
            for (int k = 0; k < 3; k++)
            {
                e0[k] = default;
                e1[k] = default;
                k0[k] = default;
                k1[k] = default;
                inv2[k] = 0;
            }
            LaneB useOff = false;
            for (int pass = 0; pass < Passes; pass++)
            {
                // Least squares endpoints for the weights, per channel (plane 2's channel with its own weights)
                LaneB offsets = colorCem;
                for (int k = 0; k < maxP; k++)
                {
                    SumsL(st, n, k, maxP, dual, channels, m1, m2, out var saa, out var sab, out var sbb, out var sap, out var sbp,
                        out LaneF cnt);
                    LaneF dsum = 0;
                    LaneF ncount = vmath.max(cnt, 1);
                    for (int c = 0; c < 4; c++)
                    {
                        if (c >= channels)
                        {
                            fa[k][c] = 0;
                            fb[k][c] = 0;
                            continue;
                        }
                        LaneF det = saa[c] * sbb[c] - sab[c] * sab[c];
                        LaneB flat = vmath.abs(det) < 1e-4f;
                        LaneF inv = vmath.select(1 / vmath.select(det, 1, flat), 0, flat);
                        LaneF mean = (sap[c] + sbp[c]) / ncount;
                        LaneF ea = vmath.select((sap[c] * sbb[c] - sbp[c] * sab[c]) * inv, mean, flat);
                        LaneF eb = vmath.select((sbp[c] * saa[c] - sap[c] * sab[c]) * inv, mean, flat);
                        fa[k][c] = vmath.clamp(ea, 0, 255);
                        fb[k][c] = vmath.clamp(eb, 0, 255);
                        LaneF d = (fb[k][c] - fa[k][c]) * mask[c];
                        offsets &= (d <= 31) & (d >= -32);
                        if (c < 3) dsum += d;
                    }
                    offsets &= dsum >= 0;
                }

                // Base + offset endpoints when every partition's endpoints are close: about twice the precision
                useOff = false;
                if (vmath.any(offsets))
                {
                    LaneB ok = offsets;
                    for (int k = 0; k < maxP; k++)
                        ok &= QuantizeOffsetsL(t, cb, fa[k], fb[k], channels, mask, out oe0[k], out oe1[k], out ok0[k], out ok1[k]) |
                              (parts <= k);
                    useOff = ok;
                }
                if (!vmath.all(useOff))
                    for (int k = 0; k < maxP; k++)
                        QuantizeEndpointsL(t, cb, lum, order, fa[k], fb[k], mask, out e0[k], out e1[k], out k0[k], out k1[k]);
                if (vmath.any(useOff))
                    for (int k = 0; k < maxP; k++)
                    for (int c = 0; c < 4; c++)
                    {
                        e0[k][c] = vmath.select(e0[k][c], oe0[k][c], useOff);
                        e1[k][c] = vmath.select(e1[k][c], oe1[k][c], useOff);
                        k0[k][c] = vmath.select(k0[k][c], ok0[k][c], useOff);
                        k1[k][c] = vmath.select(k1[k][c], ok1[k][c], useOff);
                    }

                // Weights reprojected on the quantized endpoints: plane 1 along each partition's line, plane 2 its
                // channel's range
                for (int k = 0; k < maxP; k++)
                {
                    LaneF dd = 0, d2 = 0;
                    for (int c = 0; c < channels; c++)
                    {
                        LaneF d = e1[k][c] - e0[k][c], d1 = d * m1[c];
                        dd += d1 * d1;
                        d2 += d * m2[c];
                    }
                    LaneB line = dd > 0;
                    LaneF invdd = 1 / vmath.select(1, dd, line);
                    for (int c = 0; c < channels; c++)
                        sc[k][c] = vmath.select(0, (e1[k][c] - e0[k][c]) * m1[c] * invdd, line);
                    if (dual)
                    {
                        LaneB has = d2 != 0;
                        inv2[k] = vmath.select(0, 1 / vmath.select(1, d2, has), has);
                    }
                }
                for (int i = 0; i < n; i++)
                {
                    LaneI p = pk[i];
                    LaneF w = 0, w2 = 0;
                    for (int c = 0; c < channels; c++)
                    {
                        LaneF o = C[c * n + i] - Sel(e0[0][c], e0[1][c], e0[2][c], p, maxP);
                        w += o * Sel(sc[0][c], sc[1][c], sc[2][c], p, maxP);
                        if (dual) w2 += o * m2[c];
                    }
                    st->W1[i] = vmath.clamp(w, 0, 1);
                    if (dual) st->W2[i] = vmath.clamp(w2 * Sel(inv2[0], inv2[1], inv2[2], p, maxP), 0, 1);
                }
                DecimateL(grid, st->W1, st->Gw1, n, st->Infill);
                QuantizeWeightsL(t, grid, wb65, wb32, st->Gw1, st->Q1, st->Tw1, n, st->Qv);
                if (dual)
                {
                    DecimateL(grid, st->W2, st->Gw2, n, st->Infill);
                    QuantizeWeightsL(t, grid, wb65, wb32, st->Gw2, st->Q2, st->Tw2, n, st->Qv);
                }
            }

            // Decimated grids: each grid weight moved a step when it lowers the error
            if (grid->Full == 0)
            {
                for (int i = 0; i < n; i++)
                {
                    LaneI p = pk[i];
                    LaneF w1 = st->Tw1[i], w2 = st->Tw2[i];
                    for (int c = 0; c < channels; c++)
                    {
                        LaneF o = Sel(e0[0][c], e0[1][c], e0[2][c], p, maxP);
                        LaneF d = Sel(e1[0][c], e1[1][c], e1[2][c], p, maxP) - o;
                        st->D[c * n + i] = d;
                        st->Res[c * n + i] = o + d * (m1[c] * w1 + m2[c] * w2) - C[c * n + i];
                    }
                }
                RealignL(t, grid, wb32, m1, st->Q1, st->Tw1, n, channels, st);
                if (dual) RealignL(t, grid, wb32, m2, st->Q2, st->Tw2, n, channels, st);
            }

            // Endpoint codes moved a step when it lowers the error for the final weights
            for (int k = 0; k < maxP; k++)
            {
                SumsL(st, n, k, maxP, dual, channels, m1, m2, out var saa, out var sab, out var sbb, out var sap, out var sbp,
                    out _);
                RefineL(t, cb, lum, colorCem, mask.C3 > 0, order, useOff, saa, sab, sbb, sap, sbp, e0 + k, e1 + k, k0 + k, k1 + k);
            }

            LaneF4 acc = default;
            for (int i = 0; i < n; i++)
            {
                LaneI p = pk[i];
                LaneF w1 = st->Tw1[i], w2 = st->Tw2[i];
                for (int c = 0; c < channels; c++)
                {
                    LaneF o = Sel(e0[0][c], e0[1][c], e0[2][c], p, maxP);
                    LaneF d = Sel(e1[0][c], e1[1][c], e1[2][c], p, maxP) - o;
                    LaneF e = o + d * (m1[c] * w1 + m2[c] * w2) - C[c * n + i];
                    acc[c] += e * e;
                }
            }
            LaneF error = acc.C0 + acc.C1 + acc.C2 + acc.C3;
            LaneI offsetCem = vmath.select(0, 1, useOff);
            int weights = grid->Count;
            for (int l = 0; l < count; l++)
            {
                Job* job = jobs + l;
                Config* cfg = st->Configs + job->Config;
                Result* r = st->Best + cfg->Block;
                if (!(error[l] < r->Error)) continue;
                r->Error = error[l];
                r->Mode = job->Mode;
                r->ColorLevel = colorLevel[l];
                r->P = cfg->P;
                r->Plane2 = cfg->Plane2;
                r->Seed = cfg->Seed;
                r->Cem = cfg->Cem + offsetCem[l];
                r->ChannelError = new float4(acc.C0[l], acc.C1[l], acc.C2[l], acc.C3[l]);
                for (int k = 0; k < cfg->P; k++)
                {
                    int* col = r->Color + k * cfg->Values;
                    if (cfg->Cem < 8)
                    {
                        col[0] = k0[k].C0[l];
                        col[1] = k1[k].C0[l];
                        if (cfg->Values == 4)
                        {
                            col[2] = k0[k].C3[l];
                            col[3] = k1[k].C3[l];
                        }
                    }
                    else
                        for (int c = 0; c < cfg->Values / 2; c++)
                        {
                            col[2 * c] = k0[k][c][l];
                            col[2 * c + 1] = k1[k][c][l];
                        }
                }
                for (int g = 0; g < weights; g++)
                {
                    r->Weights[g] = (byte)st->Q1[g][l];
                    if (dual) r->Weights[32 + g] = (byte)st->Q2[g][l];
                }
            }
        }

        // Least squares sums of partition k for the current texel weights, per channel (plane 2's channel with its own
        // weights): (1 - w)², (1 - w) w, w², (1 - w) p, w p, and the partition's texel count
        private static void SumsL(State* st, int n, int k, int maxP, bool dual, int channels, LaneF4 m1, LaneF4 m2,
            out LaneF4 saa, out LaneF4 sab, out LaneF4 sbb, out LaneF4 sap, out LaneF4 sbp, out LaneF cnt)
        {
            LaneF a1 = 0, ab1 = 0, bb1 = 0, a2 = 0, ab2 = 0, bb2 = 0;
            cnt = 0;
            LaneF4 sp = default, s1 = default, s2 = default;
            LaneF* C = st->C;
            for (int i = 0; i < n; i++)
            {
                LaneF pm = maxP == 1 ? (LaneF)1 : vmath.select(0f, 1f, st->Part[i] == k);
                LaneF t1 = st->Tw1[i], b1 = pm * t1, x1 = pm - b1;
                a1 += x1 * x1;
                ab1 += x1 * t1;
                bb1 += b1 * t1;
                cnt += pm;
                if (dual)
                {
                    LaneF t2 = st->Tw2[i], b2 = pm * t2, x2 = pm - b2;
                    a2 += x2 * x2;
                    ab2 += x2 * t2;
                    bb2 += b2 * t2;
                    for (int c = 0; c < channels; c++)
                    {
                        LaneF p = C[c * n + i] * pm;
                        sp[c] += p;
                        s1[c] += p * t1;
                        s2[c] += p * t2;
                    }
                }
                else
                    for (int c = 0; c < channels; c++)
                    {
                        LaneF p = C[c * n + i] * pm;
                        sp[c] += p;
                        s1[c] += p * t1;
                    }
            }
            saa = default;
            sab = default;
            sbb = default;
            sap = default;
            sbp = default;
            for (int c = 0; c < 4; c++)
            {
                saa[c] = m1[c] * a1 + m2[c] * a2;
                sab[c] = m1[c] * ab1 + m2[c] * ab2;
                sbb[c] = m1[c] * bb1 + m2[c] * bb2;
                sbp[c] = m1[c] * s1[c] + m2[c] * s2[c];
                sap[c] = sp[c] - sbp[c];
            }
        }

        // Grid weights from texel weights: averages of the texels each weight covers, then correction steps
        private static void DecimateL(Grid* grid, LaneF* w, LaneF* gw, int n, LaneF* infill)
        {
            int count = grid->Count;
            if (grid->Full != 0)
            {
                for (int i = 0; i < count; i++) gw[i] = w[i];
                return;
            }
            int* start = grid->Start;
            int* texel = grid->Texel;
            for (int g = 0; g < count; g++)
            {
                LaneF s = 0;
                for (int c = start[g]; c < start[g + 1]; c++) s += w[texel[c]] * grid->Weight[c];
                gw[g] = s;
            }
            for (int it = 0; it < DecimateIterations; it++)
            {
                int* idx = grid->Index;
                int* f = grid->Factor;
                for (int i = 0; i < n; i++, idx += 4, f += 4)
                    infill[i] = (gw[idx[0]] * f[0] + gw[idx[1]] * f[1] + gw[idx[2]] * f[2] + gw[idx[3]] * f[3]) * (1f / 16);
                for (int g = 0; g < count; g++)
                {
                    LaneF s = 0;
                    for (int c = start[g]; c < start[g + 1]; c++)
                    {
                        int tx = texel[c];
                        s += (w[tx] - infill[tx]) * grid->Step[c];
                    }
                    gw[g] = vmath.clamp(gw[g] + s, 0, 1);
                }
            }
        }

        // Texel weights (value / 64) as the decoder infills the grid weight values q
        private static void InfillQ(Grid* grid, LaneI* q, LaneF* tw, int n)
        {
            if (grid->Full != 0)
            {
                for (int i = 0; i < n; i++) tw[i] = (LaneF)q[i] * (1f / 64);
                return;
            }
            int* idx = grid->Index;
            int* f = grid->Factor;
            for (int i = 0; i < n; i++, idx += 4, f += 4)
                tw[i] = (LaneF)((q[idx[0]] * f[0] + q[idx[1]] * f[1] + q[idx[2]] * f[2] + q[idx[3]] * f[3] + 8) >> 4) * (1f / 64);
        }

        // Quantized grid weights (codes), texel weights as the decoder infills them
        private static void QuantizeWeightsL(Tables* t, Grid* grid, LaneI wb65, LaneI wb32, LaneF* gw, LaneI* codes, LaneF* tw,
            int n, LaneI* q)
        {
            int count = grid->Count;
            for (int g = 0; g < count; g++)
            {
                LaneI v = (LaneI)(vmath.clamp(gw[g], 0, 1) * 64 + 0.5f);
                LaneI c = vmath.gather(t->WQ, wb65 + v);
                codes[g] = c;
                q[g] = vmath.gather(t->WU, wb32 + c);
            }
            InfillQ(grid, q, tw, n);
        }

        // Each grid weight of one plane (channels in plane) tried a step up and down against the residuals, which follow
        // the accepted changes. Texel weights are then infilled again.
        private static void RealignL(Tables* t, Grid* grid, LaneI wb32, LaneF4 plane, LaneI* codes, LaneF* tw, int n, int channels,
            State* st)
        {
            int* unquant = t->WU;
            int count = grid->Count;
            int* start = grid->Start;
            int* texel = grid->Texel;
            LaneF* D = st->D;
            LaneF* R = st->Res;
            for (int g = 0; g < count; g++)
            {
                LaneI c = codes[g];
                LaneF rd = 0, dd = 0;
                for (int k = start[g]; k < start[g + 1]; k++)
                {
                    int i = texel[k];
                    float f = grid->Raw[k];
                    LaneF dot = 0, d2 = 0;
                    for (int ch = 0; ch < channels; ch++)
                    {
                        LaneF d = D[ch * n + i] * plane[ch];
                        dot += R[ch * n + i] * d;
                        d2 += d * d;
                    }
                    rd += dot * f;
                    dd += d2 * (f * f);
                }
                // Error change of a weight change x (in weight units): 2 x rd + x² dd
                LaneI up = vmath.gather(t->WUp, wb32 + c), down = vmath.gather(t->WDown, wb32 + c);
                LaneI uc = vmath.gather(unquant, wb32 + c);
                LaneF xu = (LaneF)(vmath.gather(unquant, wb32 + up) - uc) * (1f / 64);
                LaneF xd = (LaneF)(vmath.gather(unquant, wb32 + down) - uc) * (1f / 64);
                LaneF cu = (2f * rd + xu * dd) * xu, cd = (2f * rd + xd * dd) * xd;
                LaneB vu = (up != c) & (cu < 0);
                LaneB vd = (down != c) & (cd < vmath.select(0, cu, vu));
                if (!vmath.any(vu | vd)) continue;
                codes[g] = vmath.select(vmath.select(c, up, vu), down, vd);
                LaneF delta = vmath.select(vmath.select(0, xu, vu), xd, vd);
                for (int k = start[g]; k < start[g + 1]; k++)
                {
                    int i = texel[k];
                    LaneF s = delta * grid->Raw[k];
                    for (int ch = 0; ch < channels; ch++) R[ch * n + i] += D[ch * n + i] * plane[ch] * s;
                }
            }
            for (int g = 0; g < count; g++) st->Qv[g] = vmath.gather(unquant, wb32 + codes[g]);
            InfillQ(grid, st->Qv, tw, n);
        }

        // Endpoint codes and values. Luminance lanes quantize the color channels' mean (slot 0). RGB(A) of several
        // partitions: the second endpoint's sum is kept at least the first's (else the decoder swaps them), by moving
        // the codes apart a step at a time.
        private static void QuantizeEndpointsL(Tables* t, LaneI cb, LaneB lum, LaneB order, LaneF4 a, LaneF4 b, LaneF4 mask,
            out LaneF4 e0, out LaneF4 e1, out LaneI4 k0, out LaneI4 k1)
        {
            int* quant = t->CQ;
            int* unquant = t->CU;
            LaneF la = (a.C0 + a.C1 + a.C2) * (1f / 3), lb = (b.C0 + b.C1 + b.C2) * (1f / 3);
            k0 = default;
            k1 = default;
            for (int s = 0; s < 4; s++)
            {
                LaneF va = s == 0 ? vmath.select(a.C0, la, lum) : a[s];
                LaneF vb = s == 0 ? vmath.select(b.C0, lb, lum) : b[s];
                k0[s] = vmath.gather(quant, cb + (LaneI)(va + 0.5f));
                k1[s] = vmath.gather(quant, cb + (LaneI)(vb + 0.5f));
            }
            if (vmath.any(order))
            {
                int* up = t->CUp;
                int* down = t->CDown;
                for (int it = 0; it < 8; it++)
                {
                    LaneI s0 = 0, s1 = 0;
                    for (int c = 0; c < 3; c++)
                    {
                        s0 += vmath.gather(unquant, cb + k0[c]);
                        s1 += vmath.gather(unquant, cb + k1[c]);
                    }
                    LaneB act = order & (s1 < s0);
                    if (!vmath.any(act)) break;
                    // Channel where the endpoints are closest: raise the second endpoint or lower the first
                    LaneI ch = 0, gap = int.MaxValue;
                    for (int c = 0; c < 3; c++)
                    {
                        LaneI g = vmath.gather(unquant, cb + k0[c]) - vmath.gather(unquant, cb + k1[c]);
                        g = vmath.max(g, 0 - g);
                        LaneB movable = (vmath.gather(up, cb + k1[c]) != k1[c]) | (vmath.gather(down, cb + k0[c]) != k0[c]);
                        LaneB better = movable & (g < gap);
                        gap = vmath.select(gap, g, better);
                        ch = vmath.select(ch, c, better);
                    }
                    for (int c = 0; c < 3; c++)
                    {
                        LaneB here = act & (ch == c);
                        LaneI u = vmath.gather(up, cb + k1[c]);
                        LaneB raise = u != k1[c];
                        k1[c] = vmath.select(k1[c], u, here & raise);
                        k0[c] = vmath.select(k0[c], vmath.gather(down, cb + k0[c]), here & !raise);
                    }
                }
            }
            e0 = default;
            e1 = default;
            LaneF u00 = vmath.gather(unquant, cb + k0[0]), u10 = vmath.gather(unquant, cb + k1[0]);
            e0.C0 = u00;
            e1.C0 = u10;
            e0.C1 = vmath.select(vmath.gather(unquant, cb + k0[1]), u00, lum);
            e1.C1 = vmath.select(vmath.gather(unquant, cb + k1[1]), u10, lum);
            e0.C2 = vmath.select(vmath.gather(unquant, cb + k0[2]), u00, lum);
            e1.C2 = vmath.select(vmath.gather(unquant, cb + k1[2]), u10, lum);
            e0.C3 = (LaneF)vmath.gather(unquant, cb + k0[3]) * mask.C3;
            e1.C3 = (LaneF)vmath.gather(unquant, cb + k1[3]) * mask.C3;
        }

        // Endpoint values of code pairs (unquantized u0, u1): direct, or base + offset
        private static void DecodeL(LaneI u0, LaneI u1, LaneB offset, out LaneI v0, out LaneI v1, out LaneI off)
        {
            LaneI b = (u0 >> 1) | (u1 & 0x80);
            LaneI o = (u1 >> 1) & 0x3F;
            o -= (o & 0x20) << 1;
            v0 = vmath.select(u0, b, offset);
            off = vmath.select(0, o, offset);
            v1 = vmath.select(u1, vmath.clamp(b + o, 0, 255), offset);
        }

        // Error of endpoints (a, b) on one channel, from the least squares sums (constant term left out)
        private static LaneF PairError(LaneF a, LaneF b, LaneF saa, LaneF sab, LaneF sbb, LaneF sap, LaneF sbp) =>
            a * (a * saa + 2f * (b * sab - sap)) + b * (b * sbb - 2f * sbp);

        // RGB(A) base + offset (modes 9 and 13): base = v0 >> 1 with v1's top bit, offset = 6 bit signed (v1 >> 1).
        // Each channel picks the best of the codes around its targets. False when the offsets' sum ends up negative
        // (the decoder would swap and blue contract).
        private static LaneB QuantizeOffsetsL(Tables* t, LaneI cb, LaneF4 a, LaneF4 b, int channels, LaneF4 mask, out LaneF4 e0,
            out LaneF4 e1, out LaneI4 k0, out LaneI4 k1)
        {
            int* quant = t->CQ;
            int* unquant = t->CU;
            int* up = t->CUp;
            int* down = t->CDown;
            e0 = default;
            e1 = default;
            k0 = default;
            k1 = default;
            LaneI offsetSum = 0;
            for (int c = 0; c < channels; c++)
            {
                LaneI bas = (LaneI)(a[c] + 0.5f), off = vmath.clamp((LaneI)vmath.round(b[c] - a[c]), -32, 31);
                LaneI q0 = vmath.gather(quant, cb + ((bas & 0x7F) << 1));
                LaneI q1 = vmath.gather(quant, cb + ((bas & 0x80) | ((off & 0x3F) << 1)));
                LaneI up0 = vmath.gather(up, cb + q0), down0 = vmath.gather(down, cb + q0);
                LaneI up1 = vmath.gather(up, cb + q1), down1 = vmath.gather(down, cb + q1);
                LaneI u0 = vmath.gather(unquant, cb + q0), uu0 = vmath.gather(unquant, cb + up0), ud0 = vmath.gather(unquant, cb + down0);
                LaneI u1 = vmath.gather(unquant, cb + q1), uu1 = vmath.gather(unquant, cb + up1), ud1 = vmath.gather(unquant, cb + down1);
                LaneI best0 = q0, best1 = q1, bestOff = 0, bestBase = 0;
                LaneF bestError = float.MaxValue;
                for (int i = 0; i < 3; i++)
                {
                    LaneI c0 = i == 0 ? q0 : i == 1 ? up0 : down0, v0 = i == 0 ? u0 : i == 1 ? uu0 : ud0;
                    for (int j = 0; j < 3; j++)
                    {
                        LaneI c1 = j == 0 ? q1 : j == 1 ? up1 : down1, v1 = j == 0 ? u1 : j == 1 ? uu1 : ud1;
                        LaneI baseValue = (v0 >> 1) | (v1 & 0x80);
                        LaneI o = (v1 >> 1) & 0x3F;
                        o -= (o & 0x20) << 1;
                        LaneF d0 = (LaneF)baseValue - a[c], d1 = (LaneF)vmath.clamp(baseValue + o, 0, 255) - b[c];
                        LaneF e = d0 * d0 + d1 * d1;
                        LaneB better = e < bestError;
                        bestError = vmath.select(bestError, e, better);
                        best0 = vmath.select(best0, c0, better);
                        best1 = vmath.select(best1, c1, better);
                        bestOff = vmath.select(bestOff, o, better);
                        bestBase = vmath.select(bestBase, baseValue, better);
                    }
                }
                k0[c] = best0;
                k1[c] = best1;
                e0[c] = bestBase;
                e1[c] = vmath.clamp(bestBase + bestOff, 0, 255);
                if (c < 3) offsetSum += bestOff;
            }
            e0.C3 *= mask.C3;
            e1.C3 *= mask.C3;
            return offsetSum >= 0;
        }

        // Each slot's code pair moved to the best of its neighbors (one step up or down each), keeping the endpoint order
        // the decoder needs. Slots: luminance lanes 0 (luminance) and 3 (alpha), others one per channel.
        private static void RefineL(Tables* t, LaneI cb, LaneB lum, LaneB colorCem, LaneB alphaSlot, LaneB order, LaneB offset, LaneF4 saa,
            LaneF4 sab, LaneF4 sbb, LaneF4 sap, LaneF4 sbp, LaneF4* e0, LaneF4* e1, LaneI4* k0, LaneI4* k1)
        {
            int* unquant = t->CU;
            int* up = t->CUp;
            int* down = t->CDown;
            LaneB orderCheck = order & !offset;
            bool anyOffset = vmath.any(offset), anyOrder = vmath.any(orderCheck);
            LaneI offsetSum = 0;
            if (anyOffset)
                for (int c = 0; c < 3; c++)
                {
                    DecodeL(vmath.gather(unquant, cb + (*k0)[c]), vmath.gather(unquant, cb + (*k1)[c]), offset, out _, out _, out LaneI o);
                    offsetSum += o;
                }
            for (int s = 0; s < 4; s++)
            {
                LaneB active = s == 0 ? (LaneB)true : s == 3 ? alphaSlot : colorCem;
                if (!vmath.any(active)) continue;
                LaneF a, ab, bb, ap, bp;
                if (s == 0)
                {
                    a = vmath.select(saa.C0, saa.C0 + saa.C1 + saa.C2, lum);
                    ab = vmath.select(sab.C0, sab.C0 + sab.C1 + sab.C2, lum);
                    bb = vmath.select(sbb.C0, sbb.C0 + sbb.C1 + sbb.C2, lum);
                    ap = vmath.select(sap.C0, sap.C0 + sap.C1 + sap.C2, lum);
                    bp = vmath.select(sbp.C0, sbp.C0 + sbp.C1 + sbp.C2, lum);
                }
                else
                {
                    a = saa[s];
                    ab = sab[s];
                    bb = sbb[s];
                    ap = sap[s];
                    bp = sbp[s];
                }
                LaneI q0 = (*k0)[s], q1 = (*k1)[s];
                LaneI up0 = vmath.gather(up, cb + q0), down0 = vmath.gather(down, cb + q0);
                LaneI up1 = vmath.gather(up, cb + q1), down1 = vmath.gather(down, cb + q1);
                LaneI u0 = vmath.gather(unquant, cb + q0), uu0 = vmath.gather(unquant, cb + up0), ud0 = vmath.gather(unquant, cb + down0);
                LaneI u1 = vmath.gather(unquant, cb + q1), uu1 = vmath.gather(unquant, cb + up1), ud1 = vmath.gather(unquant, cb + down1);
                DecodeL(u0, u1, offset, out LaneI v0, out LaneI v1, out LaneI off0);
                LaneF bestError = PairError(v0, v1, a, ab, bb, ap, bp);
                LaneI best0 = q0, best1 = q1, bestV0 = v0, bestV1 = v1, bestOff = off0;
                LaneI s0 = 0, s1 = 0;
                bool checkOrder = s < 3 && anyOrder;
                if (checkOrder)
                {
                    for (int c = 0; c < 3; c++)
                    {
                        s0 += vmath.gather(unquant, cb + (*k0)[c]);
                        s1 += vmath.gather(unquant, cb + (*k1)[c]);
                    }
                    s0 -= v0;
                    s1 -= v1;
                }
                for (int i = 0; i < 3; i++)
                {
                    LaneI c0 = i == 0 ? q0 : i == 1 ? up0 : down0, w0u = i == 0 ? u0 : i == 1 ? uu0 : ud0;
                    for (int j = 0; j < 3; j++)
                    {
                        if (i == 0 && j == 0) continue;
                        LaneI c1 = j == 0 ? q1 : j == 1 ? up1 : down1, w1u = j == 0 ? u1 : j == 1 ? uu1 : ud1;
                        DecodeL(w0u, w1u, offset, out LaneI w0, out LaneI w1, out LaneI o);
                        LaneB valid = active;
                        if (s < 3 && anyOffset) valid &= !offset | (offsetSum - off0 + o >= 0);
                        if (checkOrder) valid &= !orderCheck | (s1 + w1 >= s0 + w0);
                        LaneF e = PairError(w0, w1, a, ab, bb, ap, bp);
                        LaneB better = valid & (e < bestError);
                        bestError = vmath.select(bestError, e, better);
                        best0 = vmath.select(best0, c0, better);
                        best1 = vmath.select(best1, c1, better);
                        bestV0 = vmath.select(bestV0, w0, better);
                        bestV1 = vmath.select(bestV1, w1, better);
                        bestOff = vmath.select(bestOff, o, better);
                    }
                }
                (*k0)[s] = best0;
                (*k1)[s] = best1;
                offsetSum += bestOff - off0;
                LaneF f0 = bestV0, f1 = bestV1;
                if (s == 0)
                {
                    e0->C0 = f0;
                    e1->C0 = f1;
                    e0->C1 = vmath.select(e0->C1, f0, lum);
                    e1->C1 = vmath.select(e1->C1, f1, lum);
                    e0->C2 = vmath.select(e0->C2, f0, lum);
                    e1->C2 = vmath.select(e1->C2, f1, lum);
                }
                else
                {
                    (*e0)[s] = vmath.select((*e0)[s], f0, active);
                    (*e1)[s] = vmath.select((*e1)[s], f1, active);
                }
            }
        }

        // Partitionings of the tables closest to a k-means clustering of the block (fewest texels assigned differently)
        private static int FindPartitions(float4* px, Tables* t, Scratch* s, int partitions, float4 mask, int wanted)
        {
            int n = t->Texels;
            float4* center = stackalloc float4[3];
            float4* sum = stackalloc float4[3];
            int* count = stackalloc int[3];
            ulong* cm = stackalloc ulong[9];
            int* x = stackalloc int[9];

            // Seeds: the texel farthest from the mean, the one farthest from it, then the one farthest from both
            float4 mean = 0;
            for (int i = 0; i < n; i++) mean += px[i] * mask;
            mean /= n;
            int far = 0;
            float farD = -1;
            for (int i = 0; i < n; i++)
            {
                float4 d = px[i] * mask - mean;
                float dd = math.dot(d, d);
                if (dd > farD) { farD = dd; far = i; }
            }
            center[0] = px[far] * mask;
            farD = -1;
            for (int i = 0; i < n; i++)
            {
                float4 d = px[i] * mask - center[0];
                float dd = math.dot(d, d);
                if (dd > farD) { farD = dd; far = i; }
            }
            center[1] = px[far] * mask;
            if (partitions == 3)
            {
                farD = -1;
                for (int i = 0; i < n; i++)
                {
                    float4 d0 = px[i] * mask - center[0], d1 = px[i] * mask - center[1];
                    float dd = math.min(math.dot(d0, d0), math.dot(d1, d1));
                    if (dd > farD) { farD = dd; far = i; }
                }
                center[2] = px[far] * mask;
            }

            byte* cluster = s->Cluster;
            for (int it = 0; it < 3; it++)
            {
                for (int k = 0; k < 3; k++)
                {
                    sum[k] = 0;
                    count[k] = 0;
                }
                for (int i = 0; i < n; i++)
                {
                    float4 p = px[i] * mask;
                    int bestK = 0;
                    float bestD = float.MaxValue;
                    for (int k = 0; k < partitions; k++)
                    {
                        float4 d = p - center[k];
                        float dd = math.dot(d, d);
                        if (dd < bestD) { bestD = dd; bestK = k; }
                    }
                    cluster[i] = (byte)bestK;
                    sum[bestK] += p;
                    count[bestK]++;
                }
                for (int k = 0; k < partitions; k++)
                    if (count[k] > 0) center[k] = sum[k] / count[k];
            }

            for (int k = 0; k < 9; k++) cm[k] = 0;
            for (int i = 0; i < n; i++) cm[cluster[i] * 3 + (i >> 6)] |= 1UL << (i & 63);

            int found = 0;
            int total = partitions == 2 ? t->PartCount2 : t->PartCount3;
            ulong* masks = partitions == 2 ? t->PartMask2 : t->PartMask3;
            for (int j = 0; j < total; j++)
            {
                ulong* pm = masks + j * partitions * 3;
                int mismatch;
                if (partitions == 2)
                {
                    int a = 0, c = 0;
                    for (int w = 0; w < 3; w++)
                    {
                        a += math.countbits(cm[w] ^ pm[w]);
                        c += math.countbits(cm[w] ^ pm[3 + w]);
                    }
                    mismatch = math.min(a, c);
                }
                else
                {
                    // Texels outside their cluster's partition, best of the 6 matchings
                    for (int k = 0; k < 3; k++)
                    for (int l = 0; l < 3; l++)
                    {
                        int v = 0;
                        for (int w = 0; w < 3; w++) v += math.countbits(cm[k * 3 + w] & ~pm[l * 3 + w]);
                        x[k * 3 + l] = v;
                    }
                    mismatch = math.min(math.min(math.min(x[0] + x[4] + x[8], x[0] + x[5] + x[7]),
                            math.min(x[1] + x[3] + x[8], x[1] + x[5] + x[6])),
                        math.min(x[2] + x[3] + x[7], x[2] + x[4] + x[6]));
                }
                if (found < wanted || mismatch < s->Mismatch[found - 1])
                {
                    int k = found < wanted ? found++ : found - 1;
                    while (k > 0 && s->Mismatch[k - 1] > mismatch)
                    {
                        s->Mismatch[k] = s->Mismatch[k - 1];
                        s->Partition[k] = s->Partition[k - 1];
                        k--;
                    }
                    s->Mismatch[k] = mismatch;
                    s->Partition[k] = j;
                }
            }
            return found;
        }

        private static void Pack(Tables* t, Result* r, int cem, int values, byte* dst)
        {
            Mode* mode = t->Modes + r->Mode;
            Grid* grid = t->Grids + mode->Grid;
            int count = grid->Count, partitions = r->P;
            bool dual = r->Plane2 >= 0;
            byte* colors = stackalloc byte[24];
            int total = values * partitions;
            for (int i = 0; i < total; i++) colors[i] = (byte)r->Color[i];
            byte* weights = r->Weights;

            // One partition RGB(A): the decoder swaps the endpoints (and blue contracts) when the second one's sum is
            // lower. Swap them here and flip the weights, which decodes the same.
            if ((cem == 8 || cem == 12) && partitions == 1)
            {
                byte* unquant = t->ColorUnquant + r->ColorLevel * 256;
                int s0 = unquant[colors[0]] + unquant[colors[2]] + unquant[colors[4]];
                int s1 = unquant[colors[1]] + unquant[colors[3]] + unquant[colors[5]];
                if (s1 < s0)
                {
                    for (int i = 0; i < values; i += 2)
                    {
                        byte c = colors[i];
                        colors[i] = colors[i + 1];
                        colors[i + 1] = c;
                    }
                    byte* flip = t->WeightFlip + mode->Level * 32;
                    for (int i = 0; i < count; i++)
                    {
                        weights[i] = flip[weights[i]];
                        if (dual) weights[32 + i] = flip[weights[32 + i]];
                    }
                }
            }

            ulong* block = stackalloc ulong[2];
            ulong* seq = stackalloc ulong[2];
            int colorPos;
            block[1] = 0;
            if (partitions == 1)
            {
                block[0] = (ulong)mode->Bits | ((ulong)cem << 13);
                colorPos = 17;
            }
            else
            {
                block[0] = (ulong)mode->Bits | ((ulong)(partitions - 1) << 11) | ((ulong)r->Seed << 13) | ((ulong)cem << 25);
                colorPos = 29;
            }

            IseEncode(t, colors, total, r->ColorLevel, seq);
            block[0] |= seq[0] << colorPos;
            block[1] |= (seq[0] >> (64 - colorPos)) | (seq[1] << colorPos);

            byte* interleaved = stackalloc byte[64];
            if (dual)
            {
                int pos = 128 - mode->WeightBits - 2;
                Put(block, ref pos, r->Plane2, 2);
                for (int i = 0; i < count; i++)
                {
                    interleaved[2 * i] = weights[i];
                    interleaved[2 * i + 1] = weights[32 + i];
                }
                IseEncode(t, interleaved, 2 * count, mode->Level, seq);
            }
            else IseEncode(t, weights, count, mode->Level, seq);
            block[1] |= math.reversebits(seq[0]);
            block[0] |= math.reversebits(seq[1]);

            *(ulong*)dst = block[0];
            *(ulong*)(dst + 8) = block[1];
        }

        private static void Put(ulong* seq, ref int pos, int value, int bits)
        {
            if (bits == 0) return;
            if (pos < 64)
            {
                seq[0] |= (ulong)value << pos;
                if (pos + bits > 64) seq[1] |= (ulong)value >> (64 - pos);
            }
            else if (pos < 128) seq[1] |= (ulong)value << (pos - 64);
            pos += bits;
        }

        // Integer sequence of count values at one level, bit 0 first, cut to its exact length
        private static void IseEncode(Tables* t, byte* v, int count, int level, ulong* seq)
        {
            seq[0] = seq[1] = 0;
            int bits = t->Bits[level], pos = 0;
            int mask = (1 << bits) - 1;
            int* m = stackalloc int[5];
            if (t->Trits[level] != 0)
            {
                for (int i = 0; i < count; i += 5)
                {
                    int index = 0, mul = 1;
                    for (int k = 0; k < 5; k++, mul *= 3)
                    {
                        int x = i + k < count ? v[i + k] : 0;
                        m[k] = x & mask;
                        index += (x >> bits) * mul;
                    }
                    int T = t->TritEnc[index];
                    Put(seq, ref pos, m[0], bits);
                    Put(seq, ref pos, T & 3, 2);
                    Put(seq, ref pos, m[1], bits);
                    Put(seq, ref pos, (T >> 2) & 3, 2);
                    Put(seq, ref pos, m[2], bits);
                    Put(seq, ref pos, (T >> 4) & 1, 1);
                    Put(seq, ref pos, m[3], bits);
                    Put(seq, ref pos, (T >> 5) & 3, 2);
                    Put(seq, ref pos, m[4], bits);
                    Put(seq, ref pos, (T >> 7) & 1, 1);
                }
            }
            else if (t->Quints[level] != 0)
            {
                for (int i = 0; i < count; i += 3)
                {
                    int index = 0, mul = 1;
                    for (int k = 0; k < 3; k++, mul *= 5)
                    {
                        int x = i + k < count ? v[i + k] : 0;
                        m[k] = x & mask;
                        index += (x >> bits) * mul;
                    }
                    int Q = t->QuintEnc[index];
                    Put(seq, ref pos, m[0], bits);
                    Put(seq, ref pos, Q & 7, 3);
                    Put(seq, ref pos, m[1], bits);
                    Put(seq, ref pos, (Q >> 3) & 3, 2);
                    Put(seq, ref pos, m[2], bits);
                    Put(seq, ref pos, (Q >> 5) & 3, 2);
                }
            }
            else
            {
                for (int i = 0; i < count; i++) Put(seq, ref pos, v[i], bits);
            }

            int length = count * bits + (t->Trits[level] != 0 ? (8 * count + 4) / 5 : 0) +
                         (t->Quints[level] != 0 ? (7 * count + 2) / 3 : 0);
            if (length < 64)
            {
                seq[0] &= (1UL << length) - 1;
                seq[1] = 0;
            }
            else if (length < 128) seq[1] &= (1UL << (length - 64)) - 1;
        }

        // Constant color block: UNORM16 RGBA, no extent
        private static void VoidExtent(float4 c, byte* dst)
        {
            *(ulong*)dst = 0xFFFFFFFFFFFFFDFCUL;
            uint4 v = (uint4)c * 257;
            *(ulong*)(dst + 8) = v.x | ((ulong)v.y << 16) | ((ulong)v.z << 32) | ((ulong)v.w << 48);
        }
    }
}
