// HlsPlayerController.cs
//
// PRIMARY BACKEND: UnityEngine.Video.VideoPlayer (Android MediaPlayer underneath).
// Android MediaPlayer natively handles HLS m3u8 over HTTPS.
//
// LEGACY BACKEND: ExoPlayer JNI path — enabled via `useExoPlayer = true`.
// Kept for future use; the Java plugin still compiles because the callback
// methods OnExoReady / OnExoBuffering / OnExoError are always present.

using System;
using System.Collections;
using UnityEngine;
using UnityEngine.Video;
using MoviePlayer.Player;

#if UNITY_ANDROID && !UNITY_EDITOR
using UnityEngine.Android;
#endif

namespace MoviePlayer.Player
{
    public class HlsPlayerController : MonoBehaviour
    {
        // ── Inspector ─────────────────────────────────────────────────────────
        [Header("Backend")]
        [Tooltip("False = Unity VideoPlayer (recommended). True = ExoPlayer JNI.")]
        [SerializeField] bool useExoPlayer = false;

        [Header("Screen")]
        [Tooltip("The StereoScreenRenderer to receive the RenderTexture.")]
        [SerializeField] StereoScreenRenderer ScreenRenderer;

        [Header("Buffer")]
        public long MinBufferMs         = 60_000L;
        public int  PreCacheTargetSeconds = 60;

        // ── Events ────────────────────────────────────────────────────────────
        public event Action           OnReady;
        public event Action<float>    OnBuffering;   // buffered seconds
        public event Action<string>   OnError;

        // ── State ─────────────────────────────────────────────────────────────
        public bool IsReady { get; private set; }

        // ── VideoPlayer backend ───────────────────────────────────────────────
        private VideoPlayer   _vp;
        private RenderTexture _rt;
        private bool          _vpReady;
        private float         _preCacheStartTime;
        private bool          _preCacheMode;
        private float         _bufTimer;
        private const float   BufReportInterval = 1f;

        // ── ExoPlayer JNI backend (legacy) ────────────────────────────────────
        private readonly System.Collections.Concurrent.ConcurrentQueue<Action>
            _mainQ = new System.Collections.Concurrent.ConcurrentQueue<Action>();

#if UNITY_ANDROID && !UNITY_EDITOR
        private AndroidJavaObject _exoPlayer;
        private AndroidJavaObject _unityActivity;
        private AndroidJavaClass  _playerHelper;
#endif

        // ─────────────────────────────────────────────────────────────────────
        #region Public API

        public void Initialize(string hlsUrl, string bearerToken)
        {
            string urlWithToken = AppendToken(hlsUrl, bearerToken);
            Debug.Log($"[HLS] Initialize → {urlWithToken}");

            // Reset so OnReady fires again for each new video
            IsReady  = false;
            _vpReady = false;

            if (useExoPlayer)
            {
                InitExoPlayer(urlWithToken);
                return;
            }

            InitVideoPlayer(urlWithToken);
        }

        public void Play()
        {
            if (!useExoPlayer && _vp != null) { _vp.Play(); return; }
#if UNITY_ANDROID && !UNITY_EDITOR
            if (useExoPlayer) CallExo("setPlayWhenReady", true);
#else
            Debug.Log("[HLS] Play()");
#endif
        }

        /// <summary>Stops playback and releases the VideoPlayer (VideoPlayer backend).</summary>
        public void Stop()
        {
            if (_vp != null)
            {
                _vp.prepareCompleted -= OnVpPrepared;
                _vp.errorReceived    -= OnVpError;
                _vp.Stop();
                Destroy(_vp);
                _vp = null;
            }
            IsReady  = false;
            _vpReady = false;
            Debug.Log("[HLS] Stop()");
        }

        public void Pause()
        {
            if (!useExoPlayer && _vp != null) { _vp.Pause(); return; }
#if UNITY_ANDROID && !UNITY_EDITOR
            if (useExoPlayer) CallExo("setPlayWhenReady", false);
#else
            Debug.Log("[HLS] Pause()");
#endif
        }

        public void SeekTo(long positionMs)
        {
            if (!useExoPlayer && _vp != null)
            {
                _vp.time = positionMs / 1000.0;
                return;
            }
#if UNITY_ANDROID && !UNITY_EDITOR
            if (useExoPlayer) CallExo("seekTo", positionMs);
#else
            Debug.Log($"[HLS] SeekTo({positionMs})");
#endif
        }

        public long GetPositionMs()
        {
            if (!useExoPlayer && _vp != null)
                return (long)(_vp.time * 1000.0);
#if UNITY_ANDROID && !UNITY_EDITOR
            if (useExoPlayer && _exoPlayer != null)
            {
                AndroidJNI.AttachCurrentThread();
                return _exoPlayer.Call<long>("getCurrentPosition");
            }
#endif
            return 0L;
        }

        /// <summary>Total length in milliseconds (0 until prepared).</summary>
        public long GetDurationMs()
        {
            if (!useExoPlayer && _vp != null && _vpReady)
                return (long)(_vp.length * 1000.0);
            return 0L;
        }

