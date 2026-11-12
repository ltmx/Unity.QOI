using System.Runtime.CompilerServices;
using Unity.Mathematics;

namespace LTMX.QOI.Editor
{
    /// <summary>
    /// BC7 encoder of <see cref="QoiBlockEncoder"/> (Burst compiled through it). Encodes 8 blocks at once, one per SIMD
    /// lane: every pixel value is a <see cref="LaneF"/> holding the same pixel of the 8 blocks (one AVX2 register), so the
    /// whole search runs on full vectors without horizontal sums, and each lane keeps its own best mode. Modes tried:
    /// <list type="bullet">
    /// <item>Mode 6: one RGBA line, 7 bit endpoints + p-bit, 16 steps. Every block.</item>
    /// <item>Modes 1 and 3: two RGB lines, mode 0: three, over the partitions with the best covariance estimate. Opaque
    /// blocks that neither mode 6 nor modes 5 / 4 fit closely.</item>
    /// <item>Modes 5 and 4: one RGB line and a separate scalar line holding alpha, or with a rotation one of the color
    /// channels (mode 4: fewer endpoint bits, 8 steps on one of the lines).</item>
    /// </list>
    /// Candidates are screened with one fit each, the best one per block is then refined (its last fit also tries the
    /// other p-bit combinations with the same indices). Partition screens and refinements gather their blocks 8 at a time
    /// from 16 groups, so lanes stay busy when neighbor blocks take different paths. Each line: principal axis of its
    /// pixels, endpoints at the extremes, then least squares refits; indices by projection on the line. Blocks are packed
    /// once, after their last mode is known. Opaque blocks get the two rotations whose covariance
    /// estimate is best.
    /// </summary>
    internal static unsafe class QoiBc7Encoder
    {
        // Blocks encoded together, one per lane
        private const int Lanes = LaneF.W;

        // Squared error (RGBA sum over the block) of modes 6, 5 and 4 above which modes 1 and 3 are tried
        private const int Mode1Threshold = 16 * 3 * 2;

        // Best error so far (modes 6 refined, 5 / 4 / 1 / 3 one pass) above which mode 0 is tried
        private const int Mode0Threshold = 16 * 3 * 16;

        // Power iterations of each line's principal axis
        private const int AxisIterations = 2;

        // Power iterations of the partition estimate
        private const int EstimateIterations = 1;

        // Least squares refits of mode 6 and of the refined candidate
        private const int Passes = 3;

        // Channel sets of a line (bit c = channel c)
        private const int Rgba = 15, Rgb = 7, Alpha = 8;

        // Subset of each pixel of the 64 two subset partitions: bit i set = pixel i is in subset 1
        private static readonly ushort[] Partitions2 =
        {
            0xCCCC, 0x8888, 0xEEEE, 0xECC8, 0xC880, 0xFEEC, 0xFEC8, 0xEC80,
            0xC800, 0xFFEC, 0xFE80, 0xE800, 0xFFE8, 0xFF00, 0xFFF0, 0xF000,
            0xF710, 0x008E, 0x7100, 0x08CE, 0x008C, 0x7310, 0x3100, 0x8CCE,
            0x088C, 0x3110, 0x6666, 0x366C, 0x17E8, 0x0FF0, 0x718E, 0x399C,
            0xAAAA, 0xF0F0, 0x5A5A, 0x33CC, 0x3C3C, 0x55AA, 0x9696, 0xA55A,
            0x73CE, 0x13C8, 0x324C, 0x3BDC, 0x6996, 0xC33C, 0x9966, 0x0660,
            0x0272, 0x04E4, 0x4E40, 0x2720, 0xC936, 0x936C, 0x39C6, 0x639C,
            0x9336, 0x9CC6, 0x817E, 0xE718, 0xCCF0, 0x0FCC, 0x7744, 0xEE22,
        };

        // Anchor pixel of subset 1 of each two subset partition (subset 0's anchor is pixel 0)
        private static readonly byte[] Anchors2 =
        {
            15, 15, 15, 15, 15, 15, 15, 15, 15, 15, 15, 15, 15, 15, 15, 15,
            15, 2, 8, 2, 2, 8, 8, 15, 2, 8, 2, 2, 8, 8, 2, 2,
            15, 15, 6, 8, 2, 8, 15, 15, 2, 8, 2, 2, 2, 15, 15, 6,
            6, 2, 6, 8, 15, 15, 2, 2, 15, 15, 15, 15, 15, 2, 2, 15,
        };

        // Subsets 1 and 2 of the 16 three subset partitions of mode 0 (bit i set = pixel i in the subset)
        private static readonly ushort[] Partitions3Subset1 =
        {
            0x08CC, 0x8CC8, 0xCC80, 0xEC00, 0x3300, 0x00CC, 0xFF00, 0xCCCC,
            0x0F00, 0x0FF0, 0x00F0, 0x4444, 0x6666, 0x2222, 0x136C, 0x008C,
        };

        private static readonly ushort[] Partitions3Subset2 =
        {
            0xF600, 0x7300, 0x3310, 0x00CE, 0xCC00, 0xCC00, 0x00CC, 0x3300,
            0xF000, 0xF000, 0xFF00, 0x8888, 0x8888, 0xCCCC, 0xEC80, 0x7310,
        };

        // Anchor pixels of subsets 1 and 2 of those partitions
        private static readonly byte[] Anchors3Subset1 = { 3, 3, 15, 15, 8, 3, 15, 15, 8, 8, 6, 6, 6, 5, 3, 3 };
        private static readonly byte[] Anchors3Subset2 = { 15, 8, 8, 3, 15, 15, 3, 8, 15, 15, 15, 15, 15, 15, 15, 8 };

        // One line per lane: endpoint codes (without p-bits, column = channel) and p-bits
        private struct Line4
        {
            public LaneI4 Code0, Code1;
            public LaneI P0, P1;
        }

        // One block's line
        private struct Line
        {
            public int4 Code0, Code1;
            public int P0, P1;
        }

        // 128 bit block, written from bit 0 up
        private struct Bits
        {
            public ulong Lo, Hi;
            public int Position;

            public void Put(int value, int count)
            {
                ulong v = (ulong)value & ((1UL << count) - 1);
                if (Position < 64)
                {
                    Lo |= v << Position;
                    if (Position + count > 64) Hi |= v >> (64 - Position);
                }
                else Hi |= v << (Position - 64);
                Position += count;
            }
        }

        // Per block best encoding and the candidate kept for refinement
        private struct Best
        {
            public fixed ulong Lo[Lanes];
            public fixed ulong Hi[Lanes];
            public LaneF Error;
            public LaneF CandidateError;
            public LaneI CandidateMode, CandidateParam;

            // Nonzero once a refinement replaced mode 6 (else packed from its line at the end)
            public fixed int Refined[Lanes];
        }

        // Groups of Lanes blocks screened before their refinements are batched by mode
        private const int Groups = 16;

