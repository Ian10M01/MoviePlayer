using System;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.XR;

namespace MoviePlayer.Player
{
    /// <summary>
    /// Picture enhancement for the cinema screen, run once per decoded video frame
    /// (not per rendered frame) so it stays cheap on Quest:
    ///
    ///   Off       copy only (with mipmaps, which removes shimmer when 4K is shown small)
    ///   Sharpen   RCAS edge-aware sharpening at source resolution
    ///   Upscale   Catmull-Rom edge-clamped upscale to up to 2x (capped at 4K) + RCAS,
    ///             the same two-stage approach as AMD FSR 1
    ///
    /// The result goes to the screen material, and the frame's average colour is read back
    /// a few times a second for the theater's screen glow (OnAverageColour).
    /// </summary>
    [RequireComponent(typeof(StereoScreenRenderer))]
    public class ClarityStack : MonoBehaviour
    {
        public enum EnhanceMode { Off, Sharpen, Upscale }

        // ── Inspector ─────────────────────────────────────────────────────────
        [Header("Enhancement")]
        public EnhanceMode Mode = EnhanceMode.Upscale;
        [Range(0f, 1f)] public float SharpenStrength = 0.6f;

        [Header("Colour")]
        [Range(0.5f, 1.5f)] public float Brightness = 1f;
        [Range(0.5f, 1.5f)] public float Contrast   = 1f;
        [Range(-1f, 1f)]    public float ChromaBoost = 0.05f;
        public bool Deband = true;

        [Header("Headset")]
        [Tooltip("Eye-buffer resolution scale on Quest. Above 1 renders more pixels for a sharper screen.")]
        public float EyeResolutionScale = 1.3f;

        [Header("Material")]
        [Tooltip("Screen material (ClarityShader). Kept for scene compatibility; the screen renderer's own material is used.")]
        public Material ClarityMaterial;

        /// <summary>Average colour of the current frame and its brightness (0..1).</summary>
        public event Action<Color, float> OnAverageColour;

        // ── State ─────────────────────────────────────────────────────────────
        private StereoScreenRenderer _screen;
        private Material      _enhanceMat;
        private Texture       _source;
        private Func<long>    _frameIndex;
        private long          _lastFrame = long.MinValue;
        private EnhanceMode   _lastMode;
        private float         _lastSharpness = -1f;
        private RenderTexture _upscaled;   // intermediate (Upscale mode)
        private RenderTexture _output;     // what the screen samples (mipmapped)
        private float         _nextReadback;
        private bool          _readbackPending;

        private static readonly int PropSharpness  = Shader.PropertyToID("_Sharpness");
        private static readonly int PropBrightness = Shader.PropertyToID("_Brightness");
        private static readonly int PropContrast   = Shader.PropertyToID("_Contrast");
        private static readonly int PropChroma     = Shader.PropertyToID("_ChromaBoost");
        private static readonly int PropDeband     = Shader.PropertyToID("_DebandEnabled");

        private const int PassCopy = 0, PassUpscale = 1, PassRcas = 2;
        private const int MaxOutputWidth = 3840, MaxOutputHeight = 2160;

        // ─────────────────────────────────────────────────────────────────────
        #region Unity Lifecycle

        private void Awake()
        {
            _screen = GetComponent<StereoScreenRenderer>();
            var shader = Shader.Find("Hidden/MoviePlayer/VideoEnhance");
            if (shader != null) _enhanceMat = new Material(shader);
            else Debug.LogWarning("[Clarity] VideoEnhance shader missing — showing video unprocessed.");
        }

        private void Start()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            XRSettings.eyeTextureResolutionScale = EyeResolutionScale;
            Debug.Log($"[Clarity] Eye texture resolution scale {EyeResolutionScale}");
#endif
        }

        private void OnDestroy()
        {
            ReleaseTargets();
            if (_enhanceMat != null) Destroy(_enhanceMat);
        }

        private void LateUpdate()
        {
            PushColourProperties();
            if (_source == null) return;

            long frame = _frameIndex != null ? _frameIndex() : Time.frameCount;
            bool settingsChanged = Mode != _lastMode || !Mathf.Approximately(SharpenStrength, _lastSharpness);
            if (frame == _lastFrame && !settingsChanged) return;
            _lastFrame = frame;
            _lastMode = Mode;
            _lastSharpness = SharpenStrength;

            Process();

            if (Time.unscaledTime >= _nextReadback && !_readbackPending && _output != null && SystemInfo.supportsAsyncGPUReadback)
            {
                _nextReadback = Time.unscaledTime + 0.15f;
                _readbackPending = true;
                AsyncGPUReadback.Request(_output, _output.mipmapCount - 1, OnReadback);
            }
        }

