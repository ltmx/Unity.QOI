using Unity.Mathematics;
using UnityEngine;

namespace Utility
{
    public static class ColorExtensions
    {
        public static Color unlerp(this Color In, float2 MinMax) => ((In.AsFloat4() - MinMax.x) / (MinMax.y - MinMax.x)).asColor();
        public static float4 AsFloat4(this Color c) => new(c.r, c.g, c.b, c.a);
        public static Color asColor(this float4 c) => new(c.x, c.y, c.z, c.w);
    }
}