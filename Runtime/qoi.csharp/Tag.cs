namespace Qoi.Csharp
{
    /// <summary>
    /// Contains all tag constants.
    /// </summary>
    public static class Tag
    {
        /// <summary>
        /// Value of an RGB tag.
        /// </summary>
        public const byte RGB = 0b11111110;

        /// <summary>
        /// Value of an RGBA tag.
        /// </summary>
        public const byte RGBA = 0b11111111;

        /// <summary>
        /// Mask for two-bit tags.
        /// </summary>
        public const byte MASK = 0b11_000000;

        /// <summary>
        /// Value of an index tag.
        /// </summary>
        public const byte INDEX = 0b00_000000;

        /// <summary>
        /// Value of a diff tag.
        /// </summary>
        public const byte DIFF = 0b01_000000;

        /// <summary>
        /// Value of a luma diff tag.
        /// </summary>
        public const byte LUMA = 0b10_000000;

        /// <summary>
        /// Value of a run tag.
        /// </summary>
        public const byte RUN = 0b11_000000;
    }
}
