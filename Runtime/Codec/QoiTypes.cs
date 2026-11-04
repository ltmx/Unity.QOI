using System;
using System.Runtime.InteropServices;

namespace LTMX.QOI
{
    /// <summary>
    /// Number of channels stored in a QOI file. <see cref="R"/> is an extension of the QOI format.
    /// </summary>
    public enum QoiChannels : byte
    {
        /// <summary> Single channel (masks, height maps, R8 / RHalf / RFloat textures). Extension. </summary>
        R = 1,
        Rgb = 3,
        Rgba = 4
    }

    /// <summary>
    /// Sample type of every channel. Only <see cref="Byte"/> is standard QOI, the others are extensions
    /// (see <see cref="QoiCodec"/>).
    /// </summary>
    public enum QoiSampleFormat : byte
    {
        /// <summary> 8 bit unsigned normalized, standard QOI. </summary>
        Byte = 0,

        /// <summary> 16 bit IEEE half float (HDR), stored as raw bits. </summary>
        Half = 1,

        /// <summary> 32 bit IEEE float (HDR), stored as raw bits. </summary>
        Float = 2
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
        public readonly QoiSampleFormat SampleFormat;

        public QoiHeader(int width, int height, QoiChannels channels, QoiColorSpace colorSpace, QoiSampleFormat sampleFormat = QoiSampleFormat.Byte)
        {
            Width = width;
            Height = height;
            Channels = channels;
            ColorSpace = colorSpace;
            SampleFormat = sampleFormat;
        }

        public int PixelCount => Width * Height;

        /// Bytes per channel, also the number of byte planes in the op stream
        public int BytesPerSample => QoiCodec.BytesPerSample(SampleFormat);

        /// Number of pixels in the op stream: one per pixel per byte plane
        public int StreamPixelCount => PixelCount * BytesPerSample;

        /// True for files any QOI decoder can read (8 bit RGB / RGBA)
        public bool IsStandard => SampleFormat == QoiSampleFormat.Byte && Channels != QoiChannels.R;

        public bool IsHdr => SampleFormat != QoiSampleFormat.Byte;

        public override string ToString() => $"{Width}x{Height} {Channels} {SampleFormat} {ColorSpace}";
    }

    /// <summary>
    /// Decoded QOI image. <see cref="Pixels"/> holds <see cref="PixelChannels"/> samples per pixel, each sample
    /// being <see cref="BytesPerSample"/> bytes long (little endian half / float bits for HDR files).
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
        public QoiSampleFormat SampleFormat => Header.SampleFormat;
        public int BytesPerSample => Header.BytesPerSample;

        /// Raw half bits of a <see cref="QoiSampleFormat.Half"/> image
        public Span<ushort> AsHalfs() => SampleFormat == QoiSampleFormat.Half
            ? MemoryMarshal.Cast<byte, ushort>(Pixels.AsSpan())
            : throw new InvalidOperationException($"The image holds {SampleFormat} samples.");

        /// Samples of a <see cref="QoiSampleFormat.Float"/> image
        public Span<float> AsFloats() => SampleFormat == QoiSampleFormat.Float
            ? MemoryMarshal.Cast<byte, float>(Pixels.AsSpan())
            : throw new InvalidOperationException($"The image holds {SampleFormat} samples.");
    }

    public class QoiException : Exception
    {
        public QoiException(string message) : base(message) { }
        public QoiException(string message, Exception inner) : base(message, inner) { }
    }
}
