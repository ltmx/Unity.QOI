// Table layout, endpoint quantization and block packing of QoiAstc.compute. Needs MAXG defined.

#ifndef QOI_ASTC_COMMON
#define QOI_ASTC_COMMON

// Table layout (ints), as QoiAstcGpu writes it
#define OFF_CU 0        // [21 * 256] color code -> value
#define OFF_CQ 5376     // [21 * 256] color value -> code
#define OFF_WU 10752    // [12 * 32] weight code -> value 0..64
#define OFF_WQ 11136    // [12 * 65] weight value -> code
#define OFF_WFLIP 11916 // [12 * 32] code of 64 - weight
#define OFF_TRIT 12300  // [243]
#define OFF_QUINT 12543 // [125]
#define OFF_LEVEL 12668 // [21 * 4] bits, trits, quints, range
#define OFF_CUP 12752   // [21 * 256] code of the next higher value
#define OFF_CDOWN 18128 // [21 * 256] code of the next lower value
#define OFF_AXJ 23504   // [2 * 13 * 12] separable infill: grid index of texel x, per axis and grid size
#define OFF_AXF 23816   // [2 * 13 * 12] its factor (float)
#define OFF_AXINV 24128 // [2 * 13 * 13] 1 / summed factors of each grid weight (float)
#define OFF_WUP 24466  // [12 * 32] weight code of the next higher value
#define OFF_WDOWN 24850 // [12 * 32] of the next lower value
#define GRID_STRIDE 10  // X, Y, Count, Full, Index, Start, Texel, Weight, Step, Raw
#define MODE_STRIDE 15  // Grid, Level, Bits, WeightBits, ColorLevel 1P 6 values, 1P 8, WeightNoise, Dual, 2P 6, 2P 8, 1P 4, 2P 4, 3P 6, 3P 8, 3P 4
#define PART_STRIDE 6   // Seed, mask of partition 1 (5 words)
#define PART3_STRIDE 11 // Seed, masks of partitions 1 and 2 (after the two partition patterns and their count)
#define MAXP 3

#define NORMAL_Z_WEIGHT 0.5 // error weight of z in normal maps (square root), x and y 1
#if QOI_ASTC_FAST // speed over quality (QoiAstcGpu.FastSearch)
#define CANDIDATES 2
#define OTHER_CANDIDATES 2
#define PART_CANDIDATES 1
#define PART_SCREEN 8
#define PART3_CANDIDATES 0
#define PART3_SCREEN 1
#define DUAL_TRIES 1
#define EARLY_OUT 6
#define PASSES 1
#define DECIMATE_ITERATIONS 1
#else
#if QOI_ASTC_BEST // Compression quality Best: nearly every mode fitted
#define CANDIDATES 24
#else
#define CANDIDATES 4       // modes fitted by the one partition one plane search
#endif
#define OTHER_CANDIDATES 3 // by the others
#define PART_CANDIDATES 2  // two partition patterns fitted
#define PART_SCREEN 16     // patterns ranked by their line errors to pick those
#define PART3_CANDIDATES 1 // three partition patterns fitted
#define PART3_SCREEN 8     // and ranked by line errors
#define PART_FULL 0
#define DUAL_TRIES 2       // color channels tried on plane 2 (opaque blocks)
#define EARLY_OUT 3        // squared error per texel under which nothing else is tried
#define PASSES 2
#define DECIMATE_ITERATIONS 2
#endif

StructuredBuffer<int> _Tables;
StructuredBuffer<uint> _Pixels;
RWStructuredBuffer<uint4> _Blocks;

// Per level: block offset, blocks x, width, height, pixel offset
StructuredBuffer<int> _Levels;
int _LevelCount, _BlockStart, _BlockCount, _GroupsX; // pass 1 encodes blocks _BlockStart to _BlockCount - 1
#define _Bw BW
#define _Bh BW
int _GridOffset, _GridCount, _ModeOffset, _ModeCount, _PartOffset, _PartCount;

static uint best[MAXG]; // weight codes of the best encoding, plane 1 in bits 0..7, plane 2 in 8..15
static int ise[64];

