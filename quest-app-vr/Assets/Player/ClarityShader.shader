// ClarityShader — draws the cinema screen (flat, curved or 180/360 sphere).
//
// The heavy lifting (upscale + RCAS sharpening + mip generation) happens once per
// video frame in VideoEnhancer; this shader only picks each eye's half of a 3D frame,
// samples it with trilinear filtering and applies colour controls:
//   1. Per-eye UV  — _LeftEyeScaleOffset / _RightEyeScaleOffset (xy = scale, zw = offset)
//   2. Colour      — brightness, contrast and saturation (BT.709 Y'CbCr)
//   3. Deband      — sub-LSB dither to break 8-bit banding in dark scenes
//
// Quest renders both eyes in one pass (multiview / single-pass instanced). The eye
// index comes from unity_StereoEyeIndex, so each eye gets its own half of SBS/OU video.

Shader "MoviePlayer/ClarityShader"
{
    Properties
    {
        _MainTex        ("Video Texture",       2D)     = "black" {}
        _Brightness     ("Brightness",          Range(0.5,1.5)) = 1.0
        _Contrast       ("Contrast",            Range(0.5,1.5)) = 1.0
        _ChromaBoost    ("Saturation Boost",    Range(-1,1)) = 0.05
        _DebandEnabled  ("Deband",              Float)  = 1.0
        _EyeSide        ("Eye Side (0=L,1=R)",  Float)  = 0.0

        // Per-eye UV: xy=scale, zw=offset (set by StereoScreenRenderer)
        _LeftEyeScaleOffset ("Left Eye Scale+Offset",  Vector) = (1,1,0,0)
        _RightEyeScaleOffset("Right Eye Scale+Offset", Vector) = (1,1,0,0)
    }

    SubShader
    {
        Tags
        {
            "RenderType"      = "Opaque"
            "RenderPipeline"  = "UniversalPipeline"
            "Queue"           = "Geometry"
        }

        Pass
        {
            Name "ClarityPass"
            ZWrite On
            ZTest LEqual
            Cull Off

            HLSLPROGRAM
            #pragma vertex   vert
            #pragma fragment frag
            #pragma multi_compile_instancing
            #pragma multi_compile _ STEREO_INSTANCING_ON STEREO_MULTIVIEW_ON

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            TEXTURE2D(_MainTex);
            SAMPLER(sampler_MainTex);

            CBUFFER_START(UnityPerMaterial)
                float4 _MainTex_ST;
                float4 _MainTex_TexelSize;
                float  _Brightness;
                float  _Contrast;
                float  _ChromaBoost;
                float  _DebandEnabled;
                float  _EyeSide;
                float4 _LeftEyeScaleOffset;
                float4 _RightEyeScaleOffset;
            CBUFFER_END

            struct Attributes
            {
                float4 positionOS : POSITION;
                float2 uv         : TEXCOORD0;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float2 uv         : TEXCOORD0;
                UNITY_VERTEX_OUTPUT_STEREO
            };

            Varyings vert(Attributes IN)
            {
                Varyings OUT;
                UNITY_SETUP_INSTANCE_ID(IN);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(OUT);

                OUT.positionCS = TransformObjectToHClip(IN.positionOS.xyz);

                #if defined(UNITY_STEREO_INSTANCING_ENABLED) || defined(UNITY_STEREO_MULTIVIEW_ENABLED)
                    float eyeIdx = (float)unity_StereoEyeIndex;
                #else
                    float eyeIdx = _EyeSide;
                #endif

                float4 scaleOffset = lerp(_LeftEyeScaleOffset, _RightEyeScaleOffset, eyeIdx);
                OUT.uv = IN.uv * scaleOffset.xy + scaleOffset.zw;
                return OUT;
            }

            float3 ApplyColour(float3 rgb)
            {
                rgb *= _Brightness;
                rgb = (rgb - 0.5) * _Contrast + 0.5;

                // BT.709 Y'CbCr saturation
                float Y  =  0.2126 * rgb.r + 0.7152 * rgb.g + 0.0722 * rgb.b;
                float Cb = -0.1146 * rgb.r - 0.3854 * rgb.g + 0.5000 * rgb.b;
                float Cr =  0.5000 * rgb.r - 0.4542 * rgb.g - 0.0458 * rgb.b;
                float s = 1.0 + _ChromaBoost;
                Cb *= s; Cr *= s;
                float3 o;
                o.r = Y + 1.5748 * Cr;
                o.g = Y - 0.1873 * Cb - 0.4681 * Cr;
                o.b = Y + 1.8556 * Cb;
                return saturate(o);
            }

            half4 frag(Varyings IN) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(IN);

                float3 col = SAMPLE_TEXTURE2D(_MainTex, sampler_MainTex, IN.uv).rgb;
                col = ApplyColour(col);

                if (_DebandEnabled > 0.5)
                {
                    // Interleaved gradient noise (Jimenez 2014), screen-space
                    float n = frac(52.9829189 * frac(dot(IN.positionCS.xy, float2(0.06711056, 0.00583715))));
                    col += (n - 0.5) * (1.0 / 255.0);
                }

                return half4(col, 1.0);
            }
            ENDHLSL
        }
    }

    FallBack "Hidden/Universal Render Pipeline/FallbackError"
}