        public bool IsPlaying => !useExoPlayer && _vp != null && _vp.isPlaying;

        public void SetLooping(bool loop)
        {
            if (_vp != null) _vp.isLooping = loop;
        }

        public long GetBufferedMs()
        {
            if (!useExoPlayer && _vp != null)
            {
                // VideoPlayer doesn't expose buffered bytes directly;
                // estimate from current position + a generous lookahead.
                return (long)(_vp.time * 1000.0) + 30_000L;
            }
#if UNITY_ANDROID && !UNITY_EDITOR
            if (useExoPlayer && _exoPlayer != null)
            {
                AndroidJNI.AttachCurrentThread();
                return _exoPlayer.Call<long>("getBufferedPosition") - GetPositionMs();
            }
#endif
            return 0L;
        }

        public void PreCacheSegments(int targetSeconds)
        {
            _preCacheMode      = true;
            _preCacheStartTime = Time.time;
            IsReady            = false;

            if (!useExoPlayer && _vp != null)
            {
                _vp.Prepare();
                Debug.Log($"[HLS] PreCacheSegments({targetSeconds}) — preparing VideoPlayer.");
                return;
            }
#if UNITY_ANDROID && !UNITY_EDITOR
            if (useExoPlayer)
            {
                AndroidJNI.AttachCurrentThread();
                try { _playerHelper?.CallStatic("setMinBufferMs", _exoPlayer, (long)targetSeconds * 1000L); }
                catch (Exception e) { Debug.LogWarning($"[HLS] setMinBufferMs: {e.Message}"); }
                CallExo("setPlayWhenReady", false);
                CallExo("prepare");
            }
#else
            StartCoroutine(SimulatePreCache(targetSeconds));
#endif
        }

        public void SetPlaybackRate(float rate)
        {
            if (!useExoPlayer && _vp != null) { _vp.playbackSpeed = rate; return; }
#if UNITY_ANDROID && !UNITY_EDITOR
            if (useExoPlayer) CallExo("setPlaybackRate", rate);
#endif
        }

        public void SetVolume(float volume)
        {
            if (!useExoPlayer && _vp != null)
            {
                _vp.SetDirectAudioVolume(0, Mathf.Clamp01(volume));
                return;
            }
#if UNITY_ANDROID && !UNITY_EDITOR
            if (useExoPlayer) CallExo("setVolume", Mathf.Clamp01(volume));
#endif
        }

        #endregion

        // ─────────────────────────────────────────────────────────────────────
        #region VideoPlayer backend

        void InitVideoPlayer(string url)
        {
            // Clean up previous instance
            if (_vp != null) { _vp.Stop(); Destroy(_vp); }
            if (_rt != null) { _rt.Release(); Destroy(_rt); }

            // RenderTexture — actual size set in prepareCompleted once video metadata known
            _rt = new RenderTexture(3840, 2160, 0, RenderTextureFormat.ARGB32);
            _rt.name = "VideoRT";
            _rt.Create();

            _vp = gameObject.AddComponent<VideoPlayer>();
            _vp.playOnAwake   = false;
            _vp.source        = VideoSource.Url;
            _vp.url           = url;
            _vp.renderMode    = VideoRenderMode.RenderTexture;
            _vp.targetTexture = _rt;

            // Direct audio output on Android (no AudioSource needed)
            _vp.audioOutputMode = VideoAudioOutputMode.Direct;
            _vp.EnableAudioTrack(0, true);
            _vp.SetDirectAudioVolume(0, 1f);

            _vp.waitForFirstFrame = true;
            _vp.skipOnDrop        = true;
            _vp.isLooping         = false;

            // Subscribe to events
            _vp.prepareCompleted += OnVpPrepared;
            _vp.errorReceived    += OnVpError;
            _vp.loopPointReached += _ => Debug.Log("[HLS] Playback loop point reached.");

            // Push render texture into the screen material immediately so Unity
            // can allocate the sampler before the first frame arrives.
            ApplyRenderTexture(_rt);

            _vp.Prepare();
            Debug.Log("[HLS] VideoPlayer Prepare() called.");
        }

        void OnVpPrepared(VideoPlayer vp)
        {
            Debug.Log($"[HLS] VideoPlayer prepared: {vp.width}x{vp.height}  " +
                      $"duration={vp.length:F1}s  frameRate={vp.frameRate:F1}");

            // Resize RT to actual video dimensions if needed
            if (vp.width > 0 && vp.height > 0 &&
                (_rt.width != (int)vp.width || _rt.height != (int)vp.height))
            {
                _rt.Release();
                _rt.width  = (int)vp.width;
                _rt.height = (int)vp.height;
                _rt.Create();
                vp.targetTexture = _rt;
                ApplyRenderTexture(_rt);
                Debug.Log($"[HLS] RT resized to {_rt.width}x{_rt.height}");
            }

            if (ScreenRenderer != null && vp.width > 0 && vp.height > 0)
                ScreenRenderer.SetVideoSize((int)vp.width, (int)vp.height);

            _vpReady = true;
            HandleReady();
        }

