using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem.EnhancedTouch;
using UnityEngine.Networking;
using UnityEngine.Rendering.Universal;
using UnityEngine.SceneManagement;
using UnityEngine.UI;
using MoviePlayer.Player;
using MoviePlayer.Proto;

namespace MoviePlayer.App
{
    /// <summary>
    /// 2D Quest panel. The movie is a uGUI RawImage, so it plays with no headset
    /// tracking and the window can sit beside other Horizon apps. Stereo modes
    /// keep a 3D title watchable on that flat window. "VR theater" opens the
    /// immersive activity at the same URL and time.
    /// </summary>
    public class PanelPlayer : MonoBehaviour
    {
        enum ViewMode { Mono, Left, Right, Sbs, Ou, Anaglyph }
        enum Packing { Sbs, Ou }

        static readonly string[] VideoExtensions = { ".mp4", ".mkv", ".webm", ".mov", ".m4v" };
        static readonly string[] SkipDirNames = { "Android", ".thumbnails", ".trash" };
        static readonly Color Accent = new Color(0.298f, 0.553f, 1f, 1f);
        static readonly Color Bar = new Color(0.08f, 0.09f, 0.11f, 0.94f);
        static readonly Color Warn = new Color(1f, 0.72f, 0.32f, 1f);

        HlsPlayerController _player;
        Font _font;
        RawImage _video;
        AspectRatioFitter _fit;
        Material _anaglyph;
        Text _status;
        Text _audio;
        Text _time;
        Text _playLabel;
        Slider _seek;
        InputField _urlField;
        RectTransform _listContent;
        RectTransform _langRoot;
        GameObject _listRoot;
        bool _seeking;
        bool _built;
        string _url = "";
        ViewMode _mode = ViewMode.Mono;
        Packing _pack = Packing.Sbs;
        readonly Dictionary<ViewMode, Image> _modeButtons = new Dictionary<ViewMode, Image>();

        [Serializable] class LibraryResponse { public MediaManifest[] items; }

        void Awake()
        {
            EnhancedTouchSupport.Enable();
            _player = GetComponent<HlsPlayerController>();
            if (_player == null) _player = gameObject.AddComponent<HlsPlayerController>();
            _player.OnReady += OnVideoReady;
            _player.OnAudioTracksReady += RefreshAudio;
            _player.OnAudioChoicesChanged += ShowLanguages;
            _player.OnPrepareProgress += OnPrepare;
            _player.OnError += OnVideoError;
            _font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            var shader = Shader.Find("MoviePlayer/PanelStereo");
            if (shader != null) _anaglyph = new Material(shader) { name = "PanelAnaglyph" };
            EnsureFlatCamera();
            BuildUi();
            _built = true;
            if (!ModeRouter.IsTheaterActivity())
            {
                Debug.Log("[App] Panel visible");
                StartCoroutine(PresentPanel());
            }
            string saved = PlayerPrefs.GetString("MediaUrl", "");
            if (_urlField != null) _urlField.text = saved;
            StartCoroutine(ScanLocal());
        }

        void OnDestroy()
        {
            if (_player != null)
            {
                _player.OnReady -= OnVideoReady;
                _player.OnAudioTracksReady -= RefreshAudio;
                _player.OnAudioChoicesChanged -= ShowLanguages;
                _player.OnPrepareProgress -= OnPrepare;
                _player.OnError -= OnVideoError;
            }
            if (_anaglyph != null) Destroy(_anaglyph);
        }

        void Update()
        {
            if (!_built || _player == null || _seeking) return;
            long pos = _player.GetPositionMs();
            long dur = _player.GetDurationMs();
            if (_time != null)
                _time.text = Format(pos) + " / " + Format(dur);
            if (_seek != null && dur > 0)
                _seek.SetValueWithoutNotify(Mathf.Clamp01((float)pos / dur));
            if (_playLabel != null)
                _playLabel.text = _player.IsPlaying ? "Pause" : "Play";
        }

        void OnVideoReady()
        {
            if (_video != null) _video.texture = _player.VideoTexture;
            ApplyView();
            _player.Play();
            SetStatus("");
        }

        void OnVideoError(string message)
        {
            SetStatus(string.IsNullOrEmpty(message) ? "Playback error" : message);
            if (_status != null) _status.color = Warn;
        }

        void PlayUrl(string url, string label)
        {
            if (string.IsNullOrWhiteSpace(url) || _player == null) return;
            _url = url.Trim();
            _player.PreferAudioTrack(-1);
            _player.Initialize(_url, PlayerPrefs.GetString("authToken", ""));
            if (_video != null) _video.texture = _player.VideoTexture;
            ShowList(false);
            SetStatus("Opening " + (string.IsNullOrEmpty(label) ? _url : label));
            ApplyView();
        }

