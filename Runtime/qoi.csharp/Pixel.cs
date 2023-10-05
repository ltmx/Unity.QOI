using System;
using System.Runtime.CompilerServices;

#pragma warning disable 0660, 0661

namespace Qoi.Csharp
{
    public struct Pixel : IEquatable<Pixel>
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public override int GetHashCode() => HashCode.Combine(R, G, B, A);

        public byte R;
        public byte G;
        public byte B;
        public byte A;
        
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public Pixel(byte r, byte g, byte b, byte a)
        {
            R = r;
            G = g;
            B = b;
            A = a;
        }
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public Pixel(float r, float g, float b, float a)
        {
            R = (byte)r;
            G = (byte)g;
            B = (byte)b;
            A = (byte)a;
        }
        
        // [MethodImpl(MethodImplOptions.AggressiveInlining)]
        // public Pixel(ValueType r, ValueType g, ValueType b, ValueType a)
        // {
        //     R = (byte)r;
        //     G = (byte)g;
        //     B = (byte)b;
        //     A = (byte)a;
        // }

        public override bool Equals(object obj) => obj is Pixel other && Equals(other);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool Equals(Pixel other)
        {
            return R == other.R
                && G == other.G
                && B == other.B
                && A == other.A;
        }
        
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool operator ==(Pixel a, Pixel b) => a.Equals(b);
        
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool operator !=(Pixel a, Pixel b)
        {
            return a.R != b.R
                   || a.G != b.G
                   || a.B != b.B
                   || a.A != b.A;
        }

        public static Pixel operator *(Pixel a, Pixel b) => new(a.R * b.R, a.G * b.G, a.B * b.B, a.A * b.A);

        public static Pixel operator *(Pixel a, float b) => new((a.R * b), (a.G * b), (a.B * b), (a.A * b));

        public static Pixel operator *(Pixel a, int b) => new(a.R * b, a.G * b, a.B * b, a.A * b);

        public static Pixel operator *(Pixel a, uint b) => new(a.R * b, a.G * b, a.B * b, a.A * b);

        public static Pixel operator *(Pixel a, byte b) => new(a.R * b, a.G * b, a.B * b, a.A * b);

        
        public static Pixel operator /(Pixel a, Pixel b) => new(a.R / b.R, a.G / b.G, a.B / b.B, a.A / b.A);

        public static Pixel operator /(Pixel a, float b) => new((a.R / b), (a.G / b), (a.B / b), (a.A / b));

        public static Pixel operator /(Pixel a, int b) => new(a.R / b, a.G / b, a.B / b, a.A / b);

        public static Pixel operator /(Pixel a, uint b) => new(a.R / b, a.G / b, a.B / b, a.A / b);

        public static Pixel operator /(Pixel a, byte b) => new(a.R / b, a.G / b, a.B / b, a.A / b);
        
        
        public static Pixel operator +(Pixel a, Pixel b) => new(a.R + b.R, a.G + b.G, a.B + b.B, a.A + b.A);

        public static Pixel operator +(Pixel a, float b) => new((a.R + b), (a.G + b), (a.B + b), (a.A + b));

        public static Pixel operator +(Pixel a, int b) => new(a.R + b, a.G + b, a.B + b, a.A + b);

        public static Pixel operator +(Pixel a, uint b) => new(a.R + b, a.G + b, a.B + b, a.A + b);

        public static Pixel operator +(Pixel a, byte b) => new(a.R + b, a.G + b, a.B + b, a.A + b);

        
        public static Pixel operator -(Pixel a, Pixel b) => new(a.R - b.R, a.G - b.G, a.B - b.B, a.A - b.A);

        public static Pixel operator -(Pixel a, float b) => new((a.R - b), (a.G - b), (a.B - b), (a.A - b));

        public static Pixel operator -(Pixel a, int b) => new(a.R - b, a.G - b, a.B - b, a.A - b);

        public static Pixel operator -(Pixel a, uint b) => new(a.R - b, a.G - b, a.B - b, a.A - b);

        public static Pixel operator -(Pixel a, byte b) => new(a.R - b, a.G - b, a.B - b, a.A - b);

        
        public static Pixel operator %(Pixel a, Pixel b) => new(a.R % b.R, a.G % b.G, a.B % b.B, a.A % b.A);

        public static Pixel operator %(Pixel a, float b) => new((a.R % b), (a.G % b), (a.B % b), (a.A % b));

        public static Pixel operator %(Pixel a, int b) => new(a.R % b, a.G % b, a.B % b, a.A % b);

        public static Pixel operator %(Pixel a, uint b) => new(a.R % b, a.G % b, a.B % b, a.A % b);

        public static Pixel operator %(Pixel a, byte b) => new(a.R % b, a.G % b, a.B % b, a.A % b);
    }
}
