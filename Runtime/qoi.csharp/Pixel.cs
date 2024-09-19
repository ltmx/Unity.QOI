using System;
using System.Numerics;
using System.Runtime.CompilerServices;

#pragma warning disable 0660, 0661

using M = System.Runtime.CompilerServices.MethodImplAttribute;

namespace Qoi.Csharp
{
    public struct Pixel : IEquatable<Pixel>
    {
        private const MethodImplOptions IL = MethodImplOptions.AggressiveInlining;
        
        [M(IL)]
        public override int GetHashCode() => HashCode.Combine(R, G, B, A);

        public byte R;
        public byte G;
        public byte B;
        public byte A;
        
        [M(IL)]
        public Pixel(byte r, byte g, byte b, byte a)
        {
            R = r;
            G = g;
            B = b;
            A = a;
        }
        [M(IL)]
        public Pixel(float r, float g, float b, float a)
        {
            R = (byte)r;
            G = (byte)g;
            B = (byte)b;
            A = (byte)a;
        }
        
        // [M(IL)]
        // public Pixel(ValueType r, ValueType g, ValueType b, ValueType a)
        // {
        //     R = (byte)r;
        //     G = (byte)g;
        //     B = (byte)b;
        //     A = (byte)a;
        // }

        public override bool Equals(object obj) => obj is Pixel other && Equals(other);

        [M(IL)]
        public bool Equals(Pixel other)
        {
            return R == other.R
                && G == other.G
                && B == other.B
                && A == other.A;
        }
        
        [M(IL)]
        public static bool operator ==(Pixel a, Pixel b) => a.Equals(b);
        
        [M(IL)]
        public static bool operator !=(Pixel a, Pixel b)
        {
            return a.R != b.R
                   || a.G != b.G
                   || a.B != b.B
                   || a.A != b.A;
        }

        [M(IL)] public static Pixel operator *(Pixel a, Pixel b) => new(a.R * b.R, a.G * b.G, a.B * b.B, a.A * b.A);

        [M(IL)] public static Pixel operator *(Pixel a, float b) => new((a.R * b), (a.G * b), (a.B * b), (a.A * b));

        [M(IL)] public static Pixel operator *(Pixel a, int b) => new(a.R * b, a.G * b, a.B * b, a.A * b);

        [M(IL)] public static Pixel operator *(Pixel a, uint b) => new(a.R * b, a.G * b, a.B * b, a.A * b);

        [M(IL)] public static Pixel operator *(Pixel a, byte b) => new(a.R * b, a.G * b, a.B * b, a.A * b);
        
        [M(IL)] public static Pixel operator /(Pixel a, Pixel b) => new(a.R / b.R, a.G / b.G, a.B / b.B, a.A / b.A);

        [M(IL)] public static Pixel operator /(Pixel a, float b) => new((a.R / b), (a.G / b), (a.B / b), (a.A / b));

        [M(IL)] public static Pixel operator /(Pixel a, int b) => new(a.R / b, a.G / b, a.B / b, a.A / b);

        [M(IL)] public static Pixel operator /(Pixel a, uint b) => new(a.R / b, a.G / b, a.B / b, a.A / b);

        [M(IL)] public static Pixel operator /(Pixel a, byte b) => new(a.R / b, a.G / b, a.B / b, a.A / b);
        
        
        [M(IL)] public static Pixel operator +(Pixel a, Pixel b) => new(a.R + b.R, a.G + b.G, a.B + b.B, a.A + b.A);

        [M(IL)] public static Pixel operator +(Pixel a, float b) => new((a.R + b), (a.G + b), (a.B + b), (a.A + b));

        [M(IL)] public static Pixel operator +(Pixel a, int b) => new(a.R + b, a.G + b, a.B + b, a.A + b);

        [M(IL)] public static Pixel operator +(Pixel a, uint b) => new(a.R + b, a.G + b, a.B + b, a.A + b);

        [M(IL)] public static Pixel operator +(Pixel a, byte b) => new(a.R + b, a.G + b, a.B + b, a.A + b);

        
        [M(IL)] public static Pixel operator -(Pixel a, Pixel b) => new(a.R - b.R, a.G - b.G, a.B - b.B, a.A - b.A);

        [M(IL)] public static Pixel operator -(Pixel a, float b) => new((a.R - b), (a.G - b), (a.B - b), (a.A - b));

        [M(IL)] public static Pixel operator -(Pixel a, int b) => new(a.R - b, a.G - b, a.B - b, a.A - b);

        [M(IL)] public static Pixel operator -(Pixel a, uint b) => new(a.R - b, a.G - b, a.B - b, a.A - b);

        [M(IL)] public static Pixel operator -(Pixel a, byte b) => new(a.R - b, a.G - b, a.B - b, a.A - b);

        
        [M(IL)] public static Pixel operator %(Pixel a, Pixel b) => new(a.R % b.R, a.G % b.G, a.B % b.B, a.A % b.A);

        [M(IL)] public static Pixel operator %(Pixel a, float b) => new((a.R % b), (a.G % b), (a.B % b), (a.A % b));

        [M(IL)] public static Pixel operator %(Pixel a, int b) => new(a.R % b, a.G % b, a.B % b, a.A % b);

        [M(IL)] public static Pixel operator %(Pixel a, uint b) => new(a.R % b, a.G % b, a.B % b, a.A % b);

        [M(IL)] public static Pixel operator %(Pixel a, byte b) => new(a.R % b, a.G % b, a.B % b, a.A % b);
    }
}
