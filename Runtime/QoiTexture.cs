using System;
using UnityEngine;
using UnityEngine.Experimental.Rendering;

namespace LTMX.QOI
{
    /// <summary>
    /// Conversions between <see cref="Texture"/> and QOI files (CPU). See <see cref="QoiGpu"/> for the compute shader path.
    /// </summary>
    public static class QoiTexture
    {
        /// <summary>
        /// Encodes mip 0 of any texture (readable or not, compressed or not) to QOI.
        /// Channels are RGBA only when at least one pixel is not fully opaque.
        /// </summary>
        /// <param name="seekChunkPixels">0 for a plain QOI file, otherwise writes a seekable file (see <see cref="QoiCodec"/>).</param>
        public static byte[] EncodeToQOI(this Texture texture, int seekChunkPixels = 0)
        {
            var pixels = ReadPixels32(texture, out _);
            var channels = HasTransparency(pixels) ? QoiChannels.Rgba : QoiChannels.Rgb;
            var colorSpace = GetColorSpace(texture);
            return QoiCodec.Encode(pixels, texture.width, texture.height, 4, channels, colorSpace, flipY: true, seekChunkPixels);
        }

        /// <summary>
        /// Color space to write in the header of a QOI file made from <paramref name="texture"/>.
        /// Gamma color space projects never create sRGB textures, their color data is assumed to be sRGB.
        /// </summary>
        public static QoiColorSpace GetColorSpace(Texture texture) =>
            GraphicsFormatUtility.IsSRGBFormat(texture.graphicsFormat) || QualitySettings.activeColorSpace == ColorSpace.Gamma
                ? QoiColorSpace.SRgb
                : QoiColorSpace.Linear;

        /// <summary>
        /// Decodes a QOI file into a new <see cref="Texture2D"/> (RGBA32 or RGB24, uploaded to the GPU).
        /// </summary>
        /// <param name="linear">Overrides the color space stored in the file.</param>
        public static Texture2D LoadQOI(byte[] data, bool mipChain = false, bool? linear = null, bool makeNoLongerReadable = false)
        {
            var image = QoiCodec.Decode(data, 0, flipY: true);
            var format = image.Channels == QoiChannels.Rgba ? TextureFormat.RGBA32 : TextureFormat.RGB24;
            var texture = new Texture2D(image.Width, image.Height, format, mipChain, linear ?? image.ColorSpace == QoiColorSpace.Linear);
            texture.SetPixelData(image.Pixels, 0);
            texture.Apply(mipChain, makeNoLongerReadable);
            return texture;
        }

        /// <summary>
        /// Decodes a QOI file into an existing texture, resizing it, like <see cref="ImageConversion.LoadImage(Texture2D,byte[])"/>.
        /// </summary>
        public static void LoadQOI(this Texture2D texture, byte[] data, bool makeNoLongerReadable = false)
        {
            var image = QoiCodec.Decode(data, 4, flipY: true);
            var format = GraphicsFormatUtility.GetGraphicsFormat(TextureFormat.RGBA32, image.ColorSpace == QoiColorSpace.SRgb);
            bool hasMips = texture.mipmapCount > 1;
            texture.Reinitialize(image.Width, image.Height, format, hasMips);
            texture.SetPixelData(image.Pixels, 0);
            texture.Apply(hasMips, makeNoLongerReadable);
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
        /// True when any pixel of an RGBA32 buffer has an alpha below 255.
        /// </summary>
        public static bool HasTransparency(ReadOnlySpan<byte> rgba32)
        {
            for (int i = 3; i < rgba32.Length; i += 4)
                if (rgba32[i] != 255) return true;
            return false;
        }
    }
}
