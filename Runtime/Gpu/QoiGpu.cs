using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using Unity.Collections;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;

namespace LTMX.QOI
{
    /// <summary>
    /// Compute shader QOI encoder / decoder.
    /// <para>
    /// Encoding works on any 2D texture or render texture already on the GPU (compressed, non readable, HDR...):
    /// only the compressed bytes are read back. The output is a seekable QOI file (see <see cref="QoiCodec"/>),
    /// readable by any QOI decoder.
    /// </para>
    /// <para>
    /// Decoding a seekable file runs one GPU thread per chunk and writes straight into a render texture, the pixels
    /// never exist on the CPU. Plain QOI files (and machines without compute shaders) fall back to the CPU decoder.
    /// </para>
    /// <para>
    /// GPU buffers are pooled between calls, call <see cref="ReleaseResources"/> to free them.
    /// </para>
    /// </summary>
    public static class QoiGpu
    {
        /// Chunk size used by <see cref="Encode"/>: smaller chunks mean more GPU threads when decoding,
        /// for about 9 extra bytes per chunk.
        public const int DefaultChunkPixels = 1024;

        private const int ChunkThreads = 32;
        private const int ClearThreads = 256;
        private const int MaxGroups = 65535;

        private static ComputeShader s_Shader;
        private static int s_EncodeKernel, s_ScanKernel, s_ClearKernel, s_CompactKernel, s_DecodeKernel;

        private static readonly int WidthId = Shader.PropertyToID("_Width");
        private static readonly int HeightId = Shader.PropertyToID("_Height");
        private static readonly int PixelCountId = Shader.PropertyToID("_PixelCount");
        private static readonly int ChunkPixelsId = Shader.PropertyToID("_ChunkPixels");
        private static readonly int ChunkCountId = Shader.PropertyToID("_ChunkCount");
        private static readonly int ChunkBaseId = Shader.PropertyToID("_ChunkBase");
        private static readonly int ScratchStrideId = Shader.PropertyToID("_ScratchStride");
        private static readonly int WordCountId = Shader.PropertyToID("_WordCount");
        private static readonly int ThreadCountId = Shader.PropertyToID("_ThreadCount");
        private static readonly int SourceIsSRGBId = Shader.PropertyToID("_SourceIsSRGB");
        private static readonly int ForceOpaqueId = Shader.PropertyToID("_ForceOpaque");
        private static readonly int SourceId = Shader.PropertyToID("_Source");
        private static readonly int ScratchId = Shader.PropertyToID("_Scratch");
        private static readonly int ChunkSizesId = Shader.PropertyToID("_ChunkSizes");
        private static readonly int ChunkOffsetsId = Shader.PropertyToID("_ChunkOffsets");
        private static readonly int OutputId = Shader.PropertyToID("_Output");
        private static readonly int InputId = Shader.PropertyToID("_Input");
        private static readonly int InputChunkOffsetsId = Shader.PropertyToID("_InputChunkOffsets");
        private static readonly int TargetId = Shader.PropertyToID("_Target");

        private static ComputeShader LoadedShader
        {
            get
            {
                if (s_Shader != null) return s_Shader;
                s_Shader = Resources.Load<ComputeShader>("QoiCompute");
                if (s_Shader == null) return null;
                s_EncodeKernel = s_Shader.FindKernel("EncodeChunks");
                s_ScanKernel = s_Shader.FindKernel("ScanChunks");
                s_ClearKernel = s_Shader.FindKernel("ClearWords");
                s_CompactKernel = s_Shader.FindKernel("CompactChunks");
                s_DecodeKernel = s_Shader.FindKernel("DecodeChunks");
                return s_Shader;
            }
        }

        public static bool IsSupported => SystemInfo.supportsComputeShaders && LoadedShader != null;

        // Buffer pool ----------------------------------------------------------------------------------------

        /// One set of grow-only GPU buffers per operation in flight (sync calls reuse the same set).
        private sealed class Buffers
        {
            public GraphicsBuffer Scratch, Sizes, Offsets, Output, Input, InputTable;
            // Set when the GPU may still read these buffers after the call returned (decoding does not wait)
            public GraphicsFence? InFlight;

