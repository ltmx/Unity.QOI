using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using UnityEngine;
using UnityEngine.Rendering;

namespace LTMX.QOI.Editor
{
    /// <summary>
    /// Compute shader ASTC encoder of <see cref="QoiFastCompressor"/> (QoiAstc.compute): one GPU thread per block, every
    /// mip level in one dispatch per level and one readback. Uses the tables of <see cref="QoiAstcEncoder"/>.
    /// </summary>
    internal static unsafe class QoiAstcGpu
    {
        private const int Threads = 32, MaxGroupsX = 65535;
        private const int OffLevel = 12668, TableHeader = OffLevel + 21 * 4 + 2 * 21 * 256 + 2 * 2 * 13 * 12 + 2 * 13 * 13;
        private const int GridStride = 10, ModeStride = 10, PartStride = 6;

        private static ComputeShader s_Shader;
        private static readonly Dictionary<int, (ComputeBuffer buffer, int gridOffset, int gridCount, int modeOffset, int modeCount, int partOffset, int partCount)>
            s_Tables = new Dictionary<int, (ComputeBuffer, int, int, int, int, int, int)>();

        /// <summary>Set false to force the CPU encoder.</summary>
        public static bool Enabled = true;

        /// <summary>Fewer modes, partitions and refinement passes: faster, lower quality.</summary>
        public static bool FastSearch;

        private static readonly int TablesId = Shader.PropertyToID("_Tables");
        private static readonly int PixelsId = Shader.PropertyToID("_Pixels");
        private static readonly int BlocksId = Shader.PropertyToID("_Blocks");
        private static readonly int HardId = Shader.PropertyToID("_Hard");
        private static readonly int LevelsId = Shader.PropertyToID("_Levels");
        private static readonly int LevelCountId = Shader.PropertyToID("_LevelCount");
        private static readonly int BlockStartId = Shader.PropertyToID("_BlockStart");
        private static readonly int BlockCountId = Shader.PropertyToID("_BlockCount");
        private static readonly int GroupsXId = Shader.PropertyToID("_GroupsX");
        private static readonly int GridOffsetId = Shader.PropertyToID("_GridOffset");
        private static readonly int GridCountId = Shader.PropertyToID("_GridCount");
        private static readonly int ModeOffsetId = Shader.PropertyToID("_ModeOffset");
        private static readonly int ModeCountId = Shader.PropertyToID("_ModeCount");
        private static readonly int PartOffsetId = Shader.PropertyToID("_PartOffset");
        private static readonly int PartCountId = Shader.PropertyToID("_PartCount");

        /// <summary>True when the GPU encoder can run here (compute shaders, a graphics device, main thread).</summary>
        public static bool Available(int blockSize)
        {
            if (!Enabled || !SystemInfo.supportsComputeShaders || SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null)
                return false;
            var cs = GetShader(blockSize);
            if (cs == null || !cs.HasKernel("Encode" + blockSize)) return false;
            return cs.IsSupported(cs.FindKernel("Encode" + blockSize));
        }

        private static ComputeShader GetShader(int blockSize)
        {
            if (s_Shader == null) s_Shader = Resources.Load<ComputeShader>("QoiAstc");
            return s_Shader;
        }

        /// <summary>
        /// Encode of every level in flight: <see cref="Begin"/> uploads level 0 and dispatches it, so the GPU encodes it
        /// while the CPU builds the other levels; <see cref="Job.Finish"/> uploads and dispatches those, then the blocks
        /// pass 1 fit poorly (pass 2), and reads every block back.
        /// </summary>
        public sealed class Job
        {
            private readonly ComputeShader cs;
            private readonly int kernel, hardKernel, levels;
            private readonly int[] widths, heights, pixelOffsets, blockOffsets;
            private ComputeBuffer pixelBuffer, blockBuffer, levelBuffer, hardBuffer;