        /// <summary>Encodes block row <paramref name="blockRow"/> of an RGBA32 image (edges repeat the last row / column).</summary>
        public static void EncodeRow(byte* rgba, int width, int height, int blockRow, byte* output)
        {
            int blocksX = (width + 3) >> 2;
            LaneF* pixels = stackalloc LaneF[64 * Groups];
            Best* bests = stackalloc Best[Groups];
            Line4* lines6 = stackalloc Line4[Groups];
            LaneI* indices6 = stackalloc LaneI[16 * Groups];
            uint* image = (uint*)rgba;
            for (int chunk = 0; chunk < blocksX; chunk += Lanes * Groups)
            {
                int groups = vmath.min(Groups, (blocksX - chunk + Lanes - 1) / Lanes);
                for (int group = 0; group < groups; group++)
                {
                    LaneF* px = pixels + group * 64;
                    for (int lane = 0; lane < Lanes; lane++)
                    {
                        int bx = vmath.min(chunk + group * Lanes + lane, blocksX - 1);
                        for (int j = 0; j < 4; j++)
                        {
                            uint* row = image + (long)vmath.min(blockRow * 4 + j, height - 1) * width;
                            for (int i = 0; i < 4; i++)
                            {
                                uint c = row[vmath.min(bx * 4 + i, width - 1)];
                                int k = j * 4 + i;
                                px[k][lane] = c & 255;
                                px[16 + k][lane] = (c >> 8) & 255;
                                px[32 + k][lane] = (c >> 16) & 255;
                                px[48 + k][lane] = c >> 24;
                            }
                        }
                    }
                    bests[group] = default;
                    Screen4(px, ref bests[group], ref lines6[group], indices6 + group * 16);
                }

                ScreenPartitions(pixels, bests, groups);
                Refine(pixels, bests, groups);

                for (int block = 0; block < groups * Lanes; block++)
                {
                    ref Best b = ref bests[block / Lanes];
                    if (b.Refined[block % Lanes] == 0)
                        PackMode6(ref b, block % Lanes, lines6[block / Lanes], indices6 + block / Lanes * 16);
                }

                ulong* dst = (ulong*)(output + ((long)blockRow * blocksX + chunk) * 16);
                for (int block = 0; block < groups * Lanes && chunk + block < blocksX; block++)
                {
                    dst[block * 2] = bests[block / Lanes].Lo[block % Lanes];
                    dst[block * 2 + 1] = bests[block / Lanes].Hi[block % Lanes];
                }
            }
        }

        // Mode 6 (refined) and one fit of modes 5 and 4 for Lanes blocks. px: 64 LaneF, channel c of pixel i at c * 16 + i,
        // lane = block.
        private static void Screen4(LaneF* px, ref Best best, ref Line4 line6, LaneI* indices6)
        {
            LaneI* indices = stackalloc LaneI[16];
            LaneB opaque = true;
            for (int i = 0; i < 16; i++) opaque &= px[48 + i] == 255;

            // Mode 6 for every block
            best.Error = Fit(px, 0xFFFF, Rgba, 7, 1, 4, Passes, indices6, out line6);

            best.CandidateError = float.MaxValue;
            best.CandidateMode = 0;
            best.CandidateParam = 0;
            LaneB open = best.Error > 0;
            if (!vmath.any(open)) return;

            // Modes 5 and 4: rotations put a channel on its own line
            ScreenRotations(px, opaque, open, indices, ref best);
        }

        // Gathers blocks queue[start..start + 4) (the last batch repeats its last block) into the lanes of px
        private static void Gather(LaneF* pixels, Best* bests, int* queue, int start, int count, LaneF* px, ref Best batch)
        {
            for (int lane = 0; lane < Lanes; lane++)
            {
                int block = queue[vmath.min(start + lane, count - 1)];
                LaneF* src = pixels + block / Lanes * 64;
                int from = block % Lanes;
                for (int k = 0; k < 64; k++) px[k][lane] = src[k][from];
                ref Best b = ref bests[block / Lanes];
                batch.Error[lane] = b.Error[from];
                batch.CandidateError[lane] = b.CandidateError[from];
                batch.CandidateMode[lane] = b.CandidateMode[from];
                batch.CandidateParam[lane] = b.CandidateParam[from];
            }
        }

        // Modes 1 and 3 on the partitions with the best estimates, one fit each, then mode 0 for blocks two lines still
        // fit poorly. Opaque blocks that modes 6, 5 and 4 fit poorly, gathered Lanes at a time.
        private static void ScreenPartitions(LaneF* pixels, Best* bests, int groups)
        {
            int* queue = stackalloc int[Groups * Lanes];
            LaneF* px = stackalloc LaneF[64];
            LaneI* indices = stackalloc LaneI[16];
            LaneI* top = stackalloc LaneI[TopPartitions];
            int count = 0;
            for (int block = 0; block < groups * Lanes; block++)
            {
                LaneF* src = pixels + block / Lanes * 64;
                int lane = block % Lanes;
                bool opaque = true;
                for (int i = 0; i < 16; i++) opaque &= src[48 + i][lane] == 255;
                ref Best b = ref bests[block / Lanes];
                if (opaque && math.min(b.Error[lane], b.CandidateError[lane]) > Mode1Threshold) queue[count++] = block;
            }

            for (int start = 0; start < count; start += Lanes)
            {
                var batch = new Best();
                Gather(pixels, bests, queue, start, count, px, ref batch);
                LaneB all = true;

                EstimatePartitions(px, top);
                LaneI first = top[0];

                // Modes 1 and 3 of the first partition start from the same lines
                LaneI mask1 = PartitionMasks(first);
                LaneI mask0 = ~mask1 & 0xFFFF;
                Setup(px, mask0, Rgb, out var a0, out var a1);
                Setup(px, mask1, Rgb, out var b0, out var b1);
                LaneF error = FitFrom(px, mask0, Rgb, a0, a1, 6, 2, 3, 1, indices, out _)
                               + FitFrom(px, mask1, Rgb, b0, b1, 6, 2, 3, 1, indices, out _);
                Keep(ref batch, all, error, 1, first);
                error = FitFrom(px, mask0, Rgb, a0, a1, 7, 1, 2, 1, indices, out _)
                        + FitFrom(px, mask1, Rgb, b0, b1, 7, 1, 2, 1, indices, out _);
                Keep(ref batch, all, error, 3, first);
                for (int k = 1; k < TopPartitions; k++)
                {
                    error = Mode1(px, top[k], 1, indices, out _, out _);
                    Keep(ref batch, all, error, 1, top[k]);
                }

                LaneB three = vmath.min(batch.Error, batch.CandidateError) > Mode0Threshold;
                if (vmath.any(three))
                {
                    LaneI third = EstimatePartitions3(px);
                    error = Mode0(px, third, 1, indices, out _, out _, out _);
                    Keep(ref batch, three, error, 0, third);
                }

                for (int lane = 0; lane < Lanes && start + lane < count; lane++)
                {
                    int block = queue[start + lane];
                    ref Best b = ref bests[block / Lanes];
                    int to = block % Lanes;
                    b.CandidateError[to] = batch.CandidateError[lane];
                    b.CandidateMode[to] = batch.CandidateMode[lane];
                    b.CandidateParam[to] = batch.CandidateParam[lane];
                }
            }
        }

