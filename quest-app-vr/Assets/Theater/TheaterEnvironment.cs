using UnityEngine;

namespace MoviePlayer.Theater
{
    /// <summary>
    /// Procedural movie theater built at runtime from Unity primitives (no art
    /// assets required). The theater can be hidden to show a plain void.
    ///
    /// The viewer's head sits at this transform's origin, facing +Z. The screen
    /// is at ScreenDistance metres in front (eye height). The floor is
    /// about 1.2 m below the origin.
    ///
    /// Layout (top view, Z+ toward screen)
    /// ────────────────────────────────────
    ///    Z+   ████████ SCREEN (z = ScreenDistance) ████████
    ///                         front wall (z = 9)
    ///     ▲     [row 4]  stadium rows rise toward the back
    ///     │     [row 3]
    ///     │     [row 2]
    ///     │     [row 1]
    ///     │     [row 0]  ← seat 0 = viewer (centre seat, head at origin)
    ///    origin
    ///
    /// Seat anchors are placed at the seated head height of each seat, so
    /// GetSeatPosition(i) returns where a seated person's head would be.
    ///
    /// The implementation is split across partial files:
    ///   TheaterEnvironment.cs            public API, lifecycle, visibility
    ///   TheaterEnvironment.Construction.cs  room, seats, risers and lights
    ///   TheaterEnvironment.Avatars.cs      friend avatars and RoomClient wiring
    /// </summary>
    public partial class TheaterEnvironment : MonoBehaviour
    {
        // ── Inspector ─────────────────────────────────────────────────────────
        [Header("Screen")]
        [Tooltip("Distance from the viewer to the screen plane (z).")]
        public float ScreenDistance = 8f;
        public float ScreenWidth    = 12f;
        public float ScreenHeight   = 6.75f;  // 16:9 ratio with ScreenWidth

        [Header("Hall (legacy, unused by the procedural theater)")]
        [Tooltip("Kept for serialized scene compatibility. The procedural theater uses fixed layout constants.")]
        public float HallWidth  = 14f;
        [Tooltip("Kept for serialized scene compatibility. The procedural theater uses fixed layout constants.")]
        public float HallHeight = 5f;
        [Tooltip("Kept for serialized scene compatibility. The procedural theater uses fixed layout constants.")]
        public float HallDepth  = 12f;
        [Tooltip("Optional override for the wall material. A dark acoustic-panel material is generated when empty.")]
        public Material WallMaterial;
        [Tooltip("Optional override for the carpet material.")]
        public Material FloorMaterial;
        [Tooltip("Optional override for the ceiling material.")]
        public Material CeilingMaterial;

        [Header("Seats (legacy, unused by the procedural theater)")]
        [Tooltip("Kept for serialized scene compatibility. Seat layout is generated from fixed constants.")]
        public float SeatSpacing   = 1.6f;
        [Tooltip("Kept for serialized scene compatibility. Seat layout is generated from fixed constants.")]
        public float SeatRowOffset = -3f;
        [Tooltip("Kept for serialized scene compatibility. Seat layout is generated from fixed constants.")]
        public float SeatHeight    = 0f;

        [Header("Ambient Audio")]
        public AudioClip CrowdHumClip;
        [Range(0f, 1f)]
        public float AmbientVolume = 0.08f;

        [Header("Mode")]
        public bool PassthroughFlatScreen = false;

        [Header("Friends")]
        [Tooltip("Server seat_index s is shown in theater seat s + this offset. Seat 0 is always the viewer.")]
        public int ServerSeatIndexOffset = 1;

        // ── Screen coordinate contract (local space; viewer head at origin, facing +Z) ──
        /// <summary>The movie screen is drawn by other code on the plane z = ScreenPlaneZ.</summary>
        public const float ScreenPlaneZ = 12f;
        /// <summary>The screen's bottom edge never goes lower than this y.</summary>
        public const float StageTopY = -0.6f;
        /// <summary>Largest screen width/height in metres.</summary>
        public static readonly Vector2 MaxScreenSize = new Vector2(16f, 9f);

        // ── Screen glow ───────────────────────────────────────────────────────
        private Light _glowLight;
        /// <summary>Scales the baked screen light (baked for a white screen of radiance 1).</summary>
        private const float ScreenGlowGain = 1.2f;
        private Color _glowTarget = new Color(1f, 0.92f, 0.82f);
        private float _glowTargetBrightness = 0.25f;
        private Color _glowColor = Color.black;
        private float _glowBrightness;
        private bool  _ambientSaved;
        private Color _savedAmbient;
        private UnityEngine.Rendering.AmbientMode _savedAmbientMode;

        /// <summary>
        /// Lights the room from the screen. Call every frame or so with the movie's
        /// average colour and brightness (0..1); the result is smoothed.
        /// </summary>
        public void SetScreenGlow(Color averageColor, float brightness)
        {
            float max = Mathf.Max(averageColor.r, Mathf.Max(averageColor.g, averageColor.b));
            Color hue = max > 0.001f ? averageColor / max : Color.white;
            hue.a = 1f;
            _glowTarget = Color.Lerp(Color.white, hue, 0.85f);
            _glowTargetBrightness = Mathf.Clamp01(brightness);
        }

