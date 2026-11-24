using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using UnityEditor;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using LTMX.TextureCodecKit;
using Debug = UnityEngine.Debug;

namespace LTMX.QOI.Editor
{
	/// Checks the CPU codec, the texture helpers and the compute shader codec against each other, then benchmarks them.
	public static class QoiSelfTest
	{
		[MenuItem("Tools/QOI/Run Self-Test", false, 20)]
		public static void Run()
		{
			StringBuilder log = new();
			int failures = 0;

			void Check(bool ok, string what)
			{
				log.AppendLine((ok ? "  PASS  " : "  FAIL  ") + what);
				if (!ok) failures++;
			}

			foreach ((int w, int h, bool linear) in new[] { (333, 197, false), (256, 256, true), (64, 1, false), (1, 1, true) })
			{
				string name = $"{w}x{h} {(linear ? "linear" : "sRGB")}";
				byte[] pixels = Pattern(w, h);
				Texture2D tex = new(w, h, TextureFormat.RGBA32, false, linear);
				tex.SetPixelData(pixels, 0);
				tex.Apply(false, false);

				try
				{
					// CPU: texture -> QOI -> texture
					byte[] qoi = tex.EncodeToQOI();
					Texture2D loaded = QoiTexture.LoadQOI(qoi);
					Check(ToBytes(loaded.GetPixels32()).AsSpan().SequenceEqual(pixels), $"{name}: CPU texture round trip");
					// Gamma color space projects ignore the sRGB flag of textures
					bool expectSRGB = !linear && QualitySettings.activeColorSpace == ColorSpace.Linear;
					if (QualitySettings.activeColorSpace == ColorSpace.Linear)
						Check(loaded.isDataSRGB == expectSRGB, $"{name}: color space preserved");
					UnityEngine.Object.DestroyImmediate(loaded);

					// CPU: non readable texture goes through the GPU readback path
					Texture2D nonReadable = UnityEngine.Object.Instantiate(tex);
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
					QoiColorSpace colorSpace = QoiTexture.GetColorSpace(tex);
					byte[] cpuSeekable = QoiCodec.Encode(pixels, w, h, 4, QoiChannels.Rgba, colorSpace, flipY: true, seekChunkPixels: chunk);
					byte[] gpuSeekable = QoiGpu.Encode(tex, chunk, QoiChannels.Rgba);
					Check(gpuSeekable.AsSpan().SequenceEqual(cpuSeekable), $"{name}: GPU encode == CPU seekable encode ({gpuSeekable.Length} bytes)");

					// GPU decode, raw render texture (forceGpu: small images PreferGpu=False)
					RenderTexture rt = QoiGpu.Decode(cpuSeekable, forceGpu: true);
					Check(GraphicsFormatUtility.IsSRGBFormat(rt.graphicsFormat) == expectSRGB && ReadBack(rt, !expectSRGB).AsSpan().SequenceEqual(pixels), $"{name}: GPU decode to render texture");
					UnityEngine.Object.DestroyImmediate(rt);

					// Back to back decodes share pooled upload buffers: both results must stay intact
					byte[] otherPixels = Pattern(w, h + 1);
					byte[] other = QoiCodec.Encode(otherPixels, w, h + 1, 4, QoiChannels.Rgba, colorSpace, flipY: true, seekChunkPixels: chunk);
					RenderTexture first = QoiGpu.Decode(cpuSeekable, forceGpu: true);
					RenderTexture second = QoiGpu.Decode(other, forceGpu: true);
					Check(ReadBack(first, !expectSRGB).AsSpan().SequenceEqual(pixels) && ReadBack(second, !expectSRGB).AsSpan().SequenceEqual(otherPixels), $"{name}: back to back GPU decodes");
					UnityEngine.Object.DestroyImmediate(first);
					UnityEngine.Object.DestroyImmediate(second);

					// GPU decode, final texture (copied with the file's color space)
					Texture2D gpuTex = QoiGpu.DecodeToTexture2D(cpuSeekable);
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

			// Extensions: single channel and HDR textures
			foreach (GraphicsFormat format in new[] { GraphicsFormat.R8_UNorm, GraphicsFormat.R16_SFloat, GraphicsFormat.R16G16B16A16_SFloat,
						 GraphicsFormat.R32_SFloat, GraphicsFormat.R32G32B32A32_SFloat })
			{
				foreach ((int w, int h) in new[] { (333, 197), (64, 1) })
				{
					string name = $"{w}x{h} {format}";
					int components = (int)GraphicsFormatUtility.GetComponentCount(format);
					QoiSampleFormat sampleFormat = QoiSampleFormat.Byte;
					if (GraphicsFormatUtility.IsHalfFormat(format)) sampleFormat = QoiSampleFormat.Half;
					else if (GraphicsFormatUtility.IsIEEE754Format(format)) sampleFormat = QoiSampleFormat.Float;
					QoiChannels channels = components == 1 ? QoiChannels.R : QoiChannels.Rgba;
					byte[] raw = ExtendedPattern(w, h, components, sampleFormat);
					Texture2D tex = new(w, h, format, TextureCreationFlags.None);
					tex.SetPixelData(raw, 0);
					tex.Apply(false, false);

					try
					{
						byte[] qoi = tex.EncodeToQOI();
						QoiHeader header = QoiCodec.ReadHeader(qoi);
						// The CPU encoder drops alpha when every pixel is opaque (the 64x1 pattern is)
						QoiChannels expectedChannels = channels == QoiChannels.Rgba && !QoiTexture.HasTransparency(raw, sampleFormat) ? QoiChannels.Rgb : channels;
						bool headerOk = header.Channels == expectedChannels && header.SampleFormat == sampleFormat;
						bool texOk = QoiTexture.GetSampleFormat(tex) == sampleFormat;
						Check(headerOk && texOk, $"{name}: encoded as {header.Channels} {header.SampleFormat}");

						Texture2D loaded = QoiTexture.LoadQOI(qoi);
						Check(loaded.graphicsFormat == format && loaded.GetRawTextureData<byte>().AsSpan().SequenceEqual(raw), $"{name}: CPU texture round trip");
						UnityEngine.Object.DestroyImmediate(loaded);

						Texture2D reused = new(2, 2, TextureFormat.RGBA32, false);
						reused.LoadQOI(qoi);
						Check(reused.graphicsFormat == format && reused.GetRawTextureData<byte>().AsSpan().SequenceEqual(raw), $"{name}: LoadQOI into existing texture");
						UnityEngine.Object.DestroyImmediate(reused);

						Texture2D nonReadable = UnityEngine.Object.Instantiate(tex);
						nonReadable.Apply(false, true);
						Check(nonReadable.EncodeToQOI().AsSpan().SequenceEqual(qoi), $"{name}: non readable texture encodes the same bytes");
						UnityEngine.Object.DestroyImmediate(nonReadable);

						if (!QoiGpu.IsSupported) continue;

						const int chunk = 256;
						byte[] cpuSeekable = QoiCodec.Encode(raw, w, h, components, channels, QoiColorSpace.Linear, flipY: true, chunk, sampleFormat);
						byte[] gpuSeekable = QoiGpu.Encode(tex, chunk);
						Check(gpuSeekable.AsSpan().SequenceEqual(cpuSeekable), $"{name}: GPU encode == CPU seekable encode ({gpuSeekable.Length} bytes)");

						RenderTexture rt = QoiGpu.Decode(cpuSeekable, forceGpu: true);
						Check(rt.graphicsFormat == format && ReadBackRaw(rt).AsSpan().SequenceEqual(raw), $"{name}: GPU decode to render texture");
						UnityEngine.Object.DestroyImmediate(rt);

						RenderTexture target = new(new RenderTextureDescriptor(w, h, format, 0) { enableRandomWrite = true });
						target.Create();
						Check(QoiGpu.TryDecodeInto(cpuSeekable, target, forceGpu: true) && ReadBackRaw(target).AsSpan().SequenceEqual(raw), $"{name}: GPU decode into existing render texture");
						target.Release();
						UnityEngine.Object.DestroyImmediate(target);

						Texture2D gpuTex = QoiGpu.DecodeToTexture2D(cpuSeekable);
						Check(gpuTex.graphicsFormat == format && ReadBackRaw(gpuTex).AsSpan().SequenceEqual(raw), $"{name}: GPU decode to Texture2D");
						UnityEngine.Object.DestroyImmediate(gpuTex);

						// Plain files go through the CPU decoder
						RenderTexture plainRt = QoiGpu.Decode(qoi);
						Check(plainRt.graphicsFormat == format && ReadBackRaw(plainRt).AsSpan().SequenceEqual(raw), $"{name}: GPU decode of a plain file (CPU fallback)");
						UnityEngine.Object.DestroyImmediate(plainRt);
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
			}

			Benchmark(log);
			log.Insert(0, failures == 0 ? "QOI self-test passed.\n" : $"QOI self-test failed: {failures}\n");
			if (failures != 0)
			{
				Debug.LogError(log);
				return;
			}
			foreach (string line in log.ToString().Split('\n'))
				if (line.Length > 0) Debug.Log(line);
		}

		private static void Benchmark(StringBuilder log)
		{
			const int size = 4096;
			byte[] pixels = Pattern(size, size);
			Texture2D tex = new(size, size, TextureFormat.RGBA32, false);
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
				byte[] gpuSeekable = QoiCodec.Encode(pixels, size, size, 4, QoiChannels.Rgba, flipY: true, seekChunkPixels: QoiGpu.DefaultChunkPixels);
				double decode = CodecBench.MedianOf(5, () => CodecBench.TimeGpuReadback(() => QoiGpu.Decode(gpuSeekable, forceGpu: true)));
				log.AppendLine($"  GPU decode            {decode,6:F1} ms  (force GPU, incl. readback)");

				foreach (int n in new[] { 1024, 2048, 4096 })
				{
					byte[] px = n == size ? pixels : Pattern(n, n);
					byte[] seek = QoiCodec.Encode(px, n, n, 4, QoiChannels.Rgba, flipY: true, seekChunkPixels: QoiGpu.DefaultChunkPixels);
					bool prefer = QoiGpu.PreferGpu(n, n);
					double hybrid = CodecBench.MedianOf(5, () => CodecBench.TimeHybridRt(() => QoiGpu.Decode(seek), prefer));
					log.AppendLine($"  {n}² hybrid RT         {hybrid,6:F1} ms  (PreferGpu={prefer}, fence sync if GPU)");
				}
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
			double[] times = new double[5];
			for (int i = 0; i < times.Length; i++)
			{
				Stopwatch sw = Stopwatch.StartNew();
				action();
				times[i] = sw.Elapsed.TotalMilliseconds;
			}
			Array.Sort(times);
			return times[times.Length / 2];
		}

		/// Deterministic pattern exercising every QOI op: runs, index hits, small and large diffs, alpha changes
		private static byte[] Pattern(int w, int h)
		{
			System.Random random = new(w * 7919 + h);
			byte[] px = new byte[w * h * 4];
			for (int y = 0; y < h; y++)
			{
				for (int x = 0; x < w; x++)
				{
					int o = (y * w + x) * 4;
					int zone = (x / 32 + y / 32) % 4;
					if (zone == 0) { px[o] = (byte)x; px[o + 1] = (byte)y; px[o + 2] = (byte)(x + y); px[o + 3] = 255; }
					else if (zone == 1) { px[o] = px[o + 1] = px[o + 2] = (byte)(y / 8 * 40); px[o + 3] = 255; }
					else if (zone == 2) { int c = random.Next(6); px[o] = (byte)(c * 50); px[o + 1] = (byte)(c * 90); px[o + 2] = 30; px[o + 3] = (byte)(c * 51); }
					else { px[o] = (byte)random.Next(256); px[o + 1] = (byte)random.Next(256); px[o + 2] = (byte)random.Next(256); px[o + 3] = (byte)random.Next(256); }
				}
			}

			return px;
		}

		/// Raw texture data with <paramref name="components"/> samples per pixel: gradients, flat areas, a small palette and
		/// noise, negative and large HDR values, varying alpha. No NaN or denormals, which GPUs don't always preserve.
		private static byte[] ExtendedPattern(int w, int h, int components, QoiSampleFormat format)
		{
			System.Random random = new(w * 7919 + h + components);
			int bytesPerSample = QoiCodec.BytesPerSample(format);
			byte[] bytes = new byte[w * h * components * bytesPerSample];
			Span<ushort> halfs = MemoryMarshal.Cast<byte, ushort>(bytes.AsSpan());
			Span<float> floats = MemoryMarshal.Cast<byte, float>(bytes.AsSpan());
			for (int y = 0; y < h; y++)
			{
				for (int x = 0; x < w; x++)
				{
					for (int c = 0; c < components; c++)
					{
						int i = (y * w + x) * components + c;
						int zone = (x / 32 + y / 32) % 4;
						float v = zone switch
						{
							0 => x * 0.01f + y * 0.02f + c,
							1 => y / 8 * 0.75f,
							2 => random.Next(6) * 0.5f,
							_ => (float)(random.NextDouble() * 100 - 30)
						};
						if (c == 3 && zone < 2) v = 1;
						switch (format)
						{
							case QoiSampleFormat.Half: halfs[i] = Mathf.FloatToHalf(v); break;
							case QoiSampleFormat.Float: floats[i] = v; break;
							default: bytes[i] = zone == 3 ? (byte)random.Next(256) : (byte)((int)(v * 40) & 255); break;
						}
					}
				}
			}

			return bytes;
		}

		/// Raw data of a GPU texture in its own format
		private static byte[] ReadBackRaw(Texture texture)
		{
			AsyncGPUReadbackRequest request = AsyncGPUReadback.Request(texture, 0);
			request.WaitForCompletion();
			return request.hasError ? Array.Empty<byte>() : request.GetData<byte>().ToArray();
		}

		private static byte[] ToBytes(Color32[] colors)
		{
			byte[] bytes = new byte[colors.Length * 4];
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
			RenderTextureDescriptor desc = new(texture.width, texture.height, linear ? GraphicsFormat.R8G8B8A8_UNorm : GraphicsFormat.R8G8B8A8_SRGB, 0);
			RenderTexture rt = RenderTexture.GetTemporary(desc);
			Graphics.CopyTexture(texture, 0, 0, rt, 0, 0);
			Texture2D copy = new(w, h, TextureFormat.RGBA32, false, linear);
			RenderTexture previous = RenderTexture.active;
			RenderTexture.active = rt;
			copy.ReadPixels(new Rect(0, 0, w, h), 0, 0, false);
			RenderTexture.active = previous;
			RenderTexture.ReleaseTemporary(rt);
			byte[] bytes = copy.GetRawTextureData<byte>().ToArray();
			UnityEngine.Object.DestroyImmediate(copy);
			return bytes;
		}
	}
}
