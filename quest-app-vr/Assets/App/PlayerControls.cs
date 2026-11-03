// PlayerControls.cs — playback bar and screen/picture settings, built at runtime on the UI canvas.
//
//  Bar:      Library | -10s | Play/Pause | +10s | 0:12:34 ────●──── 1:58:00 | Settings
//  Settings: 3D mode (2D, SBS, Full SBS, OU, Full OU, 180°, 360°), swap eyes, environment,
//            screen size / distance / height / curve, picture (Original, Sharpen, Upscale),
//            sharpness, brightness, volume, recenter.
//
// Screen and picture settings persist between sessions; the 3D mode is remembered per movie.

using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;
using TMPro;
using MoviePlayer.Player;
using MoviePlayer.Theater;
using Layout = MoviePlayer.Player.StereoScreenRenderer.StereoLayout;
using Projection = MoviePlayer.Player.StereoScreenRenderer.ProjectionType;

namespace MoviePlayer.App
{
    public class PlayerControls : MonoBehaviour
    {
        // ── Wiring (set by AppController) ─────────────────────────────────────
        public HlsPlayerController  Player;
        public StereoScreenRenderer Screen;
        public ClarityStack         Clarity;
        public TheaterEnvironment   Theater;
        public ViewRecenter         Recenter;
        public Action               OnLibrary;
        public Sprite               RoundedSprite;

        /// <summary>Raised when the viewer plays, pauses or seeks (playing, positionMs), for room sync.</summary>
        public event Action<bool, long> LocalPlaybackChanged;
        /// <summary>Raised by the Friends button on the bar.</summary>
        public event Action FriendsClicked;

        // ── Look ──────────────────────────────────────────────────────────────
        static readonly Color PanelColor  = new Color(0.063f, 0.075f, 0.094f, 0.92f);
        static readonly Color ButtonColor = new Color(1f, 1f, 1f, 0.08f);
        static readonly Color Accent      = new Color(0.298f, 0.553f, 1f, 1f);   // #4C8DFF
        static readonly Color TrackColor  = new Color(1f, 1f, 1f, 0.18f);
        static readonly Color Dim         = new Color(1f, 1f, 1f, 0.55f);

        const float AutoHideSeconds = 6f;

        // ── State ─────────────────────────────────────────────────────────────
        private GameObject _bar, _settings;
        private TMP_Text   _playPauseLabel, _timeLabel, _durationLabel;
        private Image      _seekFill;
        private bool       _paused, _visible;
        private float      _hideAt;
        private string     _movieKey = "";

        private readonly Dictionary<string, List<(Image img, Func<bool> active)>> _groups =
            new Dictionary<string, List<(Image, Func<bool>)>>();
        private readonly List<(TMP_Text label, Func<string> text)> _values = new List<(TMP_Text, Func<string>)>();
        private readonly List<(GameObject row, Func<bool> enabled)> _conditionalRows = new List<(GameObject, Func<bool>)>();

        // Persisted settings
        private bool  _theaterEnv = true;
        private float _volume = 1f;

        // ─────────────────────────────────────────────────────────────────────
        #region Public API

        /// <summary>Builds the UI under the given canvas and restores saved settings.</summary>
        public void Build(Transform canvas)
        {
            LoadSettings();
            BuildBar(canvas);
            BuildSettings(canvas);
            _bar.SetActive(false);
            _settings.SetActive(false);
            GazeClick.TriggerPressed += OnAnyTrigger;
            ApplyEnvironment();
        }

        /// <summary>Shows the controls for a newly started movie and applies its 3D mode.</summary>
        public void BeginMovie(string path, Layout guessedLayout, Projection guessedProjection)
        {
            _movieKey = string.IsNullOrEmpty(path) ? "" : System.IO.Path.GetFileName(path);
            var layout = (Layout)PlayerPrefs.GetInt("layout:" + _movieKey, (int)guessedLayout);
            var proj   = (Projection)PlayerPrefs.GetInt("proj:" + _movieKey, (int)guessedProjection);
            bool swap  = PlayerPrefs.GetInt("swap:" + _movieKey, 0) == 1;
            Screen.Apply(layout, proj, swap);
            ApplyEnvironment();
            Player?.SetVolume(_volume);

            _paused = false;
            if (_playPauseLabel != null) _playPauseLabel.text = "Pause";
            _settings.SetActive(false);
            SetVisible(true);
            RefreshAll();
        }

        public void EndMovie()
        {
            _settings.SetActive(false);
            SetVisible(false);
        }

