using System.Runtime.CompilerServices;
using Unity.Burst.Intrinsics;
using Unity.Mathematics;
using static Unity.Burst.Intrinsics.X86.Avx;
using static Unity.Burst.Intrinsics.X86.Avx2;

namespace LTMX.QOI.Editor
{
    // SIMD lanes of the BC7 encoder: one block per lane, 8 lanes. One 256 bit AVX2 instruction per operation, two 128 bit
    // halves (SSE / Neon, through Unity.Mathematics) elsewhere.

    internal unsafe struct LaneF
    {
        public const int W = 8;
        public v256 V;

        public float4 Lo => new float4(V.Float0, V.Float1, V.Float2, V.Float3);
        public float4 Hi => new float4(V.Float4, V.Float5, V.Float6, V.Float7);

        private static bool Avx2 => X86.Avx2.IsAvx2Supported;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static LaneF Of(v256 v) => new LaneF { V = v };

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static LaneF Of(float4 lo, float4 hi) => Of(new v256(lo.x, lo.y, lo.z, lo.w, hi.x, hi.y, hi.z, hi.w));

        public ref float this[int lane]
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get { fixed (LaneF* p = &this) return ref ((float*)p)[lane]; }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static implicit operator LaneF(float x) => Avx2 ? Of(mm256_set1_ps(x)) : Of(x, x);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static implicit operator LaneF(LaneI x) => Avx2 ? Of(mm256_cvtepi32_ps(x.V)) : Of(x.Lo, x.Hi);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static explicit operator LaneI(LaneF x) =>
            Avx2 ? LaneI.Of(mm256_cvttps_epi32(x.V)) : LaneI.Of((int4)x.Lo, (int4)x.Hi);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static LaneF operator +(LaneF a, LaneF b) => Avx2 ? Of(mm256_add_ps(a.V, b.V)) : Of(a.Lo + b.Lo, a.Hi + b.Hi);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static LaneF operator -(LaneF a, LaneF b) => Avx2 ? Of(mm256_sub_ps(a.V, b.V)) : Of(a.Lo - b.Lo, a.Hi - b.Hi);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static LaneF operator -(LaneF a) => Avx2 ? Of(mm256_sub_ps(mm256_setzero_ps(), a.V)) : Of(-a.Lo, -a.Hi);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static LaneF operator *(LaneF a, LaneF b) => Avx2 ? Of(mm256_mul_ps(a.V, b.V)) : Of(a.Lo * b.Lo, a.Hi * b.Hi);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static LaneF operator /(LaneF a, LaneF b) => Avx2 ? Of(mm256_div_ps(a.V, b.V)) : Of(a.Lo / b.Lo, a.Hi / b.Hi);

        // Ordered, non signaling predicates (unordered for !=), as C#
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static LaneB operator <(LaneF a, LaneF b) =>
            Avx2 ? LaneB.Of(mm256_cmp_ps(a.V, b.V, 0x11)) : LaneB.Of(a.Lo < b.Lo, a.Hi < b.Hi);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static LaneB operator <=(LaneF a, LaneF b) =>
            Avx2 ? LaneB.Of(mm256_cmp_ps(a.V, b.V, 0x12)) : LaneB.Of(a.Lo <= b.Lo, a.Hi <= b.Hi);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static LaneB operator >(LaneF a, LaneF b) =>
            Avx2 ? LaneB.Of(mm256_cmp_ps(a.V, b.V, 0x1E)) : LaneB.Of(a.Lo > b.Lo, a.Hi > b.Hi);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static LaneB operator >=(LaneF a, LaneF b) =>
            Avx2 ? LaneB.Of(mm256_cmp_ps(a.V, b.V, 0x1D)) : LaneB.Of(a.Lo >= b.Lo, a.Hi >= b.Hi);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static LaneB operator ==(LaneF a, LaneF b) =>
            Avx2 ? LaneB.Of(mm256_cmp_ps(a.V, b.V, 0x00)) : LaneB.Of(a.Lo == b.Lo, a.Hi == b.Hi);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static LaneB operator !=(LaneF a, LaneF b) =>
            Avx2 ? LaneB.Of(mm256_cmp_ps(a.V, b.V, 0x04)) : LaneB.Of(a.Lo != b.Lo, a.Hi != b.Hi);

        public override bool Equals(object o) => false;
        public override int GetHashCode() => 0;
    }

    internal unsafe struct LaneI
    {
        public const int W = LaneF.W;
        public v256 V;

        public int4 Lo => new int4(V.SInt0, V.SInt1, V.SInt2, V.SInt3);
        public int4 Hi => new int4(V.SInt4, V.SInt5, V.SInt6, V.SInt7);

        private static bool Avx2 => X86.Avx2.IsAvx2Supported;

