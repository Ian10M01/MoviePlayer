using UnityEngine;
using MoviePlayer.Player;
using MoviePlayer.Proto;

namespace MoviePlayer.Room
{
    /// <summary>
    /// Guest-side playback synchronisation engine.
    ///
    /// Algorithm
    /// ─────────
    /// The host periodically sends PlayStateMsg with:
    ///   - position_ms   : content position at time of send
    ///   - host_wall_ms  : host UTC wall clock ms at time of send
    ///   - playing       : play/pause flag
    ///
    /// On each Update() we compute:
    ///   expectedMs = targetPositionMs + (nowMs - targetWallMs)
    ///
    /// And compare against HlsPlayerController.GetPositionMs():
    ///   drift = expectedMs - actualMs
    ///
    ///   |drift| < 100ms  →  no action  (locked)
    ///   100–500ms        →  ±2% playback rate slew
    ///   |drift| > 500ms  →  hard seek
    /// </summary>
    [RequireComponent(typeof(RoomClient))]
    public class SyncEngine : MonoBehaviour
    {
        // ── Inspector ─────────────────────────────────────────────────────────
        [Header("References")]
        public HlsPlayerController Player;

        [Header("Thresholds (ms)")]
        public float LockThresholdMs    = 100f;
        public float SlewThresholdMs    = 500f;

        [Header("Slew Rate")]
        [Range(1.00f, 1.05f)]
        public float SlewRate = 1.02f;  // ±2% from 1.0

        // ── Public state ─────────────────────────────────────────────────────
        public float DriftMs    { get; private set; }
        public bool  IsLocked   { get; private set; }
        public bool  IsSyncing  { get; private set; }

        // ── Private state ─────────────────────────────────────────────────────
        private long  _targetPositionMs = 0L;
        private long  _targetWallMs     = 0L;
        private bool  _playing          = false;
        private bool  _hasState         = false;
        private float _currentRate      = 1.0f;

        private RoomClient _roomClient;

        // ─────────────────────────────────────────────────────────────────────
        #region Unity Lifecycle

        private void Awake()
        {
            _roomClient = GetComponent<RoomClient>();
        }

        private void OnEnable()
        {
            if (_roomClient != null)
            {
                _roomClient.OnPlayState += HandlePlayState;
                _roomClient.OnPlay      += HandlePlay;
            }
        }

        private void OnDisable()
        {
            if (_roomClient != null)
            {
                _roomClient.OnPlayState -= HandlePlayState;
                _roomClient.OnPlay      -= HandlePlay;
            }
        }

        private void Update()
        {
            if (!_hasState || Player == null) return;
            if (!_playing) return;

            // ── Compute expected position ─────────────────────────────────────
            long nowMs       = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            long expectedMs  = _targetPositionMs + (nowMs - _targetWallMs);
            long actualMs    = Player.GetPositionMs();

            DriftMs = expectedMs - actualMs;
            float absDrift = Mathf.Abs(DriftMs);

            // ── Sync decision ─────────────────────────────────────────────────
            if (absDrift < LockThresholdMs)
            {
                // Locked — restore normal rate if we were slewing
                IsLocked  = true;
                IsSyncing = false;
                if (Mathf.Abs(_currentRate - 1.0f) > 0.001f)
                    SetRate(1.0f);
            }
            else if (absDrift < SlewThresholdMs)
            {
                // Rate slew: speed up or slow down by SlewRate
                IsLocked  = false;
                IsSyncing = true;
                float targetRate = DriftMs > 0
                    ? SlewRate          // guest is behind — speed up
                    : (2.0f - SlewRate);// guest is ahead  — slow down (e.g. 0.98)
                SetRate(targetRate);
            }
            else
            {
                // Hard seek — drift too large for slewing to recover
                IsLocked  = false;
                IsSyncing = false;
                Debug.Log($"[SyncEngine] Hard seek: drift={DriftMs:F0}ms → seek to {expectedMs}ms");
                SetRate(1.0f);
                Player.SeekTo(expectedMs);
                // Update state so next frame drift resets
                _targetPositionMs = expectedMs;
                _targetWallMs     = nowMs;
            }
        }

        #endregion

        // ─────────────────────────────────────────────────────────────────────
        #region Event Handlers

        private void HandlePlayState(PlayStateMsg msg)
        {
            _targetPositionMs = msg.position_ms;
            _targetWallMs     = msg.host_wall_ms;
            _playing          = msg.playing;
            _hasState         = true;

            if (!msg.playing)
            {
                Player?.Pause();
                // Seek to the exact position so a host pause/seek lands the
                // guest at the correct frame, not just wherever they happened
                // to be (SyncEngine's Update loop early-exits when paused).
                Player?.SeekTo(msg.position_ms);
                SetRate(1.0f);
            }
            else
            {
                Player?.Play();
            }

            Debug.Log($"[SyncEngine] PlayState: pos={msg.position_ms}ms playing={msg.playing}");
        }

        private void HandlePlay(PlayMsg msg)
        {
            _targetPositionMs = msg.position_ms;
            _targetWallMs     = msg.wall_start_ms;
            _playing          = true;
            _hasState         = true;

            // Wait until wall clock reaches wall_start_ms then play
            StartCoroutine(PlayAtWallTime(msg));
        }

        private System.Collections.IEnumerator PlayAtWallTime(PlayMsg msg)
        {
            long nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            long waitMs = msg.wall_start_ms - nowMs;

            if (waitMs > 0)
            {
                Debug.Log($"[SyncEngine] Waiting {waitMs}ms before play at wall-clock sync point.");
                yield return new WaitForSecondsRealtime(waitMs / 1000f);
            }

            // Seek to exact position accounting for elapsed wait
            long elapsedMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - msg.wall_start_ms;
            long seekTarget = msg.position_ms + elapsedMs;
            Player?.SeekTo(seekTarget);
            Player?.Play();

            _targetPositionMs = seekTarget;
            _targetWallMs     = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

            Debug.Log($"[SyncEngine] Synchronized play at pos={seekTarget}ms");
        }

        #endregion

        // ─────────────────────────────────────────────────────────────────────
        #region Helpers

        private void SetRate(float rate)
        {
            if (Mathf.Approximately(_currentRate, rate)) return;
            _currentRate = rate;
            Player?.SetPlaybackRate(rate);
            Debug.Log($"[SyncEngine] Rate → {rate:F3} (drift={DriftMs:F0}ms)");
        }

        #endregion
    }
}
