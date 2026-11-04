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
        public TMP_InputField HomeNodeUrlInput;   // Media / packager URL
        public TMP_InputField ApiUrlInput;         // Sync-server URL
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

        [Header("Direct Play (testing)")]
        [Tooltip("If set, skips the home-node/library flow and plays this video URL (mp4 or m3u8) on the screen at startup.")]
        public string DirectPlayUrl = string.Empty;

        [Header("Local Movies")]
        [Tooltip("Device folders scanned (recursively) for movies when no home node is configured. /sdcard covers all of the Quest's shared storage.")]
        public string[] LocalMovieFolders = { "/storage/emulated/0" };
        static readonly string[] VideoExtensions = { ".mp4", ".mkv", ".webm", ".mov", ".m4v", ".3gp" };
        // App-private and system data: unreadable under scoped storage and slow to walk.
        static readonly string[] SkipDirNames = { "Android", ".thumbnails", ".trash" };

        private PlayerControls _controls;
        private FriendsPanel   _friends;
        private PeerRoom       _room;
        private bool           _watching;
        private readonly System.Collections.Generic.Dictionary<string, string> _localFiles =
            new System.Collections.Generic.Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private long _seekOnReady;

        // ── State ─────────────────────────────────────────────────────────────
        private string _homeNodeUrl   = string.Empty;
        private string _authToken     = string.Empty;  // JWT from PlayerPrefs
        private MediaManifest _selectedManifest;

        // ─────────────────────────────────────────────────────────────────────
        #region Unity Lifecycle

        private void Start()
        {
            // Read MediaUrl (packager) — support legacy "homeNodeUrl" key
            _homeNodeUrl = PlayerPrefs.GetString("MediaUrl",
                               PlayerPrefs.GetString("homeNodeUrl", string.Empty));
            _authToken   = PlayerPrefs.GetString("authToken", string.Empty);
            // Override inspector URL defaults with any saved values
            CloudApiUrl = PlayerPrefs.GetString("ApiUrl", CloudApiUrl);
            CloudWsUrl  = PlayerPrefs.GetString("WsUrl",  CloudWsUrl);

            Debug.Log($"[App] Started.  MediaUrl={_homeNodeUrl}  ApiUrl={CloudApiUrl}");

            // Keep screen + menu in front of the viewer (startup and Quest recenter)
            var recenter = gameObject.AddComponent<ViewRecenter>();
            recenter.Content  = transform;
            recenter.UiCanvas = LibraryPanel != null ? LibraryPanel.transform.parent : null;

            // Visible, tracked Quest controllers
            gameObject.AddComponent<ControllerVisuals>();

            // Playback bar + screen/3D/picture settings
            _controls = gameObject.AddComponent<PlayerControls>();
            _controls.Player    = PlayerController;
            _controls.Screen    = ScreenRenderer;
            _controls.Clarity   = ClarityStack;
            _controls.Theater   = Theater;
            _controls.Recenter  = recenter;
            _controls.OnLibrary = ReturnToLibrary;
            var cardImage = LibraryPanel != null ? LibraryPanel.GetComponent<Image>() : null;
            _controls.RoundedSprite = cardImage != null ? cardImage.sprite : null;
            if (LibraryPanel != null) _controls.Build(LibraryPanel.transform.parent);

            // Watch together: voice chat, seats for friends, synced playback
            _room = gameObject.AddComponent<PeerRoom>();
            _friends = gameObject.AddComponent<FriendsPanel>();
            _friends.Room = _room;
            _friends.Ui   = _controls;
            _friends.OnClosed = () => { if (!_watching) ShowPanel(LibraryPanel); };
            if (LibraryPanel != null)
            {
                _friends.Build(LibraryPanel.transform.parent);
                AddFriendsButton(LibraryPanel.transform);
            }
            _controls.FriendsClicked += _friends.Toggle;
            _controls.LocalPlaybackChanged += (playing, pos) => { if (_room.InRoom) _room.SendPlayback(playing, pos); };
            _room.OnRemotePlayback += (playing, pos) => _controls.ApplyRemotePlayback(playing, pos);
            _room.OnRemoteMovie += OnRemoteMovie;

            // The film's colour lights the theater, like a real cinema
            if (ClarityStack != null && Theater != null)
                ClarityStack.OnAverageColour += (c, b) => Theater.SetScreenGlow(c, b);

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

            if (PlaybackHandoff.TryConsume(out var handoff) && !string.IsNullOrEmpty(handoff.url))
            {
                _seekOnReady = handoff.timeMs;
                if (PlayerController != null)
                    PlayerController.PreferAudioTrack(handoff.audioTrack);
                StartDirectPlayback(
                    handoff.url,
                    (StereoScreenRenderer.StereoLayout)handoff.layout,
                    (StereoScreenRenderer.ProjectionType)handoff.projection,
                    handoff.eyeSwap != 0,
                    true);
                return;
            }

            if (!string.IsNullOrEmpty(DirectPlayUrl))
            {
                StartDirectPlayback(DirectPlayUrl);
                return;
            }

            if (string.IsNullOrEmpty(_homeNodeUrl))
            {
                // No home node: browse movies stored on the device itself.
                ShowPanel(LibraryPanel);
                StartCoroutine(ShowLocalLibrary());
            }
            else
            {
                ShowPanel(LibraryPanel);
                StartCoroutine(FetchLibrary());
            }

            HandleDeepLink();
        }

        // Plays a single video URL with no server, auto-playing once prepared.
        private void StartDirectPlayback(string url,
            StereoScreenRenderer.StereoLayout layout = StereoScreenRenderer.StereoLayout.Mono,
            StereoScreenRenderer.ProjectionType projection = StereoScreenRenderer.ProjectionType.Flat,
            bool eyeSwap = false,
            bool lockChoice = false)
        {
            Debug.Log($"[App] Direct play → {url}  layout={layout}  projection={projection}");
            // Hide every panel: the watching panel is a dark overlay that would cover the screen.
            ShowPanel(null);
            _controls?.BeginMovie(url, layout, projection, lockChoice, eyeSwap);
            _friends?.Show(false);
            _watching = true;
            if (_room != null && _room.InRoom && _room.IsHost)
                _room.SendMovie(System.IO.Path.GetFileName(url));
            if (PlayerController == null) return;
            PlayerController.OnReady -= OnPlayerReady;
            PlayerController.OnReady += OnPlayerReady;
            PlayerController.Initialize(url, string.Empty);
        }

        private void OnPlayerReady()
        {
            if (PlayerController == null) return;
            if (_seekOnReady > 0)
            {
                PlayerController.SeekTo(_seekOnReady);
                _seekOnReady = 0;
            }
            PlayerController.Play();
        }

        private void OnApplicationFocus(bool hasFocus)
        {
            if (hasFocus) HandleDeepLink();
        }

        #endregion

        // ─────────────────────────────────────────────────────────────────────
        #region Local Library

        private IEnumerator ShowLocalLibrary()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            // Android 13+ uses READ_MEDIA_VIDEO; older versions use READ_EXTERNAL_STORAGE.
            string[] perms = { "android.permission.READ_MEDIA_VIDEO", "android.permission.READ_EXTERNAL_STORAGE" };
            foreach (var p in perms)
                if (!UnityEngine.Android.Permission.HasUserAuthorizedPermission(p))
                    UnityEngine.Android.Permission.RequestUserPermission(p);
            // Wait for the permission dialog to be answered (app regains focus).
            yield return new WaitForSeconds(0.5f);
            while (!Application.isFocused) yield return null;
            string[] folders = LocalMovieFolders;