        // Refines each block's best candidate, keeps it when it beats mode 6. Blocks are gathered Lanes at a time per mode
        // (mode 4: per index precision) so every lane does useful work.
        private static void Refine(LaneF* pixels, Best* bests, int groups)
        {
            int* queue = stackalloc int[Groups * Lanes];
            LaneF* px = stackalloc LaneF[64];
            LaneI* indices = stackalloc LaneI[16];
            LaneI* indices2 = stackalloc LaneI[16];
            for (int kind = 0; kind < 6; kind++)
            {
                // Kinds: modes 0, 1, 3, 4 precision 0, 4 precision 1, 5
                int mode = kind <= 1 ? kind : kind == 2 ? 3 : kind <= 4 ? 4 : 5;
                int count = 0;
                for (int block = 0; block < groups * Lanes; block++)
                {
                    ref Best b = ref bests[block / Lanes];
                    int lane = block % Lanes;
                    if (b.CandidateError[lane] >= b.Error[lane] || b.CandidateMode[lane] != mode) continue;
                    if (mode == 4 && (b.CandidateParam[lane] & 1) != kind - 3) continue;
                    queue[count++] = block;
                }

                for (int start = 0; start < count; start += Lanes)
                {
                    var batch = new Best();
                    Gather(pixels, bests, queue, start, count, px, ref batch);
                    LaneI param = batch.CandidateParam;
                    LaneF error;
                    switch (mode)
                    {
                        case 0:
                        {
                            error = Mode0(px, param, Passes, indices, out var line0, out var line1, out var line2);
                            for (int lane = 0; lane < Lanes; lane++)
                                if (Improves(bests, queue, start, count, lane, error[lane]))
                                    PackMode0(ref batch, lane, error[lane], param[lane], line0, line1, line2, indices);
                            break;
                        }
                        case 1:
                        {
                            error = Mode1(px, param, Passes, indices, out var line0, out var line1);
                            for (int lane = 0; lane < Lanes; lane++)
                                if (Improves(bests, queue, start, count, lane, error[lane]))
                                    PackMode1(ref batch, lane, error[lane], param[lane], line0, line1, indices);
                            break;
                        }
                        case 3:
                        {
                            error = Mode3(px, param, Passes, indices, out var line0, out var line1);
                            for (int lane = 0; lane < Lanes; lane++)
                                if (Improves(bests, queue, start, count, lane, error[lane]))
                                    PackMode3(ref batch, lane, error[lane], param[lane], line0, line1, indices);
                            break;
                        }
                        case 4:
                        {
                            int precision = kind - 3;
                            LaneI rotation = param >> 1;
                            error = Mode4(px, rotation, precision, Passes, indices, indices2, out var color, out var alpha);
                            for (int lane = 0; lane < Lanes; lane++)
                                if (Improves(bests, queue, start, count, lane, error[lane]))
                                    PackMode4(ref batch, lane, error[lane], rotation[lane], precision, color, alpha, indices,
                                    indices2);
                            break;
                        }
                        default:
                        {
                            error = Mode5(px, param, Passes, indices, indices2, out var color, out var alpha);
                            for (int lane = 0; lane < Lanes; lane++)
                                if (Improves(bests, queue, start, count, lane, error[lane]))
                                    PackMode5(ref batch, lane, error[lane], param[lane], color, alpha, indices, indices2);
                            break;
                        }
                    }

                    // Scatter the improvements
                    for (int lane = 0; lane < Lanes && start + lane < count; lane++)
                    {
                        int block = queue[start + lane];
                        ref Best b = ref bests[block / Lanes];
                        int to = block % Lanes;
                        if (error[lane] >= b.Error[to]) continue;
                        b.Lo[to] = batch.Lo[lane];
                        b.Hi[to] = batch.Hi[lane];
                        b.Error[to] = error[lane];
                        b.Refined[to] = 1;
                    }
                }
            }
        }

        // Lane of a refined batch that holds a block (not a repeat) and beats its best
        private static bool Improves(Best* bests, int* queue, int start, int count, int lane, float error)
        {
            if (start + lane >= count) return false;
            int block = queue[start + lane];
            return error < bests[block / Lanes].Error[block % Lanes];
        }

        private static void Keep(ref Best best, LaneB lanes, LaneF error, int mode, LaneI param)
        {
            LaneB better = lanes & error < best.CandidateError;
            best.CandidateError = vmath.select(best.CandidateError, error, better);
            best.CandidateMode = vmath.select(best.CandidateMode, mode, better);
            best.CandidateParam = vmath.select(best.CandidateParam, param, better);
        }

        // Mode 5 of every rotation, then mode 4 (both index precisions) of each block's best rotation, one pass each.
        // Per rotation the color and scalar lines start from the same endpoints whatever the mode.
        private static void ScreenRotations(LaneF* px, LaneB opaque, LaneB lanes, LaneI* indices, ref Best best)
        {
            LaneF* rotated = stackalloc LaneF[64];
            LaneF bestError = float.MaxValue;
            LaneI bestRotation = 1;
            LaneF4 color0 = default, color1 = default, alpha0 = default, alpha1 = default;
            if (vmath.all(opaque))
            {
                // Opaque: the two rotations with the best estimates per lane
                EstimateRotations(px, out LaneI first, out LaneI second);
                for (int k = 0; k < 2; k++)
                {
                    LaneI rotation = k == 0 ? first : second;
                    Rotate(px, rotation, rotated);
                    Setup(rotated, 0xFFFF, Rgb, out var c0, out var c1);
                    Setup(rotated, 0xFFFF, Alpha, out var a0, out var a1);
                    LaneF error = FitFrom(rotated, 0xFFFF, Rgb, c0, c1, 7, 0, 2, 1, indices, out _)
                                   + FitFrom(rotated, 0xFFFF, Alpha, a0, a1, 8, 0, 2, 1, indices, out _);
                    LaneB better = error < bestError;
                    bestError = vmath.select(bestError, error, better);
                    bestRotation = vmath.select(bestRotation, rotation, better);
                    for (int c = 0; c < 4; c++)
                    {
                        color0[c] = vmath.select(color0[c], c0[c], better);
                        color1[c] = vmath.select(color1[c], c1[c], better);
                        alpha0[c] = vmath.select(alpha0[c], a0[c], better);
                        alpha1[c] = vmath.select(alpha1[c], a1[c], better);
                    }
                }
            }
            else for (int rotation = 0; rotation < 4; rotation++)
            {
                Rotate(px, rotation, rotated);
                Setup(rotated, 0xFFFF, Rgb, out var c0, out var c1);
                Setup(rotated, 0xFFFF, Alpha, out var a0, out var a1);
                LaneF error = FitFrom(rotated, 0xFFFF, Rgb, c0, c1, 7, 0, 2, 1, indices, out _)
                               + FitFrom(rotated, 0xFFFF, Alpha, a0, a1, 8, 0, 2, 1, indices, out _);
                LaneB better = error < bestError;
                bestError = vmath.select(bestError, error, better);
                bestRotation = vmath.select(bestRotation, rotation, better);
                for (int c = 0; c < 4; c++)
                {
                    color0[c] = vmath.select(color0[c], c0[c], better);
                    color1[c] = vmath.select(color1[c], c1[c], better);
                    alpha0[c] = vmath.select(alpha0[c], a0[c], better);
                    alpha1[c] = vmath.select(alpha1[c], a1[c], better);
                }
            }
            Keep(ref best, lanes, bestError, 5, bestRotation);

            Rotate(px, bestRotation, rotated);
            LaneF color52 = FitFrom(rotated, 0xFFFF, Rgb, color0, color1, 5, 0, 2, 1, indices, out _);
            LaneF color53 = FitFrom(rotated, 0xFFFF, Rgb, color0, color1, 5, 0, 3, 1, indices, out _);
            LaneF alpha63 = FitFrom(rotated, 0xFFFF, Alpha, alpha0, alpha1, 6, 0, 3, 1, indices, out _);
            LaneF alpha62 = FitFrom(rotated, 0xFFFF, Alpha, alpha0, alpha1, 6, 0, 2, 1, indices, out _);
            Keep(ref best, lanes, color52 + alpha63, 4, bestRotation * 2);
            Keep(ref best, lanes, color53 + alpha62, 4, bestRotation * 2 + 1);
        }