        void OnOpenClicked()
        {
            string text = _urlField != null ? _urlField.text.Trim() : "";
            if (string.IsNullOrEmpty(text))
            {
                StartCoroutine(ScanLocal());
                return;
            }
            if (LooksLikeMedia(text))
            {
                PlayUrl(text, "");
                return;
            }
            string baseUrl = text.TrimEnd('/');
            PlayerPrefs.SetString("MediaUrl", baseUrl);
            PlayerPrefs.Save();
            StartCoroutine(FetchLibrary(baseUrl));
        }

        static bool LooksLikeMedia(string text)
        {
            string lower = text.ToLowerInvariant();
            return lower.Contains(".m3u8") || lower.Contains(".mp4") || lower.Contains(".mkv")
                || lower.Contains(".webm") || lower.Contains(".mov") || lower.StartsWith("file:");
        }

        IEnumerator FetchLibrary(string baseUrl)
        {
            SetStatus("Loading library…");
            using var req = UnityWebRequest.Get(baseUrl + "/library");
            string token = PlayerPrefs.GetString("authToken", "");
            if (!string.IsNullOrEmpty(token))
                req.SetRequestHeader("Authorization", "Bearer " + token);
            yield return req.SendWebRequest();
            if (req.result != UnityWebRequest.Result.Success)
            {
                SetStatus("Library failed: " + req.error);
                yield break;
            }
            LibraryResponse wrapper = null;
            try { wrapper = JsonUtility.FromJson<LibraryResponse>(req.downloadHandler.text); }
            catch { /* shown below */ }
            var items = wrapper?.items ?? Array.Empty<MediaManifest>();
            ClearList();
            if (items.Length == 0)
            {
                AddListRow("No movies on the home server", null);
                ShowList(true);
                yield break;
            }
            foreach (var item in items)
            {
                var manifest = item;
                string hls = manifest.hls_path ?? "";
                string absolute = hls.StartsWith("http") ? hls : baseUrl + "/" + hls.TrimStart('/');
                AddListRow(manifest.filename, () => PlayUrl(absolute, manifest.filename));
            }
            ShowList(true);
            SetStatus(items.Length + " movie(s)");
        }

        IEnumerator ScanLocal()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            string[] perms = { "android.permission.READ_MEDIA_VIDEO", "android.permission.READ_EXTERNAL_STORAGE" };
            foreach (var perm in perms)
                if (!UnityEngine.Android.Permission.HasUserAuthorizedPermission(perm))
                    UnityEngine.Android.Permission.RequestUserPermission(perm);
            yield return new WaitForSeconds(0.4f);
            while (!Application.isFocused) yield return null;
            string[] folders = { "/storage/emulated/0" };
#else
            string[] folders = { Environment.GetFolderPath(Environment.SpecialFolder.MyVideos) };
            yield return null;
#endif
            var files = new List<string>();
            foreach (var folder in folders)
                if (!string.IsNullOrEmpty(folder) && Directory.Exists(folder))
                    Collect(folder, files);
            files.Sort((a, b) => string.Compare(Path.GetFileName(a), Path.GetFileName(b), StringComparison.OrdinalIgnoreCase));
            ClearList();
            if (files.Count == 0)
            {
                AddListRow("No movies in shared storage", null);
                ShowList(true);
                SetStatus("Copy videos onto the Quest, or enter a packager URL.");
                yield break;
            }
            foreach (var file in files)
            {
                string path = file;
                AddListRow(Path.GetFileNameWithoutExtension(path), () => PlayUrl("file://" + path, Path.GetFileName(path)));
            }
            ShowList(true);
            SetStatus(files.Count + " movie(s) on this Quest");
        }

        static void Collect(string dir, List<string> files)
        {
            try
            {
                foreach (var file in Directory.GetFiles(dir))
                    if (Array.IndexOf(VideoExtensions, Path.GetExtension(file).ToLowerInvariant()) >= 0)
                        files.Add(file);
                foreach (var sub in Directory.GetDirectories(dir))
                    if (Array.IndexOf(SkipDirNames, Path.GetFileName(sub)) < 0)
                        Collect(sub, files);
            }
            catch (Exception e) { Debug.LogWarning("[Panel] Skipping " + dir + ": " + e.Message); }
        }