        #endregion

        // ─────────────────────────────────────────────────────────────────────
        #region Public API

        /// <summary>Feeds the decoded video texture. frameIndex returns the decoder's frame number.</summary>
        public void SetSource(Texture source, Func<long> frameIndex)
        {
            _source = source;
            _frameIndex = frameIndex;
            _lastFrame = long.MinValue;
            if (_enhanceMat == null) _screen.SetMainTexture(source);
        }

        #endregion

        // ─────────────────────────────────────────────────────────────────────
        #region Processing

        private void Process()
        {
            if (_enhanceMat == null) return;

            int sw = _source.width, sh = _source.height;
            int ow = sw, oh = sh;
            if (Mode == EnhanceMode.Upscale)
            {
                float k = Mathf.Min(2f, Mathf.Min((float)MaxOutputWidth / sw, (float)MaxOutputHeight / sh));
                if (k > 1.05f) { ow = Mathf.RoundToInt(sw * k); oh = Mathf.RoundToInt(sh * k); }
            }

            EnsureOutput(ow, oh);
            _enhanceMat.SetFloat(PropSharpness, SharpenStrength);

            switch (Mode)
            {
                case EnhanceMode.Off:
                    Graphics.Blit(_source, _output, _enhanceMat, PassCopy);
                    break;

                case EnhanceMode.Sharpen:
                    Graphics.Blit(_source, _output, _enhanceMat, PassRcas);
                    break;

                case EnhanceMode.Upscale:
                    if (ow == sw)
                    {
                        // Already 4K-class: nothing to upscale, just sharpen.
                        Graphics.Blit(_source, _output, _enhanceMat, PassRcas);
                    }
                    else
                    {
                        EnsureUpscaled(ow, oh);
                        Graphics.Blit(_source, _upscaled, _enhanceMat, PassUpscale);
                        Graphics.Blit(_upscaled, _output, _enhanceMat, PassRcas);
                    }
                    break;
            }
        }

        private void EnsureOutput(int w, int h)
        {
            if (_output != null && _output.width == w && _output.height == h) return;
            if (_output != null) { _output.Release(); Destroy(_output); }

            _output = new RenderTexture(w, h, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB)
            {
                name = "EnhancedVideo",
                useMipMap = true,
                autoGenerateMips = true,
                filterMode = FilterMode.Trilinear,
                anisoLevel = 4,
                wrapMode = TextureWrapMode.Clamp,
            };
            _output.Create();
            _screen.SetMainTexture(_output);
            Debug.Log($"[Clarity] Output {w}x{h} (source {_source.width}x{_source.height}, mode {Mode})");
        }

        private void EnsureUpscaled(int w, int h)
        {
            if (_upscaled != null && _upscaled.width == w && _upscaled.height == h) return;
            if (_upscaled != null) { _upscaled.Release(); Destroy(_upscaled); }
            _upscaled = new RenderTexture(w, h, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB)
            {
                name = "UpscaledVideo",
                filterMode = FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Clamp,
            };
            _upscaled.Create();
        }

        private void ReleaseTargets()
        {
            if (_output   != null) { _output.Release();   Destroy(_output);   _output = null; }
            if (_upscaled != null) { _upscaled.Release(); Destroy(_upscaled); _upscaled = null; }
        }

        private void OnReadback(AsyncGPUReadbackRequest req)
        {
            _readbackPending = false;
            if (req.hasError || this == null) return;
            var data = req.GetData<Color32>();
            if (data.Length == 0) return;
            Color c = data[0];
            float brightness = 0.2126f * c.r + 0.7152f * c.g + 0.0722f * c.b;
            OnAverageColour?.Invoke(c, brightness);
        }

        private void PushColourProperties()
        {
            var mat = _screen != null ? _screen.ActiveMaterial : null;
            if (mat == null) return;
            mat.SetFloat(PropBrightness, Brightness);
            mat.SetFloat(PropContrast,   Contrast);
            mat.SetFloat(PropChroma,     ChromaBoost);
            mat.SetFloat(PropDeband,     Deband ? 1f : 0f);
        }

        #endregion
    }
}
