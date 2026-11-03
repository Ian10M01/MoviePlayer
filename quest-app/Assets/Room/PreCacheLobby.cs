using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using MoviePlayer.Player;
using MoviePlayer.Proto;

namespace MoviePlayer.Room
{
    /// <summary>
    /// Manages the pre-cache waiting phase before synchronized playback begins.
    ///
    /// Guest flow
    /// ──────────
    /// 1. On room join → call HlsPlayerController.PreCacheSegments(60)
    /// 2. Every 5s → SendBufferStatus(bufferedSeconds, ready)
    /// 3. When OnPlay received → wait for wall-clock sync point → call Play()
    ///
    /// Host flow
    /// ─────────
    /// 1. Tracks all guest buffer statuses via OnPeerBuffer
    /// 2. AllReady becomes true when every guest has ready=true
    /// 3. LobbyUI polls AllReady and PeerStatuses
    /// 4. On 'Play' command → SendPlay() with future wall-clock target
    /// </summary>
    [RequireComponent(typeof(RoomClient))]
    public class PreCacheLobby : MonoBehaviour
    {
        // ── Inspector ─────────────────────────────────────────────────────────
        [Header("References")]
        public HlsPlayerController PlayerController;

        [Header("Buffer")]
        public int   PreCacheTargetSeconds = 60;
        public float BufferReportIntervalSec = 5f;

        [Header("Sync delay after Play command (ms)")]
        public long PlaySyncLeadMs = 2000L; // host waits 2s before all clients start

        // ── State ─────────────────────────────────────────────────────────────
        public bool IsHost     { get; private set; }
        public string DeviceId => _roomClient != null ? _roomClient.DeviceId : string.Empty;

        /// <summary>true when all connected guests have ready=true.</summary>
        public bool AllReady
        {
            get
            {
                if (_peerStatuses.Count == 0) return false;
                foreach (var kv in _peerStatuses)
                    if (!kv.Value.ready) return false;
                return true;
            }
        }

        /// <summary>Snapshot of all peer buffer statuses (for LobbyUI).</summary>
        public IReadOnlyDictionary<string, PeerBufferStatus> PeerStatuses => _peerStatuses;

        // ── Events ────────────────────────────────────────────────────────────
        public event Action OnAllReady;
        public event Action OnPlaybackStarting;

        // ── Private ───────────────────────────────────────────────────────────
        private readonly Dictionary<string, PeerBufferStatus> _peerStatuses
            = new Dictionary<string, PeerBufferStatus>();

        private RoomClient _roomClient;
        private bool _localReady     = false;
        private float _reportTimer   = 0f;
        private bool _allReadyFired  = false;

        // ─────────────────────────────────────────────────────────────────────
        #region Unity Lifecycle

        private void Awake()
        {
            _roomClient = GetComponent<RoomClient>();
        }

        private void OnEnable()
        {
            if (_roomClient == null) return;
            _roomClient.OnJoined     += HandleJoined;
            _roomClient.OnPeerBuffer += HandlePeerBuffer;
            _roomClient.OnPeerLeft   += HandlePeerLeft;
            _roomClient.OnPlay       += HandlePlay;

            if (PlayerController != null)
            {
                PlayerController.OnReady    += HandleLocalReady;
                PlayerController.OnBuffering += HandleLocalBuffering;
            }
        }

        private void OnDisable()
        {
            if (_roomClient != null)
            {
                _roomClient.OnJoined     -= HandleJoined;
                _roomClient.OnPeerBuffer -= HandlePeerBuffer;
                _roomClient.OnPeerLeft   -= HandlePeerLeft;
                _roomClient.OnPlay       -= HandlePlay;
            }

            if (PlayerController != null)
            {
                PlayerController.OnReady    -= HandleLocalReady;
                PlayerController.OnBuffering -= HandleLocalBuffering;
            }
        }

        private void Update()
        {
            if (_localReady || IsHost) return; // guests only

            _reportTimer += Time.deltaTime;
            if (_reportTimer >= BufferReportIntervalSec)
            {
                _reportTimer = 0f;
                float bufferedSec = PlayerController != null
                    ? PlayerController.GetBufferedMs() / 1000f
                    : 0f;
                _roomClient?.SendBufferStatus(bufferedSec, _localReady);
            }

            // Check AllReady on host side
            if (IsHost && !_allReadyFired && AllReady)
            {
                _allReadyFired = true;
                OnAllReady?.Invoke();
            }
        }

