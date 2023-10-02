namespace Qoi.Csharp
{
    /// <summary>
    /// A QOI image with the raw data and the metadata.
    /// </summary>
    public class Image
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="Image"/> class with the given data.
        /// </summary>
        /// <param name="bytes">The raw color data.</param>
        /// <param name="width">The width of the image.</param>
        /// <param name="height">The height of the image.</param>
        /// <param name="channels">The number of channels of the image.</param>
        /// <param name="colorSpace">The color space of the image.</param>
        public Image(byte[] bytes, uint width, uint height, Channels channels, ColorSpace colorSpace)
        {
            Bytes = bytes;
            Width = width;
            Height = height;
            Channels = channels;
            ColorSpace = colorSpace;
        }
        
        public Image(byte[] bytes, int width, int height, Channels channels, ColorSpace colorSpace) 
            : this(bytes, (uint)width, (uint)height, channels, colorSpace) { }

        /// <summary>
        /// The raw color data.
        /// </summary>
        /// <remarks>
        /// The number of bytes per pixel and the ordering of said bytes matches the value of <see cref="Channels"/>.
        /// </remarks>
        public byte[] Bytes { get; }

        /// <summary>
        /// The width of the image.
        /// </summary>
        public uint Width { get; }

        /// <summary>
        /// The height of the image.
        /// </summary>
        public uint Height { get; }

        /// <summary>
        /// The number of channels of the image.
        /// </summary>
        public Channels Channels { get; }

        /// <summary>
        /// The color space of the image.
        /// </summary>
        public ColorSpace ColorSpace { get; }
    }
}
