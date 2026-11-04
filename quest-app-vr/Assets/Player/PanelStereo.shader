// Flat-window anaglyph. Left eye -> red, right eye -> cyan.
// _Pack 0 = side-by-side, 1 = over-under (top is left).
Shader "MoviePlayer/PanelStereo"
{
    Properties
    {
        _MainTex ("Video", 2D) = "black" {}
        _Color ("Tint", Color) = (1,1,1,1)
        _Pack ("Pack", Float) = 0
        _Swap ("Swap", Float) = 0
    }
    SubShader
    {
        Tags { "Queue"="Transparent" "IgnoreProjector"="True" "RenderType"="Transparent" "CanUseSpriteAtlas"="True" }
        Cull Off
        Lighting Off
        ZWrite Off
        ZTest [unity_GUIZTestMode]
        Blend SrcAlpha OneMinusSrcAlpha

        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"

            sampler2D _MainTex;
            fixed4 _Color;
            float _Pack;
            float _Swap;

            struct appdata
            {
                float4 vertex : POSITION;
                float2 uv : TEXCOORD0;
                float4 color : COLOR;
            };

            struct v2f
            {
                float4 pos : SV_POSITION;
                float2 uv : TEXCOORD0;
                fixed4 color : COLOR;
            };

            v2f vert(appdata v)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(v.vertex);
                o.uv = v.uv;
                o.color = v.color * _Color;
                return o;
            }

            fixed4 frag(v2f i) : SV_Target
            {
                float2 uvL, uvR;
                if (_Pack < 0.0)
                {
                    uvL = i.uv;
                    uvR = i.uv;
                }
                else if (_Pack < 0.5)
                {
                    uvL = float2(i.uv.x * 0.5, i.uv.y);
                    uvR = float2(i.uv.x * 0.5 + 0.5, i.uv.y);
                }
                else
                {
                    uvL = float2(i.uv.x, i.uv.y * 0.5 + 0.5);
                    uvR = float2(i.uv.x, i.uv.y * 0.5);
                }
                float l = dot(tex2D(_MainTex, uvL).rgb, float3(0.299, 0.587, 0.114));
                float r = dot(tex2D(_MainTex, uvR).rgb, float3(0.299, 0.587, 0.114));
                if (_Swap > 0.5)
                {
                    float tmp = l;
                    l = r;
                    r = tmp;
                }
                return fixed4(l, r, r, 1) * i.color;
            }
            ENDCG
        }
    }
}