#else
            string[] folders = { Environment.GetFolderPath(Environment.SpecialFolder.MyVideos) };
            yield return null;
#endif
            var files = new System.Collections.Generic.List<string>();
            foreach (var folder in folders)
                if (!string.IsNullOrEmpty(folder) && System.IO.Directory.Exists(folder))
                    CollectVideos(folder, files);
            files.Sort((a, b) => string.Compare(System.IO.Path.GetFileName(a),
                                                System.IO.Path.GetFileName(b),
                                                StringComparison.OrdinalIgnoreCase));
            Debug.Log($"[App] Local library: {files.Count} movie(s) in {string.Join(", ", folders)}");

            if (LibraryGrid == null) yield break;
            FixLibraryMask();
            foreach (Transform child in LibraryGrid) Destroy(child.gameObject);

            if (files.Count == 0)
            {
                AddLibraryRow($"No movies found. Copy videos to {string.Join(" or ", folders)}", null);
                yield break;
            }

            _localFiles.Clear();
            foreach (var file in files)
            {
                string path = file; // capture
                _localFiles[System.IO.Path.GetFileName(path)] = path;
                string filePath = path;
                AddLibraryRow(System.IO.Path.GetFileNameWithoutExtension(filePath), () =>
                {
                    var guess = StereoDetect.FromFilename(filePath);
                    StartDirectPlayback(OnDeviceTranscoder.ToFileUrl(filePath), guess.Layout, guess.Projection, guess.EyeSwap);
                });
            }
        }

        // The scroll viewport's stencil Mask uses a fully transparent Image, which Unity culls
        // (cullTransparentMesh), so the mask writes nothing and every row is clipped away.
        // RectMask2D clips by rectangle and needs no visible graphic.
        private void FixLibraryMask()
        {
            var viewport = LibraryGrid.parent;
            var scroll = viewport != null ? viewport.GetComponentInParent<ScrollRect>() : null;
            if (scroll != null)
            {
                scroll.horizontal = false;
                scroll.vertical = true;
                scroll.movementType = ScrollRect.MovementType.Clamped;
                scroll.inertia = true;
                scroll.decelerationRate = 0.1f;
                scroll.scrollSensitivity = 80f;
            }
            if (viewport == null || viewport.GetComponent<RectMask2D>() != null) return;
            var mask = viewport.GetComponent<Mask>();
            if (mask != null) DestroyImmediate(mask);
            viewport.gameObject.AddComponent<RectMask2D>();
        }

        // Recursive walk that skips unreadable folders instead of aborting the whole scan.
        private static void CollectVideos(string dir, System.Collections.Generic.List<string> files)
        {
            try
            {
                foreach (var f in System.IO.Directory.GetFiles(dir))
                    if (Array.IndexOf(VideoExtensions, System.IO.Path.GetExtension(f).ToLowerInvariant()) >= 0)
                        files.Add(f);

                foreach (var sub in System.IO.Directory.GetDirectories(dir))
                    if (Array.IndexOf(SkipDirNames, System.IO.Path.GetFileName(sub)) < 0)
                        CollectVideos(sub, files);
            }
            catch (Exception e) { Debug.LogWarning($"[App] Skipping {dir}: {e.Message}"); }
        }

        /// <summary>Stops the current movie and returns to the library so another one can be picked.</summary>
        public void ReturnToLibrary()
        {
            if (PlayerController != null)
            {
                // Stop any in-flight prepare first so it cannot fire OnReady and play behind the library.
                PlayerController.OnReady -= OnPlayerReady;
                PlayerController.CancelPrepare();
                PlayerController.Stop();
            }
            _seekOnReady = 0;
            _watching = false;
            _controls?.EndMovie();
            ShowPanel(LibraryPanel);
            RepopulateLibraryIfEmpty();
        }

        // Movies started from a handoff or DirectPlayUrl skip the library fetch, so the grid can be empty.
        private void RepopulateLibraryIfEmpty()
        {
            if (LibraryGrid == null || LibraryGrid.childCount > 0) return;
            if (string.IsNullOrEmpty(_homeNodeUrl)) StartCoroutine(ShowLocalLibrary());
            else StartCoroutine(FetchLibrary());
        }

        // A friend (the host) started a movie: play our own copy of the same file.
        private void OnRemoteMovie(string fileName)
        {
            if (_localFiles.TryGetValue(fileName, out var path))
            {
                var guess = StereoDetect.FromFilename(path);
                StartDirectPlayback(OnDeviceTranscoder.ToFileUrl(path), guess.Layout, guess.Projection, guess.EyeSwap);
            }
            else
                Debug.LogWarning($"[App] Friend is playing '{fileName}', which isn't on this Quest.");
        }

        // "Watch together" button under the library card.
        private void AddFriendsButton(Transform libraryPanel)
        {
            var label = _controls.MakeButton(libraryPanel, "Watch together", new Color(0.298f, 0.553f, 1f, 1f),
                                             () => { ShowPanel(null); _friends.Show(true); }, 0f);
            var rt = (RectTransform)label.transform.parent;
            rt.anchorMin = rt.anchorMax = new Vector2(0.5f, 0f);
            rt.pivot = new Vector2(0.5f, 1f);
            rt.sizeDelta = new Vector2(420f, 84f);
            rt.anchoredPosition = new Vector2(0f, -24f);
            label.fontSize = 30;
        }

        // Builds one clickable text row in the library list (no prefab needed).
        private void AddLibraryRow(string title, Action onClick)
        {
            var row = new GameObject("MovieRow", typeof(RectTransform), typeof(Image), typeof(LayoutElement));
            row.transform.SetParent(LibraryGrid, false);
            row.GetComponent<Image>().color = new Color(0.12f, 0.12f, 0.16f, 1f);
            row.GetComponent<LayoutElement>().preferredHeight = 56f;

            var labelGO = new GameObject("Title", typeof(RectTransform));
            labelGO.transform.SetParent(row.transform, false);
            var lrt = labelGO.GetComponent<RectTransform>();
            lrt.anchorMin = Vector2.zero; lrt.anchorMax = Vector2.one;
            lrt.offsetMin = new Vector2(16f, 0f); lrt.offsetMax = new Vector2(-16f, 0f);
            var label = labelGO.AddComponent<TextMeshProUGUI>();
            label.text = title;
            label.fontSize = 22;
            label.alignment = TextAlignmentOptions.MidlineLeft;
            label.color = Color.white;

            if (onClick != null)
                row.AddComponent<Button>().onClick.AddListener(() => onClick());
        }

        #endregion

        // ─────────────────────────────────────────────────────────────────────
        #region Setup

        private void OnSaveSetup()
        {
            string mediaUrl = HomeNodeUrlInput?.text?.Trim() ?? string.Empty;
            string apiUrl   = ApiUrlInput?.text?.Trim() ?? string.Empty;

            if (string.IsNullOrEmpty(mediaUrl))
            {
                Debug.LogWarning("[AppController] Packager URL is empty.");
                return;
            }

            _homeNodeUrl = mediaUrl.TrimEnd('/');
            PlayerPrefs.SetString("MediaUrl",    _homeNodeUrl);
            PlayerPrefs.SetString("homeNodeUrl", _homeNodeUrl); // backward compat

            if (!string.IsNullOrEmpty(apiUrl))
            {
                CloudApiUrl = apiUrl.TrimEnd('/');
                // Derive WebSocket URL from API URL
                CloudWsUrl = CloudApiUrl
                    .Replace("https://", "wss://")
                    .Replace("http://",  "ws://");
                PlayerPrefs.SetString("ApiUrl", CloudApiUrl);
                PlayerPrefs.SetString("WsUrl",  CloudWsUrl);
            }

            PlayerPrefs.Save();
            Debug.Log($"[App] Saved URLs — MediaUrl={_homeNodeUrl}  ApiUrl={CloudApiUrl}");

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
