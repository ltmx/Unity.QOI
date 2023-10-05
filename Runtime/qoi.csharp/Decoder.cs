using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Qoi.Csharp
{
    /// <summary>
    ///     Decoder for QOI images.
    /// </summary>
    public class Decoder
    {
        private readonly BinaryReader _binReader;
        private List<byte> _pixelBytes;
        private readonly Pixel[] _cache;
        private Channels? _channels;
        private ColorSpace? _colorSpace;
        private Pixel _prev;

        private const int CACHE_SIZE = 64;

        private Decoder(BinaryReader binReader)
        {
            _binReader = binReader;
            _pixelBytes = null;
            _cache = new Pixel[64];
            _channels = null;
            _colorSpace = null;
            _prev = new Pixel { R = 0, G = 0, B = 0, A = 255 };
        }

        /// <summary>
        ///     Decode QOI bytes into a raw image.
        /// </summary>
        /// <param name="input">The raw bytes of a QOI image.</param>
        /// <returns>An image containing color and meta data.</returns>
        public static QoiImage Decode(byte[] input)
        {
            using var memStream = new MemoryStream(input);
            using var binReader = new BinaryReader(memStream);
            var decoder = new Decoder(binReader);
            return decoder.Decode();
        }

        private void ParseMagic()
        {
            var correctMagic = "qoif".ToUTF8Bytes();

            var actualMagic = _binReader.ReadBytes(4);
            if (actualMagic.Length < correctMagic.Length)
                throw new InvalidHeaderException("Magic bytes were invalid.");

            if (actualMagic.Where((t, i) => t != correctMagic[i]).Any())
                throw new InvalidHeaderException("Magic bytes were invalid.");
        }

        private void ParseChannels()
        {
            _channels = (Channels)_binReader.ReadByte();
            if (!Enum.IsDefined(typeof(Channels), _channels))
                throw new InvalidHeaderException($"Value {_channels} for Channels is not valid.");
        }

        private void ParseColorSpace()
        {
            _colorSpace = (ColorSpace)_binReader.ReadByte();
            if (!Enum.IsDefined(typeof(ColorSpace), _colorSpace))
                throw new InvalidHeaderException($"Value {_colorSpace} for ColorSpace is not valid.");
        }

        private void ParseChunks(uint width, uint height)
        {
            var pixelSize = (_channels == Channels.Rgba) ? 4 : 3;
            _pixelBytes = new List<byte>();
    
            while (width * height * pixelSize > _pixelBytes.Count)
                ParseChunk();

            FlipVertically((int)width, (int)height, pixelSize);
        }

        private void FlipVertically(int width, int height, int pixelSize)
        {
            byte[] temp = new byte[pixelSize];
            byte[] pixelBytesArray = _pixelBytes.ToArray(); // Convert to array for performance

            for (int y = 0; y < height / 2; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    int topIndex = (y * width + x) * pixelSize;
                    int bottomIndex = ((height - 1 - y) * width + x) * pixelSize;

                    // store top pixel temporarily
                    Array.Copy(pixelBytesArray, topIndex, temp, 0, pixelSize);
                    // copy bottom pixel to top
                    Array.Copy(pixelBytesArray, bottomIndex, pixelBytesArray, topIndex, pixelSize);
                    // copy top pixel from temp to bottom
                    Array.Copy(temp, 0, pixelBytesArray, bottomIndex, pixelSize);
                }
            }

            _pixelBytes = pixelBytesArray.ToList(); // Convert back to list
        }

        private int CalculateIndex(Pixel pixel) => (pixel.R * 3 + pixel.G * 5 + pixel.B * 7 + pixel.A * 11) % CACHE_SIZE;

        private void WritePixel(Pixel pixel)
        {
            _pixelBytes.Add(pixel.R);
            _pixelBytes.Add(pixel.G);
            _pixelBytes.Add(pixel.B);
            if (_channels == Channels.Rgba)
                _pixelBytes.Add(pixel.A);
        }

        private void ParseChunk()
        {
            var tag = _binReader.ReadByte();
            if ((tag & Tag.MASK) == Tag.INDEX) {
                var pixel = _cache[tag];
                WritePixel(pixel);
                _prev = pixel;
                return;
            }

            if ((tag & Tag.MASK) == Tag.DIFF) {
                var dr = (byte)((tag & 0b00_11_00_00) >> 4);
                var dg = (byte)((tag & 0b00_00_11_00) >> 2);
                var db = (byte)((tag & 0b00_00_00_11) >> 0);
                const byte bias = 2;
                var pixel = new Pixel
                {
                    R = (byte)(_prev.R + dr - bias),
                    G = (byte)(_prev.G + dg - bias),
                    B = (byte)(_prev.B + db - bias),
                    A = _prev.A
                };

                WritePixel(pixel);
                _cache[CalculateIndex(pixel)] = pixel;
                _prev = pixel;
                return;
            }

            if ((tag & Tag.MASK) == Tag.LUMA) {
                var dg = (byte)(tag & 0b00_111111) - 32;
                var dxdg = _binReader.ReadByte();
                var drdg = (dxdg & 0b1111_0000) >> 4;
                var dr = drdg - 8 + dg;
                var dbdg = (dxdg & 0b0000_1111) >> 0;
                var db = dbdg - 8 + dg;
                var pixel = new Pixel(_prev.R + dr, _prev.G + dg, _prev.B + db, _prev.A);
                WritePixel(pixel);
                _cache[CalculateIndex(pixel)] = pixel;
                _prev = pixel;
                return;
            }

            if (tag != Tag.RGB && tag != Tag.RGBA && (tag & Tag.MASK) == Tag.RUN) {
                var run = (byte)(tag & ~Tag.MASK);
                var runLength = run + 1;
                var pixel = _prev;
                for (var i = 0; i < runLength; i++)
                    WritePixel(pixel);

                _cache[CalculateIndex(pixel)] = pixel;
                _prev = pixel;
                return;
            }

            var r = _binReader.ReadByte();
            var g = _binReader.ReadByte();
            var b = _binReader.ReadByte();
            byte a = 255;

            if (tag != Tag.RGB)
                a = _binReader.ReadByte();

            var newPixel = new Pixel { R = r, G = g, B = b, A = a };
            WritePixel(newPixel);
            _cache[CalculateIndex(newPixel)] = newPixel;
            _prev = newPixel;
        }

        private void ParseEndMarker()
        {
            var correctEndMarker = new byte[]
            {
                0, 0, 0, 0, 0, 0, 0, 1
            };

            var actualEndMarker = _binReader.ReadBytes(8);
            if (actualEndMarker.Length < correctEndMarker.Length)
                throw new InvalidHeaderException("End marker is invalid.");

            if (actualEndMarker.Where((t, i) => t != correctEndMarker[i]).Any())
                throw new InvalidHeaderException("End marker is invalid.");
        }

        private uint ReadUInt32BigEndian()
        {
            var value = 0u;
            value |= (uint)(_binReader.ReadByte() << 24);
            value |= (uint)(_binReader.ReadByte() << 16);
            value |= (uint)(_binReader.ReadByte() << 08);
            value |= (uint)(_binReader.ReadByte() << 00);
            return value;
        }

        private QoiImage Decode()
        {
            ParseMagic();
            var width = ReadUInt32BigEndian();
            var height = ReadUInt32BigEndian();
            ParseChannels();
            ParseColorSpace();
            ParseChunks(width, height);
            ParseEndMarker();
            var bytes = _pixelBytes.ToArray();
            return new QoiImage(bytes, width, height, _channels.Value, _colorSpace.Value);
        }

        /// <summary>
        ///     Represents errors that occurred parsing a QOI header.
        /// </summary>
        public class InvalidHeaderException : Exception
        {
            /// <summary>
            ///     Initializes a new instance of the <see cref="InvalidHeaderException" /> class.
            /// </summary>
            public InvalidHeaderException()
            { }

            /// <summary>
            ///     Initializes a new instance of the <see cref="InvalidHeaderException" /> class with a specified error message.
            /// </summary>
            /// <param name="message">The message that describes the error.</param>
            public InvalidHeaderException(string message) : base(message)
            { }

            /// <summary>
            ///     Initializes a new instance of the <see cref="InvalidHeaderException" /> class with a specified error message
            ///     and a reference to the inner exception that is the cause of this exception.
            /// </summary>
            /// <param name="message">
            ///     The error message that explains the reason for the exception.
            /// </param>
            /// <param name="innerException">
            ///     The exception that is the cause of the current exception,
            ///     or a null reference (Nothing in Visual Basic) if no inner exception is specified.
            /// </param>
            public InvalidHeaderException(string message, Exception innerException) : base(message, innerException)
            { }
        }
    }
}