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
using System.Collections.Generic;
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
        public event Action           OnAudioTracksReady;

        // ── State ─────────────────────────────────────────────────────────────
        public bool IsReady { get; private set; }
        public int SelectedAudioTrack { get; private set; }
        public string SelectedAudioCodec { get; private set; } = "";
        public string SelectedAudioLabel { get; private set; } = "";
        public bool AudioNeedsHomeServer { get; private set; }
        public int AudioTrackCount => _vp != null ? (int)_vp.audioTrackCount : 0;
        public int VideoWidth => _vp != null && _vp.width > 0 ? (int)_vp.width : 0;
        public int VideoHeight => _vp != null && _vp.height > 0 ? (int)_vp.height : 0;
        public RenderTexture VideoTexture => _rt;

        int _prepareGen;
        Coroutine _prepareCo;
        Coroutine _prepareWatch;
        string _sourceUrl = "";
        string _filesystemPath = "";
        OnDeviceTranscoder.ProbeResult _probe;
        OnDeviceTranscoder.Plan _plan = OnDeviceTranscoder.Plan.Direct;
        bool _retriedConvert;
        const float PrepareTimeoutSec = 15f;
        long _resumeMs;
        int _sourceOrdinal = -1;
        readonly List<OnDeviceTranscoder.Choice> _choices = new List<OnDeviceTranscoder.Choice>();
        readonly List<DeviceAudioProbe.Track> _audioTracks = new List<DeviceAudioProbe.Track>();
        int _preferTrack = -1;
        int _queuedPrefer = -1;
        float _directVolume = 1f;

        public int SelectedSourceOrdinal => _sourceOrdinal;
        public IReadOnlyList<OnDeviceTranscoder.Choice> AudioChoices => _choices;
        public event Action OnAudioChoicesChanged;
        public event Action<string, float> OnPrepareProgress;
        public event Action<OnDeviceTranscoder.ProbeResult> OnProbed;

        public bool CapTextureToHd;

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
            _sourceUrl = hlsUrl ?? "";
            _filesystemPath = "";
            _probe = null;
            _plan = OnDeviceTranscoder.Plan.Direct;
            _retriedConvert = false;
            _handlingRetry = false;
            _prepareGen++;
            if (_prepareCo != null) { StopCoroutine(_prepareCo); _prepareCo = null; }
            OnDeviceTranscoder.Cancel();

            string urlWithToken = AppendToken(hlsUrl, bearerToken);
            Debug.Log($"[HLS] Initialize → {urlWithToken}");

            Stop();
            IsReady  = false;
            _vpReady = false;
            _preferTrack = _queuedPrefer;
            _queuedPrefer = -1;
            _audioTracks.Clear();

            if (useExoPlayer)
            {
                InitExoPlayer(urlWithToken);
                return;
            }

            if (OnDeviceTranscoder.OnQuest && OnDeviceTranscoder.IsLocalFile(hlsUrl))
            {
                _prepareCo = StartCoroutine(PrepareLocal(hlsUrl, _prepareGen));
                return;
            }

            InitVideoPlayer(urlWithToken);
        }

        public void CancelPrepare()
        {
            _prepareGen++;
            if (_prepareCo != null) { StopCoroutine(_prepareCo); _prepareCo = null; }
            OnDeviceTranscoder.Cancel();
        }

        public void SelectLanguage(string language, int ordinal)
        {
            if (_sourceOrdinal == ordinal && IsReady) return;
            if (!string.IsNullOrEmpty(language))
            {
                PlayerPrefs.SetString("audio.lang", language);
                PlayerPrefs.Save();
            }
            _resumeMs = GetPositionMs();
            PreferAudioTrack(ordinal);
            if (!string.IsNullOrEmpty(_sourceUrl))
                Initialize(_sourceUrl, string.Empty);
        }

        IEnumerator PrepareLocal(string url, int generation)
        {
            string path = OnDeviceTranscoder.ToFilesystemPath(url);
            _filesystemPath = path;
            string error = null;
            Debug.Log("[compat] step 1/3 unpack ffmpeg for " + path);
            float t0 = Time.realtimeSinceStartup;
            yield return OnDeviceTranscoder.EnsureBinary(e => error = e);
            Debug.Log($"[compat] step 1/3 done in {Time.realtimeSinceStartup - t0:F1}s exe={OnDeviceTranscoder.ExecutablePath} error={error ?? "none"}");
            if (generation != _prepareGen) { Debug.Log("[compat] cancelled after unpack"); yield break; }
            if (!string.IsNullOrEmpty(error))
            {
                // Without ffmpeg, still try Android's own decoder: H.264/HEVC + AAC files play fine.
                Debug.LogWarning("[compat] " + error + " Playing the file directly.");
                InitVideoPlayer(OnDeviceTranscoder.ToFileUrl(path));
                yield break;
            }

            OnDeviceTranscoder.ProbeResult probe = null;
            Debug.Log("[compat] step 2/3 probe");
            t0 = Time.realtimeSinceStartup;
            yield return OnDeviceTranscoder.Probe(path, r => probe = r, e => error = e);
            Debug.Log($"[compat] step 2/3 done in {Time.realtimeSinceStartup - t0:F1}s video={probe?.VideoCodec ?? "-"} audio={probe?.Audio.Count ?? -1} error={error ?? "none"}");
            if (generation != _prepareGen) { Debug.Log("[compat] cancelled after probe"); yield break; }
            if (probe == null && error != null && error.StartsWith(OnDeviceTranscoder.DamagedPrefix))
            {
                Debug.LogError("[compat] " + error);
                OnError?.Invoke(error);
                yield break;
            }
            if (probe == null)
            {
                Debug.LogWarning("[compat] probe failed (" + error + "). Playing the file directly.");
                InitVideoPlayer(OnDeviceTranscoder.ToFileUrl(path));
                yield break;
            }
            _probe = probe;
            OnProbed?.Invoke(probe);

            int ordinal = _preferTrack >= 0 && (probe.Audio.Count == 0 || _preferTrack < probe.Audio.Count)
                ? _preferTrack
                : OnDeviceTranscoder.ChooseDefault(probe.Audio);
            if (probe.Audio.Count > 0 && ordinal >= probe.Audio.Count) ordinal = 0;
            _sourceOrdinal = ordinal;
            _choices.Clear();
            foreach (var choice in probe.Audio)
            {
                choice.Selected = choice.Ordinal == ordinal;
                _choices.Add(choice);
            }
            if (ordinal >= 0 && ordinal < _choices.Count)
            {
                SelectedAudioLabel = _choices[ordinal].Display;
                SelectedAudioCodec = _choices[ordinal].Codec;
            }
            AudioNeedsHomeServer = false;
            OnAudioChoicesChanged?.Invoke();

            string playable = null;
            Debug.Log("[compat] step 3/3 prepare playback");
            yield return OnDeviceTranscoder.PreparePlayback(
                path, probe, probe.Audio.Count > 0 ? ordinal : -1,
                (msg, p) => { if (generation == _prepareGen) OnPrepareProgress?.Invoke(msg, p); },
                u => playable = u,
                e => error = e,
                () => generation != _prepareGen);
            if (generation != _prepareGen) yield break;
            if (string.IsNullOrEmpty(playable)) { OnError?.Invoke(error ?? "Could not prepare playback."); yield break; }

            _plan = OnDeviceTranscoder.ChoosePlan(path, probe, probe.Audio.Count > 0 ? ordinal : -1);
            _preferTrack = string.Equals(playable.Replace('\\', '/'), path.Replace('\\', '/'), StringComparison.OrdinalIgnoreCase)
                ? ordinal
                : 0;
            InitVideoPlayer(OnDeviceTranscoder.ToFileUrl(playable));
        }

        IEnumerator ConvertAndPlay(OnDeviceTranscoder.Plan plan, int generation)
        {
            _plan = plan;
            string error = null;
            string playable = null;
            int ordinal = _probe != null && _probe.Audio.Count > 0 ? _sourceOrdinal : -1;
            yield return OnDeviceTranscoder.PreparePlayback(
                _filesystemPath, _probe, ordinal,
                (msg, p) => { if (generation == _prepareGen) OnPrepareProgress?.Invoke(msg, p); },
                u => playable = u,
                e => error = e,
                () => generation != _prepareGen,
                plan);
            if (generation != _prepareGen) yield break;
            if (string.IsNullOrEmpty(playable))
            {
                OnError?.Invoke(error ?? "Could not prepare playback.");
                yield break;
            }
            _preferTrack = 0;
            InitVideoPlayer(OnDeviceTranscoder.ToFileUrl(playable));
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

        /// <summary>
        /// Picks an audio track once the file is prepared. Pass -1 to let the
        /// player choose AAC, then MP3, then AC3.
        /// </summary>
        public void PreferAudioTrack(int index)
        {
            _queuedPrefer = index;
            _preferTrack = index;
        }

        public void SelectAudioTrack(int index)
        {
            _queuedPrefer = index;
            _preferTrack = index;
            if (_vpReady)
            {
                ApplyAudioSelection();
                OnAudioTracksReady?.Invoke();
            }
        }

        public void SetVolume(float volume)
        {
            _directVolume = Mathf.Clamp01(volume);
            if (!useExoPlayer && _vp != null)
            {
                int count = _vp.audioTrackCount;
                if (count <= 0)
                {
                    try { _vp.SetDirectAudioVolume(0, _directVolume); } catch { /* not prepared */ }
                }
                else
                {
                    for (ushort i = 0; i < count; i++)
                        try { _vp.SetDirectAudioVolume(i, _directVolume); } catch { /* track unavailable */ }
                }
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
            // Unity's Android VideoPlayer does not percent-decode file:// URLs ("could not
            // translate file:///…%20… to local file"), so hand it the plain absolute path.
            if (OnDeviceTranscoder.IsLocalFile(url)) url = OnDeviceTranscoder.ToFilesystemPath(url);
            if (_prepareWatch != null) { StopCoroutine(_prepareWatch); _prepareWatch = null; }

            // Clean up previous instance
            if (_vp != null) { _vp.Stop(); Destroy(_vp); }
            if (_rt != null) { _rt.Release(); Destroy(_rt); }

            // The 2D panel is 1280x800. Never allocate a 4K target for it.
            // Theater may grow up to 3840x2160 once the real frame size is known.
            _rt = new RenderTexture(1280, 720, 0, RenderTextureFormat.ARGB32);
            _rt.name = "VideoRT";
            _rt.Create();

            _vp = gameObject.AddComponent<VideoPlayer>();
            _vp.playOnAwake   = false;
            _vp.source        = VideoSource.Url;
            _vp.url           = url;
            _vp.renderMode    = VideoRenderMode.RenderTexture;
            _vp.targetTexture = _rt;

            // Direct audio output on Android (no AudioSource needed).
            // Reserve several controllable tracks so a later AAC/MP3 track can be selected.
            _vp.audioOutputMode = VideoAudioOutputMode.Direct;
            try { _vp.controlledAudioTrackCount = 8; } catch { /* older player */ }
            _vp.EnableAudioTrack(0, true);
            _vp.SetDirectAudioVolume(0, _directVolume);

            _vp.waitForFirstFrame = true;
            _vp.skipOnDrop        = true;
            _vp.timeUpdateMode    = VideoTimeUpdateMode.UnscaledGameTime;
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
            if (OnDeviceTranscoder.IsLocalFile(url) || !string.IsNullOrEmpty(_filesystemPath))
                _prepareWatch = StartCoroutine(WatchPrepare(_prepareGen));
        }

        IEnumerator WatchPrepare(int generation)
        {
            float deadline = Time.unscaledTime + PrepareTimeoutSec;
            while (Time.unscaledTime < deadline)
            {
                if (generation != _prepareGen || _vpReady || _vp == null) yield break;
                yield return null;
            }
            if (generation != _prepareGen || _vpReady) yield break;
            Debug.LogError("[HLS] VideoPlayer Prepare() timed out");
            BeginConvertRetry("Prepare timed out");
        }

        bool _handlingRetry;

        void BeginConvertRetry(string message)
        {
            if (_handlingRetry) return;
            _handlingRetry = true;
            Debug.LogError("[HLS] VideoPlayer error: " + message);
            bool local = _probe != null && !string.IsNullOrEmpty(_filesystemPath) && OnDeviceTranscoder.OnQuest;
            if (!local || _retriedConvert || _plan == OnDeviceTranscoder.Plan.Full)
            {
                _handlingRetry = false;
                OnError?.Invoke(message);
                return;
            }
            _retriedConvert = true;
            var next = OnDeviceTranscoder.FallbackPlan(_plan, _filesystemPath, _probe);
            Debug.Log("[compat] playback failed (" + message + ") retry " + next);
            _prepareGen++;
            if (_prepareCo != null) StopCoroutine(_prepareCo);
            if (_prepareWatch != null) { StopCoroutine(_prepareWatch); _prepareWatch = null; }
            OnDeviceTranscoder.Cancel();
            if (_vp != null)
            {
                _vp.prepareCompleted -= OnVpPrepared;
                _vp.errorReceived -= OnVpError;
                _vp.Stop();
                Destroy(_vp);
                _vp = null;
            }
            _vpReady = false;
            IsReady = false;
            _prepareCo = StartCoroutine(ConvertAndPlay(next, _prepareGen));
            _handlingRetry = false;
        }

        void OnVpPrepared(VideoPlayer vp)
        {
            Debug.Log($"[HLS] VideoPlayer prepared: {vp.width}x{vp.height}  " +
                      $"duration={vp.length:F1}s  frameRate={vp.frameRate:F1}");

            if (_prepareWatch != null) { StopCoroutine(_prepareWatch); _prepareWatch = null; }

            if (vp.width > 0 && vp.height > 0)
            {
                int maxW = CapTextureToHd ? 1920 : 3840;
                int maxH = CapTextureToHd ? 1080 : 2160;
                FitSize((int)vp.width, (int)vp.height, maxW, maxH, out int rw, out int rh);
                if (_rt.width != rw || _rt.height != rh)
                {
                    _rt.Release();
                    _rt.width = rw;
                    _rt.height = rh;
                    _rt.Create();
                    vp.targetTexture = _rt;
                    ApplyRenderTexture(_rt);
                    Debug.Log($"[HLS] RT resized to {_rt.width}x{_rt.height}");
                }
            }

            if (ScreenRenderer != null && vp.width > 0 && vp.height > 0)
                ScreenRenderer.SetVideoSize((int)vp.width, (int)vp.height);

            _vpReady = true;
            SelectPreparedAudio();
            HandleReady();
        }

        void OnVpError(VideoPlayer vp, string message)
        {
            BeginConvertRetry(string.IsNullOrEmpty(message) ? "VideoPlayer error" : message);
        }

        static void FitSize(int width, int height, int maxW, int maxH, out int rw, out int rh)
        {
            if (width <= 0 || height <= 0) { rw = Math.Min(1280, maxW); rh = Math.Min(720, maxH); return; }
            float scale = Mathf.Min(1f, maxW / (float)width, maxH / (float)height);
            rw = Math.Max(16, (int)(width * scale)) & ~1;
            rh = Math.Max(16, (int)(height * scale)) & ~1;
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

        void SelectPreparedAudio()
        {
            if (_vp == null) return;
            _audioTracks.Clear();
            try
            {
                var probed = DeviceAudioProbe.Probe(_vp.url);
                if (probed != null) _audioTracks.AddRange(probed);
            }
            catch (Exception e)
            {
                Debug.LogWarning("[HLS] Audio probe failed: " + e.Message);
            }

            if (_preferTrack < 0 && _audioTracks.Count > 0)
                _preferTrack = DeviceAudioProbe.PreferIndex(_audioTracks);
            ApplyAudioSelection();
            OnAudioTracksReady?.Invoke();
        }

        void ApplyAudioSelection()
        {
            if (_vp == null) return;
            int count = _vp.audioTrackCount;
            if (count <= 0)
            {
                SelectedAudioTrack = 0;
                SelectedAudioCodec = "";
                SelectedAudioLabel = "No audio";
                AudioNeedsHomeServer = false;
                return;
            }

            int index = _preferTrack >= 0 ? _preferTrack : 0;
            if (index >= count) index = 0;
            for (ushort i = 0; i < count; i++)
            {
                try { _vp.EnableAudioTrack(i, i == index); }
                catch (Exception e) { Debug.LogWarning($"[HLS] EnableAudioTrack {i}: {e.Message}"); }
            }

            SelectedAudioTrack = index;
            if (index < _audioTracks.Count)
            {
                var track = _audioTracks[index];
                SelectedAudioCodec = track.codec ?? "";
                SelectedAudioLabel = string.IsNullOrEmpty(track.label) ? $"Track {index + 1}" : track.label;
            }
            else
            {
                SelectedAudioCodec = "";
                SelectedAudioLabel = count > 1 ? $"Track {index + 1} of {count}" : "Audio";
            }

            var chosen = SelectedChoice();
            if (chosen != null)
            {
                SelectedAudioLabel = chosen.Display;
                if (!string.IsNullOrEmpty(chosen.Codec)) SelectedAudioCodec = chosen.Codec;
            }
            AudioNeedsHomeServer = false;
            try { _vp.SetDirectAudioVolume((ushort)index, _directVolume); } catch { /* direct output not ready */ }
            Debug.Log($"[HLS] Audio {SelectedAudioLabel}");
        }

        OnDeviceTranscoder.Choice SelectedChoice()
        {
            for (int i = 0; i < _choices.Count; i++)
                if (_choices[i].Selected) return _choices[i];
            return null;
        }

        void HandleReady()
        {
            if (IsReady) return;
            if (_resumeMs > 0 && _vp != null)
            {
                _vp.time = _resumeMs / 1000.0;
                _resumeMs = 0;
            }
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
