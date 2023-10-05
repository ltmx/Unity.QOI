using System.Runtime.CompilerServices;

namespace Qoi.Csharp
{
    internal struct LumaDiff
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public LumaDiff(Pixel prev, Pixel next)
        {
            var r = next.R - prev.R;
            var g = next.G - prev.G;
            var b = next.B - prev.B;
            G = (byte)(g + 32);
            RG = (byte)(r - g + 8);
            BG = (byte)(b - g + 8);
        }

        public byte G;
        public byte RG;
        public byte BG;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool IsSmall()
        {
            return G <= 63 && RG <= 15 && BG <= 15;
        }
    }
}