            public bool Idle => InFlight == null || InFlight.Value.passed;

            public void Release()
            {
                Scratch?.Release(); Sizes?.Release(); Offsets?.Release(); Output?.Release(); Input?.Release(); InputTable?.Release();
                Scratch = Sizes = Offsets = Output = Input = InputTable = null;
            }
        }

        private static readonly List<Buffers> s_Pool = new List<Buffers>();
        private static bool s_LockUnsupported;

        static QoiGpu()
        {
            Application.quitting += ReleaseResources;
#if UNITY_EDITOR
            UnityEditor.AssemblyReloadEvents.beforeAssemblyReload += ReleaseResources;
#endif
        }

        /// Takes a set of buffers the GPU is done with, or a new one
        private static Buffers Rent()
        {
            for (int i = s_Pool.Count - 1; i >= 0; i--)
            {
                var buffers = s_Pool[i];
                if (!buffers.Idle) continue;
                s_Pool.RemoveAt(i);
                buffers.InFlight = null;
                return buffers;
            }
            return new Buffers();
        }

        private static void Return(Buffers buffers) => s_Pool.Add(buffers);

        /// <summary>
        /// Frees the pooled GPU buffers. They are recreated on demand.
        /// </summary>
        public static void ReleaseResources()
        {
            // Releasing is safe even while in flight: Unity keeps GPU resources alive until the GPU is done
            foreach (var buffers in s_Pool) buffers.Release();
            s_Pool.Clear();
        }

        private static GraphicsBuffer Ensure(ref GraphicsBuffer buffer, GraphicsBuffer.Target target, int count,
            GraphicsBuffer.UsageFlags flags = GraphicsBuffer.UsageFlags.None)
        {
            if (buffer != null && buffer.count >= count) return buffer;
            buffer?.Release();
            // Grow by 25% more than needed so slightly larger images don't reallocate
            buffer = new GraphicsBuffer(target, flags, Math.Max(16, count + count / 4), 4);
            return buffer;
        }

        // Encoding -------------------------------------------------------------------------------------------

        /// <summary>
        /// Encodes mip 0 of a 2D texture into a seekable QOI file. Blocks until the GPU is done.
        /// </summary>
        /// <param name="channels">Defaults to RGBA when the texture format has an alpha channel.</param>
        public static byte[] Encode(Texture source, int chunkPixels = DefaultChunkPixels, QoiChannels? channels = null)
        {
            var job = new EncodeJob(source, chunkPixels, channels);
            try
            {
                var offsets = new uint[job.ChunkCount + 1];
                job.Buffers.Offsets.GetData(offsets, 0, 0, offsets.Length);
                int words = job.Compact(offsets[job.ChunkCount]);
                var request = AsyncGPUReadback.Request(job.Buffers.Output, words * 4, 0);
                request.WaitForCompletion();
                if (request.hasError) throw new InvalidOperationException("QOI GPU readback failed.");
                return job.BuildFile(request.GetData<byte>(), offsets);
            }
            finally
            {
                job.Dispose();
            }
        }

        /// <summary>
        /// Encodes mip 0 of a 2D texture into a seekable QOI file without stalling: the result is delivered
        /// a few frames later through <paramref name="onComplete"/> (null when the readback failed).
        /// </summary>
        public static void EncodeAsync(Texture source, Action<byte[]> onComplete, int chunkPixels = DefaultChunkPixels, QoiChannels? channels = null)
        {
            var job = new EncodeJob(source, chunkPixels, channels);
            AsyncGPUReadback.Request(job.Buffers.Offsets, (job.ChunkCount + 1) * 4, 0, offsetsRequest =>
            {
                if (offsetsRequest.hasError)
                {
                    job.Dispose();
                    onComplete(null);
                    return;
                }

                var offsets = offsetsRequest.GetData<uint>().ToArray();
                int words = job.Compact(offsets[job.ChunkCount]);
                AsyncGPUReadback.Request(job.Buffers.Output, words * 4, 0, outputRequest =>
                {
                    byte[] file = outputRequest.hasError ? null : job.BuildFile(outputRequest.GetData<byte>(), offsets);
                    job.Dispose();
                    onComplete(file);
                });
            });
        }

