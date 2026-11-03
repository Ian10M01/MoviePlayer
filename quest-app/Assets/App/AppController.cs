using System;
using System.Collections;
using System.Text;
using UnityEngine;
using UnityEngine.Networking;
using UnityEngine.UI;
using TMPro;
using MoviePlayer.Proto;
using MoviePlayer.Player;
using MoviePlayer.Room;
using MoviePlayer.Theater;

namespace MoviePlayer.App
{
    /// <summary>
    /// Top-level application coordinator.
    ///
    /// State machine
    /// ─────────────
    ///  Setup → Library → [MovieDetail] → [Lobby/Join] → Watching
    ///
    ///  Setup      : Prompt for homeNodeUrl if not in PlayerPrefs
    ///  Library    : Fetch GET /library, show grid of thumbnails
    ///  MovieDetail: Show manifest details + Watch/Enhance buttons
    ///  Lobby/Join : POST /rooms or POST /rooms/:code/join → WebSocket → LobbyUI
    ///  Watching   : HLS playing, VoiceChat active, SyncEngine running
    /// </summary>
    public class AppController : MonoBehaviour
    {
        // ── Inspector: References ─────────────────────────────────────────────
        [Header("Scene References")]
        public HlsPlayerController  PlayerController;
        public StereoScreenRenderer ScreenRenderer;
        public ClarityStack         ClarityStack;
        public TheaterEnvironment   Theater;
        public RoomClient           RoomClient;
        public PreCacheLobby        PreCacheLobby;
        public LobbyUI              LobbyUI;
        public VoiceChat            VoiceChat;

        [Header("UI Panels")]
        public GameObject SetupPanel;
        public GameObject LibraryPanel;
        public GameObject MovieDetailPanel;
        public GameObject WatchingPanel;

        [Header("Setup Panel")]
        public TMP_InputField HomeNodeUrlInput;
        public Button         SaveSetupButton;

        [Header("Library Panel")]
        public Transform  LibraryGrid;
        public GameObject MovieCardPrefab;  // has TMP_Text, RawImage, Button

        [Header("Movie Detail Panel")]
        public TMP_Text  DetailTitle;
        public TMP_Text  DetailMeta;
        public Button    WatchButton;
        public Button    EnhanceButton;
        public Button    JoinButton;
        public TMP_InputField InviteCodeInput;

        [Header("API")]
        [Tooltip("Cloud sync server base URL (wss://…)")]
        public string CloudWsUrl  = "wss://cloud.movieplayer.local/ws";
        [Tooltip("Cloud REST API base URL (https://…)")]
        public string CloudApiUrl = "https://cloud.movieplayer.local";

        // ── State ─────────────────────────────────────────────────────────────
        private string _homeNodeUrl   = string.Empty;
        private string _authToken     = string.Empty;  // JWT from PlayerPrefs
        private MediaManifest _selectedManifest;

        // ─────────────────────────────────────────────────────────────────────
        #region Unity Lifecycle

        private void Start()
        {
            _homeNodeUrl = PlayerPrefs.GetString("homeNodeUrl", string.Empty);
            _authToken   = PlayerPrefs.GetString("authToken",   string.Empty);
            // Override inspector URL defaults with any saved values (fallback = inspector field)
            CloudApiUrl = PlayerPrefs.GetString("ApiUrl", CloudApiUrl);
            CloudWsUrl  = PlayerPrefs.GetString("WsUrl",  CloudWsUrl);

            // Wire UI buttons
            if (SaveSetupButton != null) SaveSetupButton.onClick.AddListener(OnSaveSetup);
            if (WatchButton     != null) WatchButton.onClick.AddListener(OnWatchClicked);
            if (EnhanceButton   != null) EnhanceButton.onClick.AddListener(OnEnhanceClicked);
            if (JoinButton      != null) JoinButton.onClick.AddListener(OnJoinClicked);

            // Wire room events
            if (RoomClient != null)
            {
                RoomClient.OnJoined += OnRoomJoined;
            }

            ShowPanel(string.IsNullOrEmpty(_homeNodeUrl) ? SetupPanel : LibraryPanel);

            if (!string.IsNullOrEmpty(_homeNodeUrl))
                StartCoroutine(FetchLibrary());

            HandleDeepLink();
        }

        private void OnApplicationFocus(bool hasFocus)
        {
            if (hasFocus) HandleDeepLink();
        }

        #endregion

        // ─────────────────────────────────────────────────────────────────────
        #region Setup

