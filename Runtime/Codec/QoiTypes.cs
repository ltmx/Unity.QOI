using System;

namespace LTMX.QOI
{
    /// <summary>
    /// Number of channels stored in a QOI file.
    /// </summary>
    public enum QoiChannels : byte
    {
        Rgb = 3,
        Rgba = 4
    }

    /// <summary>
    /// Color space stored in a QOI file (informative only, it does not change how pixels are encoded).
    /// </summary>
    public enum QoiColorSpace : byte
    {
        /// <summary> sRGB color channels with a linear alpha channel. </summary>
        SRgb = 0,

        /// <summary> All channels are linear. </summary>
        Linear = 1
    }

    /// <summary>
    /// The 14 byte header of a QOI file.
    /// </summary>
    public readonly struct QoiHeader
    {
        public readonly int Width;
        public readonly int Height;
        public readonly QoiChannels Channels;
        public readonly QoiColorSpace ColorSpace;

        public QoiHeader(int width, int height, QoiChannels channels, QoiColorSpace colorSpace)
        {
            Width = width;
            Height = height;
            Channels = channels;
            ColorSpace = colorSpace;
        }

        public int PixelCount => Width * Height;

        public override string ToString() => $"{Width}x{Height} {Channels} {ColorSpace}";
    }

    /// <summary>
    /// Decoded QOI image. <see cref="Pixels"/> holds <see cref="PixelChannels"/> bytes per pixel.
    /// </summary>
    public sealed class QoiImage
    {
        public readonly QoiHeader Header;
        public readonly byte[] Pixels;
        public readonly int PixelChannels;

        public QoiImage(QoiHeader header, byte[] pixels, int pixelChannels)
        {
            Header = header;
            Pixels = pixels;
            PixelChannels = pixelChannels;
        }

        public int Width => Header.Width;
        public int Height => Header.Height;
        public QoiChannels Channels => Header.Channels;
        public QoiColorSpace ColorSpace => Header.ColorSpace;
    }

    public class QoiException : Exception
    {
        public QoiException(string message) : base(message) { }
        public QoiException(string message, Exception inner) : base(message, inner) { }
    }
}