        #endregion

        // ─────────────────────────────────────────────────────────────────────
        #region Lifecycle

        private void OnDestroy() => GazeClick.TriggerPressed -= OnAnyTrigger;

        private void Update()
        {
            if (!_visible || Player == null) return;

            long pos = Player.GetPositionMs(), dur = Player.GetDurationMs();
            if (_timeLabel != null)     _timeLabel.text = FormatTime(pos);
            if (_durationLabel != null) _durationLabel.text = FormatTime(dur);
            if (_seekFill != null)      _seekFill.fillAmount = dur > 0 ? Mathf.Clamp01((float)pos / dur) : 0f;

            // Auto-hide while the movie plays and nobody is using the controls.
            bool pointing = GazeClick.HoveredObject != null &&
                            (GazeClick.HoveredObject.transform.IsChildOf(_bar.transform) ||
                             GazeClick.HoveredObject.transform.IsChildOf(_settings.transform));
            if (pointing || _paused || _settings.activeSelf) _hideAt = Time.unscaledTime + AutoHideSeconds;
            else if (Time.unscaledTime > _hideAt) SetVisible(false, keepActive: true);
        }

        private bool _hiddenWhilePlaying;

        private void OnAnyTrigger()
        {
            if (_hiddenWhilePlaying) SetVisible(true);
        }

        private void SetVisible(bool show, bool keepActive = false)
        {
            _visible = show;
            _hiddenWhilePlaying = keepActive && !show;
            _bar.SetActive(show);
            if (!show) _settings.SetActive(false);
            _hideAt = Time.unscaledTime + AutoHideSeconds;
        }

        #endregion

        // ─────────────────────────────────────────────────────────────────────
        #region Bar

        private void BuildBar(Transform canvas)
        {
            _bar = MakePanel(canvas, "PlaybackBar", new Vector2(1440f, 112f), new Vector2(0f, -400f));
            var hlg = _bar.AddComponent<HorizontalLayoutGroup>();
            hlg.padding = new RectOffset(18, 18, 16, 16);
            hlg.spacing = 12f;
            hlg.childAlignment = TextAnchor.MiddleCenter;
            hlg.childControlWidth = hlg.childControlHeight = true;
            hlg.childForceExpandWidth = false;
            hlg.childForceExpandHeight = true;

            MakeButton(_bar.transform, "Library", ButtonColor, () => OnLibrary?.Invoke(), 150f);
            MakeButton(_bar.transform, "-10s", ButtonColor, () => SeekRelative(-10_000), 100f);
            _playPauseLabel = MakeButton(_bar.transform, "Pause", Accent, TogglePause, 130f);
            MakeButton(_bar.transform, "+10s", ButtonColor, () => SeekRelative(10_000), 100f);

            _timeLabel = MakeLabel(_bar.transform, "0:00", 24, 110f, TextAlignmentOptions.Right);
            BuildSeekBar(_bar.transform);
            _durationLabel = MakeLabel(_bar.transform, "0:00", 24, 110f, TextAlignmentOptions.Left);

            MakeButton(_bar.transform, "Friends", ButtonColor, () => FriendsClicked?.Invoke(), 140f);
            MakeButton(_bar.transform, "Settings", ButtonColor, ToggleSettings, 140f);
        }

        private void BuildSeekBar(Transform parent)
        {
            var track = new GameObject("SeekBar", typeof(RectTransform), typeof(Image), typeof(LayoutElement), typeof(SeekBarInput));
            track.transform.SetParent(parent, false);
            var le = track.GetComponent<LayoutElement>();
            le.flexibleWidth = 1f;
            le.minWidth = 300f;

            // The track itself is a tall invisible hit area with a thin visible line inside.
            track.GetComponent<Image>().color = new Color(0, 0, 0, 0.001f);
            track.GetComponent<SeekBarInput>().OnSeek = fraction =>
            {
                long dur = Player != null ? Player.GetDurationMs() : 0;
                if (dur <= 0) return;
                long target = (long)(fraction * dur);
                Player.SeekTo(target);
                LocalPlaybackChanged?.Invoke(!_paused, target);
            };

            var line = new GameObject("Track", typeof(RectTransform), typeof(Image));
            line.transform.SetParent(track.transform, false);
            var lrt = (RectTransform)line.transform;
            lrt.anchorMin = new Vector2(0f, 0.5f); lrt.anchorMax = new Vector2(1f, 0.5f);
            lrt.sizeDelta = new Vector2(0f, 10f);
            var lineImg = line.GetComponent<Image>();
            lineImg.color = TrackColor;
            lineImg.raycastTarget = false;
            if (RoundedSprite != null) { lineImg.sprite = RoundedSprite; lineImg.type = Image.Type.Sliced; }

            var fill = new GameObject("Fill", typeof(RectTransform), typeof(Image));
            fill.transform.SetParent(line.transform, false);
            var frt = (RectTransform)fill.transform;
            frt.anchorMin = Vector2.zero; frt.anchorMax = Vector2.one;
            frt.offsetMin = frt.offsetMax = Vector2.zero;
            _seekFill = fill.GetComponent<Image>();
            _seekFill.color = Accent;
            _seekFill.raycastTarget = false;
            _seekFill.type = Image.Type.Filled;
            _seekFill.fillMethod = Image.FillMethod.Horizontal;
            _seekFill.fillAmount = 0f;
        }