        void EnterTheater()
        {
            if (string.IsNullOrEmpty(_url) || _player == null)
            {
                SetStatus("Play a movie first.");
                return;
            }
            int layout = (int)VrLayout();
            int projection = (int)VrProjection();
            PlaybackHandoff.Write(_url, _player.GetPositionMs(), layout, projection, _player.SelectedSourceOrdinal);
            _player.CancelPrepare();
            SetStatus("Opening VR theater…");
            if (!TheaterLaunch.TryStart())
                StartCoroutine(EnterTheaterHere());
        }

        IEnumerator EnterTheaterHere()
        {
            yield return XRSession.EnsureStarted();
            SceneManager.LoadScene("MainScene");
        }

        StereoScreenRenderer.StereoLayout VrLayout()
        {
            string name = " " + Path.GetFileNameWithoutExtension(Uri.UnescapeDataString(_url ?? "")).ToUpperInvariant()
                .Replace('_', ' ').Replace('-', ' ').Replace('.', ' ') + " ";
            bool full = name.Contains(" FULL ") || name.Contains(" FSBS ") || name.Contains(" FOU ");
            if (_mode == ViewMode.Mono) return StereoScreenRenderer.StereoLayout.Mono;
            if (_pack == Packing.Ou)
                return full ? StereoScreenRenderer.StereoLayout.FOU : StereoScreenRenderer.StereoLayout.HOU;
            return full ? StereoScreenRenderer.StereoLayout.FSBS : StereoScreenRenderer.StereoLayout.HSBS;
        }

        StereoScreenRenderer.ProjectionType VrProjection()
        {
            string name = " " + Path.GetFileNameWithoutExtension(_url ?? "").ToUpperInvariant().Replace('_', ' ') + " ";
            if (name.Contains(" 360 ")) return StereoScreenRenderer.ProjectionType.Equirect360;
            if (name.Contains(" 180 ") || name.Contains(" VR180 ")) return StereoScreenRenderer.ProjectionType.Equirect180;
            return StereoScreenRenderer.ProjectionType.Flat;
        }

        void SetMode(ViewMode mode)
        {
            _mode = mode;
            if (mode == ViewMode.Ou) _pack = Packing.Ou;
            else if (mode == ViewMode.Sbs || mode == ViewMode.Mono) _pack = Packing.Sbs;
            ApplyView();
        }

        void ApplyView()
        {
            if (_video == null) return;
            bool anaglyph = _mode == ViewMode.Anaglyph && _anaglyph != null;
            _video.material = anaglyph ? _anaglyph : null;
            _video.uvRect = Uv(_mode, _pack);
            if (anaglyph)
                _anaglyph.SetFloat("_Pack", _pack == Packing.Ou ? 1f : 0f);
            if (_fit != null)
                _fit.aspectRatio = DisplayAspect();
            foreach (var pair in _modeButtons)
                pair.Value.color = pair.Key == _mode ? Accent : new Color(1f, 1f, 1f, 0.12f);
        }

        float DisplayAspect()
        {
            float aspect = 16f / 9f;
            if (_player != null && _player.VideoWidth > 0 && _player.VideoHeight > 0)
                aspect = (float)_player.VideoWidth / _player.VideoHeight;
            if (_mode == ViewMode.Left || _mode == ViewMode.Right || _mode == ViewMode.Anaglyph)
                aspect = _pack == Packing.Ou ? aspect * 2f : aspect * 0.5f;
            return Mathf.Max(0.2f, aspect);
        }

        static Rect Uv(ViewMode mode, Packing pack)
        {
            bool eye = mode == ViewMode.Left || mode == ViewMode.Right;
            if (!eye) return new Rect(0f, 0f, 1f, 1f);
            if (pack == Packing.Ou)
                return mode == ViewMode.Left ? new Rect(0f, 0.5f, 1f, 0.5f) : new Rect(0f, 0f, 1f, 0.5f);
            return mode == ViewMode.Left ? new Rect(0f, 0f, 0.5f, 1f) : new Rect(0.5f, 0f, 0.5f, 1f);
        }

        void TogglePlay()
        {
            if (_player == null) return;
            if (_player.IsPlaying) _player.Pause();
            else _player.Play();
        }

        void OnSeek(float value)
        {
            if (_player == null) return;
            long dur = _player.GetDurationMs();
            if (dur > 0) _player.SeekTo((long)(value * dur));
        }

        void OnPrepare(string message, float progress)
        {
            if (progress >= 0f && progress < 1f)
                SetStatus(message + " " + Mathf.RoundToInt(progress * 100f) + "%");
            else
                SetStatus(message);
        }

