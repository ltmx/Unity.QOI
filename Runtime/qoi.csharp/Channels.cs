namespace Qoi.Csharp
{
    /// <summary>
    /// Specifies how many channels an image has.
    /// </summary>
    public enum Channels : byte
    {
        /// <summary>
        /// Specifies that an image has three channels: red, green, and blue.
        /// </summary>
        Rgb = 3,

        /// <summary>
        /// Specifies that an image has four channels: red, green, blue, and alpha.
        /// </summary>
        Rgba = 4,
    }
}
