using System;
using System.Collections;
using UnityEngine;

#if UNITY_ANDROID && !UNITY_EDITOR
using UnityEngine.Android;
#endif

namespace MoviePlayer.Player
{
    /// <summary>
    /// Drives Android ExoPlayer via JNI for HLS adaptive-bitrate playback.
    ///
    /// Threading model
    /// ───────────────
    /// ExoPlayer must be created and interacted with on the Android main looper.
    /// Unity's main thread IS the Android GL thread, so calls from Update() are
    /// safe. Background callbacks from ExoPlayer (buffering events) arrive on
    /// ExoPlayer's internal thread; we marshal them to Unity via a thread-safe
    /// queue and dispatch on the next Update().
    /// </summary>
    public class HlsPlayerController : MonoBehaviour
    {
        // ── Inspector ─────────────────────────────────────────────────────────
        [Header("Buffer Thresholds")]
        [Tooltip("Milliseconds of buffered content required before IsReady = true")]
        public long MinBufferMs = 60_000L;

        [Tooltip("Seconds to pre-cache before firing OnReady in PreCacheSegments()")]
        public int PreCacheTargetSeconds = 60;

        // ── Events ────────────────────────────────────────────────────────────
        public event Action OnReady;
        public event Action<float> OnBuffering;   // arg: buffered seconds
        public event Action<string> OnError;

        // ── State ─────────────────────────────────────────────────────────────
        public bool IsReady { get; private set; }

        private bool _preCacheMode = false;
        private float _bufferingReportTimer = 0f;
        private const float BufferingReportInterval = 1f;

        // ── JNI helpers ───────────────────────────────────────────────────────
#if UNITY_ANDROID && !UNITY_EDITOR
        private AndroidJavaObject _exoPlayer;
        private AndroidJavaObject _unityActivity;
        private AndroidJavaClass  _playerHelper;   // MoviePlayer.ExoPlayerHelper Java class

        // Thread-safe queue for events arriving from the ExoPlayer thread
        private readonly System.Collections.Concurrent.ConcurrentQueue<Action>
            _mainThreadQueue = new System.Collections.Concurrent.ConcurrentQueue<Action>();
#endif

        // ─────────────────────────────────────────────────────────────────────
        #region Public API

        /// <summary>
        /// Creates an ExoPlayer instance, builds the HLS MediaSource with the
        /// bearer token appended as a query parameter, and prepares playback.
        /// </summary>
        public void Initialize(string hlsUrl, string bearerToken)
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            AndroidJNI.AttachCurrentThread();
            try
            {
                using var unityPlayerClass = new AndroidJavaClass("com.unity3d.player.UnityPlayer");
                _unityActivity = unityPlayerClass.GetStatic<AndroidJavaObject>("currentActivity");

                // Append token to URL
                string urlWithToken = hlsUrl.Contains("?")
                    ? $"{hlsUrl}&token={Uri.EscapeDataString(bearerToken)}"
                    : $"{hlsUrl}?token={Uri.EscapeDataString(bearerToken)}";

                // MoviePlayer.ExoPlayerHelper is a thin Java/Kotlin wrapper
                // that creates the ExoPlayer on the main looper and exposes
                // simple methods.  It calls back into Unity via
                // UnityPlayer.UnitySendMessage.
                _playerHelper = new AndroidJavaClass("com.movieplayer.ExoPlayerHelper");
                _exoPlayer = _playerHelper.CallStatic<AndroidJavaObject>(
                    "create", _unityActivity, urlWithToken, gameObject.name);

                Debug.Log($"[HlsPlayerController] ExoPlayer initialized → {urlWithToken}");
            }
            catch (Exception e)
            {
                EnqueueError($"Initialize failed: {e.Message}");
            }
#else
            Debug.Log($"[HlsPlayerController] EDITOR stub — Initialize({hlsUrl})");
#endif
        }

        public void Play()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            CallExoPlayer("setPlayWhenReady", true);
#else
            Debug.Log("[HlsPlayerController] Play()");
#endif
        }

        public void Pause()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            CallExoPlayer("setPlayWhenReady", false);
#else
            Debug.Log("[HlsPlayerController] Pause()");
#endif
        }

        public void SeekTo(long positionMs)
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            CallExoPlayer("seekTo", positionMs);
#else
            Debug.Log($"[HlsPlayerController] SeekTo({positionMs})");
#endif
        }

        public long GetPositionMs()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            if (_exoPlayer == null) return 0L;
            AndroidJNI.AttachCurrentThread();
            return _exoPlayer.Call<long>("getCurrentPosition");
#else
            return 0L;
#endif
        }

        public long GetBufferedMs()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            if (_exoPlayer == null) return 0L;
            AndroidJNI.AttachCurrentThread();
            return _exoPlayer.Call<long>("getBufferedPosition") - GetPositionMs();
#else
            return 0L;