        void ShowLanguages()
        {
            if (_langRoot == null || _player == null) return;
            for (int i = _langRoot.childCount - 1; i >= 0; i--)
                Destroy(_langRoot.GetChild(i).gameObject);
            var choices = _player.AudioChoices;
            if (choices == null) return;
            foreach (var choice in choices)
            {
                var picked = choice;
                var button = MakeButton(_langRoot, picked.Display, Vector2.zero, new Vector2(210f, 60f),
                    () => _player.SelectLanguage(picked.Language, picked.Ordinal), false);
                button.anchorMin = button.anchorMax = new Vector2(0f, 0.5f);
                button.pivot = new Vector2(0f, 0.5f);
                var layout = button.gameObject.AddComponent<LayoutElement>();
                layout.preferredWidth = 210f;
                layout.preferredHeight = 60f;
                button.GetComponent<Image>().color = picked.Selected ? Accent : new Color(1f, 1f, 1f, 0.12f);
            }
            RefreshAudio();
        }

        void RefreshAudio()
        {
            if (_audio == null || _player == null) return;
            _audio.text = string.IsNullOrEmpty(_player.SelectedAudioLabel) ? "Language" : _player.SelectedAudioLabel;
            _audio.color = Color.white;
        }

        void SetStatus(string text)
        {
            if (_status != null)
            {
                _status.color = Color.white;
                _status.text = text ?? "";
            }
        }

        void ShowList(bool show)
        {
            if (_listRoot != null) _listRoot.SetActive(show);
        }

        void ClearList()
        {
            if (_listContent == null) return;
            for (int i = _listContent.childCount - 1; i >= 0; i--)
                Destroy(_listContent.GetChild(i).gameObject);
        }

        void AddListRow(string title, Action onClick)
        {
            var row = new GameObject("Row", typeof(RectTransform), typeof(Image), typeof(Button), typeof(LayoutElement));
            row.transform.SetParent(_listContent, false);
            row.GetComponent<LayoutElement>().preferredHeight = 72f;
            row.GetComponent<Image>().color = new Color(1f, 1f, 1f, 0.08f);
            var text = MakeText(row.transform, title, 28, TextAnchor.MiddleLeft, false);
            var rt = (RectTransform)text.transform;
            rt.offsetMin = new Vector2(24f, 0f);
            rt.offsetMax = new Vector2(-16f, 0f);
            if (onClick != null)
                row.GetComponent<Button>().onClick.AddListener(() => onClick());
        }

        static string Format(long ms)
        {
            var t = TimeSpan.FromMilliseconds(Math.Max(0, ms));
            return t.TotalHours >= 1
                ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}"
                : $"{t.Minutes}:{t.Seconds:00}";
        }

        static void EnsureFlatCamera()
        {
            var cam = Camera.main;
            if (cam == null)
            {
                var go = new GameObject("Main Camera");
                go.tag = "MainCamera";
                cam = go.AddComponent<Camera>();
                go.AddComponent<AudioListener>();
            }
            cam.enabled = true;
            cam.targetTexture = null;
            cam.targetDisplay = 0;
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = new Color(0.12f, 0.22f, 0.45f, 1f);
            cam.orthographic = true;
            cam.orthographicSize = 5f;
            cam.nearClipPlane = 0.1f;
            cam.farClipPlane = 50f;
            var data = cam.GetUniversalAdditionalCameraData();
            data.renderType = CameraRenderType.Base;
            data.allowXRRendering = false;
        }

        IEnumerator PresentPanel()
        {
            // Horizon composites the SurfaceView. Until that view has a buffer the panel is black,
            // even though the camera already has a 1280x800 target.
            NudgeAndroidSurface();
            yield return new WaitForEndOfFrame();
            var cam = Camera.main;
            Debug.Log("[App] Panel frame " + (cam != null ? cam.pixelWidth + "x" + cam.pixelHeight : "none"));
        }

        static void NudgeAndroidSurface()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            try
            {
                using var unityPlayer = new AndroidJavaClass("com.unity3d.player.UnityPlayer");
                using var activity = unityPlayer.GetStatic<AndroidJavaObject>("currentActivity");
                activity.Call("runOnUiThread", new AndroidJavaRunnable(ApplySurfaceLayout));
            }
            catch (Exception e)
            {
                Debug.LogWarning("[App] Surface nudge failed: " + e.Message);
            }
