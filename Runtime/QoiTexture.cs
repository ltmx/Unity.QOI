using System;
using System.Runtime.InteropServices;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;

namespace LTMX.QOI
{
    /// <summary>
    /// Conversions between <see cref="Texture"/> and QOI files (CPU). See <see cref="QoiGpu"/> for the compute shader path.
    /// </summary>
    public static class QoiTexture
    {
        /// <summary>
        /// Encodes mip 0 of any texture (readable or not, compressed or not) to QOI.
        /// </summary>
        /// <param name="seekChunkPixels">0 for a plain QOI file, otherwise writes a seekable file (see <see cref="QoiCodec"/>).</param>
        /// <param name="sampleFormat">Defaults to <see cref="GetSampleFormat"/>: half / float for HDR textures, bytes otherwise.</param>
        /// <param name="channels">Defaults to <see cref="QoiChannels.R"/> for single channel textures, otherwise RGBA only when
        /// at least one pixel is not fully opaque.</param>
        public static byte[] EncodeToQOI(this Texture texture, int seekChunkPixels = 0, QoiSampleFormat? sampleFormat = null,
            QoiChannels? channels = null)
        {
            var format = sampleFormat ?? GetSampleFormat(texture);
            var colorSpace = GetColorSpace(texture, format);
            byte[] pixels = format == QoiSampleFormat.Byte ? ReadPixels32(texture, out _) : ReadPixelsHdr(texture, format);
            var resolved = channels ?? (IsSingleChannel(texture.graphicsFormat) ? QoiChannels.R
                : HasTransparency(pixels, format) ? QoiChannels.Rgba : QoiChannels.Rgb);
            return QoiCodec.Encode(pixels, texture.width, texture.height, 4, resolved, colorSpace, flipY: true, seekChunkPixels, format);
        }

        /// <summary>
        /// Color space to write in the header of a QOI file made from <paramref name="texture"/>.
        /// Gamma color space projects never create sRGB textures, their color data is assumed to be sRGB.
        /// </summary>
        public static QoiColorSpace GetColorSpace(Texture texture) =>
            GraphicsFormatUtility.IsSRGBFormat(texture.graphicsFormat) || QualitySettings.activeColorSpace == ColorSpace.Gamma
                ? QoiColorSpace.SRgb
                : QoiColorSpace.Linear;

        /// Same as <see cref="GetColorSpace(Texture)"/>, HDR and single channel data is always linear
        public static QoiColorSpace GetColorSpace(Texture texture, QoiSampleFormat sampleFormat) =>
            sampleFormat != QoiSampleFormat.Byte || IsSingleChannel(texture.graphicsFormat) ? QoiColorSpace.Linear : GetColorSpace(texture);

        /// <summary>
        /// Sample format that stores a texture without loss: <see cref="QoiSampleFormat.Half"/> for half float and HDR
        /// compressed / packed formats, <see cref="QoiSampleFormat.Float"/> for formats with more than 8 bits per channel
        /// (32 bit floats, 16 bit normalized...), <see cref="QoiSampleFormat.Byte"/> otherwise.
        /// </summary>
        public static QoiSampleFormat GetSampleFormat(Texture texture)
        {
            var f = texture.graphicsFormat;
            if (GraphicsFormatUtility.IsCompressedFormat(f))
                return f is GraphicsFormat.RGB_BC6H_SFloat or GraphicsFormat.RGB_BC6H_UFloat ? QoiSampleFormat.Half : QoiSampleFormat.Byte;
            if (GraphicsFormatUtility.IsPackedFormat(f))
                return GraphicsFormatUtility.IsIEEE754Format(f) ? QoiSampleFormat.Half : QoiSampleFormat.Byte;
            if (GraphicsFormatUtility.IsHalfFormat(f))
                return QoiSampleFormat.Half;
            uint components = GraphicsFormatUtility.GetComponentCount(f);
            if (components == 0) return QoiSampleFormat.Byte;
            return GraphicsFormatUtility.GetBlockSize(f) / components > 1 ? QoiSampleFormat.Float : QoiSampleFormat.Byte;
        }

        /// True for formats with a single color channel (R8, R16, RHalf, RFloat, BC4...). Alpha only formats are not.
        public static bool IsSingleChannel(GraphicsFormat format) =>
            GraphicsFormatUtility.GetComponentCount(format) == 1 && !GraphicsFormatUtility.HasAlphaChannel(format);

