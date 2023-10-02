namespace Qoi.Csharp
{
    /// <summary>
    /// Specifies the color space of the image.
    /// </summary
    public enum ColorSpace : byte
    {
        /// <summary>
        /// Specifies a sRGB color space with linear alpha.
        /// </summary>
        SRgb = 0,

        /// <summary>
        /// Specifies all channels are linear.
        /// </summary>
        Linear = 1,
    }
}
