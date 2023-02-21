using System;
using System.Buffers.Binary;
using System.Runtime.InteropServices;

/// <summary>
///     QOI encoder.
/// </summary>
public static class QoiEncoder
{
    /// <summary>
    ///     Encodes raw pixel data into QOI.
    /// </summary>
    /// <param name="image">QOI image.</param>
    /// <returns>Encoded image.</returns>
    /// <exception cref="QoiEncodingException">Thrown when image information is invalid.</exception>
    public static byte[] Encode(QoiImage image)
    {
        var bytes = new byte[QoiCodec.HeaderSize + QoiCodec.Padding.Length + image.Width * image.Height * (byte)image.Channels];
        return bytes[..Encode(image, bytes)];
    }

    public static int Encode(QoiImage image, Span<byte> buffer)
    {
        if (image.Width == 0) throw new QoiEncodingException($"Invalid width: {image.Width}");

        if (image.Height == 0 || image.Height >= QoiCodec.MaxPixels / image.Width) throw new QoiEncodingException($"Invalid height: {image.Height}. Maximum for this image is {QoiCodec.MaxPixels / image.Width - 1}");

        var width = image.Width;
        var height = image.Height;
        var channels = (int)image.Channels;
        var colorSpace = (byte)image.ColorSpace;
        var pixels = image.Data.Span;

        if (buffer.Length < QoiCodec.HeaderSize + QoiCodec.Padding.Length + width * height * channels)
            return -1;

        BinaryPrimitives.WriteInt32BigEndian(buffer, QoiCodec.Magic);
        BinaryPrimitives.WriteInt32BigEndian(buffer[4..], width);
        BinaryPrimitives.WriteInt32BigEndian(buffer[8..], height);

        buffer[12] = (byte)channels;
        buffer[13] = colorSpace;

        Span<int> index = stackalloc int[QoiCodec.HashTableSize];

        Span<byte> prev = stackalloc byte[4] { 0, 0, 0, 255 };
        Span<byte> rgba = stackalloc byte[4] { 0, 0, 0, 255 };
        var rgb = rgba[..3];

        var prevAsInt = MemoryMarshal.Cast<byte, int>(prev);
        var rgbaAsInt = MemoryMarshal.Cast<byte, int>(rgba);

        var run = 0;
        var counter = 0;
        int p = QoiCodec.HeaderSize;
        pixels = pixels[..(width * height * channels)];
        while (pixels.Length > 0)
        {
            pixels[..channels].CopyTo(rgba);
            pixels = pixels[channels..];

            if (prevAsInt[0] == rgbaAsInt[0])
            {
                run++;
                if (run == 62 || pixels.Length == 0)
                {
                    buffer[p++] = (byte)(QoiCodec.Run | run - 1);
                    run = 0;
                }
            }
            else
            {
                if (run > 0)
                {
                    buffer[p++] = (byte)(QoiCodec.Run | run - 1);
                    run = 0;
                }

                var indexPos = (rgba[0] * 3 + rgba[1] * 5 + rgba[2] * 7 + rgba[3] * 11) % QoiCodec.HashTableSize;
                if (rgbaAsInt[0] == index[indexPos])
                {
                    buffer[p++] = (byte)(QoiCodec.Index | indexPos);
                }
                else
                {
                    index[indexPos] = rgbaAsInt[0];

                    if (rgba[3] == prev[3])
                    {
                        var vr = rgba[0] - prev[0];
                        var vg = rgba[1] - prev[1];
                        var vb = rgba[2] - prev[2];

                        var vgr = vr - vg;
                        var vgb = vb - vg;

                        if (vr is > -3 and < 2 &&
                            vg is > -3 and < 2 &&
                            vb is > -3 and < 2)
                        {
                            counter++;
                            buffer[p++] = (byte)(QoiCodec.Diff | vr + 2 << 4 | vg + 2 << 2 | vb + 2);
                        }
                        else if (vgr is > -9 and < 8 &&
                                 vg is > -33 and < 32 &&
                                 vgb is > -9 and < 8)
                        {
                            buffer[p++] = (byte)(QoiCodec.Luma | vg + 32);
                            buffer[p++] = (byte)(vgr + 8 << 4 | vgb + 8);
                        }
                        else
                        {
                            buffer[p++] = QoiCodec.Rgb;
                            rgb.CopyTo(buffer[p..]);
                            p += 3;
                        }
                    }
                    else
                    {
                        buffer[p++] = QoiCodec.Rgba;
                        rgba.CopyTo(buffer[p..]);
                        p += 4;
                    }
                }
            }

            prevAsInt[0] = rgbaAsInt[0];
        }

        QoiCodec.Padding.Span.CopyTo(buffer[p..]);
        p += QoiCodec.Padding.Length;

        return p;
    }

    private static bool RgbaEquals(byte r1, byte g1, byte b1, byte a1, byte r2, byte g2, byte b2, byte a2) =>
        r1 == r2 &&
        g1 == g2 &&
        b1 == b2 &&
        a1 == a2;
}