#endif
        }

        /// <summary>
        /// Configures ExoPlayer to buffer <paramref name="targetSeconds"/> of
        /// content ahead before signalling OnReady. Player stays paused.
        /// </summary>
        public void PreCacheSegments(int targetSeconds)
        {
            _preCacheMode = true;
            IsReady = false;
#if UNITY_ANDROID && !UNITY_EDITOR
            // Set ExoPlayer load-control min-buffer to targetSeconds
            AndroidJNI.AttachCurrentThread();
            try
            {
                _playerHelper?.CallStatic("setMinBufferMs",
                    _exoPlayer, (long)targetSeconds * 1000L);
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[HlsPlayerController] setMinBufferMs failed: {e.Message}");
            }
            // Ensure player is paused but preparing
            CallExoPlayer("setPlayWhenReady", false);
            CallExoPlayer("prepare");
#else
            Debug.Log($"[HlsPlayerController] PreCacheSegments({targetSeconds})");
            // Editor stub: simulate ready after delay
            StartCoroutine(SimulatePreCache(targetSeconds));
#endif
        }

        /// <summary>Set ExoPlayer playback rate (0.98 – 1.02 for slew correction).</summary>
        public void SetPlaybackRate(float rate)
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            // ExoPlayerHelper exposes setPlaybackRate(float) directly (uses media3
            // PlaybackParameters internally).  The old exoplayer2 package path no
            // longer exists in media3 and would throw a JNI ClassNotFoundException.
            CallExoPlayer("setPlaybackRate", rate);
#else
            Debug.Log($"[HlsPlayerController] SetPlaybackRate({rate})");
#endif
        }

        /// <summary>Set volume 0-1 (delegates to ExoPlayer AudioComponent).</summary>
        public void SetVolume(float volume)
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            CallExoPlayer("setVolume", Mathf.Clamp01(volume));
#else
            Debug.Log($"[HlsPlayerController] SetVolume({volume})");
#endif
        }

        #endregion

        // ─────────────────────────────────────────────────────────────────────
        #region Unity Callbacks from Java (via UnitySendMessage)

        // ExoPlayerHelper calls UnitySendMessage(gameObject.name, "OnExoReady", "")
        void OnExoReady(string _)
        {
            Debug.Log("[HlsPlayerController] ExoPlayer ready");
            _mainThreadQueue?.Enqueue(HandleReady);
        }

        void OnExoBuffering(string bufferedMsStr)
        {
            if (long.TryParse(bufferedMsStr, out long ms))
                _mainThreadQueue?.Enqueue(() => HandleBuffering(ms));
        }

        void OnExoError(string msg)
        {
            _mainThreadQueue?.Enqueue(() => EnqueueError(msg));
        }

        #endregion

        // ─────────────────────────────────────────────────────────────────────
        #region Unity Lifecycle

        private void Update()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            // Drain the thread-safe queue
            while (_mainThreadQueue.TryDequeue(out Action action))
                action?.Invoke();
#endif

            // Buffering progress reporting
            _bufferingReportTimer += Time.deltaTime;
            if (_bufferingReportTimer >= BufferingReportInterval)
            {
                _bufferingReportTimer = 0f;
                float bufferedSec = GetBufferedMs() / 1000f;
                OnBuffering?.Invoke(bufferedSec);

                if (_preCacheMode && !IsReady)
                {
                    if (GetBufferedMs() >= MinBufferMs)
                        HandleReady();
                }
            }
        }

        private void OnDestroy()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            try
            {
                AndroidJNI.AttachCurrentThread();
                _exoPlayer?.Call("release");
                _exoPlayer?.Dispose();
                _unityActivity?.Dispose();
            }
            catch { /* best effort */ }
#endif
        }

        #endregion

        // ─────────────────────────────────────────────────────────────────────
        #region Private helpers

        private void HandleReady()
        {
            if (IsReady) return;
            IsReady = true;
            _preCacheMode = false;
            Debug.Log("[HlsPlayerController] IsReady = true → firing OnReady");
            OnReady?.Invoke();
        }

        private void EnqueueError(string msg)
        {
            Debug.LogError($"[HlsPlayerController] Error: {msg}");
            OnError?.Invoke(msg);
        }

#if UNITY_ANDROID && !UNITY_EDITOR
        private void CallExoPlayer(string method, params object[] args)
        {
            if (_exoPlayer == null) return;
            AndroidJNI.AttachCurrentThread();
            try { _exoPlayer.Call(method, args); }
            catch (Exception e)
            {
                Debug.LogWarning($"[HlsPlayerController] {method} failed: {e.Message}");
            }
        }
#endif

#if UNITY_EDITOR
        private IEnumerator SimulatePreCache(int targetSeconds)
        {
            float elapsed = 0f;
            while (elapsed < targetSeconds)
            {
                elapsed += Time.deltaTime;
                OnBuffering?.Invoke(elapsed);
                yield return null;
            }
            HandleReady();
        }
#endif

        #endregion
    }
}
