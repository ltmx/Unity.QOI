using System;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Unity.Collections.LowLevel.Unsafe;
using UnityEditor;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using BindingFlags = System.Reflection.BindingFlags;
using static UnityEngine.TextureFormat;

namespace LTMX.QOI.Editor
{
	/// The importer's <see cref="QoiCompressor.Fast"/> compressor: mipmaps and block compression of 8-bit images, Burst
	/// compiled (<see cref="QoiBlockEncoder"/>) and spread over every core. Writes the blocks straight into the texture's
	/// memory, without <see cref="UnityEditor.EditorUtility.CompressTexture(Texture2D, TextureFormat, TextureCompressionQuality)"/>.
	internal static unsafe class QoiFastCompressor
	{
		// Source rows per parallel band (1 << BandShift): every level down to BandShift - 2 is built and encoded in the
		// band's task. Small enough to balance cores and stay in cache, large enough to keep small levels in bands.
		private const int BandShift = 5, BandRows = 1 << BandShift;

		// Block rows per task of the levels encoded after the bands
		private const int TailRowsPerTask = 4;

		// Pixels per task of the RGBA32 expansion
		private const int ExpandPixelsPerTask = 1 << 16;

		private static ushort[] _toLinear;
		private static byte[] _toSrgb;

		/// Formats the fast compressor writes. Others fall back to Unity's compressor.
		public static bool Supports(TextureFormat format) =>
			format is DXT1 or DXT5 or BC4 or BC5 or BC7 or ASTC_4x4 or ASTC_5x5 or ASTC_6x6 or ASTC_8x8 or ASTC_10x10 or ASTC_12x12;

		// Codes past BC7: ASTC, ASTC + block size
		private const int Astc = 100;

		private static int BlockSize(int code) => code >= Astc ? code - Astc : 4;

		/// Full mip chain length, down to 1x1.
		public static int MipCount(int width, int height) => 1 + (int)Math.Floor(Math.Log(Math.Max(width, height), 2));

		public static Texture2D CreateTexture2D(byte[] pixels, int channels, int width, int height, TextureFormat format,
			bool mipmaps, bool linear, bool normalMap, bool alphaIsTransparency = false,
			TextureCompressionQuality quality = TextureCompressionQuality.Normal)
		{
			int mipCount = mipmaps ? MipCount(width, height) : 1;
			// TextureFormat ctor: accepts formats the editor GPU can't sample (ASTC on desktop), GraphicsFormat one throws
			Texture2D tex = new(width, height, format, mipCount, linear, true);
			IntPtr[] blocks = new IntPtr[mipCount];
			for (int m = 0; m < mipCount; m++) blocks[m] = TextureMemory(tex.GetPixelData<byte>(m), LevelBytes(width, height, m, Code(format)));
			int code = Code(format);
			int mipMode = MipMode(linear, normalMap);
			int profile = Profile(alphaIsTransparency, quality, normalMap);
			WithRgba32(pixels, channels, width * height, rgba =>
				Compress(rgba, width, height, code, mipCount, mipMode, profile, blocks));
			// No Apply: the importer serializes the CPU data and the asset reloads from it. Uploading would make Unity
			// decode ASTC on the CPU on desktop, for a GPU copy nobody uses.
			return tex;
		}

		/// <paramref name="faces"/>: the 6 faces in <see cref="CubemapFace"/> order.
		public static Cubemap CreateCubemap(byte[][] faces, int channels, int size, TextureFormat format, bool mipmaps, bool linear,
			TextureCompressionQuality quality = TextureCompressionQuality.Normal)
		{
			int mipCount = mipmaps ? MipCount(size, size) : 1;
			// GraphicsFormat ctor throws on formats the editor GPU can't sample (ASTC on desktop): TextureFormat one then
			GraphicsFormat graphicsFormat = GetGraphicsFormat(format, linear);
			TextureCreationFlags flags = mipCount > 1 ? TextureCreationFlags.MipChain : TextureCreationFlags.None;
			Cubemap cube = SystemInfo.IsFormatSupported(graphicsFormat, GraphicsFormatUsage.Sample)
				? new Cubemap(size, graphicsFormat, flags, mipCount)
				: CreateUnvalidatedCubemap(size, format, graphicsFormat, mipCount);
			IntPtr[] blocks = new IntPtr[mipCount];
			int code = Code(format);
			int mipMode = MipMode(linear, false);
			int profile = Profile(false, quality, false);
			for (int f = 0; f < 6; f++)
			{
				for (int m = 0; m < mipCount; m++)
					blocks[m] = TextureMemory(cube.GetPixelData<byte>(m, (CubemapFace)f), LevelBytes(size, size, m, Code(format)));
				WithRgba32(faces[f], channels, size * size, rgba =>
					Compress(rgba, size, size, code, mipCount, mipMode, profile, blocks));
			}
			return cube; // No Apply, as for Texture2D
		}

