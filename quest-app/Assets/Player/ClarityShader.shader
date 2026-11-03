// ClarityShader — Cinema post-processing for MoviePlayer Quest app.
//
// Passes (all combined in one shader; ordered in the fragment function):
//   1. Chroma Restore  — slight saturation boost in YCbCr space
//   2. Deband          — dithered noise to break 8-bit banding
//   3. Cinema RCAS     — full Robust Contrast Adaptive Sharpening (edge-aware)
//   4. HDR Remap       — Reinhard-like tone map with configurable knee/peak
//
// Per-eye UV is handled via _LeftEyeScaleOffset / _RightEyeScaleOffset
// (xy = scale, zw = offset).  The host MonoBehaviour (ClarityStack) updates
// _SharpenStrength, _ChromaBoost, _HdrPeakNits, _DebandEnabled each frame.
//
// _EyeSide is set to 0 (left) or 1 (right) by Unity's SRP when rendering
// each eye pass.  For single-pass instanced rendering the vertex shader
// reads unity_StereoEyeIndex directly.

Shader "MoviePlayer/ClarityShader"
{
    Properties
    {
        _MainTex        ("Video Texture",       2D)     = "black" {}
        _SharpenStrength("Sharpen Strength",    Range(0,1)) = 0.5
        _ChromaBoost    ("Chroma Boost",        Range(0,1)) = 0.1
        _HdrPeakNits    ("HDR Peak (nits)",     Float)  = 500.0
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
            Cull Back

            HLSLPROGRAM
            #pragma vertex   vert
            #pragma fragment frag
            #pragma multi_compile_instancing
            #pragma multi_compile _ UNITY_SINGLE_PASS_STEREO STEREO_INSTANCING_ON

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            // ── Textures & Samplers ───────────────────────────────────────────
            TEXTURE2D(_MainTex);
            SAMPLER(sampler_MainTex);

            // ── CBUFFER (SRP Batcher compatible) ─────────────────────────────
            CBUFFER_START(UnityPerMaterial)
                float4 _MainTex_ST;
                float4 _MainTex_TexelSize;   // (1/w, 1/h, w, h)
                float  _SharpenStrength;
                float  _ChromaBoost;
                float  _HdrPeakNits;
                float  _DebandEnabled;
                float  _EyeSide;
                float4 _LeftEyeScaleOffset;
                float4 _RightEyeScaleOffset;
            CBUFFER_END

            // ── Vertex IO ─────────────────────────────────────────────────────
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

            // ── Vertex Shader ─────────────────────────────────────────────────
            Varyings vert(Attributes IN)
            {
                Varyings OUT;
                UNITY_SETUP_INSTANCE_ID(IN);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(OUT);

                OUT.positionCS = TransformObjectToHClip(IN.positionOS.xyz);

                // Select per-eye UV transform
                #if defined(UNITY_SINGLE_PASS_STEREO) || defined(STEREO_INSTANCING_ON)
                    float eyeIdx = (float)unity_StereoEyeIndex;
                #else
                    float eyeIdx = _EyeSide;
                #endif

                float4 scaleOffset = lerp(_LeftEyeScaleOffset, _RightEyeScaleOffset, eyeIdx);
                OUT.uv = IN.uv * scaleOffset.xy + scaleOffset.zw;
                return OUT;
            }

            // ═════════════════════════════════════════════════════════════════
            // ── Utility functions ─────────────────────────────────────────────

            // ── 1. YCbCr Chroma Boost ─────────────────────────────────────────
            float3 ApplyChromaBoost(float3 rgb, float boost)
            {
                // BT.709 RGB → Y'CbCr
                float Y  =  0.2126 * rgb.r + 0.7152 * rgb.g + 0.0722 * rgb.b;
                float Cb = -0.1146 * rgb.r - 0.3854 * rgb.g + 0.5000 * rgb.b;
                float Cr =  0.5000 * rgb.r - 0.4542 * rgb.g - 0.0458 * rgb.b;

                // Scale chroma channels
                float scale = 1.0 + boost;
                Cb *= scale;
                Cr *= scale;

                // Back to RGB
                float3 out_rgb;
                out_rgb.r = Y + 1.5748 * Cr;
                out_rgb.g = Y - 0.1873 * Cb - 0.4681 * Cr;
                out_rgb.b = Y + 1.8556 * Cb;
                return saturate(out_rgb);
            }

            // ── 2. Deband (dithered noise) ────────────────────────────────────
            float3 ApplyDeband(float3 rgb, float2 uv)
            {
                // Interleaved gradient noise (Jimenez 2014) — avoids tiling
                float noise = frac(52.9829189 * frac(dot(uv, float2(0.06711056, 0.00583715))));
                // Remap from [0,1] to [-0.5/255, 0.5/255] — sub-LSB dither
                float dither = (noise - 0.5) * (1.0 / 255.0);
                return rgb + dither;
            }

            // ── 3. Cinema RCAS (Robust Contrast Adaptive Sharpening) ──────────
            // Reference: AMD FidelityFX RCAS (MIT licence)
            // https://github.com/GPUOpen-Effects/FidelityFX-FSR/blob/master/ffx-fsr/ffx_fsr1.h
            //
            // RCAS samples a 5-tap cross and computes a min/max neighbourhood.
            // The sharpening weight is clamped so that:
            //   a) edges are not over-sharpened (no ringing)
            //   b) film grain is left alone (noise below threshold not boosted)
            float3 ApplyRCAS(TEXTURE2D_PARAM(tex, smp), float2 uv, float strength)
            {
                float2 texelSize = _MainTex_TexelSize.xy;   // (1/w, 1/h)

                // 5-tap cross neighbourhood
                float3 c  = SAMPLE_TEXTURE2D(tex, smp, uv).rgb;                                  // centre
                float3 n  = SAMPLE_TEXTURE2D(tex, smp, uv + float2( 0, 1) * texelSize).rgb;      // N
                float3 s  = SAMPLE_TEXTURE2D(tex, smp, uv + float2( 0,-1) * texelSize).rgb;      // S
                float3 e  = SAMPLE_TEXTURE2D(tex, smp, uv + float2( 1, 0) * texelSize).rgb;      // E
                float3 w  = SAMPLE_TEXTURE2D(tex, smp, uv + float2(-1, 0) * texelSize).rgb;      // W

                // Luma weights (BT.709)
                float lumaC = dot(c, float3(0.2126, 0.7152, 0.0722));
                float lumaN = dot(n, float3(0.2126, 0.7152, 0.0722));
                float lumaS = dot(s, float3(0.2126, 0.7152, 0.0722));
                float lumaE = dot(e, float3(0.2126, 0.7152, 0.0722));
                float lumaW = dot(w, float3(0.2126, 0.7152, 0.0722));

                // Neighbourhood min/max for anti-ringing clamp
                float lumaMin = min(lumaC, min(min(lumaN, lumaS), min(lumaE, lumaW)));
                float lumaMax = max(lumaC, max(max(lumaN, lumaS), max(lumaE, lumaW)));

                // RCAS sharpening weight
                // w = -strength / (8 * max(lumaMin, 1 - lumaMax))
                // Clamped to [-0.25, 0] (no amplification of DC)
                float rcasStrength = strength * 0.25; // remap user 0-1 → 0-0.25
                float lumaMinMaxRange = max(lumaMin, 1.0 - lumaMax);
                // Protect against div-by-zero in flat regions
                lumaMinMaxRange = max(lumaMinMaxRange, 1.0 / 256.0);
                float weight = -rcasStrength / (4.0 * lumaMinMaxRange);
                weight = max(weight, -0.25); // clamp lower bound

                // Apply sharpening filter
                float4 weighted_sum = weight * (float4(n, 1) + float4(s, 1) + float4(e, 1) + float4(w, 1));
                float3 sharpened = (c + weighted_sum.rgb) / (1.0 + weighted_sum.w);

                // Anti-ringing: clamp to neighbourhood
                sharpened = clamp(sharpened,
                    min(c, min(min(n, s), min(e, w))),
                    max(c, max(max(n, s), max(e, w))));

                return sharpened;
            }

            // ── 4. HDR Luminance Remap ────────────────────────────────────────
            // Maps SDR [0,1] signal through a Reinhard-variant curve targeting
            // peakNits on Quest 3 display (rated ~500 nit peak in privileged mode).
            //
            // Formula: L_out = L_in * (1 + L_in / knee²) / (1 + L_in)
            // where knee = peakNits / 100.0  (SDR reference = 100 nit)
            float3 ApplyHdrRemap(float3 rgb, float peakNits)
            {
                float knee = peakNits / 100.0;
                float kneeInv = 1.0 / (knee * knee);

                // Per-channel Reinhard Extended
                float3 num = rgb * (1.0 + rgb * kneeInv);
                float3 den = 1.0 + rgb;
                return num / den;
            }

            // ═════════════════════════════════════════════════════════════════
            // ── Fragment Shader ───────────────────────────────────────────────
            half4 frag(Varyings IN) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(IN);

                float2 uv = IN.uv;

                // Pass 1 — Chroma Restore
                float3 col = SAMPLE_TEXTURE2D(_MainTex, sampler_MainTex, uv).rgb;
                col = ApplyChromaBoost(col, _ChromaBoost);

                // Pass 2 — Deband
                if (_DebandEnabled > 0.5)
                    col = ApplyDeband(col, IN.positionCS.xy); // use screen-space pos for noise

                // Pass 3 — Cinema RCAS
                if (_SharpenStrength > 0.001)
                    col = ApplyRCAS(TEXTURE2D_ARGS(_MainTex, sampler_MainTex), uv, _SharpenStrength);

                // Pass 4 — HDR Luminance Remap
                col = ApplyHdrRemap(col, _HdrPeakNits);

                return half4(col, 1.0);
            }

            ENDHLSL
        }
    }

    FallBack "Hidden/Universal Render Pipeline/FallbackError"
}
