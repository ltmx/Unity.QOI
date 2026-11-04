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
    /// readable by any QOI decoder for 8 bit RGB / RGBA files. HDR (half / float) and single channel files use
    /// the extensions described in <see cref="QoiCodec"/>.
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
        private const int ComposeThreads = 8;
        private const int MaxGroups = 65535;

        private static ComputeShader s_Shader;
        private static int s_EncodeKernel, s_ScanKernel, s_ClearKernel, s_DecodeKernel;
        private static int s_DecodePlanarKernel, s_ClearScratchKernel, s_ComposeKernel;

        private static readonly int WidthId = Shader.PropertyToID("_Width");
        private static readonly int HeightId = Shader.PropertyToID("_Height");
        private static readonly int PixelCountId = Shader.PropertyToID("_PixelCount");
        private static readonly int StreamPixelsId = Shader.PropertyToID("_StreamPixels");
        private static readonly int PlanesId = Shader.PropertyToID("_Planes");
        private static readonly int SampleFormatId = Shader.PropertyToID("_SampleFormat");
        private static readonly int ChannelsId = Shader.PropertyToID("_Channels");
        private static readonly int ClearWordCountId = Shader.PropertyToID("_ClearWordCount");
        private static readonly int ChunkPixelsId = Shader.PropertyToID("_ChunkPixels");
        private static readonly int ChunkCountId = Shader.PropertyToID("_ChunkCount");
        private static readonly int ThreadCountId = Shader.PropertyToID("_ThreadCount");
        private static readonly int WriteOpsId = Shader.PropertyToID("_WriteOps");
        private static readonly int SourceIsSRGBId = Shader.PropertyToID("_SourceIsSRGB");
        private static readonly int ForceOpaqueId = Shader.PropertyToID("_ForceOpaque");
        private static readonly int SourceId = Shader.PropertyToID("_Source");
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
                s_DecodeKernel = s_Shader.FindKernel("DecodeChunks");
                s_DecodePlanarKernel = s_Shader.FindKernel("DecodeChunksPlanar");
                s_ClearScratchKernel = s_Shader.FindKernel("ClearScratch");
                s_ComposeKernel = s_Shader.FindKernel("ComposePlanar");
                return s_Shader;
            }
        }

        public static bool IsSupported => SystemInfo.supportsComputeShaders && LoadedShader != null;

        // Buffer pool ----------------------------------------------------------------------------------------

        /// One set of grow-only GPU buffers per operation in flight (sync calls reuse the same set).
        private sealed class Buffers
        {
            public GraphicsBuffer Sizes, Offsets, Output, Input, InputTable;
            // Set when the GPU may still read these buffers after the call returned (decoding does not wait)
            public GraphicsFence? InFlight;

            public bool Idle => InFlight == null || InFlight.Value.passed;

            public void Release()
            {
                Sizes?.Release(); Offsets?.Release(); Output?.Release(); Input?.Release(); InputTable?.Release();
                Sizes = Offsets = Output = Input = InputTable = null;
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
        /// <param name="channels">Defaults to <see cref="QoiChannels.R"/> for single channel formats, RGBA when the texture format
        /// has an alpha channel, RGB otherwise.</param>
        /// <param name="sampleFormat">Defaults to <see cref="QoiTexture.GetSampleFormat"/>: half / float for HDR textures.</param>
        public static byte[] Encode(Texture source, int chunkPixels = DefaultChunkPixels, QoiChannels? channels = null,
            QoiSampleFormat? sampleFormat = null)
        {
            var job = new EncodeJob(source, chunkPixels, channels, sampleFormat);
            try
            {
                var offsets = new uint[job.ChunkCount + 1];
                job.Buffers.Offsets.GetData(offsets, 0, 0, offsets.Length);
                var file = job.BuildFile(offsets);
                // Straight from the GPU buffer into the file, no intermediate copy. Reads have to be whole words:
                // the up to 3 extra bytes land in the end marker, written again right after.
                int opsSize = (int)offsets[job.ChunkCount];
                job.Buffers.Output.GetData(file, QoiCodec.HeaderSize, 0, (opsSize + 3) & ~3);
                QoiCodec.WritePadding(file, QoiCodec.HeaderSize + opsSize);
                return file;
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
        public static void EncodeAsync(Texture source, Action<byte[]> onComplete, int chunkPixels = DefaultChunkPixels,
            QoiChannels? channels = null, QoiSampleFormat? sampleFormat = null)
        {
            var job = new EncodeJob(source, chunkPixels, channels, sampleFormat);
            AsyncGPUReadback.Request(job.Buffers.Offsets, (job.ChunkCount + 1) * 4, 0, offsetsRequest =>
            {
                if (offsetsRequest.hasError)
                {
                    job.Dispose();
                    onComplete(null);
                    return;
                }

                var offsets = offsetsRequest.GetData<uint>().ToArray();
                int opsSize = (int)offsets[job.ChunkCount];
                AsyncGPUReadback.Request(job.Buffers.Output, Math.Max(4, (opsSize + 3) & ~3), 0, outputRequest =>
                {
                    byte[] file = null;
                    if (!outputRequest.hasError)
                    {
                        file = job.BuildFile(offsets);
                        NativeArray<byte>.Copy(outputRequest.GetData<byte>(), 0, file, QoiCodec.HeaderSize, opsSize);
                    }
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
            private readonly QoiHeader _header;

            public EncodeJob(Texture source, int chunkPixels, QoiChannels? channels, QoiSampleFormat? sampleFormat)
            {
                if (!IsSupported) throw new NotSupportedException("Compute shaders are not supported, use QoiTexture.EncodeToQOI instead.");
                if (source == null) throw new ArgumentNullException(nameof(source));
                if (source.dimension != TextureDimension.Tex2D) throw new ArgumentException("Only 2D textures can be encoded.", nameof(source));

                _width = source.width;
                _height = source.height;
                var format = source.graphicsFormat;
                var samples = sampleFormat ?? QoiTexture.GetSampleFormat(source);
                var resolvedChannels = channels ?? (QoiTexture.IsSingleChannel(format) ? QoiChannels.R
                    : GraphicsFormatUtility.HasAlphaChannel(format) ? QoiChannels.Rgba : QoiChannels.Rgb);
                _header = new QoiHeader(_width, _height, resolvedChannels, QoiTexture.GetColorSpace(source, samples), samples);

                int pixelCount = _width * _height;
                if ((long)pixelCount * _header.BytesPerSample > QoiCodec.MaxPixels) throw new QoiException("Image too large to encode.");
                int streamPixels = _header.StreamPixelCount;
                // Keep the dispatch within the group count limit
                _chunkPixels = Mathf.Max(chunkPixels, 64, (streamPixels + MaxGroups * ChunkThreads - 1) / (MaxGroups * ChunkThreads));
                ChunkCount = (streamPixels + _chunkPixels - 1) / _chunkPixels;
                bool isSRGB = GraphicsFormatUtility.IsSRGBFormat(format);

                Buffers = Rent();
                var sizes = Ensure(ref Buffers.Sizes, GraphicsBuffer.Target.Structured, ChunkCount);
                var offsets = Ensure(ref Buffers.Offsets, GraphicsBuffer.Target.Structured, ChunkCount + 1);
                // Worst case: 5 bytes per pixel (QOI_OP_RGBA) plus one byte for the RGBA op starting each chunk
                long worstBytes = (long)streamPixels * 5 + ChunkCount;
                int worstWords = (int)((worstBytes + 3) / 4);
                var output = Ensure(ref Buffers.Output, GraphicsBuffer.Target.Raw, worstWords);

                var cs = LoadedShader;
                SetFormat(cs, _header);
                cs.SetInt(ChunkPixelsId, _chunkPixels);
                cs.SetInt(ChunkCountId, ChunkCount);
                // sRGB textures are decoded when loaded in linear color space projects only
                cs.SetInt(SourceIsSRGBId, isSRGB && QualitySettings.activeColorSpace == ColorSpace.Linear ? 1 : 0);
                cs.SetInt(ForceOpaqueId, _header.Channels == QoiChannels.Rgb ? 1 : 0);

                cs.SetTexture(s_EncodeKernel, SourceId, source);
                cs.SetBuffer(s_EncodeKernel, ChunkSizesId, sizes);
                cs.SetBuffer(s_EncodeKernel, ChunkOffsetsId, offsets);
                cs.SetBuffer(s_EncodeKernel, OutputId, output);
                int encodeGroups = (ChunkCount + ChunkThreads - 1) / ChunkThreads;

                // 1. Size of every chunk
                cs.SetInt(WriteOpsId, 0);
                cs.Dispatch(s_EncodeKernel, encodeGroups, 1, 1);

                // 2. Byte offset of every chunk
                cs.SetBuffer(s_ScanKernel, ChunkSizesId, sizes);
                cs.SetBuffer(s_ScanKernel, ChunkOffsetsId, offsets);
                cs.Dispatch(s_ScanKernel, 1, 1, 1);

                // 3. Zero the output (chunk boundaries are merged with atomic ORs), the shader reads the size from the scan
                int clearGroups = Mathf.Min((worstWords + ClearThreads - 1) / ClearThreads, 4096);
                cs.SetInt(ThreadCountId, clearGroups * ClearThreads);
                cs.SetBuffer(s_ClearKernel, ChunkOffsetsId, offsets);
                cs.SetBuffer(s_ClearKernel, OutputId, output);
                cs.Dispatch(s_ClearKernel, clearGroups, 1, 1);

                // 4. Every chunk writes its ops at its final offset. Encoding twice is cheaper than compacting scratch copies.
                cs.SetInt(WriteOpsId, 1);
                cs.Dispatch(s_EncodeKernel, encodeGroups, 1, 1);
            }

            /// File with everything but the ops, which the caller copies at <see cref="QoiCodec.HeaderSize"/>
            public byte[] BuildFile(uint[] offsets)
            {
                int opsSize = (int)offsets[ChunkCount];
                var file = new byte[QoiCodec.HeaderSize + opsSize + QoiCodec.PaddingSize + ChunkCount * 4 + 12 + QoiCodec.PaddingSize];
                var span = file.AsSpan();
                QoiCodec.WriteHeader(span, _header);

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
        /// Decodes a QOI file into a GPU only render texture. This is the fast path: seekable files never touch the CPU.
        /// Plain files are decoded on the CPU. The format is <see cref="QoiTexture.GetGraphicsFormat"/>: R8G8B8A8 (sRGB unless the
        /// file or <paramref name="linear"/> says otherwise), R8 for single channel files, R16 / R16G16B16A16 SFloat for half files,
        /// R32 / R32G32B32A32 SFloat for float files.
        /// </summary>
        public static RenderTexture Decode(byte[] data, bool mipChain = false, bool? linear = null)
        {
            var header = QoiCodec.ReadHeader(data);
            // Gamma color space projects sample everything as is
            bool srgb = !(linear ?? header.ColorSpace == QoiColorSpace.Linear) && QualitySettings.activeColorSpace == ColorSpace.Linear;
            var format = QoiTexture.GetGraphicsFormat(header, srgb);
            srgb = GraphicsFormatUtility.IsSRGBFormat(format);
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
                    var uav = RenderTexture.GetTemporary(new RenderTextureDescriptor(header.Width, header.Height, GraphicsFormatUtility.GetLinearFormat(format), 0)
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
        /// Decodes a seekable QOI file into an existing render texture, without allocating anything: the fastest way to
        /// stream images. <paramref name="target"/> must be a random write texture of the image size, in the format given by
        /// <c>QoiTexture.GetGraphicsFormat(header, srgb: false)</c>: R8G8B8A8_UNorm for 8 bit RGB / RGBA files (it receives the
        /// raw bytes, sRGB values are not converted), R8_UNorm for single channel files, R16 / R16G16B16A16 / R32 / R32G32B32A32
        /// SFloat for HDR files.
        /// Returns false, leaving <paramref name="target"/> untouched, for plain QOI files or without compute shaders.
        /// </summary>
        public static bool TryDecodeInto(byte[] data, RenderTexture target)
        {
            var header = QoiCodec.ReadHeader(data);
            if (target == null) throw new ArgumentNullException(nameof(target));
            var expected = QoiTexture.GetGraphicsFormat(header, false);
            if (target.width != header.Width || target.height != header.Height || !target.enableRandomWrite
                || target.graphicsFormat != expected)
                throw new ArgumentException($"The target must be an {expected} random write texture of the image size.", nameof(target));

            if (!IsSupported || !QoiCodec.TryReadSeekTable(data, header, out int chunkPixels, out int[] offsets)
                || (offsets.Length - 1 + ChunkThreads - 1) / ChunkThreads > MaxGroups)
                return false;

            if (!target.IsCreated()) target.Create();
            Dispatch(data, header, chunkPixels, offsets, target);
            return true;
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
                SetFormat(cs, header);
                cs.SetInt(ChunkPixelsId, chunkPixels);
                cs.SetInt(ChunkCountId, offsets.Length - 1);
                int decodeGroups = (offsets.Length - 1 + ChunkThreads - 1) / ChunkThreads;

                if (header.BytesPerSample == 1)
                {
                    cs.SetBuffer(s_DecodeKernel, InputId, buffers.Input);
                    cs.SetBuffer(s_DecodeKernel, InputChunkOffsetsId, table);
                    cs.SetTexture(s_DecodeKernel, TargetId, target);
                    cs.Dispatch(s_DecodeKernel, decodeGroups, 1, 1);
                }
                else
                {
                    // Byte planes are decoded by different threads: samples are assembled in a zeroed scratch buffer first
                    int words = header.PixelCount * (int)header.Channels;
                    var scratch = Ensure(ref buffers.Output, GraphicsBuffer.Target.Raw, words);
                    int clearGroups = Mathf.Min((words + ClearThreads - 1) / ClearThreads, 4096);
                    cs.SetInt(ThreadCountId, clearGroups * ClearThreads);
                    cs.SetInt(ClearWordCountId, words);
                    cs.SetBuffer(s_ClearScratchKernel, OutputId, scratch);
                    cs.Dispatch(s_ClearScratchKernel, clearGroups, 1, 1);

                    cs.SetBuffer(s_DecodePlanarKernel, InputId, buffers.Input);
                    cs.SetBuffer(s_DecodePlanarKernel, InputChunkOffsetsId, table);
                    cs.SetBuffer(s_DecodePlanarKernel, OutputId, scratch);
                    cs.Dispatch(s_DecodePlanarKernel, decodeGroups, 1, 1);

                    cs.SetBuffer(s_ComposeKernel, OutputId, scratch);
                    cs.SetTexture(s_ComposeKernel, TargetId, target);
                    cs.Dispatch(s_ComposeKernel, (header.Width + ComposeThreads - 1) / ComposeThreads,
                        (header.Height + ComposeThreads - 1) / ComposeThreads, 1);
                }

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

        private static void SetFormat(ComputeShader cs, in QoiHeader header)
        {
            cs.SetInt(WidthId, header.Width);
            cs.SetInt(HeightId, header.Height);
            cs.SetInt(PixelCountId, header.PixelCount);
            cs.SetInt(StreamPixelsId, header.StreamPixelCount);
            cs.SetInt(PlanesId, header.BytesPerSample);
            cs.SetInt(SampleFormatId, (int)header.SampleFormat);
            cs.SetInt(ChannelsId, (int)header.Channels);
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
