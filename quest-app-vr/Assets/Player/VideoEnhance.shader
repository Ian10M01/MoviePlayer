// VideoEnhance — per-video-frame image passes run by VideoEnhancer with Graphics.Blit.
//
//   Pass 0  Copy       — plain copy (destination generates mips for clean minification)
//   Pass 1  Upscale    — Catmull-Rom bicubic reconstruction clamped to the 2x2 source
//                        neighbourhood (no ringing), the edge-preserving core of FSR1 EASU
//   Pass 2  RCAS       — AMD FidelityFX Robust Contrast Adaptive Sharpening (MIT licence)
//                        https://github.com/GPUOpen-Effects/FidelityFX-FSR

Shader "Hidden/MoviePlayer/VideoEnhance"
{
    Properties
    {
        _MainTex ("Source", 2D) = "black" {}
        _Sharpness ("Sharpness", Range(0,1)) = 0.6
    }

    CGINCLUDE
    #include "UnityCG.cginc"

    sampler2D _MainTex;
    float4    _MainTex_TexelSize;   // (1/w, 1/h, w, h)
    float     _Sharpness;
    ENDCG

    SubShader
    {
        Cull Off ZWrite Off ZTest Always

        // 0 ── Copy
        Pass
        {
            CGPROGRAM
            #pragma vertex vert_img
            #pragma fragment frag
            float4 frag(v2f_img i) : SV_Target
            {
                return float4(tex2D(_MainTex, i.uv).rgb, 1);
            }
            ENDCG
        }

        // 1 ── Catmull-Rom upscale (9 bilinear taps) with anti-ringing clamp
        Pass
        {
            CGPROGRAM
            #pragma vertex vert_img
            #pragma fragment frag
            float4 frag(v2f_img i) : SV_Target
            {
                float2 texSize = _MainTex_TexelSize.zw;
                float2 inv     = _MainTex_TexelSize.xy;
                float2 samplePos = i.uv * texSize;
                float2 tc1 = floor(samplePos - 0.5) + 0.5;
                float2 f   = samplePos - tc1;

                float2 w0 = f * (-0.5 + f * (1.0 - 0.5 * f));
                float2 w1 = 1.0 + f * f * (-2.5 + 1.5 * f);
                float2 w2 = f * (0.5 + f * (2.0 - 1.5 * f));
                float2 w3 = f * f * (-0.5 + 0.5 * f);
                float2 w12 = w1 + w2;
                float2 o12 = w2 / w12;

                float2 tc0  = (tc1 - 1.0) * inv;
                float2 tc3  = (tc1 + 2.0) * inv;
                float2 tc12 = (tc1 + o12) * inv;

                float3 c =
                    tex2D(_MainTex, float2(tc0.x,  tc0.y)).rgb  * w0.x  * w0.y +
                    tex2D(_MainTex, float2(tc12.x, tc0.y)).rgb  * w12.x * w0.y +
                    tex2D(_MainTex, float2(tc3.x,  tc0.y)).rgb  * w3.x  * w0.y +
                    tex2D(_MainTex, float2(tc0.x,  tc12.y)).rgb * w0.x  * w12.y +
                    tex2D(_MainTex, float2(tc12.x, tc12.y)).rgb * w12.x * w12.y +
                    tex2D(_MainTex, float2(tc3.x,  tc12.y)).rgb * w3.x  * w12.y +
                    tex2D(_MainTex, float2(tc0.x,  tc3.y)).rgb  * w0.x  * w3.y +
                    tex2D(_MainTex, float2(tc12.x, tc3.y)).rgb  * w12.x * w3.y +
                    tex2D(_MainTex, float2(tc3.x,  tc3.y)).rgb  * w3.x  * w3.y;

                // Clamp to the four nearest source texels so edges stay crisp without halos.
                float2 b = tc1 * inv;
                float3 a00 = tex2D(_MainTex, b).rgb;
                float3 a10 = tex2D(_MainTex, b + float2(inv.x, 0)).rgb;
                float3 a01 = tex2D(_MainTex, b + float2(0, inv.y)).rgb;
                float3 a11 = tex2D(_MainTex, b + inv).rgb;
                float3 mn = min(min(a00, a10), min(a01, a11));
                float3 mx = max(max(a00, a10), max(a01, a11));
                return float4(clamp(c, mn, mx), 1);
            }
            ENDCG
        }

        // 2 ── RCAS
        Pass
        {
            CGPROGRAM
            #pragma vertex vert_img
            #pragma fragment frag
            float Max3(float3 v) { return max(v.x, max(v.y, v.z)); }

            float4 frag(v2f_img i) : SV_Target
            {
                float2 t = _MainTex_TexelSize.xy;
                float3 b = tex2D(_MainTex, i.uv + float2(0, -t.y)).rgb;
                float3 d = tex2D(_MainTex, i.uv + float2(-t.x, 0)).rgb;
                float3 e = tex2D(_MainTex, i.uv).rgb;
                float3 f = tex2D(_MainTex, i.uv + float2(t.x, 0)).rgb;
                float3 h = tex2D(_MainTex, i.uv + float2(0, t.y)).rgb;

                float3 mn4 = min(min(b, d), min(f, h));
                float3 mx4 = max(max(b, d), max(f, h));

                // Largest negative lobe that keeps the result inside [0,1] (no clipping/ringing)
                const float2 peakC = float2(1.0, -4.0);
                float3 hitMin = min(mn4, e) / max(4.0 * mx4, 1e-4);
                float3 hitMax = (peakC.x - max(mx4, e)) / min(4.0 * min(mn4, e) + peakC.y, -1e-4);
                float3 lobeRGB = max(-hitMin, hitMax);
                const float RCAS_LIMIT = 0.25 - (1.0 / 16.0);
                float lobe = max(-RCAS_LIMIT, min(Max3(lobeRGB), 0.0)) * _Sharpness;

                float3 c = (lobe * (b + d + f + h) + e) / (4.0 * lobe + 1.0);
                return float4(saturate(c), 1);
            }
            ENDCG
        }
    }
}