        #endregion

        // ─────────────────────────────────────────────────────────────────────
        #region Public API (called from LobbyUI)

        /// <summary>HOST: triggers synchronized play across all guests.</summary>
        public void HostPlay()
        {
            if (!IsHost)
            {
                Debug.LogWarning("[PreCacheLobby] HostPlay called on non-host client.");
                return;
            }

            long wallStartMs  = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + PlaySyncLeadMs;
            long positionMs   = PlayerController != null ? PlayerController.GetPositionMs() : 0L;

            _roomClient.SendPlay(positionMs, wallStartMs);

            // Host plays locally too
            StartCoroutine(PlayAtWallTime(positionMs, wallStartMs));
            OnPlaybackStarting?.Invoke();

            Debug.Log($"[PreCacheLobby] HOST issued play at wallMs={wallStartMs}");
        }

        #endregion

        // ─────────────────────────────────────────────────────────────────────
        #region Event Handlers

        private void HandleJoined(RoomState state)
        {
            IsHost = (state.device_id == state.host_device_id);

            // Populate peer statuses from initial seat info
            _peerStatuses.Clear();
            if (state.seats != null)
            {
                foreach (var seat in state.seats)
                {
                    if (seat.device_id == state.device_id) continue; // skip self
                    _peerStatuses[seat.device_id] = new PeerBufferStatus
                    {
                        device_id    = seat.device_id,
                        display_name = seat.display_name,
                        ready        = false
                    };
                }
            }

            if (!IsHost)
            {
                Debug.Log("[PreCacheLobby] Guest: starting pre-cache.");
                PlayerController?.PreCacheSegments(PreCacheTargetSeconds);
            }
            else
            {
                Debug.Log("[PreCacheLobby] Host: tracking guest buffer states.");
            }
        }

        private void HandlePeerBuffer(BufferStatusMsg msg)
        {
            if (!_peerStatuses.TryGetValue(msg.device_id, out var status))
            {
                status = new PeerBufferStatus { device_id = msg.device_id };
                _peerStatuses[msg.device_id] = status;
            }

            status.buffered_seconds  = msg.buffered_seconds;
            status.ready             = msg.ready;
            status.last_update_time  = Time.realtimeSinceStartup;

            Debug.Log($"[PreCacheLobby] Peer {msg.device_id}: {msg.buffered_seconds:F1}s buffered, ready={msg.ready}");
        }

        private void HandlePeerLeft(string deviceId)
        {
            _peerStatuses.Remove(deviceId);
            Debug.Log($"[PreCacheLobby] Peer left: {deviceId}");
        }

        private void HandleLocalReady()
        {
            _localReady = true;
            float bufferedSec = PlayerController != null
                ? PlayerController.GetBufferedMs() / 1000f
                : PreCacheTargetSeconds;
            _roomClient?.SendBufferStatus(bufferedSec, ready: true);
            Debug.Log("[PreCacheLobby] Local buffer ready — notified host.");
        }

        private void HandleLocalBuffering(float bufferedSeconds)
        {
            // Intermediate buffering events are handled in Update() on a timer
            // to avoid spamming the server. This event can be used by UI directly.
        }

        private void HandlePlay(PlayMsg msg)
        {
            if (IsHost) return; // host handles its own play

            OnPlaybackStarting?.Invoke();
            StartCoroutine(PlayAtWallTime(msg.position_ms, msg.wall_start_ms));
        }

        #endregion

        // ─────────────────────────────────────────────────────────────────────
        #region Helpers

        private IEnumerator PlayAtWallTime(long positionMs, long wallStartMs)
        {
            long nowMs  = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            long waitMs = wallStartMs - nowMs;

            if (waitMs > 0)
            {
                Debug.Log($"[PreCacheLobby] Waiting {waitMs}ms for wall-clock sync point.");
                yield return new WaitForSecondsRealtime(waitMs / 1000f);
            }

            // Compensate for any elapsed time during the wait
            long elapsedMs  = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - wallStartMs;
            long seekTarget  = positionMs + elapsedMs;

            if (PlayerController != null)
            {
                PlayerController.SeekTo(seekTarget);
                PlayerController.Play();
            }

            Debug.Log($"[PreCacheLobby] Playback started at {seekTarget}ms");
        }

        #endregion
    }
}
