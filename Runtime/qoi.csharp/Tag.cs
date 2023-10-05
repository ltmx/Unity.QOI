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
        public const byte RGB = 0b_11111110;

        /// <summary>
        /// Value of an RGBA tag.
        /// </summary>
        public const byte RGBA = 0b_11111111;

        /// <summary>
        /// Mask for two-bit tags.
        /// </summary>
        public const byte MASK = 0b_11000000;

        /// <summary>
        /// Value of an index tag.
        /// </summary>
        public const byte INDEX = 0b_00000000;

        /// <summary>
        /// Value of a diff tag.
        /// </summary>
        public const byte DIFF = 0b_01000000;

        /// <summary>
        /// Value of a luma diff tag.
        /// </summary>
        public const byte LUMA = 0b_10000000;

        /// <summary>
        /// Value of a run tag.
        /// </summary>
        public const byte RUN = 0b_11000000;
    }
}