        // Opaque blocks: rotation k leaves channel k alone on the scalar line and the two others on the color line. Error
        // estimate: variance off the color line's axis, plus both lines' 4 step quantization (variance / 9 for an even
        // spread)
        private static void EstimateRotations(LaneF* px, out LaneI first, out LaneI second)
        {
            LaneF* sums = stackalloc LaneF[9];
            for (int k = 0; k < 9; k++) sums[k] = 0;
            for (int i = 0; i < 16; i++) Accumulate(sums, px[i], px[16 + i], px[32 + i]);
            const float inv = 1f / 16;
            LaneF vr = sums[3] - sums[0] * sums[0] * inv, vg = sums[4] - sums[1] * sums[1] * inv;
            LaneF vb = sums[5] - sums[2] * sums[2] * inv;
            LaneF rg = sums[6] - sums[0] * sums[1] * inv, rb = sums[7] - sums[0] * sums[2] * inv;
            LaneF gb = sums[8] - sums[1] * sums[2] * inv;
            LaneF e1 = RotationError(vg, vb, gb, vr), e2 = RotationError(vr, vb, rb, vg), e3 = RotationError(vr, vg, rg, vb);
            // Drop the worst of the three
            LaneB drop1 = e1 >= e2 & e1 >= e3, drop2 = !drop1 & e2 >= e3;
            first = vmath.select(LaneI.zero + 1, 2, drop1);
            second = vmath.select(vmath.select(LaneI.zero + 2, 3, drop2 | drop1), 3, drop1);
        }

        private static LaneF RotationError(LaneF a, LaneF c, LaneF b, LaneF scalar)
        {
            LaneF half = (a - c) * 0.5f;
            LaneF lambda = (a + c) * 0.5f + vmath.sqrt(half * half + b * b);
            return vmath.max(a + c - lambda, 0) + (lambda + scalar) * (1f / 9);
        }

        // Rotation 1 / 2 / 3: the decoder swaps alpha with red / green / blue
        private static void Rotate(LaneF* px, LaneI rotation, LaneF* rotated)
        {
            LaneB r1 = rotation == 1, r2 = rotation == 2, r3 = rotation == 3;
            for (int i = 0; i < 16; i++)
            {
                LaneF r = px[i], g = px[16 + i], b = px[32 + i], a = px[48 + i];
                rotated[i] = vmath.select(r, a, r1);
                rotated[16 + i] = vmath.select(g, a, r2);
                rotated[32 + i] = vmath.select(b, a, r3);
                rotated[48 + i] = vmath.select(vmath.select(vmath.select(a, b, r3), g, r2), r, r1);
            }
        }

        private static LaneF Mode0(LaneF* px, LaneI partition, int passes, LaneI* indices, out Line4 line0, out Line4 line1,
            out Line4 line2)
        {
            LaneI mask1 = default, mask2 = default;
            for (int lane = 0; lane < Lanes; lane++)
            {
                mask1[lane] = Partitions3Subset1[partition[lane]];
                mask2[lane] = Partitions3Subset2[partition[lane]];
            }
            LaneI mask0 = ~(mask1 | mask2) & 0xFFFF;
            return Fit(px, mask0, Rgb, 4, 1, 3, passes, indices, out line0)
                   + Fit(px, mask1, Rgb, 4, 1, 3, passes, indices, out line1)
                   + Fit(px, mask2, Rgb, 4, 1, 3, passes, indices, out line2);
        }

        private static LaneF Mode1(LaneF* px, LaneI partition, int passes, LaneI* indices, out Line4 line0, out Line4 line1)
        {
            LaneI mask1 = PartitionMasks(partition);
            LaneI mask0 = ~mask1 & 0xFFFF;
            return Fit(px, mask0, Rgb, 6, 2, 3, passes, indices, out line0)
                   + Fit(px, mask1, Rgb, 6, 2, 3, passes, indices, out line1);
        }

        private static LaneF Mode3(LaneF* px, LaneI partition, int passes, LaneI* indices, out Line4 line0, out Line4 line1)
        {
            LaneI mask1 = PartitionMasks(partition);
            LaneI mask0 = ~mask1 & 0xFFFF;
            return Fit(px, mask0, Rgb, 7, 1, 2, passes, indices, out line0)
                   + Fit(px, mask1, Rgb, 7, 1, 2, passes, indices, out line1);
        }

        // Precision 0: color 2 bit indices, alpha 3 bit. 1: the reverse.
        private static LaneF Mode4(LaneF* px, LaneI rotation, int precision, int passes, LaneI* colorIndices,
            LaneI* alphaIndices, out Line4 color, out Line4 alpha)
        {
            LaneF* rotated = stackalloc LaneF[64];
            Rotate(px, rotation, rotated);
            int colorBits = precision == 0 ? 2 : 3;
            return Fit(rotated, 0xFFFF, Rgb, 5, 0, colorBits, passes, colorIndices, out color)
                   + Fit(rotated, 0xFFFF, Alpha, 6, 0, 5 - colorBits, passes, alphaIndices, out alpha);
        }

        private static LaneF Mode5(LaneF* px, LaneI rotation, int passes, LaneI* colorIndices, LaneI* alphaIndices,
            out Line4 color, out Line4 alpha)
        {
            LaneF* rotated = stackalloc LaneF[64];
            Rotate(px, rotation, rotated);
            return Fit(rotated, 0xFFFF, Rgb, 7, 0, 2, passes, colorIndices, out color)
                   + Fit(rotated, 0xFFFF, Alpha, 8, 0, 2, passes, alphaIndices, out alpha);
        }

        private static LaneI PartitionMasks(LaneI partition)
        {
            LaneI mask = default;
            for (int lane = 0; lane < Lanes; lane++) mask[lane] = Partitions2[partition[lane]];
            return mask;
        }

        private static LaneB InMask(LaneI mask, int pixel) => ((mask >> pixel) & 1) != 0;

        private static bool Used(int channels, int c) => ((channels >> c) & 1) != 0;

        private static LaneF Square(LaneF x) => x * x;

        /// <summary>
        /// Fits a line per lane to the pixels of <paramref name="mask"/> over the channel set <paramref name="channels"/>,
        /// writes their indices (other pixels' indices untouched), returns their squared error. <paramref name="pBits"/>:
        /// 0 none, 1 one per endpoint, 2 one shared by both endpoints.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static LaneF Fit(LaneF* px, LaneI mask, int channels, int bits, int pBits, int indexBits, int passes,
            LaneI* indices, out Line4 line)
        {
            Setup(px, mask, channels, out var end0, out var end1);
            return FitFrom(px, mask, channels, end0, end1, bits, pBits, indexBits, passes, indices, out line);
        }