        private void OnSaveSetup()
        {
            string url = HomeNodeUrlInput?.text?.Trim() ?? string.Empty;
            if (string.IsNullOrEmpty(url))
            {
                Debug.LogWarning("[AppController] Home node URL is empty.");
                return;
            }

            _homeNodeUrl = url.TrimEnd('/');
            PlayerPrefs.SetString("homeNodeUrl", _homeNodeUrl);
            PlayerPrefs.Save();

            ShowPanel(LibraryPanel);
            StartCoroutine(FetchLibrary());
        }

        #endregion

        // ─────────────────────────────────────────────────────────────────────
        #region Library

        private IEnumerator FetchLibrary()
        {
            string url = $"{_homeNodeUrl}/library";
            Debug.Log($"[AppController] Fetching library: {url}");

            using var req = UnityWebRequest.Get(url);
            req.SetRequestHeader("Authorization", $"Bearer {_authToken}");
            yield return req.SendWebRequest();

            if (req.result != UnityWebRequest.Result.Success)
            {
                Debug.LogError($"[AppController] Library fetch failed: {req.error}");
                yield break;
            }

            var wrapper = JsonUtility.FromJson<LibraryResponse>(req.downloadHandler.text);
            PopulateLibraryGrid(wrapper?.items ?? Array.Empty<MediaManifest>());
        }

        private void PopulateLibraryGrid(MediaManifest[] items)
        {
            if (LibraryGrid == null || MovieCardPrefab == null) return;

            // Clear old cards
            foreach (Transform child in LibraryGrid)
                Destroy(child.gameObject);

            foreach (var manifest in items)
            {
                var card = Instantiate(MovieCardPrefab, LibraryGrid);
                var titleLabel = card.GetComponentInChildren<TMP_Text>();
                if (titleLabel != null) titleLabel.text = manifest.filename;

                var btn = card.GetComponentInChildren<Button>();
                if (btn != null)
                {
                    var m = manifest; // capture
                    btn.onClick.AddListener(() => ShowMovieDetail(m));
                }

                // Load thumbnail async
                if (!string.IsNullOrEmpty(manifest.thumbs_path))
                    StartCoroutine(LoadThumbnail(card, manifest.thumbs_path));
            }
        }

        private IEnumerator LoadThumbnail(GameObject card, string thumbPath)
        {
            string url = thumbPath.StartsWith("http") ? thumbPath : $"{_homeNodeUrl}{thumbPath}";
            using var req = UnityWebRequestTexture.GetTexture(url);
            yield return req.SendWebRequest();

            if (req.result != UnityWebRequest.Result.Success) yield break;

            var texture  = DownloadHandlerTexture.GetContent(req);
            var rawImage = card.GetComponentInChildren<RawImage>();
            if (rawImage != null) rawImage.texture = texture;
        }

        #endregion

        // ─────────────────────────────────────────────────────────────────────
        #region Movie Detail

        private void ShowMovieDetail(MediaManifest manifest)
        {
            _selectedManifest = manifest;

            if (DetailTitle != null)
                DetailTitle.text = manifest.filename;

            if (DetailMeta != null)
                DetailMeta.text =
                    $"{manifest.width}×{manifest.height} {manifest.fps:F2}fps  " +
                    $"{manifest.codec}  {manifest.stereo?.ToUpper()}  {manifest.projection}  " +
                    $"{manifest.duration_s:F0}s  Clarity: {manifest.clarity_status}";

            if (EnhanceButton != null)
                EnhanceButton.gameObject.SetActive(
                    manifest.clarity_status == "bake_recommended");

            ShowPanel(MovieDetailPanel);
        }

        private void OnEnhanceClicked()
        {
            if (_selectedManifest == null) return;
            StartCoroutine(PostBakeRequest(_selectedManifest.id));
        }

        private IEnumerator PostBakeRequest(string manifestId)
        {
            string url = $"{_homeNodeUrl}/media/{manifestId}/bake";
            using var req = new UnityWebRequest(url, "POST");
            req.SetRequestHeader("Authorization", $"Bearer {_authToken}");
            req.downloadHandler = new DownloadHandlerBuffer();
            yield return req.SendWebRequest();

            Debug.Log(req.result == UnityWebRequest.Result.Success
                ? "[AppController] Bake requested successfully."
                : $"[AppController] Bake request failed: {req.error}");
        }

        #endregion

        // ─────────────────────────────────────────────────────────────────────
        #region Watch (Host) Flow

        private void OnWatchClicked()
        {
            if (_selectedManifest == null) return;
            StartCoroutine(CreateRoom(_selectedManifest));
        }