            internal Job(IntPtr level0, int[] widths, int[] heights, int blockSize)
            {
                this.widths = widths;
                this.heights = heights;
                levels = widths.Length;
                pixelOffsets = new int[levels + 1];
                blockOffsets = new int[levels + 1];
                var levelData = new int[levels * 5];
                for (int m = 0; m < levels; m++)
                {
                    int bx = (widths[m] + blockSize - 1) / blockSize;
                    pixelOffsets[m + 1] = pixelOffsets[m] + widths[m] * heights[m];
                    blockOffsets[m + 1] = blockOffsets[m] + bx * ((heights[m] + blockSize - 1) / blockSize);
                    levelData[m * 5] = blockOffsets[m];
                    levelData[m * 5 + 1] = bx;
                    levelData[m * 5 + 2] = widths[m];
                    levelData[m * 5 + 3] = heights[m];
                    levelData[m * 5 + 4] = pixelOffsets[m];
                }

                var tables = GetTables(blockSize);
                cs = GetShader(blockSize);
                kernel = cs.FindKernel("Encode" + blockSize);
                hardKernel = cs.FindKernel("Hard" + blockSize);
                pixelBuffer = new ComputeBuffer(pixelOffsets[levels], 4);
                blockBuffer = new ComputeBuffer(blockOffsets[levels], 16);
                levelBuffer = new ComputeBuffer(levels * 5, 4);
                hardBuffer = new ComputeBuffer(blockOffsets[levels] * 5 + 1, 4);
                try
                {
                    levelBuffer.SetData(levelData);
                    hardBuffer.SetData(new uint[1]);
                    foreach (int k in new[] { kernel, hardKernel })
                    {
                        cs.SetBuffer(k, TablesId, tables.buffer);
                        cs.SetBuffer(k, PixelsId, pixelBuffer);
                        cs.SetBuffer(k, BlocksId, blockBuffer);
                        cs.SetBuffer(k, LevelsId, levelBuffer);
                        cs.SetBuffer(k, HardId, hardBuffer);
                    }
                    if (FastSearch) cs.EnableKeyword("QOI_ASTC_FAST"); else cs.DisableKeyword("QOI_ASTC_FAST");
                    cs.SetInt(GridOffsetId, tables.gridOffset);
                    cs.SetInt(GridCountId, tables.gridCount);
                    cs.SetInt(ModeOffsetId, tables.modeOffset);
                    cs.SetInt(ModeCountId, tables.modeCount);
                    cs.SetInt(PartOffsetId, tables.partOffset);
                    cs.SetInt(PartCountId, tables.partCount);
                    cs.SetInt(LevelCountId, levels);
                    Upload(0, level0);
                    Dispatch(kernel, 0, blockOffsets[1]);
                    GL.Flush(); // start now, not at the readback
                }
                catch
                {
                    Release();
                    throw;
                }
            }

            private void Upload(int m, IntPtr pixels)
            {
                // Straight from the level memory, no managed staging copy
                var src = NativeArrayUnsafeUtility.ConvertExistingDataToNativeArray<uint>((void*)pixels,
                    widths[m] * heights[m], Allocator.None);
#if ENABLE_UNITY_COLLECTIONS_CHECKS
                NativeArrayUnsafeUtility.SetAtomicSafetyHandle(ref src, AtomicSafetyHandle.GetTempMemoryHandle());
#endif
                pixelBuffer.SetData(src, 0, pixelOffsets[m], src.Length);
            }

            private void Dispatch(int k, int start, int end)
            {
                int groups = (end - start + Threads - 1) / Threads;
                if (groups <= 0) return;
                int gx = Math.Min(groups, MaxGroupsX), gy = (groups + gx - 1) / gx;
                cs.SetInt(BlockStartId, start);
                cs.SetInt(BlockCountId, end);
                cs.SetInt(GroupsXId, gx);
                cs.Dispatch(k, gx, gy, 1);
            }

            /// <summary>Encodes levels 1 and up (<paramref name="pixels"/>, level 0 unused) and writes every level's blocks.</summary>
            public void Finish(IntPtr[] pixels, IntPtr[] blocks)
            {
                try
                {
                    // Every other level in one dispatch: small levels would each cost a whole block's latency on their own
                    for (int m = 1; m < levels; m++) Upload(m, pixels[m]);
                    Dispatch(kernel, blockOffsets[1], blockOffsets[levels]);

                    // Pass 2: the blocks pass 1 fit poorly, packed together
                    var hard = new uint[1];
                    hardBuffer.GetData(hard, 0, 0, 1);
                    Dispatch(hardKernel, 0, (int)hard[0]);

                    var result = new byte[(long)blockOffsets[levels] * 16];
                    blockBuffer.GetData(result);
                    fixed (byte* r = result)
                        for (int m = 0; m < levels; m++)
                            UnsafeUtility.MemCpy((void*)blocks[m], r + (long)blockOffsets[m] * 16,
                                (long)(blockOffsets[m + 1] - blockOffsets[m]) * 16);
                }
                finally
                {
                    Release();
                }
            }

            /// <summary>Frees the buffers (<see cref="Finish"/> does).</summary>
            public void Release()
            {
                pixelBuffer?.Release();
                blockBuffer?.Release();
                levelBuffer?.Release();
                hardBuffer?.Release();
                pixelBuffer = blockBuffer = levelBuffer = hardBuffer = null;
            }
        }

        /// <summary>Uploads and dispatches level 0 (RGBA32 <paramref name="level0"/>); the others follow with <see cref="Job.Finish"/>.</summary>
        public static Job Begin(IntPtr level0, int[] widths, int[] heights, int blockSize) => new Job(level0, widths, heights, blockSize);

        /// <summary>Encodes every level: RGBA32 <paramref name="pixels"/> of each level into <paramref name="blocks"/> (16 bytes per block, row major).</summary>
        public static void Encode(IntPtr[] pixels, int[] widths, int[] heights, int blockSize, IntPtr[] blocks) =>
            Begin(pixels[0], widths, heights, blockSize).Finish(pixels, blocks);