        // Starting endpoints: the extremes of the pixels along their principal axis (alpha alone: its min and max)
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void Setup(LaneF* px, LaneI mask, int channels, out LaneF4 end0, out LaneF4 end1)
        {
            LaneF count = 0;
            LaneF4 mean = default, lo = default, hi = default;
            for (int c = 0; c < 4; c++)
            {
                lo[c] = 255;
                hi[c] = 0;
            }
            for (int i = 0; i < 16; i++)
            {
                LaneB inside = InMask(mask, i);
                count += vmath.select(0f, 1f, inside);
                for (int c = 0; c < 4; c++)
                {
                    if (!Used(channels, c)) continue;
                    LaneF v = px[c * 16 + i];
                    mean[c] += vmath.select(0f, v, inside);
                    lo[c] = vmath.select(lo[c], vmath.min(lo[c], v), inside);
                    hi[c] = vmath.select(hi[c], vmath.max(hi[c], v), inside);
                }
            }
            LaneF invCount = 1 / vmath.max(count, 1);
            for (int c = 0; c < 4; c++) mean[c] *= invCount;

            end0 = default;
            end1 = default;
            if (channels == Alpha)
            {
                end0[3] = lo[3];
                end1[3] = hi[3];
                return;
            }

            // Principal axis by power iteration, from the bounding box diagonal
            LaneF4 axis = default;
            for (int c = 0; c < 4; c++)
                if (Used(channels, c))
                    axis[c] = hi[c] - lo[c];
            for (int iteration = 0; iteration < AxisIterations; iteration++)
            {
                LaneF4 next = default;
                for (int i = 0; i < 16; i++)
                {
                    LaneB inside = InMask(mask, i);
                    LaneF dot = 0;
                    for (int c = 0; c < 4; c++)
                        if (Used(channels, c))
                            dot += (px[c * 16 + i] - mean[c]) * axis[c];
                    dot = vmath.select(0f, dot, inside);
                    for (int c = 0; c < 4; c++)
                        if (Used(channels, c))
                            next[c] += (px[c * 16 + i] - mean[c]) * dot;
                }
                LaneF length = 0;
                for (int c = 0; c < 4; c++) length += next[c] * next[c];
                length = vmath.sqrt(length);
                LaneF inv = vmath.select(0f, 1 / length, length > 1e-6f);
                for (int c = 0; c < 4; c++) axis[c] = next[c] * inv;
            }

            LaneF tMin = 0, tMax = 0;
            for (int i = 0; i < 16; i++)
            {
                LaneB inside = InMask(mask, i);
                LaneF t = 0;
                for (int c = 0; c < 4; c++)
                    if (Used(channels, c))
                        t += (px[c * 16 + i] - mean[c]) * axis[c];
                tMin = vmath.select(tMin, vmath.min(tMin, t), inside);
                tMax = vmath.select(tMax, vmath.max(tMax, t), inside);
            }
            for (int c = 0; c < 4; c++)
            {
                if (!Used(channels, c)) continue;
                end0[c] = vmath.clamp(mean[c] + axis[c] * tMin, 0, 255);
                end1[c] = vmath.clamp(mean[c] + axis[c] * tMax, 0, 255);
            }
        }

        // Quantizes, indexes and refits (passes times) the lines from end0 to end1, keeps each lane's best
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static LaneF FitFrom(LaneF* px, LaneI mask, int channels, LaneF4 end0, LaneF4 end1, int bits,
            int pBits, int indexBits, int passes, LaneI* indices, out Line4 line)
        {
            LaneI* trial = stackalloc LaneI[16];
            LaneF bestError = float.MaxValue;
            line = default;
            for (int pass = 0; pass < passes; pass++)
            {
                // Final pass of a refined fit: neighbor indices too, then every other p-bit combination with those indices
                bool precise = passes > 1 && pass == passes - 1;
                LaneI p0 = -1, p1 = -1;
                if (pBits != 0) ChooseP(end0, end1, channels, bits, pBits, out p0, out p1);
                Line4 candidate = default;
                LaneI4 color0 = default, color1 = default;
                for (int c = 0; c < 4; c++)
                {
                    if (!Used(channels, c)) continue;
                    candidate.Code0[c] = Code(end0[c], bits, p0, out var col0);
                    candidate.Code1[c] = Code(end1[c], bits, p1, out var col1);
                    color0[c] = col0;
                    color1[c] = col1;
                }
                candidate.P0 = vmath.max(p0, 0);
                candidate.P1 = vmath.max(p1, 0);

                LaneF error = PickIndices(px, mask, channels, color0, color1, indexBits, precise, trial);
                Take(ref line, ref bestError, candidate, error, mask, indices, trial);

                if (precise && pBits != 0)
                {
                    Line4 candidate0 = line;
                    int variants = pBits == 1 ? 4 : 2;
                    for (int variant = 0; variant < variants; variant++)
                    {
                        LaneI q0 = pBits == 1 ? variant & 1 : variant, q1 = pBits == 1 ? variant >> 1 : variant;
                        LaneB same = q0 == p0 & q1 == p1;
                        if (vmath.all(same)) continue;
                        for (int c = 0; c < 4; c++)
                        {
                            if (!Used(channels, c)) continue;
                            candidate.Code0[c] = Code(end0[c], bits, q0, out var col0);
                            candidate.Code1[c] = Code(end1[c], bits, q1, out var col1);
                            color0[c] = col0;
                            color1[c] = col1;
                        }
                        candidate.P0 = q0;
                        candidate.P1 = q1;
                        error = vmath.select(Error(px, mask, channels, color0, color1, indexBits, trial), float.MaxValue, same);
                        Take(ref line, ref bestError, candidate, error, mask, indices, trial);
                    }

                    // Indices of the lanes whose p-bits changed, picked again for their codes
                    LaneB changed = line.P0 != candidate0.P0 | line.P1 != candidate0.P1;
                    if (vmath.any(changed))
                    {
                        for (int c = 0; c < 4; c++)
                        {
                            if (!Used(channels, c)) continue;
                            color0[c] = Expand((line.Code0[c] << 1) | line.P0, bits + 1);
                            color1[c] = Expand((line.Code1[c] << 1) | line.P1, bits + 1);
                        }
                        error = vmath.select(float.MaxValue,
                            PickIndices(px, mask, channels, color0, color1, indexBits, true, trial), changed);
                        Take(ref line, ref bestError, line, error, mask, indices, trial);
                    }
                }

                if (pass == passes - 1 || !Refit(px, mask, channels, trial, indexBits, ref end0, ref end1)) break;
            }
            return bestError;
        }

        // Keeps candidate and its indices in the lanes where error beats bestError
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void Take(ref Line4 line, ref LaneF bestError, in Line4 candidate, LaneF error, LaneI mask,
            LaneI* indices, LaneI* trial)
        {
            LaneB better = error < bestError;
            bestError = vmath.select(bestError, error, better);
            for (int c = 0; c < 4; c++)
            {
                line.Code0[c] = vmath.select(line.Code0[c], candidate.Code0[c], better);
                line.Code1[c] = vmath.select(line.Code1[c], candidate.Code1[c], better);
            }
            line.P0 = vmath.select(line.P0, candidate.P0, better);
            line.P1 = vmath.select(line.P1, candidate.P1, better);
            if (!vmath.any(better)) return;
            for (int i = 0; i < 16; i++) indices[i] = vmath.select(indices[i], trial[i], better & InMask(mask, i));
        }

        // Squared error of the pixels of mask with fixed indices
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static LaneF Error(LaneF* px, LaneI mask, int channels, LaneI4 color0, LaneI4 color1, int indexBits,
            LaneI* indices)
        {
            int steps = 1 << indexBits;
            LaneF4 start = default, delta = default;
            for (int c = 0; c < 4; c++)
            {
                if (!Used(channels, c)) continue;
                start[c] = (LaneF)color0[c] * 64 + 32;
                delta[c] = color1[c] - color0[c];
            }
            LaneF total = 0;
            for (int i = 0; i < 16; i++)
            {
                LaneF w = vmath.round((LaneF)indices[i] * (64f / (steps - 1)));
                LaneF e = 0;
                for (int c = 0; c < 4; c++)
                    if (Used(channels, c))
                        e += Square(px[c * 16 + i] - vmath.floor((start[c] + w * delta[c]) * (1f / 64)));
                total += vmath.select(0f, e, InMask(mask, i));
            }
            return total;
        }

        // p-bits whose endpoint codes land closest to the endpoints, per lane
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void ChooseP(LaneF4 end0, LaneF4 end1, int channels, int bits, int pBits, out LaneI p0, out LaneI p1)
        {
            LaneF e00 = 0, e01 = 0, e10 = 0, e11 = 0;
            for (int c = 0; c < 4; c++)
            {
                if (!Used(channels, c)) continue;
                Code(end0[c], bits, 0, out var c00);
                Code(end0[c], bits, 1, out var c01);
                Code(end1[c], bits, 0, out var c10);
                Code(end1[c], bits, 1, out var c11);
                e00 += Square(end0[c] - c00);
                e01 += Square(end0[c] - c01);
                e10 += Square(end1[c] - c10);
                e11 += Square(end1[c] - c11);
            }
            if (pBits == 1)
            {
                p0 = vmath.select(LaneI.zero, 1, e01 < e00);
                p1 = vmath.select(LaneI.zero, 1, e11 < e10);
            }
            else p0 = p1 = vmath.select(LaneI.zero, 1, e01 + e11 < e00 + e10);
        }