// Search config
static int cP, cSeed, cC2;
static bool cAlpha;
static float4 cMask, cM1, cM2;
static float cLen1, cLen2;

// Best result
static float bErr;
static float4 bChErr;
static int bMode, bP, bSeed, bC2;static int bKind, bCl; // endpoints: partitions on base + offset (bit k, others direct), or 4 RGB scale; color level

static int bk[24]; // endpoint codes: partition k at k * 8, channel c at 2c (first) and 2c + 1 (second)

int T(int i) { return _Tables[i]; }
float TF(int i) { return asfloat(_Tables[i]); }

// ---- Endpoints ----

// Endpoint codes. Several partitions share the weights, so the decoder's swap (second sum lower) can't be undone by
// flipping them: codes are moved apart a step at a time until the order holds.
void QuantizeEndpoints(int cl, float4 a, float4 b, bool order, out int4 k0, out int4 k1, out float4 e0, out float4 e1)
{
    int o = OFF_CQ + cl * 256, u = OFF_CU + cl * 256;
    [unroll] for (int c = 0; c < 4; c++)
    {
        k0[c] = T(o + (int)(a[c] + 0.5));
        k1[c] = T(o + (int)(b[c] + 0.5));
    }
    if (order)
    {
        [loop] for (int it = 0; it < 8; it++)
        {
            int s0 = T(u + k0.x) + T(u + k0.y) + T(u + k0.z), s1 = T(u + k1.x) + T(u + k1.y) + T(u + k1.z);
            if (s1 >= s0) break;
            int ch = 0, gap = 1000;
            [unroll] for (int c2 = 0; c2 < 3; c2++)
            {
                int gp = abs(T(u + k0[c2]) - T(u + k1[c2]));
                bool movable = T(OFF_CUP + cl * 256 + k1[c2]) != k1[c2] || T(OFF_CDOWN + cl * 256 + k0[c2]) != k0[c2];
                if (movable && gp < gap)
                {
                    gap = gp;
                    ch = c2;
                }
            }
            [unroll] for (int c4 = 0; c4 < 3; c4++)
            {
                int up = T(OFF_CUP + cl * 256 + k1[c4]), down = T(OFF_CDOWN + cl * 256 + k0[c4]);
                bool here = c4 == ch, raise = up != k1[c4];
                k1[c4] = here && raise ? up : k1[c4];
                k0[c4] = here && !raise ? down : k0[c4];
            }
        }
    }
    [unroll] for (int c3 = 0; c3 < 4; c3++)
    {
        e0[c3] = T(u + k0[c3]);
        e1[c3] = T(u + k1[c3]);
    }
    if (!cAlpha)
    {
        e0.w = 255;
        e1.w = 255;
    }
}

// Base + offset codes (modes 9 and 13): base = v0 >> 1 with v1's top bit, offset = 6 bit signed (v1 >> 1), about twice
// the precision of direct codes. Each channel picks the best of the codes around its targets. False when the offsets'
// sum ends up negative (the decoder would swap and blue contract).
bool QuantizeOffsets(int cl, float4 a, float4 b, out int4 k0, out int4 k1, out float4 e0, out float4 e1)
{
    int q = OFF_CQ + cl * 256, u = OFF_CU + cl * 256, cu = OFF_CUP + cl * 256, cd = OFF_CDOWN + cl * 256;
    int sum = 0;
    k0 = 0;
    k1 = 0;
    e0 = 255;
    e1 = 255;
    [loop] for (int c = 0; c < (cAlpha ? 4 : 3); c++)
    {
        int bas = (int)(a[c] + 0.5), off = clamp((int)round(b[c] - a[c]), -32, 31);
        int q0 = T(q + ((bas & 0x7F) << 1)), q1 = T(q + ((bas & 0x80) | ((off & 0x3F) << 1)));
        int c0s[3] = { q0, T(cu + q0), T(cd + q0) }, c1s[3] = { q1, T(cu + q1), T(cd + q1) };
        float be = 1e30;
        int bb = 0, bo = 0;
        [unroll] for (int i = 0; i < 3; i++)
        [unroll] for (int j = 0; j < 3; j++)
        {
            int v0 = T(u + c0s[i]), v1 = T(u + c1s[j]);
            int bv = (v0 >> 1) | (v1 & 0x80), o = (v1 >> 1) & 0x3F;
            o -= (o & 0x20) << 1;
            float d0 = bv - a[c], d1 = clamp(bv + o, 0, 255) - b[c], e = d0 * d0 + d1 * d1;
            if (e < be)
            {
                be = e;
                k0[c] = c0s[i];
                k1[c] = c1s[j];
                bb = bv;
                bo = o;
            }
        }
        e0[c] = bb;
        e1[c] = clamp(bb + bo, 0, 255);
        sum += c < 3 ? bo : 0;
    }
    return sum >= 0;
}