        public static LaneI zero => default;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static LaneI Of(v256 v) => new LaneI { V = v };

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static LaneI Of(int4 lo, int4 hi) => Of(new v256(lo.x, lo.y, lo.z, lo.w, hi.x, hi.y, hi.z, hi.w));

        public ref int this[int lane]
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get { fixed (LaneI* p = &this) return ref ((int*)p)[lane]; }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static implicit operator LaneI(int x) => Avx2 ? Of(mm256_set1_epi32(x)) : Of(x, x);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static LaneI operator +(LaneI a, LaneI b) =>
            Avx2 ? Of(mm256_add_epi32(a.V, b.V)) : Of(a.Lo + b.Lo, a.Hi + b.Hi);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static LaneI operator -(LaneI a, LaneI b) =>
            Avx2 ? Of(mm256_sub_epi32(a.V, b.V)) : Of(a.Lo - b.Lo, a.Hi - b.Hi);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static LaneI operator *(LaneI a, LaneI b) =>
            Avx2 ? Of(mm256_mullo_epi32(a.V, b.V)) : Of(a.Lo * b.Lo, a.Hi * b.Hi);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static LaneI operator &(LaneI a, LaneI b) =>
            Avx2 ? Of(mm256_and_si256(a.V, b.V)) : Of(a.Lo & b.Lo, a.Hi & b.Hi);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static LaneI operator |(LaneI a, LaneI b) =>
            Avx2 ? Of(mm256_or_si256(a.V, b.V)) : Of(a.Lo | b.Lo, a.Hi | b.Hi);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static LaneI operator ~(LaneI a) =>
            Avx2 ? Of(mm256_xor_si256(a.V, mm256_set1_epi32(-1))) : Of(~a.Lo, ~a.Hi);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static LaneI operator <<(LaneI a, int n) =>
            Avx2 ? Of(mm256_sll_epi32(a.V, new v128(n, 0, 0, 0))) : Of(a.Lo << n, a.Hi << n);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static LaneI operator >>(LaneI a, int n) =>
            Avx2 ? Of(mm256_sra_epi32(a.V, new v128(n, 0, 0, 0))) : Of(a.Lo >> n, a.Hi >> n);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static LaneB operator <(LaneI a, LaneI b) =>
            Avx2 ? LaneB.Of(mm256_cmpgt_epi32(b.V, a.V)) : LaneB.Of(a.Lo < b.Lo, a.Hi < b.Hi);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static LaneB operator >(LaneI a, LaneI b) =>
            Avx2 ? LaneB.Of(mm256_cmpgt_epi32(a.V, b.V)) : LaneB.Of(a.Lo > b.Lo, a.Hi > b.Hi);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static LaneB operator <=(LaneI a, LaneI b) => !(a > b);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static LaneB operator >=(LaneI a, LaneI b) => !(a < b);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static LaneB operator ==(LaneI a, LaneI b) =>
            Avx2 ? LaneB.Of(mm256_cmpeq_epi32(a.V, b.V)) : LaneB.Of(a.Lo == b.Lo, a.Hi == b.Hi);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static LaneB operator !=(LaneI a, LaneI b) => !(a == b);

        public override bool Equals(object o) => false;
        public override int GetHashCode() => 0;
    }

    // Lane mask: all bits set (true) or clear per lane
    internal struct LaneB
    {
        public v256 V;

        public int4 Lo => new int4(V.SInt0, V.SInt1, V.SInt2, V.SInt3);
        public int4 Hi => new int4(V.SInt4, V.SInt5, V.SInt6, V.SInt7);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static LaneB Of(int4 lo, int4 hi) => Of(new v256(lo.x, lo.y, lo.z, lo.w, hi.x, hi.y, hi.z, hi.w));

        private static bool Avx2 => X86.Avx2.IsAvx2Supported;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static LaneB Of(v256 v) => new LaneB { V = v };

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static LaneB Of(bool4 lo, bool4 hi) =>
            Of(math.select(int4.zero, -1, lo), math.select(int4.zero, -1, hi));

        internal bool4 LoMask => Lo != 0;
        internal bool4 HiMask => Hi != 0;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static implicit operator LaneB(bool x) => Avx2 ? Of(mm256_set1_epi32(x ? -1 : 0)) : Of(x, x);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static LaneB operator &(LaneB a, LaneB b) =>
            Avx2 ? Of(mm256_and_si256(a.V, b.V)) : Of(a.Lo & b.Lo, a.Hi & b.Hi);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static LaneB operator |(LaneB a, LaneB b) =>
            Avx2 ? Of(mm256_or_si256(a.V, b.V)) : Of(a.Lo | b.Lo, a.Hi | b.Hi);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static LaneB operator !(LaneB a) =>
            Avx2 ? Of(mm256_xor_si256(a.V, mm256_set1_epi32(-1))) : Of(~a.Lo, ~a.Hi);
    }