        // Endpoint code of bits bits (plus p-bit p when p >= 0) nearest to value, and the 8 bit color it decodes to
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static LaneI Code(LaneF value, int bits, LaneI p, out LaneI color)
        {
            int max = (1 << bits) - 1;
            if (p[0] < 0)
            {
                LaneI code = vmath.clamp((LaneI)vmath.round(value * (max / 255f)), 0, max);
                color = Expand(code, bits);
                return code;
            }

            LaneI c = vmath.clamp((LaneI)vmath.round((value * ((2 * max + 1) / 255f) - p) * 0.5f), 0, max);
            color = Expand((c << 1) | p, bits + 1);
            return c;
        }

        private static LaneI Expand(LaneI code, int bits) => bits >= 8 ? code : (code << (8 - bits)) | (code >> (2 * bits - 8));

        // Index weights (of 64) are round(k * 64 / (steps - 1)) for the 4, 8 and 16 step BC7 tables
        private static LaneI Weight(LaneI k, int steps) => (LaneI)vmath.round((LaneF)k * (64f / (steps - 1)));

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static LaneF PickIndices(LaneF* px, LaneI mask, int channels, LaneI4 color0, LaneI4 color1, int indexBits,
            bool precise, LaneI* indices)
        {
            int steps = 1 << indexBits;
            float spacing = 64f / (steps - 1);

            // Nearest step by projection on the line: the position (of 64) is compared with the midpoints between the
            // steps' weights (not exactly evenly spaced after rounding). Decoded step: (start + w * delta) / 64, floored.
            LaneF4 start = default, delta = default;
            LaneF length = 0;
            for (int c = 0; c < 4; c++)
            {
                if (!Used(channels, c)) continue;
                start[c] = (LaneF)color0[c] * 64 + 32;
                delta[c] = color1[c] - color0[c];
                length += delta[c] * delta[c];
            }
            LaneF scale = vmath.select(0f, 64 / length, length > 0);
            float* midpoints = stackalloc float[15];
            for (int s = 0; s < steps - 1; s++)
                midpoints[s] = (vmath.round(s * spacing) + vmath.round((s + 1) * spacing)) * 0.5f;

            LaneF total = 0;
            for (int i = 0; i < 16; i++)
            {
                LaneF t = 0;
                for (int c = 0; c < 4; c++)
                    if (Used(channels, c))
                        t += (px[c * 16 + i] - color0[c]) * delta[c];
                t *= scale;
                LaneF k;
                if (steps == 16)
                {
                    // Nearest evenly spaced step, then the exact midpoints on both sides
                    k = vmath.clamp(vmath.round(t * (1 / spacing)), 0, 15);
                    LaneF w = vmath.round(k * spacing);
                    LaneF below = (vmath.round((k - 1) * spacing) + w) * 0.5f;
                    LaneF above = (vmath.round((k + 1) * spacing) + w) * 0.5f;
                    k += vmath.select(0f, -1f, t <= below & k > 0) + vmath.select(0f, 1f, t > above & k < 15);
                }
                else
                {
                    k = 0;
                    for (int s = 0; s < steps - 1; s++) k += vmath.select(0f, 1f, t > midpoints[s]);
                }

                // Precise: also the two neighbors, the decoder rounds each channel
                LaneF best = float.MaxValue;
                LaneF bestIndex = k;
                for (int d = precise ? -1 : 0; d <= (precise ? 1 : 0); d++)
                {
                    LaneF n = vmath.clamp(k + d, 0, steps - 1);
                    LaneF w = vmath.round(n * spacing);
                    LaneF e = 0;
                    for (int c = 0; c < 4; c++)
                        if (Used(channels, c))
                            e += Square(px[c * 16 + i] - vmath.floor((start[c] + w * delta[c]) * (1f / 64)));
                    LaneB better = e < best;
                    best = vmath.select(best, e, better);
                    bestIndex = vmath.select(bestIndex, n, better);
                }
                indices[i] = (LaneI)bestIndex;
                total += vmath.select(0f, best, InMask(mask, i));
            }
            return total;
        }

        // Least squares endpoints for fixed indices; false when no lane could be refit
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static bool Refit(LaneF* px, LaneI mask, int channels, LaneI* indices, int indexBits, ref LaneF4 end0,
            ref LaneF4 end1)
        {
            int steps = 1 << indexBits;
            LaneF aa = 0, ab = 0, bb = 0;
            LaneF4 ax = default, bx = default;
            for (int i = 0; i < 16; i++)
            {
                LaneB inside = InMask(mask, i);
                LaneF t = vmath.select(0f, (LaneF)Weight(indices[i], steps) * (1f / 64), inside);
                LaneF s = vmath.select(0f, 1 - t, inside);
                aa += s * s;
                ab += s * t;
                bb += t * t;
                for (int c = 0; c < 4; c++)
                {
                    if (!Used(channels, c)) continue;
                    ax[c] += s * px[c * 16 + i];
                    bx[c] += t * px[c * 16 + i];
                }
            }
            LaneF det = aa * bb - ab * ab;
            LaneB valid = vmath.abs(det) > 1e-6f;
            if (!vmath.any(valid)) return false;
            LaneF inv = vmath.select(0f, 1 / det, valid);
            for (int c = 0; c < 4; c++)
            {
                if (!Used(channels, c)) continue;
                end0[c] = vmath.select(end0[c], vmath.clamp((ax[c] * bb - bx[c] * ab) * inv, 0, 255), valid);
                end1[c] = vmath.select(end1[c], vmath.clamp((bx[c] * aa - ax[c] * ab) * inv, 0, 255), valid);
            }
            return true;
        }

        // Partitions screened for mode 1 (the first one also for mode 3)
        private const int TopPartitions = 2;

        // The TopPartitions partitions whose subsets lie closest to one RGB line each (best first), per lane: squared
        // distance off each subset's principal axis, from its covariance
        private static void EstimatePartitions(LaneF* px, LaneI* top)
        {
            LaneF* rows = stackalloc LaneF[RowSums];
            BuildRowSums(px, rows);
            LaneF* all = stackalloc LaneF[9];
            LaneF* part = stackalloc LaneF[9];
            SubsetSums(rows, 0xFFFF, all);

            LaneF* topError = stackalloc LaneF[TopPartitions];
            for (int k = 0; k < TopPartitions; k++)
            {
                topError[k] = float.MaxValue;
                top[k] = 0;
            }
            for (int partition = 0; partition < 64; partition++)
            {
                uint mask = Partitions2[partition];
                SubsetSums(rows, mask, part);
                int count = vmath.countbits(mask);
                LaneF error = OffAxis(count, part[0], part[1], part[2], part[3], part[4], part[5], part[6], part[7], part[8])
                               + OffAxis(16 - count, all[0] - part[0], all[1] - part[1], all[2] - part[2], all[3] - part[3],
                                   all[4] - part[4], all[5] - part[5], all[6] - part[6], all[7] - part[7], all[8] - part[8]);
                // Insertion into the sorted list, per lane
                LaneI id = partition;
                for (int k = 0; k < TopPartitions; k++)
                {
                    LaneB swap = error < topError[k];
                    LaneF e = topError[k];
                    LaneI t = top[k];
                    topError[k] = vmath.select(e, error, swap);
                    top[k] = vmath.select(t, id, swap);
                    error = vmath.select(error, e, swap);
                    id = vmath.select(id, t, swap);
                }
            }
        }