        private sealed class EncodeJob : IDisposable
        {
            public readonly int ChunkCount;
            public readonly Buffers Buffers;

            private readonly int _width, _height, _chunkPixels;
            private readonly QoiChannels _channels;
            private readonly QoiColorSpace _colorSpace;

            public EncodeJob(Texture source, int chunkPixels, QoiChannels? channels)
            {
                if (!IsSupported) throw new NotSupportedException("Compute shaders are not supported, use QoiTexture.EncodeToQOI instead.");
                if (source == null) throw new ArgumentNullException(nameof(source));
                if (source.dimension != TextureDimension.Tex2D) throw new ArgumentException("Only 2D textures can be encoded.", nameof(source));

                _width = source.width;
                _height = source.height;
                int pixelCount = _width * _height;
                // Keep the dispatch within the group count limit
                _chunkPixels = Mathf.Max(chunkPixels, 64, (pixelCount + MaxGroups * ChunkThreads - 1) / (MaxGroups * ChunkThreads));
                ChunkCount = (pixelCount + _chunkPixels - 1) / _chunkPixels;

                var format = source.graphicsFormat;
                bool isSRGB = GraphicsFormatUtility.IsSRGBFormat(format);
                _channels = channels ?? (GraphicsFormatUtility.HasAlphaChannel(format) ? QoiChannels.Rgba : QoiChannels.Rgb);
                _colorSpace = QoiTexture.GetColorSpace(source);

                // Worst case is 5 bytes per pixel (QOI_OP_RGBA)
                int scratchStride = (_chunkPixels * 5 + 3) & ~3;
                Buffers = Rent();
                var scratch = Ensure(ref Buffers.Scratch, GraphicsBuffer.Target.Raw, ChunkCount * (scratchStride / 4));
                var sizes = Ensure(ref Buffers.Sizes, GraphicsBuffer.Target.Structured, ChunkCount);
                var offsets = Ensure(ref Buffers.Offsets, GraphicsBuffer.Target.Structured, ChunkCount + 1);

                var cs = LoadedShader;
                cs.SetInt(WidthId, _width);
                cs.SetInt(HeightId, _height);
                cs.SetInt(PixelCountId, pixelCount);
                cs.SetInt(ChunkPixelsId, _chunkPixels);
                cs.SetInt(ChunkCountId, ChunkCount);
                cs.SetInt(ScratchStrideId, scratchStride);
                // sRGB textures are decoded when loaded in linear color space projects only
                cs.SetInt(SourceIsSRGBId, isSRGB && QualitySettings.activeColorSpace == ColorSpace.Linear ? 1 : 0);
                cs.SetInt(ForceOpaqueId, _channels == QoiChannels.Rgb ? 1 : 0);

                cs.SetTexture(s_EncodeKernel, SourceId, source);
                cs.SetBuffer(s_EncodeKernel, ScratchId, scratch);
                cs.SetBuffer(s_EncodeKernel, ChunkSizesId, sizes);
                cs.Dispatch(s_EncodeKernel, (ChunkCount + ChunkThreads - 1) / ChunkThreads, 1, 1);

                cs.SetBuffer(s_ScanKernel, ChunkSizesId, sizes);
                cs.SetBuffer(s_ScanKernel, ChunkOffsetsId, offsets);
                cs.Dispatch(s_ScanKernel, 1, 1, 1);
            }

            /// Gathers every chunk at its final offset once the total size is known. Returns the output size in words.
            public int Compact(uint totalBytes)
            {
                var cs = LoadedShader;
                int words = Mathf.Max(1, (int)((totalBytes + 3) / 4));
                var output = Ensure(ref Buffers.Output, GraphicsBuffer.Target.Raw, words);

                // Parameters may have been changed by another job since the constructor ran
                cs.SetInt(ChunkCountId, ChunkCount);
                cs.SetInt(ScratchStrideId, (_chunkPixels * 5 + 3) & ~3);

                int clearGroups = Mathf.Min((words + ClearThreads - 1) / ClearThreads, 4096);
                cs.SetInt(WordCountId, words);
                cs.SetInt(ThreadCountId, clearGroups * ClearThreads);
                cs.SetBuffer(s_ClearKernel, OutputId, output);
                cs.Dispatch(s_ClearKernel, clearGroups, 1, 1);

                cs.SetBuffer(s_CompactKernel, ScratchId, Buffers.Scratch);
                cs.SetBuffer(s_CompactKernel, ChunkSizesId, Buffers.Sizes);
                cs.SetBuffer(s_CompactKernel, ChunkOffsetsId, Buffers.Offsets);
                cs.SetBuffer(s_CompactKernel, OutputId, output);
                for (int chunkBase = 0; chunkBase < ChunkCount; chunkBase += MaxGroups)
                {
                    cs.SetInt(ChunkBaseId, chunkBase);
                    cs.Dispatch(s_CompactKernel, Mathf.Min(MaxGroups, ChunkCount - chunkBase), 1, 1);
                }
                return words;
            }

