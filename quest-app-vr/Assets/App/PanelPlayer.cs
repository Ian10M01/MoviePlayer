using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem.EnhancedTouch;
using UnityEngine.Networking;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.SceneManagement;
using UnityEngine.InputSystem;
using UnityEngine.UI;
using MoviePlayer.Player;
using MoviePlayer.Proto;
using Layout = MoviePlayer.Player.StereoScreenRenderer.StereoLayout;
using Projection = MoviePlayer.Player.StereoScreenRenderer.ProjectionType;

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
        enum ViewMode { Mono, Left, Right, Sbs, Anaglyph }

        static readonly string[] VideoExtensions = { ".mp4", ".mkv", ".webm", ".mov", ".m4v", ".3gp" };
        static readonly (Layout layout, Projection projection)[] LayoutCycle =
        {
            (Layout.Mono, Projection.Flat),
            (Layout.HSBS, Projection.Flat),
            (Layout.FSBS, Projection.Flat),
            (Layout.HOU, Projection.Flat),
            (Layout.FOU, Projection.Flat),
            (Layout.HSBS, Projection.Equirect180),
            (Layout.Mono, Projection.Equirect180),
            (Layout.Mono, Projection.Equirect360),
        };
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
        Text _hint;
        Text _layoutLabel;
        Text _audio;
        Text _time;
        Text _playLabel;
        Image _swapButton;
        Slider _seek;
        InputField _urlField;
        RectTransform _listContent;
        RectTransform _langRoot;
        GameObject _listRoot;
        ScrollRect _libraryScroll;
        bool _seeking;
        bool _built;
        string _url = "";
        MediaManifest _manifest;
        ViewMode _mode = ViewMode.Mono;
        Layout _layout = Layout.Mono;
        Projection _projection = Projection.Flat;
        bool _eyeSwap;
        bool _userLocked;
        string _appliedKey = "";
        readonly Dictionary<ViewMode, Image> _modeButtons = new Dictionary<ViewMode, Image>();
        InputAction _leftStick;
        InputAction _rightStick;

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
            _player.OnProbed += OnProbed;
            _player.CapTextureToHd = true;
            _leftStick = MakeStick("LeftHand");
            _rightStick = MakeStick("RightHand");
            _font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            if (_font == null)
            {
                var names = Font.GetOSInstalledFontNames();
                if (names != null && names.Length > 0)
                    _font = Font.CreateDynamicFontFromOSFont(names[0], 64);
            }
            Debug.Log("[App] UI font " + (_font != null ? _font.name : "null"));
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
            string testPlay = ModeRouter.IntentExtra("mp_play");
            if (!string.IsNullOrEmpty(testPlay))
            {
                // Test hook: adb shell am start -n com.movieplayer.vrcinema/com.unity3d.player.UnityPlayerActivity --es mp_play /sdcard/Movies/x.mp4
                Debug.Log("[Panel] mp_play extra → " + testPlay);
                PlayUrl(OnDeviceTranscoder.ToFileUrl(testPlay), Path.GetFileName(testPlay));
            }
        }

        void OnEnable()
        {
            Canvas.willRenderCanvases += BeforeRenderUi;
            StartCoroutine(PresentUiToScreen());
            StartCoroutine(PostUiLoop());
        }

        System.Collections.IEnumerator PresentUiToScreen()
        {
            var wait = new WaitForEndOfFrame();
            while (isActiveAndEnabled)
            {
                yield return wait;
                BlitUiToScreen();
            }
        }

        static void BlitUiToScreen()
        {
        }

        IEnumerator PostUiLoop()
        {
            var wait = new WaitForSeconds(0.4f);
            while (isActiveAndEnabled)
            {
                yield return new WaitForEndOfFrame();
                PostUiPreview();
                yield return wait;
            }
        }

        void OnDisable()
        {
            Canvas.willRenderCanvases -= BeforeRenderUi;
        }

        static RenderTexture _uiRt;
        static RTHandle _uiHandle;
        static Camera _uiCam;
        static RawImage _overlay;

        static void BeforeRenderUi()
        {
            foreach (var g in UnityEngine.Object.FindObjectsByType<Graphic>(FindObjectsInactive.Exclude, FindObjectsSortMode.None))
            {
                g.canvasRenderer.cull = false;
                g.canvasRenderer.DisableRectClipping();
            }
        }

        static Camera CreateUiCamera()
        {
            var go = new GameObject("UI Camera");
            var cam = go.AddComponent<Camera>();
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = new Color(0.09f, 0.10f, 0.12f, 1f);
            cam.orthographic = true;
            cam.orthographicSize = 5f;
            cam.nearClipPlane = 0.3f;
            cam.farClipPlane = 30f;
            cam.depth = -10f;
            cam.stereoTargetEye = StereoTargetEyeMask.None;
            cam.allowMSAA = false;
            cam.allowHDR = false;
            cam.cullingMask = -1;
            cam.forceIntoRenderTexture = true;
            var data = go.AddComponent<UniversalAdditionalCameraData>();
            data.renderType = CameraRenderType.Base;
            data.allowXRRendering = false;
            data.renderPostProcessing = false;
            return cam;
        }

        static void EnsureUiTarget()
        {
            if (_uiCam == null) return;
            int w = Screen.width > 16 ? Screen.width : 1280;
            int h = Screen.height > 16 ? Screen.height : 800;
            if (_uiRt != null && (_uiRt.width != w || _uiRt.height != h))
            {
                _uiCam.targetTexture = null;
                _uiRt.Release();
                UnityEngine.Object.Destroy(_uiRt);
                _uiRt = null;
                _uiHandle?.Release();
                _uiHandle = null;
            }
            if (_uiRt == null)
            {
                _uiRt = new RenderTexture(w, h, 24, RenderTextureFormat.ARGB32)
                {
                    name = "PanelUI",
                    useMipMap = false,
                    autoGenerateMips = false,
                    antiAliasing = 1
                };
                _uiRt.Create();
                _uiHandle = RTHandles.Alloc(_uiRt);
                Debug.Log("[App] UI target " + w + "x" + h);
            }
            _uiCam.targetTexture = _uiRt;
            PanelUiFeature.Source = _uiHandle;
            if (_overlay != null)
            {
                _overlay.texture = _uiRt;
                if (_overlay.material != null && _uiRt != null)
                    _overlay.material.SetTexture("_MainTex", _uiRt);
            }
        }

        static void CreateOverlay()
        {
            var go = new GameObject("PanelOverlay");
            var canvas = go.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 100;
            var child = new GameObject("Blit", typeof(RectTransform), typeof(RawImage));
            child.transform.SetParent(go.transform, false);
            var rt = (RectTransform)child.transform;
            rt.anchorMin = Vector2.zero;
            rt.anchorMax = Vector2.one;
            rt.offsetMin = Vector2.zero;
            rt.offsetMax = Vector2.zero;
            _overlay = child.GetComponent<RawImage>();
            _overlay.texture = _uiRt;
            _overlay.color = Color.white;
            _overlay.raycastTarget = false;
            var present = Shader.Find("MoviePlayer/PresentUI");
            if (present != null)
            {
                var mat = new Material(present) { name = "PresentUI" };
                if (_uiRt != null) mat.SetTexture("_MainTex", _uiRt);
                _overlay.material = mat;
            }
            else Debug.LogError("[App] PresentUI shader missing");
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
                _player.OnProbed -= OnProbed;
            }
            if (_leftStick != null) { _leftStick.Disable(); _leftStick.Dispose(); }
            if (_rightStick != null) { _rightStick.Disable(); _rightStick.Dispose(); }
            if (_anaglyph != null) Destroy(_anaglyph);
        }

        static InputAction MakeStick(string hand)
        {
            var action = new InputAction("Stick " + hand, InputActionType.Value,
                "<XRController>{" + hand + "}/{Primary2DAxis}", expectedControlType: "Vector2");
            action.Enable();
            return action;
        }

        void Update()
        {
            // Android back maps to Escape. Only act when the library is hidden,
            // so Escape while browsing the list does nothing.
            if (_built && _listRoot != null && !_listRoot.activeSelf
                && UnityEngine.InputSystem.Keyboard.current != null
                && UnityEngine.InputSystem.Keyboard.current.escapeKey.wasPressedThisFrame)
                BackToLibrary();
            if (!_built || _player == null || _seeking) return;
            long pos = _player.GetPositionMs();
            long dur = _player.GetDurationMs();
            if (_time != null)
                _time.text = Format(pos) + " / " + Format(dur);
            if (_seek != null && dur > 0)
                _seek.SetValueWithoutNotify(Mathf.Clamp01((float)pos / dur));
            if (_playLabel != null)
                _playLabel.text = _player.IsPlaying ? "Pause" : "Play";
            ScrollFromStick();
        }

        void ScrollFromStick()
        {
            if (_libraryScroll == null || _listRoot == null || !_listRoot.activeInHierarchy) return;
            Vector2 left = _leftStick != null ? _leftStick.ReadValue<Vector2>() : Vector2.zero;
            Vector2 right = _rightStick != null ? _rightStick.ReadValue<Vector2>() : Vector2.zero;
            Vector2 stick = left.sqrMagnitude >= right.sqrMagnitude ? left : right;
            if (Mathf.Abs(stick.y) < 0.15f) return;
            _libraryScroll.velocity = new Vector2(0f, -stick.y * 4500f);
        }

        void OnVideoReady()
        {
            if (!_userLocked && _appliedKey == "default" && _player.VideoWidth > 0)
            {
                var aspect = StereoDetect.FromAspect(_player.VideoWidth, _player.VideoHeight);
                if (aspect.Locked) ApplyDetected(aspect, true);
            }
            if (_video != null)
            {
                _video.texture = _player.VideoTexture;
                _video.color = Color.white;
            }
            ApplyView();
            _player.Play();
            SetStatus("");
        }

        void BackToLibrary()
        {
            if (_player != null)
            {
                _player.CancelPrepare();
                _player.Stop();
            }
            _url = "";
            if (_video != null) _video.color = new Color(1f, 1f, 1f, 0f);
            if (_listContent != null && _listContent.childCount > 0)
            {
                ShowList(true);
                SetStatus("Choose a video");
            }
            else
            {
                StartCoroutine(ScanLocal());
            }
        }

        void OnVideoError(string message)
        {
            SetStatus((string.IsNullOrEmpty(message) ? "Playback error" : message)
                + "  —  press Library to pick another movie");
            if (_status != null) _status.color = Warn;
        }

        void PlayUrl(string url, string label, MediaManifest manifest = null)
        {
            if (string.IsNullOrWhiteSpace(url) || _player == null) return;
            _url = url.Trim();
            if (_url.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
                _url = OnDeviceTranscoder.ToFileUrl(_url);
            _manifest = manifest;
            _userLocked = false;
            _appliedKey = "";
            LoadUserLayout();
            if (!_userLocked)
                ApplyDetected(StereoDetect.Resolve(_url, _manifest, null), true);
            _player.PreferAudioTrack(-1);
            _player.Initialize(_url, PlayerPrefs.GetString("authToken", ""));
            if (_video != null) _video.texture = _player.VideoTexture;
            ShowList(false);
            SetStatus("Opening " + (string.IsNullOrEmpty(label) ? _url : label));
            ApplyView();
        }

        void OnProbed(OnDeviceTranscoder.ProbeResult probe)
        {
            if (_userLocked || probe == null) return;
            ApplyDetected(StereoDetect.Resolve(_url, _manifest, probe), true);
        }

        void LoadUserLayout()
        {
            string key = LayoutKey();
            if (!PlayerPrefs.HasKey("stereo.user:" + key)) return;
            _userLocked = true;
            _layout = (Layout)PlayerPrefs.GetInt("stereo.layout:" + key, (int)Layout.Mono);
            _projection = (Projection)PlayerPrefs.GetInt("stereo.proj:" + key, (int)Projection.Flat);
            _eyeSwap = PlayerPrefs.GetInt("stereo.swap:" + key, 0) != 0;
            _mode = (ViewMode)PlayerPrefs.GetInt("stereo.view:" + key, (int)ViewMode.Mono);
            _appliedKey = "user";
            StereoDetect.Log(new StereoGuess
            {
                Layout = _layout,
                Projection = _projection,
                EyeSwap = _eyeSwap,
                Source = "user"
            });
            ApplyView();
        }

        void ApplyDetected(StereoGuess guess, bool resetView)
        {
            string key = guess.Source + ":" + guess.Layout + ":" + guess.Projection + ":" + guess.EyeSwap;
            if (_appliedKey == key) return;
            _layout = guess.Layout;
            _projection = guess.Projection;
            _eyeSwap = guess.EyeSwap;
            _appliedKey = key;
            if (resetView)
            {
                if (guess.Projection != Projection.Flat)
                    _mode = ViewMode.Mono;
                else if (guess.Layout != Layout.Mono)
                    _mode = ViewMode.Anaglyph;
                else
                    _mode = ViewMode.Mono;
            }
            StereoDetect.Log(guess);
            ApplyView();
        }

        string LayoutKey()
        {
            string name;
            try { name = Path.GetFileNameWithoutExtension(Uri.UnescapeDataString(_url ?? "")); }
            catch { name = Path.GetFileNameWithoutExtension(_url ?? ""); }
            return name ?? "";
        }

        void RememberLayout()
        {
            string key = LayoutKey();
            PlayerPrefs.SetInt("stereo.user:" + key, 1);
            PlayerPrefs.SetInt("stereo.layout:" + key, (int)_layout);
            PlayerPrefs.SetInt("stereo.proj:" + key, (int)_projection);
            PlayerPrefs.SetInt("stereo.swap:" + key, _eyeSwap ? 1 : 0);
            PlayerPrefs.SetInt("stereo.view:" + key, (int)_mode);
            PlayerPrefs.Save();
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
                AddListRow(manifest.filename, () => PlayUrl(absolute, manifest.filename, manifest));
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
                AddListRow(Path.GetFileNameWithoutExtension(path), () => PlayUrl(OnDeviceTranscoder.ToFileUrl(path), Path.GetFileName(path)));
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
            PlaybackHandoff.Write(_url, _player.GetPositionMs(), layout, projection, _player.SelectedSourceOrdinal, _eyeSwap);
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

        Layout VrLayout() => _layout;

        Projection VrProjection() => _projection;

        void SetMode(ViewMode mode)
        {
            _mode = mode;
            _userLocked = true;
            RememberLayout();
            ApplyView();
        }

        void StepLayout(int direction)
        {
            int index = 0;
            for (int i = 0; i < LayoutCycle.Length; i++)
            {
                if (LayoutCycle[i].layout == _layout && LayoutCycle[i].projection == _projection)
                {
                    index = i;
                    break;
                }
            }
            index = (index + direction + LayoutCycle.Length) % LayoutCycle.Length;
            _layout = LayoutCycle[index].layout;
            _projection = LayoutCycle[index].projection;
            _userLocked = true;
            _appliedKey = "user";
            if (_projection != Projection.Flat) _mode = ViewMode.Mono;
            else if (_layout != Layout.Mono && _mode == ViewMode.Mono) _mode = ViewMode.Anaglyph;
            RememberLayout();
            StereoDetect.Log(new StereoGuess
            {
                Layout = _layout,
                Projection = _projection,
                EyeSwap = _eyeSwap,
                Source = "user"
            });
            ApplyView();
        }

        void ToggleSwap()
        {
            _eyeSwap = !_eyeSwap;
            _userLocked = true;
            RememberLayout();
            StereoDetect.Log(new StereoGuess
            {
                Layout = _layout,
                Projection = _projection,
                EyeSwap = _eyeSwap,
                Source = "user"
            });
            ApplyView();
        }

        void ApplyView()
        {
            if (_layoutLabel != null) _layoutLabel.text = LayoutCaption();
            if (_hint != null)
            {
                bool vr = _projection != Projection.Flat;
                _hint.text = vr ? "Open VR theater to look around in 180/360" : "";
            }
            if (_swapButton != null)
                _swapButton.color = _eyeSwap ? Accent : new Color(0.22f, 0.25f, 0.32f, 1f);
            if (_video == null) return;
            bool anaglyph = _mode == ViewMode.Anaglyph && _anaglyph != null;
            bool oneEye = _mode == ViewMode.Left || _mode == ViewMode.Right;
            _video.material = anaglyph ? _anaglyph : null;
            _video.uvRect = oneEye
                ? StereoDetect.EyeRect(_layout, _mode == ViewMode.Left, _eyeSwap)
                : new Rect(0f, 0f, 1f, 1f);
            if (anaglyph)
            {
                bool overUnder = _layout == Layout.HOU || _layout == Layout.FOU;
                float pack = _layout == Layout.Mono ? -1f : (overUnder ? 1f : 0f);
                _anaglyph.SetFloat("_Pack", pack);
                _anaglyph.SetFloat("_Swap", _eyeSwap ? 1f : 0f);
            }
            if (_fit != null)
                _fit.aspectRatio = DisplayAspect();
            foreach (var pair in _modeButtons)
                pair.Value.color = pair.Key == _mode ? Accent : new Color(1f, 1f, 1f, 0.12f);
        }

        string LayoutCaption()
        {
            string name = _layout.ToString();
            if (_projection == Projection.Equirect180) return _layout == Layout.Mono ? "180" : "180 " + name;
            if (_projection == Projection.Equirect360) return _layout == Layout.Mono ? "360" : "360 " + name;
            return name;
        }

        float DisplayAspect()
        {
            float aspect = 16f / 9f;
            if (_player != null && _player.VideoWidth > 0 && _player.VideoHeight > 0)
                aspect = (float)_player.VideoWidth / _player.VideoHeight;
            bool oneEye = _mode == ViewMode.Left || _mode == ViewMode.Right || _mode == ViewMode.Anaglyph;
            return Mathf.Max(0.2f, StereoDetect.EyeAspect(aspect, _layout, oneEye));
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
            if (show && _listContent != null)
                LayoutRebuilder.ForceRebuildLayoutImmediate(_listContent);
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
            // Both would send this camera to an XR eye texture. The panel must resolve
            // to the flat window so Screen Space Overlay is composited into that frame.
            cam.stereoTargetEye = StereoTargetEyeMask.None;
            cam.allowMSAA = false;
            cam.allowHDR = false;
            cam.cullingMask = -1;
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = new Color(0.07f, 0.08f, 0.10f, 1f);
            cam.orthographic = true;
            cam.orthographicSize = 5f;
            cam.nearClipPlane = 0.1f;
            cam.farClipPlane = 50f;
            var data = cam.GetUniversalAdditionalCameraData();
            data.renderType = CameraRenderType.Base;
            data.allowXRRendering = false;
            data.renderPostProcessing = false;
        }

        IEnumerator PresentPanel()
        {
            NudgeAndroidSurface();
            yield return null;
            EnsureUiTarget();
            RefreshUiMeshes();
            yield return new WaitForEndOfFrame();
            var cam = Camera.main;
            Debug.Log("[App] Panel frame " + (cam != null ? cam.pixelWidth + "x" + cam.pixelHeight : "none"));
            bool varied = CaptureGameView();
            if (!varied)
            {
                yield return new WaitForSeconds(1f);
                yield return new WaitForEndOfFrame();
                CaptureGameView();
            }
        }

        bool CaptureGameView()
        {
            int w = Screen.width;
            int h = Screen.height;
            if (w < 2 || h < 2)
            {
                Debug.LogWarning("[App] UI frame skipped " + w + "x" + h);
                return false;
            }

            BlitUiToScreen();
            var tex = new Texture2D(w, h, TextureFormat.RGB24, false);
            tex.ReadPixels(new Rect(0, 0, w, h), 0, 0);
            tex.Apply();

            int samples = 0;
            int white = 0;
            int varied = 0;
            float minL = 1f;
            float maxL = 0f;
            var row = new System.Text.StringBuilder();
            const int cols = 16;
            const int rows = 8;
            for (int y = 0; y < rows; y++)
            {
                int py = Mathf.Clamp(Mathf.RoundToInt((rows - 1 - y) * (h - 1) / (float)(rows - 1)), 0, h - 1);
                for (int x = 0; x < cols; x++)
                {
                    int px = Mathf.Clamp(Mathf.RoundToInt(x * (w - 1) / (float)(cols - 1)), 0, w - 1);
                    Color c = tex.GetPixel(px, py);
                    samples++;
                    float lum = c.grayscale;
                    if (lum < minL) minL = lum;
                    if (lum > maxL) maxL = lum;
                    if (c.r > 0.65f && c.g > 0.65f && c.b > 0.65f) white++;
                    if (Mathf.Abs(c.r - 0.12f) > 0.08f || Mathf.Abs(c.g - 0.22f) > 0.08f || Mathf.Abs(c.b - 0.45f) > 0.08f)
                        varied++;
                    row.Append(lum > 0.72f ? '#' : lum > 0.35f ? '+' : lum > 0.14f ? '.' : ' ');
                }
                row.Append('\n');
            }

            string path = Path.Combine(Application.persistentDataPath, "panel-check.png");
            try { File.WriteAllBytes(path, tex.EncodeToPNG()); }
            catch (Exception e) { Debug.LogWarning("[App] UI frame write: " + e.Message); }
            Destroy(tex);
            CaptureUiTarget();

            bool drawn = white > 0 || (maxL - minL) > 0.12f;
            Debug.Log("[App] UI frame " + w + "x" + h
                + " white=" + white + "/" + samples
                + " varied=" + varied
                + " lum=" + minL.ToString("0.00") + "-" + maxL.ToString("0.00")
                + " drawn=" + drawn
                + " path=" + path + "\n" + row);
            return drawn;
        }

        static void CaptureUiTarget()
        {
            if (_uiRt == null || !_uiRt.IsCreated()) return;
            var prev = RenderTexture.active;
            RenderTexture.active = _uiRt;
            var tex = new Texture2D(_uiRt.width, _uiRt.height, TextureFormat.RGB24, false);
            tex.ReadPixels(new Rect(0, 0, _uiRt.width, _uiRt.height), 0, 0);
            tex.Apply();
            RenderTexture.active = prev;
            int white = 0;
            float minL = 1f, maxL = 0f;
            for (int i = 0; i < 64; i++)
            {
                int x = (i % 8) * (_uiRt.width - 1) / 7;
                int y = (i / 8) * (_uiRt.height - 1) / 7;
                Color c = tex.GetPixel(x, y);
                float lum = c.grayscale;
                if (lum < minL) minL = lum;
                if (lum > maxL) maxL = lum;
                if (c.r > 0.65f && c.g > 0.65f && c.b > 0.65f) white++;
            }
            string path = Path.Combine(Application.persistentDataPath, "panel-rt.png");
            try { File.WriteAllBytes(path, tex.EncodeToPNG()); }
            catch (Exception e) { Debug.LogWarning("[App] UI target write: " + e.Message); }
            Destroy(tex);
            Debug.Log("[App] UI target frame white=" + white + "/64 lum=" + minL.ToString("0.00") + "-" + maxL.ToString("0.00"));
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

        static AndroidJavaObject _preview;

        static void ShowAndroidPreview(AndroidJavaObject activity)
        {
            if (_preview != null) return;
            const int ContentId = 16908290;
            var content = activity.Call<AndroidJavaObject>("findViewById", ContentId);
            if (content == null) return;
            _preview = new AndroidJavaObject("android.widget.ImageView", activity);
            using var scaleType = new AndroidJavaClass("android.widget.ImageView$ScaleType");
            using var fit = scaleType.GetStatic<AndroidJavaObject>("FIT_XY");
            _preview.Call("setScaleType", fit);
            try
            {
                _preview.Call("setClickable", false);
                _preview.Call("setFocusable", false);
                _preview.Call("setFocusableInTouchMode", false);
            }
            catch (Exception touch)
            {
                Debug.LogWarning("[App] Preview touch passthrough: " + touch.Message);
            }
            using var lp = new AndroidJavaObject("android.widget.FrameLayout$LayoutParams", -1, -1);
            content.Call("addView", _preview, lp);
        }

        static void PostUiPreview()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            if (_uiRt == null || !_uiRt.IsCreated() || _preview == null) return;
            var prev = RenderTexture.active;
            RenderTexture.active = _uiRt;
            var tex = new Texture2D(_uiRt.width, _uiRt.height, TextureFormat.RGB24, false);
            tex.ReadPixels(new Rect(0, 0, _uiRt.width, _uiRt.height), 0, 0);
            tex.Apply();
            RenderTexture.active = prev;
            var png = tex.EncodeToPNG();
            Destroy(tex);
            try
            {
                using var unityPlayer = new AndroidJavaClass("com.unity3d.player.UnityPlayer");
                using var activity = unityPlayer.GetStatic<AndroidJavaObject>("currentActivity");
                activity.Call("runOnUiThread", new AndroidJavaRunnable(() =>
                {
                    using var factory = new AndroidJavaClass("android.graphics.BitmapFactory");
                    var bmp = factory.CallStatic<AndroidJavaObject>("decodeByteArray", png, 0, png.Length);
                    _preview.Call("setImageBitmap", bmp);
                }));
                Debug.Log("[App] UI posted");
            }
            catch (Exception e)
            {
                Debug.LogWarning("[App] UI post: " + e.Message);
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
                ShowAndroidPreview(activity);
                ShowAndroidTitle(activity);
                try
                {
                    surface.Call("setZOrderMediaOverlay", false);
                    surface.Call("setZOrderOnTop", false);
                }
                catch (Exception z)
                {
                    Debug.LogWarning("[App] Surface Z order: " + z.Message);
                }
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
            // Screen Space Camera is not in URP's overlay pass. The camera color
            // target that gets presented stays the clear color, so the window is empty.
            // Stay inactive until every graphic exists. Enabling the canvas first
            // leaves later Images and Text with empty meshes, so only the background draws.
            var canvasGo = new GameObject("PanelCanvas");
            canvasGo.SetActive(false);
            canvasGo.AddComponent<RectTransform>();
            var canvas = canvasGo.AddComponent<Canvas>();
            // Overlay uGUI on this URP build submits only the first graphic, stretched
            // over the window. Draw the real canvas with its own camera into a texture,
            // then the presenting camera copies that texture onto the backbuffer.
            _uiCam = CreateUiCamera();
            canvas.renderMode = RenderMode.ScreenSpaceCamera;
            canvas.worldCamera = _uiCam;
            canvas.planeDistance = 10f;
            canvas.sortingOrder = 0;
            canvas.pixelPerfect = false;
            EnsureUiTarget();
            var scaler = canvasGo.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920f, 1080f);
            scaler.matchWidthOrHeight = 0.5f;
            canvasGo.AddComponent<GraphicRaycaster>();

            if (FindAnyObjectByType<EventSystem>() == null)
            {
                var es = new GameObject("EventSystem", typeof(EventSystem), typeof(UnityEngine.InputSystem.UI.InputSystemUIInputModule));
                es.transform.SetParent(transform, false);
            }

            var root = canvasGo.GetComponent<RectTransform>();

            var bg = new GameObject("Background", typeof(RectTransform), typeof(Image));
            bg.transform.SetParent(root, false);
            Stretch((RectTransform)bg.transform);
            bg.GetComponent<Image>().color = new Color(0.09f, 0.10f, 0.12f, 1f);
            bg.GetComponent<Image>().raycastTarget = false;
            bg.GetComponent<Image>().raycastTarget = false;

            var top = Band(root, "Top", 0f, 1f, 1f, 1f, new Vector2(0f, -120f), Vector2.zero);
            top.GetComponent<Image>().color = Bar;
            var title = MakeText(top, "MoviePlayer", 64, TextAnchor.MiddleLeft, false);
            title.fontStyle = FontStyle.Bold;
            title.color = Color.white;
            Place(title.rectTransform, new Vector2(0f, 0f), new Vector2(0f, 1f), new Vector2(24f, 8f), new Vector2(560f, -8f));
            _urlField = MakeField(top, "https://packager…  or a video URL");
            MakeButton(top, "Open", new Vector2(-250f, 0f), new Vector2(220f, 72f), OnOpenClicked, false);
            MakeButton(top, "Library", new Vector2(-16f, 0f), new Vector2(220f, 72f), BackToLibrary, true);

            var bottom = Band(root, "Bottom", 0f, 0f, 1f, 0f, Vector2.zero, new Vector2(0f, 430f));
            bottom.GetComponent<Image>().color = Bar;

            var layouts = Child(bottom, "Layouts", new Vector2(0f, 1f), new Vector2(1f, 1f), new Vector2(0f, -52f), new Vector2(0f, 96f));
            var prev = MakeButton(layouts, "Prev layout", new Vector2(16f, 0f), new Vector2(420f, 84f), () => StepLayout(-1), false);
            prev.anchorMin = prev.anchorMax = new Vector2(0f, 0.5f);
            prev.pivot = new Vector2(0f, 0.5f);
            prev.anchoredPosition = new Vector2(16f, 0f);
            var next = MakeButton(layouts, "Next layout", new Vector2(-16f, 0f), new Vector2(420f, 84f), () => StepLayout(1), false);
            next.anchorMin = next.anchorMax = new Vector2(1f, 0.5f);
            next.pivot = new Vector2(1f, 0.5f);
            next.anchoredPosition = new Vector2(-16f, 0f);
            _layoutLabel = MakeText(layouts, "Mono", 32, TextAnchor.MiddleCenter, false);
            _layoutLabel.fontStyle = FontStyle.Bold;
            var swap = MakeButton(layouts, "Swap eyes", new Vector2(0f, 0f), new Vector2(240f, 84f), ToggleSwap, false);
            swap.anchorMin = swap.anchorMax = new Vector2(0.5f, 0.5f);
            swap.pivot = new Vector2(0.5f, 0.5f);
            swap.anchoredPosition = new Vector2(280f, 0f);
            _swapButton = swap.GetComponent<Image>();

            var present = Child(bottom, "Present", new Vector2(0.5f, 1f), new Vector2(0.5f, 1f), new Vector2(0f, -148f), new Vector2(1680f, 76f));
            float x = -800f;
            x = ModeButton(present, "Mono", ViewMode.Mono, x);
            x = ModeButton(present, "Left", ViewMode.Left, x);
            x = ModeButton(present, "Right", ViewMode.Right, x);
            x = ModeButton(present, "SBS", ViewMode.Sbs, x);
            ModeButton(present, "Anaglyph", ViewMode.Anaglyph, x);

            _audio = MakeText(bottom, "Language", 26, TextAnchor.MiddleLeft, false);
            Place(_audio.rectTransform, new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(24f, -268f), new Vector2(220f, -204f));
            var langGo = new GameObject("Languages", typeof(RectTransform), typeof(HorizontalLayoutGroup));
            langGo.transform.SetParent(bottom, false);
            _langRoot = (RectTransform)langGo.transform;
            Place(_langRoot, new Vector2(0f, 1f), new Vector2(1f, 1f), new Vector2(230f, -272f), new Vector2(-16f, -200f));
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
            Place(_status.rectTransform, new Vector2(0f, 0f), new Vector2(1f, 0f), new Vector2(24f, 430f), new Vector2(-24f, 470f));
            _status.color = Color.white;
            _hint = MakeText(root, "", 22, TextAnchor.MiddleRight, false);
            Place(_hint.rectTransform, new Vector2(0.4f, 0f), new Vector2(1f, 0f), new Vector2(0f, 430f), new Vector2(-24f, 470f));
            _hint.color = Warn;

            var videoGo = new GameObject("Video", typeof(RectTransform), typeof(RawImage), typeof(AspectRatioFitter));
            videoGo.transform.SetParent(root, false);
            var vrt = (RectTransform)videoGo.transform;
            vrt.anchorMin = Vector2.zero;
            vrt.anchorMax = Vector2.one;
            vrt.offsetMin = new Vector2(24f, 490f);
            vrt.offsetMax = new Vector2(-24f, -112f);
            _video = videoGo.GetComponent<RawImage>();
            _video.color = new Color(1f, 1f, 1f, 0f);
            _video.raycastTarget = false;
            _fit = videoGo.GetComponent<AspectRatioFitter>();
            _fit.aspectMode = AspectRatioFitter.AspectMode.FitInParent;
            _fit.aspectRatio = 16f / 9f;

            _listRoot = new GameObject("Library", typeof(RectTransform), typeof(Image));
            _listRoot.transform.SetParent(root, false);
            var lrt = (RectTransform)_listRoot.transform;
            lrt.anchorMin = Vector2.zero;
            lrt.anchorMax = Vector2.one;
            lrt.offsetMin = new Vector2(24f, 490f);
            lrt.offsetMax = new Vector2(-24f, -112f);
            _listRoot.GetComponent<Image>().color = new Color(0.05f, 0.055f, 0.065f, 0.96f);
            BuildScroll(_listRoot.transform);

            ApplyView();
            RefreshAudio();
            SetStatus("Choose a video");
            canvasGo.SetActive(true);
            canvas.enabled = true;
            Canvas.ForceUpdateCanvases();
            Debug.Log("[App] UI drawn");
        }

        static void RefreshUiMeshes()
        {
            var canvasGo = GameObject.Find("PanelCanvas");
            if (canvasGo == null) return;
            var sb = new System.Text.StringBuilder();
            foreach (var g in canvasGo.GetComponentsInChildren<Graphic>(true))
            {
                g.SetAllDirty();
                var rt = g.rectTransform;
                if (sb.Length < 900 && (g.gameObject.name is "Background" or "Top" or "Choose video" or "Library" or "Text"))
                {
                    var corners = new Vector3[4];
                    rt.GetWorldCorners(corners);
                    sb.Append(g.gameObject.name).Append(' ')
                      .Append(rt.rect.width.ToString("0")).Append('x').Append(rt.rect.height.ToString("0"))
                      .Append(" cull=").Append(g.canvasRenderer.cull ? "1" : "0")
                      .Append(" y=").Append(corners[0].y.ToString("0")).Append('-').Append(corners[1].y.ToString("0"))
                      .Append(' ');
                }
            }
            Canvas.ForceUpdateCanvases();
            Debug.Log("[App] UI layout " + sb);
        }

        void BuildScroll(Transform parent)
        {
            var scrollGo = new GameObject("Scroll", typeof(RectTransform), typeof(Image), typeof(ScrollRect));
            scrollGo.transform.SetParent(parent, false);
            Stretch((RectTransform)scrollGo.transform);
            scrollGo.GetComponent<Image>().color = new Color(0f, 0f, 0f, 0f);
            var scroll = scrollGo.GetComponent<ScrollRect>();
            _libraryScroll = scroll;
            scroll.horizontal = false;
            scroll.vertical = true;
            scroll.movementType = ScrollRect.MovementType.Clamped;
            scroll.inertia = true;
            scroll.decelerationRate = 0.1f;
            scroll.scrollSensitivity = 80f;

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
            var fitter = content.GetComponent<ContentSizeFitter>();
            fitter.horizontalFit = ContentSizeFitter.FitMode.Unconstrained;
            fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;
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
            image.color = (label == "VR theater" || label == "Library" || label == "Open")
                ? Accent
                : new Color(0.22f, 0.25f, 0.32f, 1f);
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
            rt.offsetMin = new Vector2(580f, 16f);
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