        /// <summary>
        /// Texture format matching the samples of a QOI file: R8G8B8A8 (sRGB when <paramref name="srgb"/>) for 8 bit RGB / RGBA,
        /// R8_UNorm for single channel, R16 / R16G16B16A16 SFloat for half and R32 / R32G32B32A32 SFloat for float files.
        /// RGB files use the RGBA format (opaque alpha).
        /// </summary>
        public static GraphicsFormat GetGraphicsFormat(in QoiHeader header, bool srgb)
        {
            bool single = header.Channels == QoiChannels.R;
            return header.SampleFormat switch
            {
                QoiSampleFormat.Half => single ? GraphicsFormat.R16_SFloat : GraphicsFormat.R16G16B16A16_SFloat,
                QoiSampleFormat.Float => single ? GraphicsFormat.R32_SFloat : GraphicsFormat.R32G32B32A32_SFloat,
                _ => single ? GraphicsFormat.R8_UNorm : srgb ? GraphicsFormat.R8G8B8A8_SRGB : GraphicsFormat.R8G8B8A8_UNorm
            };
        }

        /// <summary>
        /// Decodes a QOI file into a new <see cref="Texture2D"/> (uploaded to the GPU): RGBA32 or RGB24 for 8 bit files,
        /// R8 for single channel files, RHalf / RGBAHalf / RFloat / RGBAFloat for HDR files.
        /// </summary>
        /// <param name="linear">Overrides the color space stored in the file (8 bit RGB / RGBA files only).</param>
        public static Texture2D LoadQOI(byte[] data, bool mipChain = false, bool? linear = null, bool makeNoLongerReadable = false)
        {
            var header = QoiCodec.ReadHeader(data);
            bool rgb24 = header.IsStandard && header.Channels == QoiChannels.Rgb;
            var image = QoiCodec.Decode(data, rgb24 ? 3 : OutputChannels(header), flipY: true);
            bool isLinear = !header.IsStandard || (linear ?? image.ColorSpace == QoiColorSpace.Linear);
            var format = rgb24 ? TextureFormat.RGB24 : GetTextureFormat(header);
            var texture = new Texture2D(image.Width, image.Height, format, mipChain, isLinear);
            texture.SetPixelData(image.Pixels, 0);
            texture.Apply(mipChain, makeNoLongerReadable);
            return texture;
        }

        /// <summary>
        /// Decodes a QOI file into an existing texture, resizing it and changing its format, like <see cref="ImageConversion.LoadImage(Texture2D,byte[])"/>.
        /// </summary>
        public static void LoadQOI(this Texture2D texture, byte[] data, bool makeNoLongerReadable = false)
        {
            var header = QoiCodec.ReadHeader(data);
            var image = QoiCodec.Decode(data, OutputChannels(header), flipY: true);
            var format = GetGraphicsFormat(header, header.ColorSpace == QoiColorSpace.SRgb);
            bool hasMips = texture.mipmapCount > 1;
            texture.Reinitialize(image.Width, image.Height, format, hasMips);
            texture.SetPixelData(image.Pixels, 0);
            texture.Apply(hasMips, makeNoLongerReadable);
        }

        /// Samples per pixel of the textures made from a QOI file: 1 for single channel files, 4 otherwise
        public static int OutputChannels(in QoiHeader header) => header.Channels == QoiChannels.R ? 1 : 4;

        private static TextureFormat GetTextureFormat(in QoiHeader header)
        {
            bool single = header.Channels == QoiChannels.R;
            return header.SampleFormat switch
            {
                QoiSampleFormat.Half => single ? TextureFormat.RHalf : TextureFormat.RGBAHalf,
                QoiSampleFormat.Float => single ? TextureFormat.RFloat : TextureFormat.RGBAFloat,
                _ => single ? TextureFormat.R8 : TextureFormat.RGBA32
            };
        }