		private static readonly System.Reflection.MethodInfo CubemapCreate = FindCubemapCreate();

		private static System.Reflection.MethodInfo FindCubemapCreate()
		{
			BindingFlags flags = BindingFlags.Static | BindingFlags.NonPublic;
			return typeof(Cubemap).GetMethod("Internal_Create", flags);
		}

		// No public Cubemap ctor takes TextureFormat + sRGB: call the native create the public ctors use, minus the format check.
		// Falls back to the TextureFormat ctor (sRGB lost) if Unity renames it.
		private static Cubemap CreateUnvalidatedCubemap(int size, TextureFormat format, GraphicsFormat graphicsFormat, int mipCount)
		{
			TextureCreationFlags flags = mipCount > 1 ? TextureCreationFlags.MipChain : TextureCreationFlags.None;
			if (CubemapCreate != null && CubemapCreate.GetParameters().Length == 7)
			{
				Cubemap cube = (Cubemap)System.Runtime.Serialization.FormatterServices.GetUninitializedObject(typeof(Cubemap));
				Type spaceType = CubemapCreate.GetParameters()[4].ParameterType; // internal TextureColorSpace
				string spaceName = GraphicsFormatUtility.IsSRGBFormat(graphicsFormat) ? "sRGB" : "Linear";
				object space = Enum.Parse(spaceType, spaceName);
				CubemapCreate.Invoke(null, new object[] { cube, size, mipCount, graphicsFormat, space, flags, IntPtr.Zero });
				return cube;
			}
			return new Cubemap(size, format, mipCount, true);
		}

		/// Block compressed data of every mip, from RGBA32 pixels.
		public static byte[][] Compress(byte[] rgba, int width, int height, TextureFormat format, bool mipmaps, int mipMode, bool alphaIsTransparency = false,
			TextureCompressionQuality quality = TextureCompressionQuality.Normal)
		{
			int code = Code(format);
			int mipCount = mipmaps ? MipCount(width, height) : 1;
			byte[][] result = new byte[mipCount][];
			GCHandle[] handles = new GCHandle[mipCount + 1];
			try
			{
				IntPtr[] blocks = new IntPtr[mipCount];
				for (int m = 0; m < mipCount; m++)
				{
					result[m] = new byte[LevelBytes(width, height, m, code)];
					handles[m] = GCHandle.Alloc(result[m], GCHandleType.Pinned);
					blocks[m] = handles[m].AddrOfPinnedObject();
				}
				handles[mipCount] = GCHandle.Alloc(rgba, GCHandleType.Pinned);
				Compress(handles[mipCount].AddrOfPinnedObject(), width, height, code, mipCount, mipMode, Profile(alphaIsTransparency, quality, mipMode == QoiBlockEncoder.MipNormal), blocks);
			}
			finally
			{
				foreach (GCHandle handle in handles)
					if (handle.IsAllocated) handle.Free();
			}
			return result;
		}

		// Writable pointer to a texture level's CPU copy (valid until the texture is applied or changed)
		private static IntPtr TextureMemory(Unity.Collections.NativeArray<byte> data, long bytes)
		{
			if (data.Length != bytes)
				throw new InvalidOperationException($"Texture level holds {data.Length} bytes, the compressor writes {bytes}");
			return (IntPtr)NativeArrayUnsafeUtility.GetUnsafePtr(data);
		}

		private static int Code(TextureFormat format) => format switch
		{
			DXT1 => QoiBlockEncoder.BC1,
			DXT5 => QoiBlockEncoder.BC3,
			BC4 => QoiBlockEncoder.BC4,
			BC5 => QoiBlockEncoder.BC5,
			BC7 => QoiBlockEncoder.BC7,
			ASTC_4x4 => Astc + 4,
			ASTC_5x5 => Astc + 5,
			ASTC_6x6 => Astc + 6,
			ASTC_8x8 => Astc + 8,
			ASTC_10x10 => Astc + 10,
			ASTC_12x12 => Astc + 12,
			_ => throw new ArgumentOutOfRangeException(nameof(format), format, "Not supported by the fast compressor")
		};