        // Best of the 16 mode 0 partitions per lane, by the same covariance estimate over three subsets
        private static LaneI EstimatePartitions3(LaneF* px)
        {
            LaneF* rows = stackalloc LaneF[RowSums];
            BuildRowSums(px, rows);
            LaneF* all = stackalloc LaneF[9];
            LaneF* one = stackalloc LaneF[9];
            LaneF* two = stackalloc LaneF[9];
            SubsetSums(rows, 0xFFFF, all);

            LaneF bestError = float.MaxValue;
            LaneI best = 0;
            for (int partition = 0; partition < 16; partition++)
            {
                uint mask1 = Partitions3Subset1[partition], mask2 = Partitions3Subset2[partition];
                SubsetSums(rows, mask1, one);
                SubsetSums(rows, mask2, two);
                int count1 = vmath.countbits(mask1), count2 = vmath.countbits(mask2);
                LaneF error = OffAxis(count1, one[0], one[1], one[2], one[3], one[4], one[5], one[6], one[7], one[8])
                               + OffAxis(count2, two[0], two[1], two[2], two[3], two[4], two[5], two[6], two[7], two[8])
                               + OffAxis(16 - count1 - count2, all[0] - one[0] - two[0], all[1] - one[1] - two[1],
                                   all[2] - one[2] - two[2], all[3] - one[3] - two[3], all[4] - one[4] - two[4],
                                   all[5] - one[5] - two[5], all[6] - one[6] - two[6], all[7] - one[7] - two[7],
                                   all[8] - one[8] - two[8]);
                LaneB better = error < bestError;
                bestError = vmath.select(bestError, error, better);
                best = vmath.select(best, partition, better);
            }
            return best;
        }

        // Sums of r g b, rr gg bb, rg rb gb of every subset of each pixel row: (row * 16 + 4 bit subset) * 9 + sum
        private const int RowSums = 4 * 16 * 9;

        private static void BuildRowSums(LaneF* px, LaneF* rows)
        {
            for (int row = 0; row < 4; row++)
            {
                LaneF* table = rows + row * 16 * 9;
                for (int k = 0; k < 9; k++) table[k] = 0;
                for (int subset = 1; subset < 16; subset++)
                {
                    int low = vmath.tzcnt(subset), i = row * 4 + low;
                    LaneF* dst = table + subset * 9;
                    LaneF* src = table + (subset & (subset - 1)) * 9;
                    LaneF r = px[i], g = px[16 + i], b = px[32 + i];
                    dst[0] = src[0] + r;
                    dst[1] = src[1] + g;
                    dst[2] = src[2] + b;
                    dst[3] = src[3] + r * r;
                    dst[4] = src[4] + g * g;
                    dst[5] = src[5] + b * b;
                    dst[6] = src[6] + r * g;
                    dst[7] = src[7] + r * b;
                    dst[8] = src[8] + g * b;
                }
            }
        }

        private static void SubsetSums(LaneF* rows, uint mask, LaneF* sums)
        {
            LaneF* r0 = rows + (mask & 15) * 9;
            LaneF* r1 = rows + (16 + ((mask >> 4) & 15)) * 9;
            LaneF* r2 = rows + (32 + ((mask >> 8) & 15)) * 9;
            LaneF* r3 = rows + (48 + ((mask >> 12) & 15)) * 9;
            for (int k = 0; k < 9; k++) sums[k] = r0[k] + r1[k] + r2[k] + r3[k];
        }

        private static void Accumulate(LaneF* sums, LaneF r, LaneF g, LaneF b)
        {
            sums[0] += r;
            sums[1] += g;
            sums[2] += b;
            sums[3] += r * r;
            sums[4] += g * g;
            sums[5] += b * b;
            sums[6] += r * g;
            sums[7] += r * b;
            sums[8] += g * b;
        }

        // Variance off the principal axis: trace of the covariance minus its largest eigenvalue (power iteration)
        private static LaneF OffAxis(int count, LaneF r, LaneF g, LaneF b, LaneF rr, LaneF gg, LaneF bb, LaneF rg,
            LaneF rb, LaneF gb)
        {
            if (count < 2) return 0;
            float inv = 1f / count;
            LaneF cRR = rr - r * r * inv, cGG = gg - g * g * inv, cBB = bb - b * b * inv;
            LaneF cRG = rg - r * g * inv, cRB = rb - r * b * inv, cGB = gb - g * b * inv;
            LaneF trace = cRR + cGG + cBB;

            LaneB rMax = cRR >= cGG & cRR >= cBB, gMax = !rMax & cGG >= cBB;
            LaneF vx = vmath.select(vmath.select(cRB, cRG, gMax), cRR, rMax);
            LaneF vy = vmath.select(vmath.select(cGB, cGG, gMax), cRG, rMax);
            LaneF vz = vmath.select(vmath.select(cBB, cGB, gMax), cRB, rMax);
            for (int i = 0; i < EstimateIterations; i++)
            {
                LaneF nx = cRR * vx + cRG * vy + cRB * vz;
                LaneF ny = cRG * vx + cGG * vy + cGB * vz;
                LaneF nz = cRB * vx + cGB * vy + cBB * vz;
                LaneF length = vmath.sqrt(nx * nx + ny * ny + nz * nz);
                LaneF scale = vmath.select(0f, 1 / length, length > 1e-6f);
                vx = nx * scale;
                vy = ny * scale;
                vz = nz * scale;
            }
            LaneF lambda = vx * (cRR * vx + cRG * vy + cRB * vz) + vy * (cRG * vx + cGG * vy + cGB * vz)
                            + vz * (cRB * vx + cGB * vy + cBB * vz);
            return vmath.max(trace - lambda, 0);
        }

        // Packing, one block at a time

        private static Line Lane(in Line4 line, int lane) => new Line
        {
            Code0 = new int4(line.Code0[0][lane], line.Code0[1][lane], line.Code0[2][lane], line.Code0[3][lane]),
            Code1 = new int4(line.Code1[0][lane], line.Code1[1][lane], line.Code1[2][lane], line.Code1[3][lane]),
            P0 = line.P0[lane],
            P1 = line.P1[lane],
        };

        private static void LaneIndices(LaneI* indices, int lane, int* dst)
        {
            for (int i = 0; i < 16; i++) dst[i] = indices[i][lane];
        }

        // The anchor pixel's index must have its top bit clear: else swap the endpoints and mirror the indices
        private static void FixAnchor(int anchor, uint mask, int indexBits, int* indices, ref Line line)
        {
            int max = (1 << indexBits) - 1;
            if (indices[anchor] <= max >> 1) return;
            (line.Code0, line.Code1) = (line.Code1, line.Code0);
            (line.P0, line.P1) = (line.P1, line.P0);
            for (int i = 0; i < 16; i++)
                if (((mask >> i) & 1) != 0)
                    indices[i] = max - indices[i];
        }

        private static void Store(ref Best best, int lane, float error, Bits bits)
        {
            best.Lo[lane] = bits.Lo;
            best.Hi[lane] = bits.Hi;
            best.Error[lane] = error;
        }

