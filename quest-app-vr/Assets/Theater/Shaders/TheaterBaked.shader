// TheaterBaked — the Blender-baked cinema (Quest 2 friendly, no realtime lights).
//
//   UV0  tiling albedo (metres * _BaseMap_ST)
//   UV1  shared lightmap atlas baked in Cycles:
//          rgb = sqrt(house light / _LightRange.x)   coves, sconces, step lights...
//          a   = sqrt(screen light / _LightRange.y)  light from a white 16x9 screen
//   colour = albedo * (house * _TheaterHouseLight + screen * _GlowColor * _GlowBrightness)
// _GlowColor / _GlowBrightness / _TheaterHouseLight are globals set by
// TheaterEnvironment.SetScreenGlow, so the film's colour spills onto the room.
Shader "MoviePlayer/TheaterBaked"
{
    Properties
    {
        _BaseMap    ("Albedo", 2D) = "white" {}
        _BaseColor  ("Tint", Color) = (1,1,1,1)
        _LightMap   ("Baked Light (rgb house, a screen)", 2D) = "black" {}
        _LightRange ("Light Range (x house, y screen)", Vector) = (1,1,0,0)
    }
    SubShader
    {
        Tags { "RenderType"="Opaque" "RenderPipeline"="UniversalPipeline" "Queue"="Geometry" }
        Pass
        {
            Name "TheaterBaked"
            Tags { "LightMode"="UniversalForward" }
            Cull Back
            ZWrite On

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_instancing
            #pragma multi_compile _ STEREO_INSTANCING_ON STEREO_MULTIVIEW_ON
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            TEXTURE2D(_BaseMap);  SAMPLER(sampler_BaseMap);
            TEXTURE2D(_LightMap); SAMPLER(sampler_LightMap);

            CBUFFER_START(UnityPerMaterial)
                float4 _BaseMap_ST;
                float4 _BaseColor;
                float4 _LightRange;
            CBUFFER_END

            // Globals (TheaterEnvironment)
            float4 _GlowColor;
            float  _GlowBrightness;
            float  _TheaterHouseLight;

            struct Attributes
            {
                float4 positionOS : POSITION;
                float2 uv0        : TEXCOORD0;
                float2 uv1        : TEXCOORD1;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };
            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float2 uv0        : TEXCOORD0;
                float2 uv1        : TEXCOORD1;
                UNITY_VERTEX_OUTPUT_STEREO
            };

            Varyings vert(Attributes v)
            {
                Varyings o;
                UNITY_SETUP_INSTANCE_ID(v);
                ZERO_INITIALIZE(Varyings, o);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);
                o.positionCS = TransformObjectToHClip(v.positionOS.xyz);
                o.uv0 = TRANSFORM_TEX(v.uv0, _BaseMap);
                o.uv1 = v.uv1;
                return o;
            }

            half4 frag(Varyings i) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(i);
                half3 albedo = SAMPLE_TEXTURE2D(_BaseMap, sampler_BaseMap, i.uv0).rgb * _BaseColor.rgb;
                half4 lm = SAMPLE_TEXTURE2D(_LightMap, sampler_LightMap, i.uv1);
                half3 house  = lm.rgb * lm.rgb * _LightRange.x * _TheaterHouseLight;
                half  screen = lm.a * lm.a * _LightRange.y;
                half3 col = albedo * (house + screen * _GlowColor.rgb * _GlowBrightness);
                // sub-LSB dither: hides 8-bit banding in the dark room
                float n = frac(52.9829189 * frac(dot(i.positionCS.xy, float2(0.06711056, 0.00583715))));
                col += (n - 0.5) / 255.0;
                return half4(col, 1);
            }
            ENDHLSL
        }
    }
}