        private void Update()
        {
            bool shown = _theaterMode && !PassthroughFlatScreen;
            if (!shown) return;
            float t = 1f - Mathf.Exp(-Time.unscaledDeltaTime * 5f);
            _glowColor = Color.Lerp(_glowColor, _glowTarget, t);
            _glowBrightness = Mathf.Lerp(_glowBrightness, _glowTargetBrightness, t);
            if (_glowLight != null)
            {
                _glowLight.color = _glowColor;
                _glowLight.intensity = 0.05f + 1.1f * _glowBrightness;
            }
            RenderSettings.ambientMode = UnityEngine.Rendering.AmbientMode.Flat;
            RenderSettings.ambientLight = new Color(0.02f, 0.018f, 0.022f) + _glowColor * (0.10f * _glowBrightness);
            ApplyGlowGlobals();
        }

        // ── State ─────────────────────────────────────────────────────────────
        [SerializeField] private bool _theaterMode = true;

        /// <summary>
        /// True shows the cinema (room, seats, lights, friend avatars) and
        /// plays the crowd hum. False shows a plain void.
        /// </summary>
        public bool TheaterMode
        {
            get => _theaterMode;
            set
            {
                _theaterMode = value;
                ApplyVisibilityState();
            }
        }

        /// <summary>Number of seat anchors, including seat 0 (the viewer).</summary>
        public int SeatCount => _seatAnchors.Count;

        private AudioSource _ambientSource;

        // ─────────────────────────────────────────────────────────────────────
        #region Unity Lifecycle

        private void Start()
        {
            BuildTheater();
            SetupAmbientAudio();
            ApplyVisibilityState();
            SubscribeToRoomClient();
        }

        private void OnDestroy()
        {
            UnsubscribeFromRoomClient();
        }

        #endregion

        // ─────────────────────────────────────────────────────────────────────
        #region Audio

        private void SetupAmbientAudio()
        {
            _ambientSource = gameObject.AddComponent<AudioSource>();
            _ambientSource.clip         = CrowdHumClip;
            _ambientSource.loop         = true;
            _ambientSource.spatialBlend = 0f;   // 2D for constant ambient level
            _ambientSource.volume       = AmbientVolume;

            if (CrowdHumClip != null)
                _ambientSource.Play();
        }

        #endregion

        // ─────────────────────────────────────────────────────────────────────
        #region Visibility

        /// <summary>Shows or hides the theater according to TheaterMode and PassthroughFlatScreen.</summary>
        private void ApplyVisibilityState()
        {
            bool showTheater = _theaterMode && !PassthroughFlatScreen;

            if (_theaterRoot != null)
                _theaterRoot.SetActive(showTheater);

            if (_avatarRoot != null)
                _avatarRoot.SetActive(showTheater);

            if (_ambientSource != null)
                _ambientSource.mute = !showTheater;

            // Room lighting: save the scene ambient while the cinema is shown, restore it otherwise
            if (showTheater && !_ambientSaved)
            {
                _savedAmbient = RenderSettings.ambientLight;
                _savedAmbientMode = RenderSettings.ambientMode;
                _ambientSaved = true;
            }
            else if (!showTheater && _ambientSaved)
            {
                RenderSettings.ambientMode = _savedAmbientMode;
                RenderSettings.ambientLight = _savedAmbient;
                _ambientSaved = false;
            }
        }

        #endregion

        // ─────────────────────────────────────────────────────────────────────
        #region Public API

        /// <summary>Toggle between cinema hall and passthrough flat-screen mode.</summary>
        public void SetPassthroughMode(bool enabled)
        {
            PassthroughFlatScreen = enabled;
            ApplyVisibilityState();
        }

        /// <summary>Returns the world-space position of a seat (0-based). Seat 0 is the viewer.</summary>
        public Vector3 GetSeatPosition(int index)
        {
            if (index < 0 || index >= _seatAnchors.Count || _seatAnchors[index] == null)
                return transform.position;
            return _seatAnchors[index].position;
        }

        /// <summary>Sets ambient crowd volume (0-1).</summary>
        public void SetAmbientVolume(float volume)
        {
            AmbientVolume = volume;
            if (_ambientSource != null)
                _ambientSource.volume = volume;
        }

        #endregion

        // ─────────────────────────────────────────────────────────────────────
        #region Helpers

        /// <summary>
        /// Creates a URP Lit material (falls back to Standard). Colour is set on
        /// both _BaseColor and the generic colour so either pipeline path reads it.
        /// </summary>
        private static Material CreateLitMaterial(Color color, float smoothness = 0.2f)
        {
            Shader shader = Shader.Find("Universal Render Pipeline/Lit") ?? Shader.Find("Standard");
            var mat = new Material(shader);
            mat.SetColor("_BaseColor", color);
            mat.color = color;
            mat.SetFloat("_Smoothness", smoothness);
            if (mat.HasProperty("_Glossiness"))
                mat.SetFloat("_Glossiness", smoothness);
            return mat;
        }

        /// <summary>Creates an unlit material for emissive-looking details (step lights).</summary>
        private static Material CreateUnlitMaterial(Color color)
        {
            Shader shader = Shader.Find("Universal Render Pipeline/Unlit") ?? Shader.Find("Unlit/Color");
            var mat = new Material(shader);
            mat.SetColor("_BaseColor", color);
            mat.color = color;
            return mat;
        }

        #endregion
    }
}