            public byte[] BuildFile(NativeArray<byte> ops, uint[] offsets)
            {
                int opsSize = (int)offsets[ChunkCount];
                var file = new byte[QoiCodec.HeaderSize + opsSize + QoiCodec.PaddingSize + ChunkCount * 4 + 12 + QoiCodec.PaddingSize];
                var span = file.AsSpan();

                BinaryPrimitives.WriteUInt32BigEndian(span, QoiCodec.Magic);
                BinaryPrimitives.WriteUInt32BigEndian(span.Slice(4), (uint)_width);
                BinaryPrimitives.WriteUInt32BigEndian(span.Slice(8), (uint)_height);
                file[12] = (byte)_channels;
                file[13] = (byte)_colorSpace;
                NativeArray<byte>.Copy(ops, 0, file, QoiCodec.HeaderSize, opsSize);

                int p = QoiCodec.WritePadding(span, QoiCodec.HeaderSize + opsSize);
                for (int c = 0; c < ChunkCount; c++, p += 4)
                    BinaryPrimitives.WriteUInt32BigEndian(span.Slice(p), (uint)QoiCodec.HeaderSize + offsets[c]);
                QoiCodec.WriteSeekFooter(span, p, _chunkPixels, ChunkCount);
                return file;
            }

            public void Dispose() => Return(Buffers);
        }

        // Decoding -------------------------------------------------------------------------------------------

        /// <summary>
        /// Decodes a QOI file into a GPU only render texture (R8G8B8A8, sRGB unless the file or <paramref name="linear"/>
        /// says otherwise). This is the fast path: seekable files never touch the CPU. Plain files are decoded on the CPU.
        /// </summary>
        public static RenderTexture Decode(byte[] data, bool mipChain = false, bool? linear = null)
        {
            var header = QoiCodec.ReadHeader(data);
            // Gamma color space projects sample everything as is
            bool srgb = !(linear ?? header.ColorSpace == QoiColorSpace.Linear) && QualitySettings.activeColorSpace == ColorSpace.Linear;
            var format = srgb ? GraphicsFormat.R8G8B8A8_SRGB : GraphicsFormat.R8G8B8A8_UNorm;
            var descriptor = new RenderTextureDescriptor(header.Width, header.Height, format, 0)
            {
                useMipMap = mipChain,
                autoGenerateMips = false
            };

            RenderTexture result;
            if (IsSupported && QoiCodec.TryReadSeekTable(data, header, out int chunkPixels, out int[] offsets)
                            && (offsets.Length - 1 + ChunkThreads - 1) / ChunkThreads <= MaxGroups)
            {
                if (srgb)
                {
                    // Compute shaders can't write sRGB textures: decode into a UNorm one and copy the raw bytes
                    var uav = RenderTexture.GetTemporary(new RenderTextureDescriptor(header.Width, header.Height, GraphicsFormat.R8G8B8A8_UNorm, 0)
                    {
                        enableRandomWrite = true
                    });
                    Dispatch(data, header, chunkPixels, offsets, uav);
                    result = new RenderTexture(descriptor);
                    result.Create();
                    Graphics.CopyTexture(uav, 0, 0, result, 0, 0);
                    RenderTexture.ReleaseTemporary(uav);
                }
                else
                {
                    descriptor.enableRandomWrite = true;
                    result = new RenderTexture(descriptor);
                    result.Create();
                    Dispatch(data, header, chunkPixels, offsets, result);
                }
            }
            else
            {
                var cpu = QoiTexture.LoadQOI(data, false, !srgb, makeNoLongerReadable: true);
                result = new RenderTexture(descriptor);
                result.Create();
                Graphics.Blit(cpu, result);
                DestroySafe(cpu);
            }

            if (mipChain) result.GenerateMips();
            return result;
        }