    // 4 channels of lanes
    internal struct LaneF4
    {
        public LaneF C0, C1, C2, C3;

        public unsafe ref LaneF this[int channel]
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get { fixed (LaneF4* p = &this) return ref ((LaneF*)p)[channel]; }
        }
    }

    internal struct LaneI4
    {
        public LaneI C0, C1, C2, C3;

        public unsafe ref LaneI this[int channel]
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get { fixed (LaneI4* p = &this) return ref ((LaneI*)p)[channel]; }
        }
    }

    // math over lanes, plus the scalar functions the encoder uses
    internal static class vmath
    {
        private static bool Avx2 => X86.Avx2.IsAvx2Supported;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static LaneF select(LaneF a, LaneF b, LaneB c) => Avx2
            ? new LaneF { V = mm256_blendv_ps(a.V, b.V, c.V) }
            : LaneF.Of(math.select(a.Lo, b.Lo, c.LoMask), math.select(a.Hi, b.Hi, c.HiMask));

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static LaneI select(LaneI a, LaneI b, LaneB c) => Avx2
            ? LaneI.Of(mm256_blendv_ps(a.V, b.V, c.V))
            : LaneI.Of(math.select(a.Lo, b.Lo, c.LoMask), math.select(a.Hi, b.Hi, c.HiMask));

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static LaneF min(LaneF a, LaneF b) => Avx2
            ? new LaneF { V = mm256_min_ps(a.V, b.V) }
            : LaneF.Of(math.min(a.Lo, b.Lo), math.min(a.Hi, b.Hi));

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static LaneF max(LaneF a, LaneF b) => Avx2
            ? new LaneF { V = mm256_max_ps(a.V, b.V) }
            : LaneF.Of(math.max(a.Lo, b.Lo), math.max(a.Hi, b.Hi));

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static LaneI min(LaneI a, LaneI b) =>
            Avx2 ? LaneI.Of(mm256_min_epi32(a.V, b.V)) : LaneI.Of(math.min(a.Lo, b.Lo), math.min(a.Hi, b.Hi));

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static LaneI max(LaneI a, LaneI b) =>
            Avx2 ? LaneI.Of(mm256_max_epi32(a.V, b.V)) : LaneI.Of(math.max(a.Lo, b.Lo), math.max(a.Hi, b.Hi));

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static LaneF clamp(LaneF x, LaneF lo, LaneF hi) => min(max(x, lo), hi);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static LaneI clamp(LaneI x, LaneI lo, LaneI hi) => min(max(x, lo), hi);

        // Nearest, ties to even (as math.round)
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static LaneF round(LaneF x) => Avx2
            ? new LaneF { V = mm256_round_ps(x.V, 8) }
            : LaneF.Of(math.round(x.Lo), math.round(x.Hi));

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static LaneF floor(LaneF x) => Avx2
            ? new LaneF { V = mm256_round_ps(x.V, 9) }
            : LaneF.Of(math.floor(x.Lo), math.floor(x.Hi));

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static LaneF sqrt(LaneF x) => Avx2
            ? new LaneF { V = mm256_sqrt_ps(x.V) }
            : LaneF.Of(math.sqrt(x.Lo), math.sqrt(x.Hi));

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static LaneF abs(LaneF x) => Avx2
            ? new LaneF { V = mm256_and_ps(x.V, mm256_set1_epi32(0x7FFFFFFF)) }
            : LaneF.Of(math.abs(x.Lo), math.abs(x.Hi));

        // table[index] per lane
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static unsafe LaneI gather(int* table, LaneI index)
        {
            if (Avx2) return LaneI.Of(mm256_i32gather_epi32(table, index.V, 4));
            int4 lo = index.Lo, hi = index.Hi;
            return LaneI.Of(new int4(table[lo.x], table[lo.y], table[lo.z], table[lo.w]),
                new int4(table[hi.x], table[hi.y], table[hi.z], table[hi.w]));
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool any(LaneB x) => Avx2 ? mm256_movemask_ps(x.V) != 0 : math.any(x.LoMask | x.HiMask);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool all(LaneB x) => Avx2 ? mm256_movemask_ps(x.V) == 0xFF : math.all(x.LoMask & x.HiMask);

        // Scalars
        public static int min(int a, int b) => math.min(a, b);
        public static int max(int a, int b) => math.max(a, b);
        public static float min(float a, float b) => math.min(a, b);
        public static float max(float a, float b) => math.max(a, b);
        public static int clamp(int x, int lo, int hi) => math.clamp(x, lo, hi);
        public static float round(float x) => math.round(x);
        public static int countbits(uint x) => math.countbits(x);
        public static int countbits(int x) => math.countbits(x);
        public static int tzcnt(int x) => math.tzcnt(x);
    }
}
