namespace Qoi.Csharp
{
    internal struct Diff
    {
        public Diff(Pixel prev, Pixel next)
        {
            R = (byte)(next.R - prev.R + 2);
            G = (byte)(next.G - prev.G + 2);
            B = (byte)(next.B - prev.B + 2);
        }

        public byte R;
        public byte G;
        public byte B;

        public bool IsSmall() => R <= 3 && G <= 3 && B <= 3;
    }
}
