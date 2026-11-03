using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using TMPro;
using MoviePlayer.Proto;

namespace MoviePlayer.Room
{
    /// <summary>
    /// Pre-cache lobby UI. Shows per-seat buffer progress rings, ready states,
    /// the room invite code, and host play controls.
    ///
    /// Wire-up
    /// ───────
    /// Assign references in the Inspector.  The component polls PreCacheLobby
    /// every frame; no additional event wiring is needed beyond OnEnable/Disable.
    ///
    /// Progress Ring
    /// ─────────────
    /// Each seat row uses a Unity UI Image in Filled / Radial360 mode as the
    /// progress ring.  Set Image.fillAmount = buffered / targetSeconds.
    /// </summary>
    public class LobbyUI : MonoBehaviour
    {
        // ── Inspector References ──────────────────────────────────────────────
        [Header("Top Bar")]
        public TMP_Text InviteCodeLabel;    // e.g. "Room: ABCD-1234"
        public TMP_Text StatusLabel;        // generic status line

        [Header("Seat Row Template")]
        [Tooltip("Inactive GameObject used as a template for each seat row.")]
        public GameObject SeatRowPrefab;

        [Tooltip("Parent container (Vertical Layout Group) for seat rows.")]
        public Transform SeatRowContainer;

        [Header("Host Controls")]
        public Button PlayWhenReadyButton;  // enabled only when AllReady
        public Button PlayNowButton;        // always enabled (host only)
        public GameObject HostControlsPanel;

        [Header("Guest Status")]
        public TMP_Text GuestStatusLabel;   // "Buffering... 45s / 60s"
        public GameObject GuestStatusPanel;

        [Header("Logic")]
        public PreCacheLobby LobbyLogic;

        [Header("Buffer")]
        public int TargetBufferSeconds = 60;

        // ── Internal Seat Row State ───────────────────────────────────────────
        private class SeatRowUI
        {
            public GameObject Root;
            public TMP_Text   NameLabel;
            public TMP_Text   RoleLabel;
            public Image      ProgressRing;   // Filled / Radial360
            public Image      ReadyIcon;
            public float      DisplayedFill;  // for smooth lerp
        }

        private readonly List<SeatRowUI> _rows = new List<SeatRowUI>();
        private string _roomCode = string.Empty;

        // ─────────────────────────────────────────────────────────────────────
        #region Unity Lifecycle

        private void OnEnable()
        {
            if (LobbyLogic == null) return;
            LobbyLogic.OnAllReady          += HandleAllReady;
            LobbyLogic.OnPlaybackStarting  += HandlePlaybackStarting;

            if (PlayWhenReadyButton != null)
                PlayWhenReadyButton.onClick.AddListener(OnPlayWhenReadyClicked);

            if (PlayNowButton != null)
                PlayNowButton.onClick.AddListener(OnPlayNowClicked);
        }

        private void OnDisable()
        {
            if (LobbyLogic == null) return;
            LobbyLogic.OnAllReady         -= HandleAllReady;
            LobbyLogic.OnPlaybackStarting -= HandlePlaybackStarting;

            if (PlayWhenReadyButton != null)
                PlayWhenReadyButton.onClick.RemoveListener(OnPlayWhenReadyClicked);

            if (PlayNowButton != null)
                PlayNowButton.onClick.RemoveListener(OnPlayNowClicked);
        }

        private void Update()
        {
            if (LobbyLogic == null) return;
            RefreshUI();
        }

        #endregion

        // ─────────────────────────────────────────────────────────────────────
        #region Public API

        /// <summary>Call after joining a room to set the invite code display.</summary>
        public void SetRoomCode(string code)
        {
            _roomCode = code;
            if (InviteCodeLabel != null)
                InviteCodeLabel.text = $"Room: {code}";
        }

        #endregion

        // ─────────────────────────────────────────────────────────────────────
        #region UI Refresh

