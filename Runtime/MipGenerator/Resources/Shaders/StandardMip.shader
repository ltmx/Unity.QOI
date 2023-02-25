Shader "MipGenerator/StandardMip"
{
    Properties {
        _MainTex ("Texture", 2D) = "white" {}
    }
    SubShader
    {
        Cull Off ZWrite Off ZTest Always

        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag

            #include "UnityCG.cginc"

            struct appdata
            {
                float4 vertex : POSITION;
                float2 uv : TEXCOORD0;
            };

            struct v2f
            {
                float2 uv : TEXCOORD0;
                float4 vertex : SV_POSITION;
            };

            v2f vert (appdata v)
            {
                v2f o;
                o.vertex = UnityObjectToClipPos(v.vertex);
                o.uv = v.uv;
                return o;
            }

            sampler2D _MainTex;
            float4 _MainTex_TexelSize;

            float4 frag (v2f i) : SV_Target
            {
                float4 a = tex2D(_MainTex, i.uv);
                float4 b = tex2D(_MainTex, i.uv - float2(_MainTex_TexelSize.x, 0));
                float4 c = tex2D(_MainTex, i.uv - float2(0, _MainTex_TexelSize.y));
                float4 d = tex2D(_MainTex, i.uv - _MainTex_TexelSize.xy);
                
                return (a + b + c + d) / 4.0;
            }
            ENDCG
        }
    }
}
