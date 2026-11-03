using System;
using System.Diagnostics;
using System.Text;
using UnityEditor;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using Debug = UnityEngine.Debug;

namespace LTMX.QOI.Editor
{
    /// <summary>
    /// Checks the CPU codec, the texture helpers and the compute shader codec against each other, then benchmarks them.
    /// </summary>
    public static class QoiSelfTest
    {
        [MenuItem("Tools/QOI/Run Self-Test")]
        public static void Run()
        {
            var log = new StringBuilder("QOI self-test\n");
            int failures = 0;

            void Check(bool ok, string what)
            {
                log.AppendLine((ok ? "  PASS  " : "  FAIL  ") + what);
                if (!ok) failures++;
            }

            foreach (var (w, h, linear) in new[] { (333, 197, false), (256, 256, true), (64, 1, false), (1, 1, true) })
            {
                var name = $"{w}x{h} {(linear ? "linear" : "sRGB")}";
                var pixels = Pattern(w, h);
                var tex = new Texture2D(w, h, TextureFormat.RGBA32, false, linear);
                tex.SetPixelData(pixels, 0);
                tex.Apply(false, false);

                try
                {
                    // CPU: texture -> QOI -> texture
                    var qoi = tex.EncodeToQOI();
                    var loaded = QoiTexture.LoadQOI(qoi);
                    Check(ToBytes(loaded.GetPixels32()).AsSpan().SequenceEqual(pixels), $"{name}: CPU texture round trip");
                    // Gamma color space projects ignore the sRGB flag of textures
                    bool expectSRGB = !linear && QualitySettings.activeColorSpace == ColorSpace.Linear;
                    if (QualitySettings.activeColorSpace == ColorSpace.Linear)
                        Check(loaded.isDataSRGB == expectSRGB, $"{name}: color space preserved");
                    UnityEngine.Object.DestroyImmediate(loaded);

                    // CPU: non readable texture goes through the GPU readback path
                    var nonReadable = UnityEngine.Object.Instantiate(tex);
                    nonReadable.Apply(false, true);
                    Check(nonReadable.EncodeToQOI().AsSpan().SequenceEqual(qoi), $"{name}: non readable texture encodes the same bytes");
                    UnityEngine.Object.DestroyImmediate(nonReadable);

                    if (!QoiGpu.IsSupported)
                    {
                        log.AppendLine("  SKIP  compute shaders not supported");
                        continue;
                    }

                    // GPU encode must produce exactly the CPU seekable bytes
                    const int chunk = 256;
                    var colorSpace = QoiTexture.GetColorSpace(tex);
                    var cpuSeekable = QoiCodec.Encode(pixels, w, h, 4, QoiChannels.Rgba, colorSpace, flipY: true, seekChunkPixels: chunk);
                    var gpuSeekable = QoiGpu.Encode(tex, chunk, QoiChannels.Rgba);
                    Check(gpuSeekable.AsSpan().SequenceEqual(cpuSeekable), $"{name}: GPU encode == CPU seekable encode ({gpuSeekable.Length} bytes)");

                    // GPU decode, raw render texture
                    var rt = QoiGpu.Decode(cpuSeekable);
                    Check(GraphicsFormatUtility.IsSRGBFormat(rt.graphicsFormat) == expectSRGB && ReadBack(rt, !expectSRGB).AsSpan().SequenceEqual(pixels), $"{name}: GPU decode to render texture");
                    UnityEngine.Object.DestroyImmediate(rt);

                    // Back to back decodes share pooled upload buffers: both results must stay intact
                    var otherPixels = Pattern(w, h + 1);
                    var other = QoiCodec.Encode(otherPixels, w, h + 1, 4, QoiChannels.Rgba, colorSpace, flipY: true, seekChunkPixels: chunk);
                    var first = QoiGpu.Decode(cpuSeekable);
                    var second = QoiGpu.Decode(other);
                    Check(ReadBack(first, !expectSRGB).AsSpan().SequenceEqual(pixels) && ReadBack(second, !expectSRGB).AsSpan().SequenceEqual(otherPixels), $"{name}: back to back GPU decodes");
                    UnityEngine.Object.DestroyImmediate(first);
                    UnityEngine.Object.DestroyImmediate(second);

                    // GPU decode, final texture (copied with the file's color space)
                    var gpuTex = QoiGpu.DecodeToTexture2D(cpuSeekable);
                    Check(GraphicsFormatUtility.IsSRGBFormat(gpuTex.graphicsFormat) == expectSRGB && ReadBack(gpuTex, !expectSRGB).AsSpan().SequenceEqual(pixels), $"{name}: GPU decode to Texture2D");
                    UnityEngine.Object.DestroyImmediate(gpuTex);
                }
                catch (Exception e)
                {
                    Check(false, $"{name}: {e.GetType().Name} {e.Message}");
                }
                finally
                {
                    UnityEngine.Object.DestroyImmediate(tex);
                }
            }

            Benchmark(log);
            log.Insert(0, failures == 0 ? "[OK] " : $"[{failures} FAILURES] ");
            if (failures == 0) Debug.Log(log);
            else Debug.LogError(log);
        }