        private void RefreshUI()
        {
            bool isHost = LobbyLogic.IsHost;

            // Show/hide panels
            if (HostControlsPanel != null) HostControlsPanel.SetActive(isHost);
            if (GuestStatusPanel  != null) GuestStatusPanel.SetActive(!isHost);

            if (isHost)
                RefreshHostUI();
            else
                RefreshGuestUI();

            // Seat rows (visible to both host and guests)
            RefreshSeatRows(LobbyLogic.PeerStatuses);
        }

        private void RefreshHostUI()
        {
            bool allReady = LobbyLogic.AllReady;

            if (PlayWhenReadyButton != null)
                PlayWhenReadyButton.interactable = allReady;

            if (StatusLabel != null)
            {
                StatusLabel.text = allReady
                    ? "All guests ready — tap Play!"
                    : "Waiting for guests to buffer…";
            }
        }

        private void RefreshGuestUI()
        {
            if (GuestStatusLabel == null || LobbyLogic == null) return;

            // Pull local buffered time from player if accessible
            float buffered = 0f;
            var player = LobbyLogic.GetComponent<Player.HlsPlayerController>();
            if (player != null)
                buffered = player.GetBufferedMs() / 1000f;

            GuestStatusLabel.text = player != null && player.IsReady
                ? "Ready! Waiting for host…"
                : $"Buffering… {buffered:F0}s / {TargetBufferSeconds}s";
        }

        private void RefreshSeatRows(IReadOnlyDictionary<string, PeerBufferStatus> statuses)
        {
            if (SeatRowPrefab == null || SeatRowContainer == null) return;

            // Grow pool
            int needed = statuses.Count;
            while (_rows.Count < needed)
            {
                var go  = Instantiate(SeatRowPrefab, SeatRowContainer);
                var row = new SeatRowUI
                {
                    Root         = go,
                    NameLabel    = go.transform.Find("NameLabel")?.GetComponent<TMP_Text>(),
                    RoleLabel    = go.transform.Find("RoleLabel")?.GetComponent<TMP_Text>(),
                    ProgressRing = go.transform.Find("ProgressRing")?.GetComponent<Image>(),
                    ReadyIcon    = go.transform.Find("ReadyIcon")?.GetComponent<Image>(),
                    DisplayedFill = 0f
                };
                _rows.Add(row);
            }

            // Hide extras
            for (int i = needed; i < _rows.Count; i++)
                _rows[i].Root.SetActive(false);

            // Populate visible rows
            int idx = 0;
            foreach (var kv in statuses)
            {
                var status = kv.Value;
                var row    = _rows[idx++];
                row.Root.SetActive(true);

                if (row.NameLabel != null)
                    row.NameLabel.text = string.IsNullOrEmpty(status.display_name)
                        ? status.device_id
                        : status.display_name;

                // Smooth progress ring animation
                float targetFill = Mathf.Clamp01(status.buffered_s / TargetBufferSeconds);
                row.DisplayedFill = Mathf.Lerp(row.DisplayedFill, targetFill, Time.deltaTime * 4f);
                if (row.ProgressRing != null)
                    row.ProgressRing.fillAmount = row.DisplayedFill;

                // Ready indicator
                if (row.ReadyIcon != null)
                    row.ReadyIcon.gameObject.SetActive(status.ready);
            }
        }

        #endregion

        // ─────────────────────────────────────────────────────────────────────
        #region Button Handlers

        private void OnPlayWhenReadyClicked()
        {
            if (LobbyLogic != null && LobbyLogic.AllReady)
                LobbyLogic.HostPlay();
        }

        private void OnPlayNowClicked()
        {
            LobbyLogic?.HostPlay();
        }

        #endregion

        // ─────────────────────────────────────────────────────────────────────
        #region Lobby Events

        private void HandleAllReady()
        {
            if (StatusLabel != null)
                StatusLabel.text = "All guests ready!";
            if (PlayWhenReadyButton != null)
                PlayWhenReadyButton.interactable = true;
        }

        private void HandlePlaybackStarting()
        {
            // Hide lobby overlay when movie starts
            gameObject.SetActive(false);
        }

        #endregion
    }
}