#endif
        }

        static void ShowAndroidTitle(AndroidJavaObject activity)
        {
            const int ContentId = 16908290;
            using var content = activity.Call<AndroidJavaObject>("findViewById", ContentId);
            if (content == null) return;
            using var label = new AndroidJavaObject("android.widget.TextView", activity);
            label.Call("setText", "MoviePlayer");
            label.Call("setTextSize", 28f);
            label.Call("setTextColor", -1);
            label.Call("setGravity", 17);
            label.Call("setBackgroundColor", unchecked((int)0xFF1F3866));
            using var lp = new AndroidJavaObject("android.widget.FrameLayout$LayoutParams", -1, 140);
            lp.Set("gravity", 48);
            content.Call("addView", label, lp);
        }

        static void ApplySurfaceLayout()
        {
            try
            {
                using var unityPlayer = new AndroidJavaClass("com.unity3d.player.UnityPlayer");
                using var activity = unityPlayer.GetStatic<AndroidJavaObject>("currentActivity");
                using var window = activity.Call<AndroidJavaObject>("getWindow");
                using var decor = window.Call<AndroidJavaObject>("getDecorView");
                int width = decor.Call<int>("getWidth");
                int height = decor.Call<int>("getHeight");
                if (width < 16) width = 1280;
                if (height < 16) height = 800;

                using var resources = activity.Call<AndroidJavaObject>("getResources");
                string packageName = activity.Call<string>("getPackageName");
                int viewId = resources.Call<int>("getIdentifier", "unitySurfaceView", "id", packageName);
                using var surface = viewId != 0 ? activity.Call<AndroidJavaObject>("findViewById", viewId) : null;
                if (surface == null)
                {
                    Debug.LogWarning("[App] unitySurfaceView id=" + viewId + " not in the hierarchy");
                    return;
                }

                string type = surface.Call<AndroidJavaObject>("getClass").Call<string>("getName");
                surface.Call("setVisibility", 0);
                surface.Call("setBackgroundColor", 0);
                ShowAndroidTitle(activity);
                using var holder = surface.Call<AndroidJavaObject>("getHolder");
                holder.Call("setFormat", -1);
                holder.Call("setFixedSize", width, height);
                Debug.Log("[App] SurfaceView " + type + " " + surface.Call<int>("getWidth") + "x" + surface.Call<int>("getHeight"));
            }
            catch (Exception e)
            {
                Debug.LogWarning("[App] Surface layout: " + e.Message);
            }
        }

        // ─── uGUI ────────────────────────────────────────────────────────────

        void BuildUi()
        {
            var canvasGo = new GameObject("PanelCanvas", typeof(RectTransform), typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
            var canvas = canvasGo.GetComponent<Canvas>();
            var cam = Camera.main;
            canvas.renderMode = cam != null ? RenderMode.ScreenSpaceCamera : RenderMode.ScreenSpaceOverlay;
            canvas.worldCamera = cam;
            canvas.planeDistance = 10f;
            canvas.sortingOrder = 0;
            var scaler = canvasGo.GetComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920f, 1080f);
            scaler.matchWidthOrHeight = 0.5f;

            if (FindAnyObjectByType<EventSystem>() == null)
            {
                var es = new GameObject("EventSystem", typeof(EventSystem), typeof(UnityEngine.InputSystem.UI.InputSystemUIInputModule));
                es.transform.SetParent(transform, false);
            }

            var root = canvasGo.GetComponent<RectTransform>();
            Stretch(root);

            var bg = new GameObject("Background", typeof(RectTransform), typeof(Image));
            bg.transform.SetParent(root, false);
            Stretch((RectTransform)bg.transform);
            bg.GetComponent<Image>().color = new Color(0.12f, 0.22f, 0.45f, 1f);
            bg.GetComponent<Image>().raycastTarget = false;

            var top = Band(root, "Top", 0f, 1f, 1f, 1f, new Vector2(0f, -96f), Vector2.zero);
            top.GetComponent<Image>().color = Bar;
            var title = MakeText(top, "MoviePlayer", 36, TextAnchor.MiddleLeft, false);
            title.fontStyle = FontStyle.Bold;
            Place(title.rectTransform, new Vector2(0f, 0f), new Vector2(0f, 1f), new Vector2(24f, 8f), new Vector2(300f, -8f));
            _urlField = MakeField(top, "https://packager…  or a video URL");
            MakeButton(top, "Open", new Vector2(-250f, 0f), new Vector2(220f, 72f), OnOpenClicked, false);
            MakeButton(top, "On this Quest", new Vector2(-16f, 0f), new Vector2(220f, 72f), () => StartCoroutine(ScanLocal()), true);

            var bottom = Band(root, "Bottom", 0f, 0f, 1f, 0f, Vector2.zero, new Vector2(0f, 300f));
            bottom.GetComponent<Image>().color = Bar;

            var modes = Child(bottom, "Modes", new Vector2(0.5f, 1f), new Vector2(0.5f, 1f), new Vector2(0f, -8f), new Vector2(1600f, 64f));
            float x = -750f;
            x = ModeButton(modes, "Mono", ViewMode.Mono, x);
            x = ModeButton(modes, "Left eye", ViewMode.Left, x);
            x = ModeButton(modes, "Right eye", ViewMode.Right, x);
            x = ModeButton(modes, "SBS", ViewMode.Sbs, x);
            x = ModeButton(modes, "OU", ViewMode.Ou, x);
            ModeButton(modes, "Anaglyph", ViewMode.Anaglyph, x);

            _audio = MakeText(bottom, "Language", 26, TextAnchor.MiddleLeft, false);
            Place(_audio.rectTransform, new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(24f, -148f), new Vector2(220f, -84f));
            var langGo = new GameObject("Languages", typeof(RectTransform), typeof(HorizontalLayoutGroup));
            langGo.transform.SetParent(bottom, false);
            _langRoot = (RectTransform)langGo.transform;
            Place(_langRoot, new Vector2(0f, 1f), new Vector2(1f, 1f), new Vector2(230f, -156f), new Vector2(-16f, -84f));
            var langLayout = langGo.GetComponent<HorizontalLayoutGroup>();
            langLayout.spacing = 8f;
            langLayout.childAlignment = TextAnchor.MiddleLeft;
            langLayout.childControlWidth = false;
            langLayout.childControlHeight = true;
            langLayout.childForceExpandWidth = false;
            langLayout.childForceExpandHeight = true;

            var transport = Child(bottom, "Transport", new Vector2(0f, 0f), new Vector2(1f, 0f), new Vector2(0f, 78f), new Vector2(0f, 64f));
            var play = MakeButton(transport, "Play", new Vector2(16f, 0f), new Vector2(140f, 64f), TogglePlay, false);
            play.anchorMin = play.anchorMax = new Vector2(0f, 0.5f);
            play.pivot = new Vector2(0f, 0.5f);
            _playLabel = play.GetComponentInChildren<Text>();
            _seek = MakeSlider(transport);
            _time = MakeText(transport, "0:00 / 0:00", 24, TextAnchor.MiddleRight, false);
            Place(_time.rectTransform, new Vector2(1f, 0f), new Vector2(1f, 1f), new Vector2(-280f, 0f), new Vector2(-16f, 0f));

            var theater = MakeButton(bottom, "VR theater", new Vector2(0f, 16f), new Vector2(420f, 72f), EnterTheater, false);
            theater.anchorMin = theater.anchorMax = new Vector2(0.5f, 0f);
            theater.pivot = new Vector2(0.5f, 0f);

            _status = MakeText(root, "", 24, TextAnchor.MiddleLeft, false);
            Place(_status.rectTransform, new Vector2(0f, 0f), new Vector2(1f, 0f), new Vector2(24f, 300f), new Vector2(-24f, 348f));
            _status.color = new Color(1f, 1f, 1f, 0.7f);

            var videoGo = new GameObject("Video", typeof(RectTransform), typeof(RawImage), typeof(AspectRatioFitter));
            videoGo.transform.SetParent(root, false);
            var vrt = (RectTransform)videoGo.transform;
            vrt.anchorMin = Vector2.zero;
            vrt.anchorMax = Vector2.one;
            vrt.offsetMin = new Vector2(24f, 360f);
            vrt.offsetMax = new Vector2(-24f, -112f);
            _video = videoGo.GetComponent<RawImage>();
            _video.color = Color.white;
            _video.raycastTarget = false;
            _fit = videoGo.GetComponent<AspectRatioFitter>();
            _fit.aspectMode = AspectRatioFitter.AspectMode.FitInParent;
            _fit.aspectRatio = 16f / 9f;

            _listRoot = new GameObject("Library", typeof(RectTransform), typeof(Image));
            _listRoot.transform.SetParent(root, false);
            var lrt = (RectTransform)_listRoot.transform;
            lrt.anchorMin = Vector2.zero;
            lrt.anchorMax = Vector2.one;
            lrt.offsetMin = new Vector2(24f, 360f);
            lrt.offsetMax = new Vector2(-24f, -112f);
            _listRoot.GetComponent<Image>().color = new Color(0.05f, 0.055f, 0.065f, 0.96f);
            BuildScroll(_listRoot.transform);

            ApplyView();
            RefreshAudio();
        }

        void BuildScroll(Transform parent)
        {
            var scrollGo = new GameObject("Scroll", typeof(RectTransform), typeof(Image), typeof(ScrollRect));
            scrollGo.transform.SetParent(parent, false);
            Stretch((RectTransform)scrollGo.transform);
            scrollGo.GetComponent<Image>().color = new Color(0f, 0f, 0f, 0f);
            var scroll = scrollGo.GetComponent<ScrollRect>();
            scroll.horizontal = false;

            var viewport = new GameObject("Viewport", typeof(RectTransform), typeof(Image), typeof(RectMask2D));
            viewport.transform.SetParent(scrollGo.transform, false);
            Stretch((RectTransform)viewport.transform);
            viewport.GetComponent<Image>().color = new Color(0f, 0f, 0f, 0.01f);

            var content = new GameObject("Content", typeof(RectTransform), typeof(VerticalLayoutGroup), typeof(ContentSizeFitter));
            content.transform.SetParent(viewport.transform, false);
            _listContent = (RectTransform)content.transform;
            _listContent.anchorMin = new Vector2(0f, 1f);
            _listContent.anchorMax = new Vector2(1f, 1f);
            _listContent.pivot = new Vector2(0.5f, 1f);
            _listContent.offsetMin = Vector2.zero;
            _listContent.offsetMax = Vector2.zero;
            var layout = content.GetComponent<VerticalLayoutGroup>();
            layout.childControlWidth = true;
            layout.childControlHeight = true;
            layout.childForceExpandWidth = true;
            layout.childForceExpandHeight = false;
            layout.spacing = 8f;
            layout.padding = new RectOffset(12, 12, 12, 12);
            content.GetComponent<ContentSizeFitter>().verticalFit = ContentSizeFitter.FitMode.PreferredSize;
            scroll.viewport = (RectTransform)viewport.transform;
            scroll.content = _listContent;
        }

        float ModeButton(RectTransform parent, string label, ViewMode mode, float x)
        {
            var rt = MakeButton(parent, label, new Vector2(x, 0f), new Vector2(230f, 60f), () => SetMode(mode), true);
            rt.anchorMin = rt.anchorMax = new Vector2(0.5f, 0.5f);
            _modeButtons[mode] = rt.GetComponent<Image>();
            return x + 250f;
        }

        RectTransform MakeButton(Transform parent, string label, Vector2 pos, Vector2 size, Action onClick, bool fromRightOrCenter)
        {
            var go = new GameObject(label, typeof(RectTransform), typeof(Image), typeof(Button));
            go.transform.SetParent(parent, false);
            var rt = (RectTransform)go.transform;
            if (fromRightOrCenter && pos.x < 0f && parent.name == "Top")
            {
                rt.anchorMin = rt.anchorMax = new Vector2(1f, 0.5f);
                rt.pivot = new Vector2(1f, 0.5f);
            }
            else if (parent.name == "Top")
            {
                rt.anchorMin = rt.anchorMax = new Vector2(1f, 0.5f);
                rt.pivot = new Vector2(1f, 0.5f);
            }
            else
            {
                rt.anchorMin = rt.anchorMax = new Vector2(0.5f, 0.5f);
                rt.pivot = new Vector2(0f, 0.5f);
            }
            rt.anchoredPosition = pos;
            rt.sizeDelta = size;
            var image = go.GetComponent<Image>();
            image.color = label == "VR theater" ? Accent : new Color(1f, 1f, 1f, 0.12f);
            go.GetComponent<Button>().onClick.AddListener(() => onClick());
            var text = MakeText(go.transform, label, 26, TextAnchor.MiddleCenter, false);
            text.fontStyle = FontStyle.Bold;
            return rt;
        }

        InputField MakeField(RectTransform parent, string placeholder)
        {
            var go = new GameObject("Url", typeof(RectTransform), typeof(Image), typeof(InputField));
            go.transform.SetParent(parent, false);
            var rt = (RectTransform)go.transform;
            rt.anchorMin = Vector2.zero;
            rt.anchorMax = Vector2.one;
            rt.offsetMin = new Vector2(300f, 16f);
            rt.offsetMax = new Vector2(-490f, -16f);
            go.GetComponent<Image>().color = new Color(1f, 1f, 1f, 0.08f);
            var text = MakeText(go.transform, "", 26, TextAnchor.MiddleLeft, true);
            Place(text.rectTransform, Vector2.zero, Vector2.one, new Vector2(16f, 8f), new Vector2(-16f, -8f));
            var ph = MakeText(go.transform, placeholder, 26, TextAnchor.MiddleLeft, false);
            Place(ph.rectTransform, Vector2.zero, Vector2.one, new Vector2(16f, 8f), new Vector2(-16f, -8f));
            ph.color = new Color(1f, 1f, 1f, 0.35f);
            ph.fontStyle = FontStyle.Italic;
            var field = go.GetComponent<InputField>();
            field.textComponent = text;
            field.placeholder = ph;
            field.lineType = InputField.LineType.SingleLine;
            return field;
        }

        Slider MakeSlider(RectTransform parent)
        {
            var go = new GameObject("Seek", typeof(RectTransform), typeof(Image), typeof(Slider));
            go.transform.SetParent(parent, false);
            var rt = (RectTransform)go.transform;
            rt.anchorMin = Vector2.zero;
            rt.anchorMax = Vector2.one;
            rt.offsetMin = new Vector2(180f, 8f);
            rt.offsetMax = new Vector2(-300f, -8f);
            go.GetComponent<Image>().color = new Color(1f, 1f, 1f, 0.15f);

            var fill = new GameObject("Fill", typeof(RectTransform), typeof(Image));
            fill.transform.SetParent(go.transform, false);
            var fillRt = (RectTransform)fill.transform;
            fillRt.anchorMin = Vector2.zero;
            fillRt.anchorMax = Vector2.one;
            fillRt.offsetMin = Vector2.zero;
            fillRt.offsetMax = Vector2.zero;
            fill.GetComponent<Image>().color = Accent;
            fill.GetComponent<Image>().raycastTarget = false;

            var slider = go.GetComponent<Slider>();
            slider.fillRect = fillRt;
            slider.targetGraphic = go.GetComponent<Image>();
            slider.minValue = 0f;
            slider.maxValue = 1f;
            slider.onValueChanged.AddListener(OnSeek);
            var trigger = go.AddComponent<EventTrigger>();
            AddTrigger(trigger, EventTriggerType.PointerDown, () => _seeking = true);
            AddTrigger(trigger, EventTriggerType.PointerUp, () => _seeking = false);
            return slider;
        }

        static void AddTrigger(EventTrigger trigger, EventTriggerType type, Action action)
        {
            var entry = new EventTrigger.Entry { eventID = type };
            entry.callback.AddListener(_ => action());
            trigger.triggers.Add(entry);
        }

        Text MakeText(Transform parent, string value, int size, TextAnchor anchor, bool raycast)
        {
            var go = new GameObject("Text", typeof(RectTransform));
            go.transform.SetParent(parent, false);
            var text = go.AddComponent<Text>();
            text.font = _font;
            text.text = value;
            text.fontSize = size;
            text.alignment = anchor;
            text.color = Color.white;
            text.raycastTarget = raycast;
            text.horizontalOverflow = HorizontalWrapMode.Wrap;
            text.verticalOverflow = VerticalWrapMode.Truncate;
            Stretch((RectTransform)go.transform);
            return text;
        }

        static RectTransform Band(RectTransform parent, string name, float ax, float ay, float bx, float by, Vector2 offsetMin, Vector2 offsetMax)
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(Image));
            go.transform.SetParent(parent, false);
            var rt = (RectTransform)go.transform;
            rt.anchorMin = new Vector2(ax, ay);
            rt.anchorMax = new Vector2(bx, by);
            rt.offsetMin = offsetMin;
            rt.offsetMax = offsetMax;
            rt.pivot = new Vector2(0.5f, 0.5f);
            return rt;
        }

        static RectTransform Child(RectTransform parent, string name, Vector2 amin, Vector2 amax, Vector2 pos, Vector2 size)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(parent, false);
            var rt = (RectTransform)go.transform;
            rt.anchorMin = amin;
            rt.anchorMax = amax;
            rt.pivot = new Vector2(0.5f, 0.5f);
            rt.anchoredPosition = pos;
            rt.sizeDelta = size;
            return rt;
        }

        static void Stretch(RectTransform rt)
        {
            rt.anchorMin = Vector2.zero;
            rt.anchorMax = Vector2.one;
            rt.offsetMin = Vector2.zero;
            rt.offsetMax = Vector2.zero;
            rt.pivot = new Vector2(0.5f, 0.5f);
        }

        static void Place(RectTransform rt, Vector2 amin, Vector2 amax, Vector2 offsetMin, Vector2 offsetMax)
        {
            rt.anchorMin = amin;
            rt.anchorMax = amax;
            rt.offsetMin = offsetMin;
            rt.offsetMax = offsetMax;
        }
    }
}