		// BC4 and BC5 have no sRGB variant
		private static GraphicsFormat GetGraphicsFormat(TextureFormat format, bool linear) =>
			GraphicsFormatUtility.GetGraphicsFormat(format, !linear && format is not (BC4 or BC5));

		// Content and effort hints of the ASTC GPU encoder
		private static int Profile(bool alphaIsTransparency, TextureCompressionQuality quality, bool normalMap) =>
			(alphaIsTransparency ? QoiAstcGpu.ProfileTransparency : 0) | (normalMap ? QoiAstcGpu.ProfileNormal : 0) |
			(quality == TextureCompressionQuality.Best ? QoiAstcGpu.ProfileBest : quality == TextureCompressionQuality.Fast ? QoiAstcGpu.ProfileFast : 0);

		private static int MipMode(bool linear, bool normalMap) =>
			normalMap ? QoiBlockEncoder.MipNormal : linear ? QoiBlockEncoder.MipLinear : QoiBlockEncoder.MipSrgb;

		private static long LevelBytes(int width, int height, int level, int code)
		{
			int b = BlockSize(code);
			return (long)((Math.Max(1, width >> level) + b - 1) / b) * ((Math.Max(1, height >> level) + b - 1) / b) *
				   (code >= Astc ? 16 : QoiBlockEncoder.BlockBytes(code));
		}

		// Block rows [from, to) of a level
		private static void EncodeRows(IntPtr pixels, int width, int height, int code, IntPtr blocks, int from, int to)
		{
			if (code >= Astc)
				QoiAstcEncoder.EncodeRows((byte*)pixels, width, height, (IntPtr)QoiAstcEncoder.GetTables(code - Astc, code - Astc), (byte*)blocks, from, to);
			else QoiBlockEncoder.EncodeBlockRows((byte*)pixels, width, height, code, (byte*)blocks, from, to);
		}

		/// Runs <paramref name="body"/> on RGBA32 pixels: <paramref name="pixels"/> itself when it has 4 channels, else a
		/// temporary expansion (RGB gets an opaque alpha, a single channel is replicated to RGB).
		private static void WithRgba32(byte[] pixels, int channels, int count, Action<IntPtr> body)
		{
			fixed (byte* src = pixels)
			{
				if (channels == 4)
				{
					body((IntPtr)src);
					return;
				}

				IntPtr rgba = Marshal.AllocHGlobal((IntPtr)((long)count * 4));
				try
				{
					IntPtr s = (IntPtr)src;
					int tasks = (count + ExpandPixelsPerTask - 1) / ExpandPixelsPerTask;
					Parallel.For(0, tasks, t =>
					{
						long start = (long)t * ExpandPixelsPerTask;
						long end = Math.Min(count, start + ExpandPixelsPerTask);
						QoiBlockEncoder.ExpandToRgba((byte*)s, channels, (byte*)rgba, start, end);
					});
					body(rgba);
				}
				finally
				{
					Marshal.FreeHGlobal(rgba);
				}
			}
		}