        /// <summary>Frees the cached table buffers.</summary>
        public static void ReleaseResources()
        {
            foreach (var t in s_Tables.Values) t.buffer.Release();
            s_Tables.Clear();
        }

        // Tables of QoiAstcEncoder flattened to ints, in the layout of QoiAstc.compute
        private static (ComputeBuffer buffer, int gridOffset, int gridCount, int modeOffset, int modeCount, int partOffset, int partCount) GetTables(int blockSize)
        {
            if (s_Tables.TryGetValue(blockSize, out var cached) && cached.buffer.IsValid()) return cached;
            var t = QoiAstcEncoder.GetTables(blockSize, blockSize);
            var data = new List<int>(TableHeader);
            for (int i = 0; i < 21 * 256; i++) data.Add(t->ColorUnquant[i]);
            for (int i = 0; i < 21 * 256; i++) data.Add(t->ColorQuant[i]);
            for (int i = 0; i < 12 * 32; i++) data.Add(t->WeightUnquant[i]);
            for (int i = 0; i < 12 * 65; i++) data.Add(t->WeightQuant[i]);
            for (int i = 0; i < 12 * 32; i++) data.Add(t->WeightFlip[i]);
            for (int i = 0; i < 243; i++) data.Add(t->TritEnc[i]);
            for (int i = 0; i < 125; i++) data.Add(t->QuintEnc[i]);
            if (data.Count != OffLevel) throw new InvalidOperationException("ASTC GPU table layout mismatch");
            for (int l = 0; l < 21; l++)
            {
                data.Add(t->Bits[l]);
                data.Add(t->Trits[l]);
                data.Add(t->Quints[l]);
                data.Add(t->Ranges[l]);
            }
            for (int i = 0; i < 21 * 256; i++) data.Add(t->ColorUp[i]);
            for (int i = 0; i < 21 * 256; i++) data.Add(t->ColorDown[i]);
            for (int i = 0; i < 2 * 13 * 12; i++) data.Add(t->AxisJ[i]);
            for (int i = 0; i < 2 * 13 * 12; i++) data.Add(BitConverter.SingleToInt32Bits(t->AxisF[i]));
            for (int i = 0; i < 2 * 13 * 13; i++) data.Add(BitConverter.SingleToInt32Bits(t->AxisInv[i]));
            if (data.Count != TableHeader) throw new InvalidOperationException("ASTC GPU table layout mismatch");

            int texels = t->Texels;
            int gridOffset = data.Count;
            for (int i = 0; i < t->GridCount * GridStride; i++) data.Add(0);
            for (int g = 0; g < t->GridCount; g++)
            {
                var grid = t->Grids + g;
                int b = gridOffset + g * GridStride;
                int total = grid->Start[grid->Count];
                data[b] = grid->X;
                data[b + 1] = grid->Y;
                data[b + 2] = grid->Count;
                data[b + 3] = grid->Full;
                data[b + 4] = data.Count;
                for (int i = 0; i < texels * 4; i++) data.Add(grid->Index[i] | (grid->Factor[i] << 8));
                data[b + 5] = data.Count;
                for (int i = 0; i <= grid->Count; i++) data.Add(grid->Start[i]);
                data[b + 6] = data.Count;
                for (int i = 0; i < total; i++) data.Add(grid->Texel[i]);
                data[b + 7] = data.Count;
                for (int i = 0; i < total; i++) data.Add(BitConverter.SingleToInt32Bits(grid->Weight[i]));
                data[b + 8] = data.Count;
                for (int i = 0; i < total; i++) data.Add(BitConverter.SingleToInt32Bits(grid->Step[i]));
            }

            // Modes grouped by grid, one plane before two (the shader reuses a grid's decimation errors)
            int modeOffset = data.Count, modeCount = t->ModeCount;
            for (int i = 0; i < modeCount; i++)
            {
                var mode = t->Modes + i;
                data.Add(mode->Grid);
                data.Add(mode->Level);
                data.Add(mode->Bits);
                data.Add(mode->WeightBits);
                data.Add(mode->ColorLevel[3]);
                data.Add(mode->ColorLevel[4]);
                data.Add(BitConverter.SingleToInt32Bits(mode->WeightNoise));
                data.Add(mode->Dual);
                data.Add(mode->ColorLevel[8]);
                data.Add(mode->ColorLevel[9]);
            }

            // Two partition patterns: seed, texels of partition 1 (5 words, up to 144 texels)
            int partOffset = data.Count, partCount = t->PartCount2;
            for (int j = 0; j < partCount; j++)
            {
                data.Add(t->PartSeed2[j]);
                ulong* mask = t->PartMask2 + (j * 2 + 1) * 3;
                for (int w = 0; w < 5; w++) data.Add((int)(uint)(mask[w >> 1] >> (32 * (w & 1))));
            }

            var buffer = new ComputeBuffer(data.Count, 4);
            buffer.SetData(data);
            var entry = (buffer, gridOffset, t->GridCount, modeOffset, modeCount, partOffset, partCount);
            s_Tables[blockSize] = entry;
            return entry;
        }
    }
}