        /// <summary>
        /// Same as <see cref="Decode"/> but returns a <see cref="Texture2D"/>. Slower: Unity allocates a CPU copy for
        /// every Texture2D, prefer <see cref="Decode"/> when any <see cref="Texture"/> will do.
        /// </summary>
        public static Texture2D DecodeToTexture2D(byte[] data, bool mipChain = false, bool? linear = null)
        {
            var rt = Decode(data, mipChain, linear);
            var flags = (mipChain ? TextureCreationFlags.MipChain : TextureCreationFlags.None) | TextureCreationFlags.DontInitializePixels;
            var texture = new Texture2D(rt.width, rt.height, rt.graphicsFormat, flags);
            Graphics.CopyTexture(rt, texture);
            rt.Release();
            DestroySafe(rt);
            return texture;
        }

        private static void Dispatch(byte[] data, in QoiHeader header, int chunkPixels, int[] offsets, RenderTexture target)
        {
            var buffers = Rent();
            try
            {
                // Only the op stream is read by the shader, rounded to whole words within the file
                int uploadBytes = Math.Min(data.Length & ~3, (offsets[offsets.Length - 1] + 3) & ~3);
                Upload(buffers, data, uploadBytes);
                var table = Ensure(ref buffers.InputTable, GraphicsBuffer.Target.Structured, offsets.Length);
                table.SetData(offsets);

                var cs = LoadedShader;
                cs.SetInt(WidthId, header.Width);
                cs.SetInt(HeightId, header.Height);
                cs.SetInt(PixelCountId, header.PixelCount);
                cs.SetInt(ChunkPixelsId, chunkPixels);
                cs.SetInt(ChunkCountId, offsets.Length - 1);
                cs.SetBuffer(s_DecodeKernel, InputId, buffers.Input);
                cs.SetBuffer(s_DecodeKernel, InputChunkOffsetsId, table);
                cs.SetTexture(s_DecodeKernel, TargetId, target);
                cs.Dispatch(s_DecodeKernel, (offsets.Length - 1 + ChunkThreads - 1) / ChunkThreads, 1, 1);

                // The call returns before the GPU reads the upload buffer: keep this set out of reach until it has
                if (SystemInfo.supportsGraphicsFence)
                    buffers.InFlight = Graphics.CreateGraphicsFence(GraphicsFenceType.CPUSynchronisation, SynchronisationStageFlags.ComputeProcessing);
                else
                {
                    buffers.Input.Release();
                    buffers.Input = null;
                    buffers.InputTable.Release();
                    buffers.InputTable = null;
                }
            }
            finally
            {
                Return(buffers);
            }
        }

        /// Writes straight into GPU visible memory when the platform allows it, about 2-3x faster than SetData
        private static void Upload(Buffers buffers, byte[] data, int bytes)
        {
            if (!s_LockUnsupported)
            {
                try
                {
                    var input = Ensure(ref buffers.Input, GraphicsBuffer.Target.Raw, bytes / 4, GraphicsBuffer.UsageFlags.LockBufferForWrite);
                    var mapped = input.LockBufferForWrite<byte>(0, bytes);
                    NativeArray<byte>.Copy(data, 0, mapped, 0, bytes);
                    input.UnlockBufferAfterWrite<byte>(bytes);
                    return;
                }
                catch (Exception)
                {
                    s_LockUnsupported = true;
                    buffers.Input?.Release();
                    buffers.Input = null;
                }
            }

            Ensure(ref buffers.Input, GraphicsBuffer.Target.Raw, bytes / 4).SetData(data, 0, 0, bytes);
        }

        private static void DestroySafe(UnityEngine.Object obj)
        {
            if (Application.isPlaying) UnityEngine.Object.Destroy(obj);
            else UnityEngine.Object.DestroyImmediate(obj);
        }
    }
}