        private static void PackMode6(ref Best best, int lane, in Line4 line4, LaneI* indices4)
        {
            int* indices = stackalloc int[16];
            LaneIndices(indices4, lane, indices);
            var line = Lane(line4, lane);
            FixAnchor(0, 0xFFFF, 4, indices, ref line);

            var bits = new Bits();
            bits.Put(1 << 6, 7);
            for (int c = 0; c < 4; c++)
            {
                bits.Put(line.Code0[c], 7);
                bits.Put(line.Code1[c], 7);
            }
            bits.Put(line.P0, 1);
            bits.Put(line.P1, 1);
            for (int i = 0; i < 16; i++) bits.Put(indices[i], i == 0 ? 3 : 4);
            best.Lo[lane] = bits.Lo;
            best.Hi[lane] = bits.Hi;
        }

        private static void PackMode0(ref Best best, int lane, float error, int partition, in Line4 line0x4, in Line4 line1x4,
            in Line4 line2x4, LaneI* indices4)
        {
            int* indices = stackalloc int[16];
            LaneIndices(indices4, lane, indices);
            var line0 = Lane(line0x4, lane);
            var line1 = Lane(line1x4, lane);
            var line2 = Lane(line2x4, lane);
            uint mask1 = Partitions3Subset1[partition], mask2 = Partitions3Subset2[partition];
            int anchor1 = Anchors3Subset1[partition], anchor2 = Anchors3Subset2[partition];
            FixAnchor(0, ~(mask1 | mask2) & 0xFFFF, 3, indices, ref line0);
            FixAnchor(anchor1, mask1, 3, indices, ref line1);
            FixAnchor(anchor2, mask2, 3, indices, ref line2);

            var bits = new Bits();
            bits.Put(1, 1);
            bits.Put(partition, 4);
            for (int c = 0; c < 3; c++)
            {
                bits.Put(line0.Code0[c], 4);
                bits.Put(line0.Code1[c], 4);
                bits.Put(line1.Code0[c], 4);
                bits.Put(line1.Code1[c], 4);
                bits.Put(line2.Code0[c], 4);
                bits.Put(line2.Code1[c], 4);
            }
            bits.Put(line0.P0, 1);
            bits.Put(line0.P1, 1);
            bits.Put(line1.P0, 1);
            bits.Put(line1.P1, 1);
            bits.Put(line2.P0, 1);
            bits.Put(line2.P1, 1);
            for (int i = 0; i < 16; i++) bits.Put(indices[i], i == 0 || i == anchor1 || i == anchor2 ? 2 : 3);
            Store(ref best, lane, error, bits);
        }

        private static void PackMode1(ref Best best, int lane, float error, int partition, in Line4 line0x4, in Line4 line1x4,
            LaneI* indices4)
        {
            int* indices = stackalloc int[16];
            LaneIndices(indices4, lane, indices);
            var line0 = Lane(line0x4, lane);
            var line1 = Lane(line1x4, lane);
            uint mask1 = Partitions2[partition];
            int anchor = Anchors2[partition];
            FixAnchor(0, ~mask1 & 0xFFFF, 3, indices, ref line0);
            FixAnchor(anchor, mask1, 3, indices, ref line1);

            var bits = new Bits();
            bits.Put(1 << 1, 2);
            bits.Put(partition, 6);
            for (int c = 0; c < 3; c++)
            {
                bits.Put(line0.Code0[c], 6);
                bits.Put(line0.Code1[c], 6);
                bits.Put(line1.Code0[c], 6);
                bits.Put(line1.Code1[c], 6);
            }
            bits.Put(line0.P0, 1);
            bits.Put(line1.P0, 1);
            for (int i = 0; i < 16; i++) bits.Put(indices[i], i == 0 || i == anchor ? 2 : 3);
            Store(ref best, lane, error, bits);
        }

        private static void PackMode3(ref Best best, int lane, float error, int partition, in Line4 line0x4, in Line4 line1x4,
            LaneI* indices4)
        {
            int* indices = stackalloc int[16];
            LaneIndices(indices4, lane, indices);
            var line0 = Lane(line0x4, lane);
            var line1 = Lane(line1x4, lane);
            uint mask1 = Partitions2[partition];
            int anchor = Anchors2[partition];
            FixAnchor(0, ~mask1 & 0xFFFF, 2, indices, ref line0);
            FixAnchor(anchor, mask1, 2, indices, ref line1);

            var bits = new Bits();
            bits.Put(1 << 3, 4);
            bits.Put(partition, 6);
            for (int c = 0; c < 3; c++)
            {
                bits.Put(line0.Code0[c], 7);
                bits.Put(line0.Code1[c], 7);
                bits.Put(line1.Code0[c], 7);
                bits.Put(line1.Code1[c], 7);
            }
            bits.Put(line0.P0, 1);
            bits.Put(line0.P1, 1);
            bits.Put(line1.P0, 1);
            bits.Put(line1.P1, 1);
            for (int i = 0; i < 16; i++) bits.Put(indices[i], i == 0 || i == anchor ? 1 : 2);
            Store(ref best, lane, error, bits);
        }

        private static void PackMode4(ref Best best, int lane, float error, int rotation, int precision, in Line4 color4,
            in Line4 alpha4, LaneI* colorIndices4, LaneI* alphaIndices4)
        {
            int* colorIndices = stackalloc int[16];
            int* alphaIndices = stackalloc int[16];
            LaneIndices(colorIndices4, lane, colorIndices);
            LaneIndices(alphaIndices4, lane, alphaIndices);
            var color = Lane(color4, lane);
            var alpha = Lane(alpha4, lane);
            int colorBits = precision == 0 ? 2 : 3, alphaBits = 5 - colorBits;
            FixAnchor(0, 0xFFFF, colorBits, colorIndices, ref color);
            FixAnchor(0, 0xFFFF, alphaBits, alphaIndices, ref alpha);

            var bits = new Bits();
            bits.Put(1 << 4, 5);
            bits.Put(rotation, 2);
            bits.Put(precision, 1);
            for (int c = 0; c < 3; c++)
            {
                bits.Put(color.Code0[c], 5);
                bits.Put(color.Code1[c], 5);
            }
            bits.Put(alpha.Code0.w, 6);
            bits.Put(alpha.Code1.w, 6);
            // 2 bit index set first, then the 3 bit set
            int* first = precision == 0 ? colorIndices : alphaIndices;
            int* second = precision == 0 ? alphaIndices : colorIndices;
            for (int i = 0; i < 16; i++) bits.Put(first[i], i == 0 ? 1 : 2);
            for (int i = 0; i < 16; i++) bits.Put(second[i], i == 0 ? 2 : 3);
            Store(ref best, lane, error, bits);
        }

        private static void PackMode5(ref Best best, int lane, float error, int rotation, in Line4 color4, in Line4 alpha4,
            LaneI* colorIndices4, LaneI* alphaIndices4)
        {
            int* colorIndices = stackalloc int[16];
            int* alphaIndices = stackalloc int[16];
            LaneIndices(colorIndices4, lane, colorIndices);
            LaneIndices(alphaIndices4, lane, alphaIndices);
            var color = Lane(color4, lane);
            var alpha = Lane(alpha4, lane);
            FixAnchor(0, 0xFFFF, 2, colorIndices, ref color);
            FixAnchor(0, 0xFFFF, 2, alphaIndices, ref alpha);

            var bits = new Bits();
            bits.Put(1 << 5, 6);
            bits.Put(rotation, 2);
            for (int c = 0; c < 3; c++)
            {
                bits.Put(color.Code0[c], 7);
                bits.Put(color.Code1[c], 7);
            }
            bits.Put(alpha.Code0.w, 8);
            bits.Put(alpha.Code1.w, 8);
            for (int i = 0; i < 16; i++) bits.Put(colorIndices[i], i == 0 ? 1 : 2);
            for (int i = 0; i < 16; i++) bits.Put(alphaIndices[i], i == 0 ? 1 : 2);
            Store(ref best, lane, error, bits);
        }
    }
}