		/// Builds the mip chain of RGBA32 pixels and writes every level's blocks to <paramref name="blocks"/>
		/// (<see cref="LevelBytes"/> each).
		private static void Compress(IntPtr rgba, int width, int height, int code, int mipCount, int mipMode, int profile, IntPtr[] blocks)
		{
			if (mipMode == QoiBlockEncoder.MipSrgb) BuildTables();
			int blockSize = BlockSize(code);
			if (code >= Astc) QoiAstcEncoder.GetTables(blockSize, blockSize);

			// ASTC on the GPU when it can (main thread, graphics device): the CPU only builds the mip levels
			bool gpu = code >= Astc && QoiAstcGpu.Available(blockSize);

			int[] widths = new int[mipCount];
			int[] heights = new int[mipCount];
			long pixelBytes = 0;
			for (int m = 0; m < mipCount; m++)
			{
				widths[m] = Math.Max(1, width >> m);
				heights[m] = Math.Max(1, height >> m);
				if (m > 0) pixelBytes += (long)widths[m] * heights[m] * 4;
			}

			// GPU: level 0 encodes while the CPU builds the others
			QoiAstcGpu.Job job = gpu ? QoiAstcGpu.Begin(rgba, widths, heights, blockSize, profile) : null;

			// Pixels of every level: the source, then the downsampled levels in one unmanaged buffer
			IntPtr[] pixels = new IntPtr[mipCount];
			pixels[0] = rgba;
			IntPtr mipPixels = pixelBytes > 0 ? Marshal.AllocHGlobal((IntPtr)pixelBytes) : IntPtr.Zero;
			try
			{
				long offset = 0;
				for (int m = 1; m < mipCount; m++)
				{
					pixels[m] = (IntPtr)((byte*)mipPixels + offset);
					offset += (long)widths[m] * heights[m] * 4;
				}

				fixed (ushort* toLinear = _toLinear)
				fixed (byte* toSrgb = _toSrgb)
				{
					IntPtr lin = (IntPtr)toLinear;
					IntPtr srgb = (IntPtr)toSrgb;

					void Down(int m, int r0, int r1)
					{
						byte* src = (byte*)pixels[m - 1];
						byte* dst = (byte*)pixels[m];
						int sw = widths[m - 1], sh = heights[m - 1];
						ushort* tl = (ushort*)lin;
						byte* ts = (byte*)srgb;
						QoiBlockEncoder.DownsampleRows(src, sw, sh, dst, widths[m], mipMode, tl, ts, r0, r1);
					}

					// Bands of BandRows source rows build and encode their part of every level they hold whole rows of,
					// in one task while the rows are in cache: level m gets BandRows >> m rows of each band. A level
					// reads only its own band's rows of the level above (2y and 2y + 1, clamped to the last row), so
					// bands never wait on each other. Levels with less than 4 rows per band are encoded after.
					int bandLevels = Math.Min(mipCount, BandShift + 1);
					int encodeLevels = blockSize == 4 && !gpu ? Math.Min(mipCount, BandShift - 1) : 0;
					int bands = (height + BandRows - 1) >> BandShift;
					void Band(int band)
					{
						for (int m = 0; m < bandLevels; m++)
						{
							int rowStart = (band << BandShift) >> m;
							int rowEnd = Math.Min(heights[m], ((band + 1) << BandShift) >> m);
							if (rowStart >= rowEnd) break;
							if (m > 0)
							{
								Down(m, rowStart, rowEnd);
							}

							if (m < encodeLevels)
								EncodeRows(pixels[m], widths[m], heights[m], code, blocks[m], rowStart >> 2, (rowEnd + 3) >> 2);
						}
					}

					if (bands > 1 && Environment.ProcessorCount > 1) Parallel.For(0, bands, Band);
					else for (int b = 0; b < bands; b++) Band(b);

					// Remaining small levels, at most height / BandRows rows each
					for (int m = encodeLevels; m < mipCount; m++)
					{
						if (m >= bandLevels)
						{
							Down(m, 0, heights[m]);
						}

						if (gpu) continue;
						IntPtr p = pixels[m];
						IntPtr b = blocks[m];
						int lw = widths[m], lh = heights[m];
						ForRows((lh + blockSize - 1) / blockSize, (from, to) => EncodeRows(p, lw, lh, code, b, from, to));
					}
					if (gpu)
					{
						job.Finish(pixels, blocks);
						job = null;
					}
				}
			}
			finally
			{
				job?.Release();
				if (mipPixels != IntPtr.Zero) Marshal.FreeHGlobal(mipPixels);
			}
		}

		private static void ForRows(int rows, Action<int, int> body)
		{
			int tasks = (rows + TailRowsPerTask - 1) / TailRowsPerTask;
			if (tasks <= 1 || Environment.ProcessorCount < 2) body(0, rows);
			else Parallel.For(0, tasks, t => body(t * TailRowsPerTask, Math.Min(rows, (t + 1) * TailRowsPerTask)));
		}

		private static void BuildTables()
		{
			if (_toSrgb != null) return;
			ushort[] toLinear = new ushort[256];
			for (int i = 0; i < 256; i++) toLinear[i] = (ushort)Math.Round(Mathf.GammaToLinearSpace(i / 255f) * 65535);
			byte[] toSrgb = new byte[65536];
			for (int i = 0; i < 65536; i++)
				toSrgb[i] = (byte)Math.Clamp((int)(Mathf.LinearToGammaSpace(i / 65535f) * 255 + 0.5f), 0, 255);
			_toLinear = toLinear;
			_toSrgb = toSrgb;
		}
	}
}