        private static void Benchmark(StringBuilder log)
        {
            const int size = 4096;
            var pixels = Pattern(size, size);
            var tex = new Texture2D(size, size, TextureFormat.RGBA32, false);
            tex.SetPixelData(pixels, 0);
            tex.Apply(false, false);
            try
            {
                log.AppendLine($"Benchmark {size}x{size} RGBA ({SystemInfo.processorCount} cores, {SystemInfo.graphicsDeviceName})");

                byte[] plain = null, seekable = null, gpuBytes = null;
                log.AppendLine($"  CPU encode            {Median(() => plain = QoiCodec.Encode(pixels, size, size, 4, QoiChannels.Rgba, flipY: true)),6:F1} ms  {plain.Length / 1048576f:F1} MB");
                log.AppendLine($"  CPU seekable encode   {Median(() => seekable = QoiCodec.Encode(pixels, size, size, 4, QoiChannels.Rgba, flipY: true, seekChunkPixels: QoiCodec.DefaultChunkPixels)),6:F1} ms  {seekable.Length / 1048576f:F1} MB");
                log.AppendLine($"  CPU decode            {Median(() => QoiCodec.Decode(plain, 4, true, parallel: false)),6:F1} ms");
                log.AppendLine($"  CPU parallel decode   {Median(() => QoiCodec.Decode(seekable, 4, true)),6:F1} ms");

                if (!QoiGpu.IsSupported) return;

                log.AppendLine($"  GPU encode            {Median(() => gpuBytes = QoiGpu.Encode(tex)),6:F1} ms  {gpuBytes.Length / 1048576f:F1} MB (incl. readback)");
                var gpuSeekable = QoiCodec.Encode(pixels, size, size, 4, QoiChannels.Rgba, flipY: true, seekChunkPixels: QoiGpu.DefaultChunkPixels);
                double decode = Median(() =>
                {
                    var rt = QoiGpu.Decode(gpuSeekable);
                    ReadBack(rt, false, 1); // wait for the GPU
                    UnityEngine.Object.DestroyImmediate(rt);
                });
                log.AppendLine($"  GPU decode            {decode,6:F1} ms  (incl. upload)");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(tex);
            }
        }

        /// Median of 5 runs after a warm up run
        private static double Median(Action action)
        {
            action();
            var times = new double[5];
            for (int i = 0; i < times.Length; i++)
            {
                var sw = Stopwatch.StartNew();
                action();
                times[i] = sw.Elapsed.TotalMilliseconds;
            }
            Array.Sort(times);
            return times[times.Length / 2];
        }

        /// Deterministic pattern exercising every QOI op: runs, index hits, small and large diffs, alpha changes
        private static byte[] Pattern(int w, int h)
        {
            var random = new System.Random(w * 7919 + h);
            var px = new byte[w * h * 4];
            for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int o = (y * w + x) * 4;
                int zone = (x / 32 + y / 32) % 4;
                if (zone == 0) { px[o] = (byte)x; px[o + 1] = (byte)y; px[o + 2] = (byte)(x + y); px[o + 3] = 255; }
                else if (zone == 1) { px[o] = px[o + 1] = px[o + 2] = (byte)(y / 8 * 40); px[o + 3] = 255; }
                else if (zone == 2) { int c = random.Next(6); px[o] = (byte)(c * 50); px[o + 1] = (byte)(c * 90); px[o + 2] = 30; px[o + 3] = (byte)(c * 51); }
                else { px[o] = (byte)random.Next(256); px[o + 1] = (byte)random.Next(256); px[o + 2] = (byte)random.Next(256); px[o + 3] = (byte)random.Next(256); }
            }
            return px;
        }

        private static byte[] ToBytes(Color32[] colors)
        {
            var bytes = new byte[colors.Length * 4];
            for (int i = 0; i < colors.Length; i++)
            {
                bytes[i * 4] = colors[i].r;
                bytes[i * 4 + 1] = colors[i].g;
                bytes[i * 4 + 2] = colors[i].b;
                bytes[i * 4 + 3] = colors[i].a;
            }
            return bytes;
        }

        /// Raw RGBA32 bytes of a GPU texture, without any color conversion
        private static byte[] ReadBack(Texture texture, bool linear, int size = 0)
        {
            int w = size > 0 ? size : texture.width, h = size > 0 ? size : texture.height;
            var desc = new RenderTextureDescriptor(texture.width, texture.height, linear ? GraphicsFormat.R8G8B8A8_UNorm : GraphicsFormat.R8G8B8A8_SRGB, 0);
            var rt = RenderTexture.GetTemporary(desc);
            Graphics.CopyTexture(texture, 0, 0, rt, 0, 0);
            var copy = new Texture2D(w, h, TextureFormat.RGBA32, false, linear);
            var previous = RenderTexture.active;
            RenderTexture.active = rt;
            copy.ReadPixels(new Rect(0, 0, w, h), 0, 0, false);
            RenderTexture.active = previous;
            RenderTexture.ReleaseTemporary(rt);
            var bytes = copy.GetRawTextureData<byte>().ToArray();
            UnityEngine.Object.DestroyImmediate(copy);
            return bytes;
        }
    }
}