        private void TogglePause()
        {
            if (Player == null) return;
            _paused = !_paused;
            if (_paused) Player.Pause(); else Player.Play();
            _playPauseLabel.text = _paused ? "Play" : "Pause";
            LocalPlaybackChanged?.Invoke(!_paused, Player.GetPositionMs());
        }

        /// <summary>Applies a play/pause/seek that came from a friend, without echoing it back.</summary>
        public void ApplyRemotePlayback(bool playing, long positionMs)
        {
            if (Player == null) return;
            if (Math.Abs(Player.GetPositionMs() - positionMs) > 1500) Player.SeekTo(positionMs);
            _paused = !playing;
            if (playing) Player.Play(); else Player.Pause();
            if (_playPauseLabel != null) _playPauseLabel.text = _paused ? "Play" : "Pause";
        }

        private void SeekRelative(long deltaMs)
        {
            if (Player == null) return;
            long dur = Player.GetDurationMs();
            long target = Math.Max(0L, Player.GetPositionMs() + deltaMs);
            if (dur > 0) target = Math.Min(target, dur - 1000);
            Player.SeekTo(target);
            LocalPlaybackChanged?.Invoke(!_paused, target);
        }

        private void ToggleSettings()
        {
            _settings.SetActive(!_settings.activeSelf);
            RefreshAll();
        }

        #endregion

        // ─────────────────────────────────────────────────────────────────────
        #region Settings panel