        private IEnumerator CreateRoom(MediaManifest manifest)
        {
            string url  = $"{CloudApiUrl}/rooms";
            var body    = JsonUtility.ToJson(new CreateRoomRequest
            {
                manifest_id = manifest.id,
                hls_url     = $"{_homeNodeUrl}{manifest.hls_path}"
            });

            using var req = new UnityWebRequest(url, "POST");
            req.uploadHandler   = new UploadHandlerRaw(Encoding.UTF8.GetBytes(body));
            req.downloadHandler = new DownloadHandlerBuffer();
            req.SetRequestHeader("Content-Type",  "application/json");
            req.SetRequestHeader("Authorization", $"Bearer {_authToken}");
            yield return req.SendWebRequest();

            if (req.result != UnityWebRequest.Result.Success)
            {
                Debug.LogError($"[AppController] CreateRoom failed: {req.error}");
                yield break;
            }

            var resp = JsonUtility.FromJson<CreateRoomResponse>(req.downloadHandler.text);
            _authToken = resp.room_token; // room-scoped token

            LobbyUI?.SetRoomCode(resp.room_code);
            ShowPanel(WatchingPanel);

            // Start HLS + connect WebSocket
            string hlsUrl = $"{_homeNodeUrl}{manifest.hls_path}";
            PlayerController?.Initialize(hlsUrl, _authToken);
            ScreenRenderer?.ApplyManifest(manifest);
            RoomClient?.ConnectAsync($"{CloudWsUrl}", _authToken);
        }

        #endregion

        // ─────────────────────────────────────────────────────────────────────
        #region Join (Guest) Flow

        private void OnJoinClicked()
        {
            string code = InviteCodeInput?.text?.Trim().ToUpperInvariant() ?? string.Empty;
            if (string.IsNullOrEmpty(code)) return;
            StartCoroutine(JoinRoom(code));
        }

        private IEnumerator JoinRoom(string roomCode)
        {
            string url = $"{CloudApiUrl}/rooms/{roomCode}/join";
            using var req = new UnityWebRequest(url, "POST");
            req.uploadHandler   = new UploadHandlerRaw(new byte[0]);
            req.downloadHandler = new DownloadHandlerBuffer();
            req.SetRequestHeader("Authorization", $"Bearer {_authToken}");
            yield return req.SendWebRequest();

            if (req.result != UnityWebRequest.Result.Success)
            {
                Debug.LogError($"[AppController] JoinRoom failed: {req.error}");
                yield break;
            }

            var resp = JsonUtility.FromJson<JoinRoomResponse>(req.downloadHandler.text);
            _authToken = resp.room_token;

            ShowPanel(WatchingPanel);

            // Start HLS playback using URL from join response (play_state syncs via 'joined' WS message)
            if (!string.IsNullOrEmpty(resp.hls_url))
                PlayerController?.Initialize(resp.hls_url, _authToken);

            // Connect WebSocket; 'joined' message carries current play_state for sync
            RoomClient?.ConnectAsync($"{CloudWsUrl}", _authToken);
        }

        private void OnRoomJoined(RoomState state)
        {
            // Display room code (available for both host and guest)
            if (!string.IsNullOrEmpty(state.room_code))
                LobbyUI?.SetRoomCode(state.room_code);

            // Sync player to room's current play position
            if (state.play_state != null && PlayerController != null)
            {
                if (state.play_state.position_ms > 0)
                    PlayerController.SeekTo(state.play_state.position_ms);
                if (state.play_state.playing)
                    PlayerController.Play();
            }
        }

        #endregion

        // ─────────────────────────────────────────────────────────────────────
        #region Deep Link (Quest invite links)

        private void HandleDeepLink()
        {
            // Quest deep links arrive as oculus://share/<roomCode>
            // or as a custom URI scheme configured in the Android manifest.
            string link = Application.absoluteURL;
            if (string.IsNullOrEmpty(link)) return;

            Uri uri;
            try { uri = new Uri(link); } catch { return; }

            if (uri.Segments.Length >= 2)
            {
                string code = uri.Segments[^1].Trim('/').ToUpperInvariant();
                if (!string.IsNullOrEmpty(code) && InviteCodeInput != null)
                {
                    InviteCodeInput.text = code;
                    StartCoroutine(JoinRoom(code));
                }
            }
        }

        #endregion

        // ─────────────────────────────────────────────────────────────────────
        #region Helpers

        private void ShowPanel(GameObject target)
        {
            GameObject[] all =
            {
                SetupPanel, LibraryPanel, MovieDetailPanel, WatchingPanel
            };
            foreach (var p in all)
                if (p != null) p.SetActive(p == target);
        }

        #endregion

        // ─────────────────────────────────────────────────────────────────────
        // Local DTO types for Unity JSON serialization (JsonUtility needs plain types)

        [Serializable] private class LibraryResponse   { public MediaManifest[] items; }
        [Serializable] private class CreateRoomRequest { public string manifest_id; public string hls_url; }
        // CreateRoomResponse and JoinRoomResponse are defined in MoviePlayer.Proto.RoomMessages
    }
}