// ---- Packing ----

void Put(inout uint4 s, inout int pos, uint v, int bits)
{
    if (bits == 0) return;
    int wd = pos >> 5, o = pos & 31;
    uint lo = v << o, hi = o + bits > 32 ? v >> (32 - o) : 0;
    s.x |= wd == 0 ? lo : 0;
    s.y |= wd == 1 ? lo : wd == 0 ? hi : 0;
    s.z |= wd == 2 ? lo : wd == 1 ? hi : 0;
    s.w |= wd == 3 ? lo : wd == 2 ? hi : 0;
    pos += bits;
}

uint4 Mask128(uint4 s, int length)
{
    [unroll] for (int i = 0; i < 4; i++)
    {
        int lo = i * 32;
        if (length <= lo) s[i] = 0;
        else if (length < lo + 32) s[i] &= (1u << (length - lo)) - 1;
    }
    return s;
}

uint4 Shl128(uint4 s, int sh)
{
    uint4 r = 0;
    int wd = sh >> 5, o = sh & 31;
    [unroll] for (int i = 3; i >= 0; i--)
    {
        int src = i - wd;
        uint v = src >= 0 ? s[src] << o : 0;
        if (o != 0 && src - 1 >= 0) v |= s[src - 1] >> (32 - o);
        r[i] = v;
    }
    return r;
}

// Integer sequence of ise[0..count) at one level, bit 0 first, cut to its length
uint4 IseEncode(int count, int level)
{
    uint4 s = 0;
    int pos = 0;
    int bits = T(OFF_LEVEL + level * 4), trits = T(OFF_LEVEL + level * 4 + 1), quints = T(OFF_LEVEL + level * 4 + 2);
    int mask = (1 << bits) - 1;
    if (trits != 0)
    {
        [loop] for (int i = 0; i < count; i += 5)
        {
            int m[5];
            int index = 0, mul = 1;
            [unroll] for (int k = 0; k < 5; k++)
            {
                int x = i + k < count ? ise[i + k] : 0;
                m[k] = x & mask;
                index += (x >> bits) * mul;
                mul *= 3;
            }
            uint t = T(OFF_TRIT + index);
            Put(s, pos, m[0], bits);
            Put(s, pos, t & 3, 2);
            Put(s, pos, m[1], bits);
            Put(s, pos, (t >> 2) & 3, 2);
            Put(s, pos, m[2], bits);
            Put(s, pos, (t >> 4) & 1, 1);
            Put(s, pos, m[3], bits);
            Put(s, pos, (t >> 5) & 3, 2);
            Put(s, pos, m[4], bits);
            Put(s, pos, (t >> 7) & 1, 1);
        }
    }
    else if (quints != 0)
    {
        [loop] for (int i = 0; i < count; i += 3)
        {
            int m[3];
            int index = 0, mul = 1;
            [unroll] for (int k = 0; k < 3; k++)
            {
                int x = i + k < count ? ise[i + k] : 0;
                m[k] = x & mask;
                index += (x >> bits) * mul;
                mul *= 5;
            }
            uint q = T(OFF_QUINT + index);
            Put(s, pos, m[0], bits);
            Put(s, pos, q & 7, 3);
            Put(s, pos, m[1], bits);
            Put(s, pos, (q >> 3) & 3, 2);
            Put(s, pos, m[2], bits);
            Put(s, pos, (q >> 5) & 3, 2);
        }
    }
    else
    {
        [loop] for (int i = 0; i < count; i++) Put(s, pos, ise[i], bits);
    }
    int length = count * bits + (trits != 0 ? (8 * count + 4) / 5 : 0) + (quints != 0 ? (7 * count + 2) / 3 : 0);
    return Mask128(s, length);
}