        private void BuildSettings(Transform canvas)
        {
            _settings = MakePanel(canvas, "ScreenSettings", new Vector2(1440f, 880f), new Vector2(0f, 100f));
            var vlg = _settings.AddComponent<VerticalLayoutGroup>();
            vlg.padding = new RectOffset(28, 28, 24, 24);
            vlg.spacing = 10f;
            vlg.childControlWidth = vlg.childControlHeight = true;
            vlg.childForceExpandWidth = true;
            vlg.childForceExpandHeight = false;

            var t = _settings.transform;

            Row(t, "3D mode", r =>
            {
                ModeOption(r, "2D",       Layout.Mono, Projection.Flat);
                ModeOption(r, "3D SBS",   Layout.HSBS, Projection.Flat);
                ModeOption(r, "Full SBS", Layout.FSBS, Projection.Flat);
                ModeOption(r, "3D OU",    Layout.HOU,  Projection.Flat);
                ModeOption(r, "Full OU",  Layout.FOU,  Projection.Flat);
                ProjectionOption(r, "180°", Projection.Equirect180);
                ProjectionOption(r, "360°", Projection.Equirect360);
            });

            Row(t, "Eyes", r =>
            {
                Option(r, "eyes", "Normal",  () => !Screen.EyeSwap, () => SetSwap(false));
                Option(r, "eyes", "Swapped", () => Screen.EyeSwap,  () => SetSwap(true));
            });

            Row(t, "Environment", r =>
            {
                Option(r, "env", "Theater", () => _theaterEnv,  () => { _theaterEnv = true;  ApplyEnvironment(); Save(); });
                Option(r, "env", "Void",    () => !_theaterEnv, () => { _theaterEnv = false; ApplyEnvironment(); Save(); });
            });

            Row(t, "Screen size", r => Stepper(r,
                () => $"{Screen.EffectiveWidth:0.0} m wide",
                () => ChangeScreen(w: Screen.ScreenWidth / 1.1f),
                () => ChangeScreen(w: Screen.ScreenWidth * 1.1f)));

            _conditionalRows.Add((Row(t, "Distance", r => Stepper(r,
                () => $"{Screen.ScreenDistance:0.0} m",
                () => ChangeScreen(d: Screen.ScreenDistance / 1.1f),
                () => ChangeScreen(d: Screen.ScreenDistance * 1.1f))), () => !InTheater));

            _conditionalRows.Add((Row(t, "Height", r => Stepper(r,
                () => $"{Screen.HeightOffset:+0.0;-0.0;0.0} m",
                () => ChangeScreen(h: Screen.HeightOffset - 0.25f),
                () => ChangeScreen(h: Screen.HeightOffset + 0.25f))), () => !InTheater));

            _conditionalRows.Add((Row(t, "Curve", r =>
            {
                Option(r, "curve", "Flat",   () => Screen.Curvature < 0.1f, () => ChangeScreen(c: 0f));
                Option(r, "curve", "Slight", () => Screen.Curvature >= 0.1f && Screen.Curvature < 0.6f, () => ChangeScreen(c: 0.35f));
                Option(r, "curve", "Curved", () => Screen.Curvature >= 0.6f, () => ChangeScreen(c: 1f));
            }), () => !InTheater));

            Row(t, "Picture", r =>
            {
                Option(r, "pic", "Original", () => Clarity.Mode == ClarityStack.EnhanceMode.Off,     () => SetEnhance(ClarityStack.EnhanceMode.Off));
                Option(r, "pic", "Sharpen",  () => Clarity.Mode == ClarityStack.EnhanceMode.Sharpen, () => SetEnhance(ClarityStack.EnhanceMode.Sharpen));
                Option(r, "pic", "Upscale",  () => Clarity.Mode == ClarityStack.EnhanceMode.Upscale, () => SetEnhance(ClarityStack.EnhanceMode.Upscale));
            });

            Row(t, "Sharpness", r => Stepper(r,
                () => $"{Mathf.RoundToInt(Clarity.SharpenStrength * 100)}%",
                () => { Clarity.SharpenStrength = Mathf.Clamp01(Clarity.SharpenStrength - 0.1f); Save(); },
                () => { Clarity.SharpenStrength = Mathf.Clamp01(Clarity.SharpenStrength + 0.1f); Save(); }));

            Row(t, "Brightness", r => Stepper(r,
                () => $"{Mathf.RoundToInt(Clarity.Brightness * 100)}%",
                () => { Clarity.Brightness = Mathf.Clamp(Clarity.Brightness - 0.05f, 0.5f, 1.5f); Save(); },
                () => { Clarity.Brightness = Mathf.Clamp(Clarity.Brightness + 0.05f, 0.5f, 1.5f); Save(); }));

            Row(t, "Volume", r => Stepper(r,
                () => $"{Mathf.RoundToInt(_volume * 100)}%",
                () => SetVolume(_volume - 0.1f),
                () => SetVolume(_volume + 0.1f)));

            Row(t, "", r =>
            {
                MakeButton(r, "Recenter view", ButtonColor, () => Recenter?.Recenter(), 260f);
                MakeButton(r, "Close", Accent, ToggleSettings, 200f);
            });
        }

        private bool InTheater => _theaterEnv && Screen.Projection == Projection.Flat;

        private void ModeOption(Transform row, string text, Layout layout, Projection proj) =>
            Option(row, "mode", text,
                () => Screen.Projection == proj && Screen.Layout == layout,
                () => SetMode(layout, proj));

        private void ProjectionOption(Transform row, string text, Projection proj) =>
            Option(row, "mode", text,
                () => Screen.Projection == proj,
                // 180/360 videos are nearly always side-by-side 3D (180) or mono (360); keep the
                // current stereo layout if one is already set, else pick the common default.
                () => SetMode(Screen.Layout != Layout.Mono ? Screen.Layout
                              : proj == Projection.Equirect180 ? Layout.FSBS : Layout.Mono, proj));

        private void SetMode(Layout layout, Projection proj)
        {
            Screen.Apply(layout, proj, Screen.EyeSwap);
            ApplyEnvironment();
            PlayerPrefs.SetInt("layout:" + _movieKey, (int)layout);
            PlayerPrefs.SetInt("proj:" + _movieKey, (int)proj);
            PlayerPrefs.Save();
            RefreshAll();
        }

        private void SetSwap(bool swap)
        {
            Screen.Apply(Screen.Layout, Screen.Projection, swap);
            PlayerPrefs.SetInt("swap:" + _movieKey, swap ? 1 : 0);
            PlayerPrefs.Save();
            RefreshAll();
        }