        void OnVpError(VideoPlayer vp, string message)
        {
            Debug.LogError($"[HLS] VideoPlayer error: {message}");
            OnError?.Invoke(message);
        }

        void ApplyRenderTexture(RenderTexture rt)
        {
            if (ScreenRenderer == null) return;

            // Normal path: the enhancer processes each decoded frame and feeds the screen.
            var clarity = ScreenRenderer.GetComponent<ClarityStack>();
            if (clarity != null)
            {
                clarity.SetSource(rt, () => _vp != null ? _vp.frame : 0L);
                return;
            }
            ScreenRenderer.SetMainTexture(rt);
        }

        #endregion

        // ─────────────────────────────────────────────────────────────────────
        #region ExoPlayer JNI backend (legacy)

        void InitExoPlayer(string urlWithToken)
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            AndroidJNI.AttachCurrentThread();
            try
            {
                using var up = new AndroidJavaClass("com.unity3d.player.UnityPlayer");
                _unityActivity = up.GetStatic<AndroidJavaObject>("currentActivity");
                _playerHelper  = new AndroidJavaClass("com.movieplayer.ExoPlayerHelper");
                _exoPlayer     = _playerHelper.CallStatic<AndroidJavaObject>(
                    "create", _unityActivity, urlWithToken, gameObject.name);
                Debug.Log($"[HLS] ExoPlayer initialised → {urlWithToken}");
            }
            catch (Exception e) { EnqueueError($"ExoPlayer init: {e.Message}"); }
#else
            Debug.Log($"[HLS] ExoPlayer stub — {urlWithToken}");
#endif
        }

        // Java callbacks via UnitySendMessage
        void OnExoReady(string _)    => _mainQ.Enqueue(HandleReady);
        void OnExoBuffering(string s)
        {
            if (long.TryParse(s, out long ms))
                _mainQ.Enqueue(() => OnBuffering?.Invoke(ms / 1000f));
        }
        void OnExoError(string msg)  => _mainQ.Enqueue(() => EnqueueError(msg));

        #endregion

        // ─────────────────────────────────────────────────────────────────────
        #region Unity lifecycle

        private void Update()
        {
            // Drain ExoPlayer thread queue
            while (_mainQ.TryDequeue(out var action)) action?.Invoke();

            // Periodic buffering events
            _bufTimer += Time.deltaTime;
            if (_bufTimer >= BufReportInterval)
            {
                _bufTimer = 0f;
                if (!useExoPlayer && _vp != null)
                    OnBuffering?.Invoke(GetBufferedMs() / 1000f);

                // Pre-cache readiness check for VideoPlayer
                if (_preCacheMode && !IsReady)
                {
                    float elapsed = Time.time - _preCacheStartTime;
                    if (_vpReady || elapsed >= PreCacheTargetSeconds)
                        HandleReady();
                }
            }
        }

        private void OnDestroy()
        {
            if (_vp != null)
            {
                _vp.prepareCompleted -= OnVpPrepared;
                _vp.errorReceived    -= OnVpError;
                _vp.Stop();
            }
            if (_rt != null) { _rt.Release(); Destroy(_rt); }

#if UNITY_ANDROID && !UNITY_EDITOR
            if (useExoPlayer)
            {
                try
                {
                    AndroidJNI.AttachCurrentThread();
                    _exoPlayer?.Call("release");
                    _exoPlayer?.Dispose();
                    _unityActivity?.Dispose();
                }
                catch { }
            }
#endif
        }

        #endregion

        // ─────────────────────────────────────────────────────────────────────
        #region Private helpers

        void HandleReady()
        {
            if (IsReady) return;
            IsReady       = true;
            _preCacheMode = false;
            Debug.Log("[HLS] IsReady = true → firing OnReady");
            OnReady?.Invoke();
        }

        void EnqueueError(string msg)
        {
            Debug.LogError($"[HLS] Error: {msg}");
            OnError?.Invoke(msg);
        }

        static string AppendToken(string url, string token)
        {
            if (string.IsNullOrEmpty(token)) return url;
            return url.Contains("?")
                ? $"{url}&token={Uri.EscapeDataString(token)}"
                : $"{url}?token={Uri.EscapeDataString(token)}";
        }

#if UNITY_ANDROID && !UNITY_EDITOR
        void CallExo(string method, params object[] args)
        {
            if (_exoPlayer == null) return;
            AndroidJNI.AttachCurrentThread();
            try { _exoPlayer.Call(method, args); }
            catch (Exception e)
            { Debug.LogWarning($"[HLS] ExoPlayer.{method}: {e.Message}"); }
        }
#endif

#if UNITY_EDITOR
        IEnumerator SimulatePreCache(int targetSeconds)
        {
            float t = 0f;
            while (t < targetSeconds) { t += Time.deltaTime; OnBuffering?.Invoke(t); yield return null; }
            HandleReady();
        }
#endif

        #endregion
    }
}