        /// <summary>
        /// Reads mip 0 of a texture as tightly packed RGBA32 bytes, bottom row first.
        /// Uses the CPU copy when the texture is readable and a GPU blit + readback otherwise.
        /// </summary>
        public static byte[] ReadPixels32(Texture texture, out bool isSRGB)
        {
            if (texture == null) throw new ArgumentNullException(nameof(texture));
            isSRGB = GraphicsFormatUtility.IsSRGBFormat(texture.graphicsFormat);

            if (texture is Texture2D t2d && t2d.isReadable)
            {
                var colors = t2d.GetPixels32(0);
                var bytes = new byte[colors.Length * 4];
                for (int i = 0, o = 0; i < colors.Length; i++, o += 4)
                {
                    var c = colors[i];
                    bytes[o] = c.r;
                    bytes[o + 1] = c.g;
                    bytes[o + 2] = c.b;
                    bytes[o + 3] = c.a;
                }
                return bytes;
            }

            // Blitting into a render target with the same color space keeps the stored values unchanged
            var readWrite = isSRGB ? RenderTextureReadWrite.sRGB : RenderTextureReadWrite.Linear;
            var rt = RenderTexture.GetTemporary(texture.width, texture.height, 0, RenderTextureFormat.ARGB32, readWrite);
            var readback = new Texture2D(texture.width, texture.height, TextureFormat.RGBA32, false, !isSRGB);
            var previous = RenderTexture.active;
            try
            {
                Graphics.Blit(texture, rt);
                RenderTexture.active = rt;
                readback.ReadPixels(new Rect(0, 0, texture.width, texture.height), 0, 0, false);
                // View on the texture memory, owned by the texture: must not be disposed
                return readback.GetRawTextureData<byte>().ToArray();
            }
            finally
            {
                RenderTexture.active = previous;
                RenderTexture.ReleaseTemporary(rt);
                UnityEngine.Object.DestroyImmediate(readback);
            }
        }

        /// <summary>
        /// Reads mip 0 of a texture as tightly packed RGBA half (raw bits) or float samples, bottom row first.
        /// Uses the CPU copy when the texture is readable and a GPU blit + readback otherwise. Values are not clamped.
        /// </summary>
        public static byte[] ReadPixelsHdr(Texture texture, QoiSampleFormat format)
        {
            if (texture == null) throw new ArgumentNullException(nameof(texture));
            if (format == QoiSampleFormat.Byte) throw new ArgumentException("Use ReadPixels32 for 8 bit samples.", nameof(format));
            int count = texture.width * texture.height * 4;

            if (texture is Texture2D t2d && t2d.isReadable)
            {
                var colors = t2d.GetPixels(0);
                var bytes = new byte[count * QoiCodec.BytesPerSample(format)];
                if (format == QoiSampleFormat.Float)
                {
                    var floats = MemoryMarshal.Cast<byte, Color>(bytes.AsSpan());
                    colors.AsSpan().CopyTo(floats);
                }
                else
                {
                    var halfs = MemoryMarshal.Cast<byte, ushort>(bytes.AsSpan());
                    for (int i = 0, o = 0; i < colors.Length; i++, o += 4)
                    {
                        var c = colors[i];
                        halfs[o] = Mathf.FloatToHalf(c.r);
                        halfs[o + 1] = Mathf.FloatToHalf(c.g);
                        halfs[o + 2] = Mathf.FloatToHalf(c.b);
                        halfs[o + 3] = Mathf.FloatToHalf(c.a);
                    }
                }
                return bytes;
            }

            var rtFormat = format == QoiSampleFormat.Half ? GraphicsFormat.R16G16B16A16_SFloat : GraphicsFormat.R32G32B32A32_SFloat;
            var rt = RenderTexture.GetTemporary(new RenderTextureDescriptor(texture.width, texture.height, rtFormat, 0));
            try
            {
                Graphics.Blit(texture, rt);
                var request = AsyncGPUReadback.Request(rt, 0);
                request.WaitForCompletion();
                if (request.hasError) throw new QoiException("GPU readback failed.");
                return request.GetData<byte>().ToArray();
            }
            finally
            {
                RenderTexture.ReleaseTemporary(rt);
            }
        }

        /// <summary>
        /// True when any pixel of an RGBA32 buffer has an alpha below 255.
        /// </summary>
        public static bool HasTransparency(ReadOnlySpan<byte> rgba32)
        {
            for (int i = 3; i < rgba32.Length; i += 4)
                if (rgba32[i] != 255) return true;
            return false;
        }

        /// <summary>
        /// True when any pixel of an RGBA buffer of <paramref name="format"/> samples has an alpha that is not exactly 1.
        /// </summary>
        public static bool HasTransparency(ReadOnlySpan<byte> rgba, QoiSampleFormat format)
        {
            switch (format)
            {
                case QoiSampleFormat.Half:
                    var halfs = MemoryMarshal.Cast<byte, ushort>(rgba);
                    for (int i = 3; i < halfs.Length; i += 4)
                        if (halfs[i] != 0x3C00) return true;
                    return false;
                case QoiSampleFormat.Float:
                    var floats = MemoryMarshal.Cast<byte, float>(rgba);
                    for (int i = 3; i < floats.Length; i += 4)
                        if (floats[i] != 1f) return true;
                    return false;
                default:
                    return HasTransparency(rgba);
            }
        }
    }
}