        private void SetEnhance(ClarityStack.EnhanceMode mode)
        {
            Clarity.Mode = mode;
            Save();
        }

        private void SetVolume(float v)
        {
            _volume = Mathf.Clamp01(v);
            Player?.SetVolume(_volume);
            Save();
        }

        private void ChangeScreen(float? w = null, float? d = null, float? h = null, float? c = null)
        {
            if (w.HasValue)
            {
                // In the theater the size is capped by the screen wall; don't let the stored
                // width run away past what can be shown.
                float max = InTheater ? TheaterEnvironment.MaxScreenSize.x : 60f;
                Screen.ScreenWidth = Mathf.Clamp(w.Value, 1f, max);
            }
            if (d.HasValue) Screen.ScreenDistance = Mathf.Clamp(d.Value, 1f, 40f);
            if (h.HasValue) Screen.HeightOffset   = Mathf.Clamp(h.Value, -5f, 5f);
            if (c.HasValue) Screen.Curvature      = c.Value;
            Screen.Rebuild();
            Save();
            RefreshAll();
        }

        /// <summary>Theater for flat movies when chosen; 180/360 always surround the viewer in the void.</summary>
        private void ApplyEnvironment()
        {
            if (Screen == null) return;
            bool theater = InTheater;
            if (Theater != null) Theater.TheaterMode = theater;
            Screen.SetEnvironmentConstraint(theater, TheaterEnvironment.ScreenPlaneZ,
                                            TheaterEnvironment.StageTopY, TheaterEnvironment.MaxScreenSize);
            RefreshAll();
        }

        #endregion

        // ─────────────────────────────────────────────────────────────────────
        #region Persistence

        private void LoadSettings()
        {
            _theaterEnv = PlayerPrefs.GetInt("env.theater", 1) == 1;
            _volume     = PlayerPrefs.GetFloat("audio.volume", 1f);
            if (Screen != null)
            {
                Screen.ScreenWidth    = PlayerPrefs.GetFloat("screen.width", 14f);
                Screen.ScreenDistance = PlayerPrefs.GetFloat("screen.distance", 10f);
                Screen.HeightOffset   = PlayerPrefs.GetFloat("screen.height", 0f);
                Screen.Curvature      = PlayerPrefs.GetFloat("screen.curve", 0f);
            }
            if (Clarity != null)
            {
                Clarity.Mode            = (ClarityStack.EnhanceMode)PlayerPrefs.GetInt("pic.mode", (int)ClarityStack.EnhanceMode.Upscale);
                Clarity.SharpenStrength = PlayerPrefs.GetFloat("pic.sharpness", 0.6f);
                Clarity.Brightness      = PlayerPrefs.GetFloat("pic.brightness", 1f);
            }
        }

        private void Save()
        {
            PlayerPrefs.SetInt("env.theater", _theaterEnv ? 1 : 0);
            PlayerPrefs.SetFloat("audio.volume", _volume);
            PlayerPrefs.SetFloat("screen.width", Screen.ScreenWidth);
            PlayerPrefs.SetFloat("screen.distance", Screen.ScreenDistance);
            PlayerPrefs.SetFloat("screen.height", Screen.HeightOffset);
            PlayerPrefs.SetFloat("screen.curve", Screen.Curvature);
            PlayerPrefs.SetInt("pic.mode", (int)Clarity.Mode);
            PlayerPrefs.SetFloat("pic.sharpness", Clarity.SharpenStrength);
            PlayerPrefs.SetFloat("pic.brightness", Clarity.Brightness);
            PlayerPrefs.Save();
            RefreshAll();
        }

        #endregion

        // ─────────────────────────────────────────────────────────────────────
        #region UI helpers

        private void RefreshAll()
        {
            foreach (var group in _groups.Values)
                foreach (var (img, active) in group)
                    img.color = active() ? Accent : ButtonColor;
            foreach (var (label, text) in _values) label.text = text();
            foreach (var (row, enabled) in _conditionalRows) row.SetActive(enabled());
        }

        private GameObject Row(Transform parent, string title, Action<Transform> fill)
        {
            var row = new GameObject(title + "Row", typeof(RectTransform), typeof(HorizontalLayoutGroup), typeof(LayoutElement));
            row.transform.SetParent(parent, false);
            row.GetComponent<LayoutElement>().preferredHeight = 58f;
            var hlg = row.GetComponent<HorizontalLayoutGroup>();
            hlg.spacing = 10f;
            hlg.childAlignment = TextAnchor.MiddleLeft;
            hlg.childControlWidth = hlg.childControlHeight = true;
            hlg.childForceExpandWidth = false;
            hlg.childForceExpandHeight = true;

            MakeLabel(row.transform, title, 26, 220f, TextAlignmentOptions.MidlineLeft).color = Dim;
            fill(row.transform);
            return row;
        }