uint4 VoidExtent(float4 c)
{
    uint4 v = (uint4)c * 257;
    return uint4(0xFFFFFDFCu, 0xFFFFFFFFu, v.x | (v.y << 16), v.z | (v.w << 16));
}

uint4 Pack()
{
    int values = bKind == 4 ? (cAlpha ? 6 : 4) : (cAlpha ? 8 : 6);
    int cem = bKind == 4 ? (cAlpha ? 10 : 6) : (cAlpha ? 12 : 8) + (bKind & 1);
    // Two partitions, one on base + offset: modes by partition (class 2 or 3, each partition's mode bit), the second
    // partition's 2 bits right under the weights
    bool mixed = bP == 2 && (bKind == 1 || bKind == 2);
    int base = _ModeOffset + bMode * MODE_STRIDE;
    int g = T(base), level = T(base + 1);
    int cl = bCl;
    int count = T(_GridOffset + g * GRID_STRIDE + 2);
    bool dual = bC2 >= 0;

    // One partition: the decoder swaps the endpoints (and blue contracts) when the second one's sum is lower. Swap
    // them here and flip the weights, which decodes the same.
    if (bP == 1 && bKind == 0)
    {
        int u = OFF_CU + cl * 256;
        int s0 = T(u + bk[0]) + T(u + bk[2]) + T(u + bk[4]), s1 = T(u + bk[1]) + T(u + bk[3]) + T(u + bk[5]);
        if (s1 < s0)
        {
            [unroll] for (int c = 0; c < 4; c++)
            {
                int t = bk[2 * c];
                bk[2 * c] = bk[2 * c + 1];
                bk[2 * c + 1] = t;
            }
            [loop] for (int j = 0; j < count; j++)
            {
                best[j] = T(OFF_WFLIP + level * 32 + (best[j] & 255)) | (T(OFF_WFLIP + level * 32 + (best[j] >> 8)) << 8);
            }
        }
    }

    uint4 r = 0;
    int colorPos;
    if (bP == 1)
    {
        r.x = (uint)T(base + 2) | ((uint)cem << 13);
        colorPos = 17;
    }
    else
    {
        r.x = (uint)T(base + 2) | ((uint)(bP - 1) << 11) | ((uint)bSeed << 13) | (mixed ? 3u << 23 | (uint)(cAlpha ? 3 : 0) << 25 | (uint)(bKind & 1) << 27 : (uint)cem << 25);
        colorPos = 29;
    }
    [loop] for (int k = 0; k < bP; k++)
    [loop] for (int v = 0; v < values; v++) ise[k * values + v] = bk[k * 8 + v];
    r |= Shl128(IseEncode(values * bP, cl), colorPos);

    if (mixed)
    {
        int mp = 128 - T(base + 3) - 2;
        Put(r, mp, (uint)(bKind >> 1), 2);
    }
    if (dual)
    {
        int pos = 128 - T(base + 3) - 2 - (mixed ? 2 : 0);
        Put(r, pos, (uint)bC2, 2);
        [loop] for (int j = 0; j < count; j++)
        {
            ise[2 * j] = best[j] & 255;
            ise[2 * j + 1] = best[j] >> 8;
        }
    }
    else [loop] for (int j2 = 0; j2 < count; j2++) ise[j2] = best[j2] & 255;
    uint4 ws = IseEncode(dual ? 2 * count : count, level);
    r |= uint4(reversebits(ws.w), reversebits(ws.z), reversebits(ws.y), reversebits(ws.x));
    return r;
}

#endif