        private void Option(Transform row, string group, string text, Func<bool> active, Action onClick)
        {
            var label = MakeButton(row, text, ButtonColor, () => { onClick(); RefreshAll(); }, 150f);
            var img = label.transform.parent.GetComponent<Image>();
            if (!_groups.TryGetValue(group, out var list)) _groups[group] = list = new List<(Image, Func<bool>)>();
            list.Add((img, active));
        }

        private void Stepper(Transform row, Func<string> value, Action minus, Action plus)
        {
            MakeButton(row, "-", ButtonColor, () => { minus(); RefreshAll(); }, 70f);
            var label = MakeLabel(row, value(), 26, 220f, TextAlignmentOptions.Center);
            _values.Add((label, value));
            MakeButton(row, "+", ButtonColor, () => { plus(); RefreshAll(); }, 70f);
        }

        internal GameObject MakePanel(Transform parent, string name, Vector2 size, Vector2 pos)
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(Image));
            go.transform.SetParent(parent, false);
            var rt = (RectTransform)go.transform;
            rt.anchorMin = rt.anchorMax = rt.pivot = new Vector2(0.5f, 0.5f);
            rt.sizeDelta = size;
            rt.anchoredPosition = pos;
            var img = go.GetComponent<Image>();
            img.color = PanelColor;
            if (RoundedSprite != null) { img.sprite = RoundedSprite; img.type = Image.Type.Sliced; }
            return go;
        }

        internal TMP_Text MakeButton(Transform parent, string text, Color bg, Action onClick, float width)
        {
            var go = new GameObject(text + "Button", typeof(RectTransform), typeof(Image), typeof(Button), typeof(LayoutElement));
            go.transform.SetParent(parent, false);
            go.GetComponent<LayoutElement>().preferredWidth = width;
            var img = go.GetComponent<Image>();
            img.color = bg;
            if (RoundedSprite != null) { img.sprite = RoundedSprite; img.type = Image.Type.Sliced; }

            var btn = go.GetComponent<Button>();
            var colors = btn.colors;
            colors.highlightedColor = new Color(1.35f, 1.35f, 1.35f, 1f);
            colors.pressedColor     = new Color(0.7f, 0.7f, 0.7f, 1f);
            colors.fadeDuration     = 0.08f;
            btn.colors = colors;
            btn.onClick.AddListener(() => onClick());

            var label = MakeLabel(go.transform, text, 24, 0f, TextAlignmentOptions.Center);
            label.fontStyle = FontStyles.Bold;
            var lrt = (RectTransform)label.transform;
            lrt.anchorMin = Vector2.zero; lrt.anchorMax = Vector2.one;
            lrt.offsetMin = lrt.offsetMax = Vector2.zero;
            return label;
        }

        internal static TMP_Text MakeLabel(Transform parent, string text, float size, float width, TextAlignmentOptions align)
        {
            var go = new GameObject("Label", typeof(RectTransform));
            go.transform.SetParent(parent, false);
            if (width > 0f) go.AddComponent<LayoutElement>().preferredWidth = width;
            var label = go.AddComponent<TextMeshProUGUI>();
            label.text = text;
            label.fontSize = size;
            label.alignment = align;
            label.color = Color.white;
            label.raycastTarget = false;
            label.textWrappingMode = TextWrappingModes.NoWrap;
            return label;
        }

        private static string FormatTime(long ms)
        {
            var t = TimeSpan.FromMilliseconds(Math.Max(0, ms));
            return t.TotalHours >= 1 ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}" : $"{t.Minutes}:{t.Seconds:00}";
        }

        #endregion
    }

    /// <summary>Click anywhere on the seek bar to jump there.</summary>
    public class SeekBarInput : MonoBehaviour, IPointerClickHandler
    {
        public Action<float> OnSeek;

        public void OnPointerClick(PointerEventData eventData)
        {
            var rt = (RectTransform)transform;
            Vector3 local = rt.InverseTransformPoint(eventData.pointerCurrentRaycast.worldPosition);
            float fraction = Mathf.InverseLerp(rt.rect.xMin, rt.rect.xMax, local.x);
            OnSeek?.Invoke(fraction);
        }
    }
}
