using UnityEngine;

namespace MoviePlayer.Theater
{
    /// <summary>
    /// Constructs a minimal cinema hall from Unity primitives (no art assets
    /// required). The hall can be hidden for a passthrough flat-screen mode.
    ///
    /// Cinema layout
    /// ─────────────
    ///    Z+
    ///     ▲   ████ SCREEN ████
    ///     │
    ///    [S0]  [S1]   [S2]  [S3]   ← 4 seats facing +Z (screen)
    ///     │
    ///     origin
    ///
    /// Seat positions are fixed relative to this transform's origin.
    /// </summary>
    public class TheaterEnvironment : MonoBehaviour
    {
        // ── Inspector ─────────────────────────────────────────────────────────
        [Header("Screen")]
        public float ScreenDistance = 8f;
        public float ScreenWidth    = 12f;
        public float ScreenHeight   = 6.75f;  // 16:9 ratio with ScreenWidth

        [Header("Hall")]
        public float HallWidth  = 14f;
        public float HallHeight = 5f;
        public float HallDepth  = 12f;
        public Material WallMaterial;
        public Material FloorMaterial;
        public Material CeilingMaterial;

        [Header("Seats")]
        public float SeatSpacing   = 1.6f;   // metres between seat centres
        public float SeatRowOffset = -3f;     // behind origin
        public float SeatHeight    = 0f;      // y of seat (floor-level AvatarHeight)

        [Header("Ambient Audio")]
        public AudioClip CrowdHumClip;
        [Range(0f, 1f)]
        public float AmbientVolume = 0.08f;

        [Header("Mode")]
        public bool PassthroughFlatScreen = false;

        // ── Internal refs ─────────────────────────────────────────────────────
        private GameObject _hallRoot;
        private GameObject[] _seats = new GameObject[4];
        private AudioSource _ambientSource;

        // Seat positions relative to room centre (seated, facing +Z)
        private static readonly Vector3[] SeatLocalPositions =
        {
            new Vector3(-2.4f, 0f, -3f),
            new Vector3(-0.8f, 0f, -3f),
            new Vector3( 0.8f, 0f, -3f),
            new Vector3( 2.4f, 0f, -3f)
        };

        // ─────────────────────────────────────────────────────────────────────
        #region Unity Lifecycle

        private void Start()
        {
            BuildHall();
            BuildSeats();
            SetupAmbientAudio();
            ApplyPassthroughMode();
        }

        #endregion

        // ─────────────────────────────────────────────────────────────────────
        #region Construction

        private void BuildHall()
        {
            _hallRoot = new GameObject("CinemaHall");
            _hallRoot.transform.SetParent(transform, false);

            Material wallMat  = WallMaterial  ?? CreateSolidMat(new Color(0.12f, 0.10f, 0.09f));
            Material floorMat = FloorMaterial ?? CreateSolidMat(new Color(0.08f, 0.07f, 0.06f));
            Material ceilMat  = CeilingMaterial ?? CreateSolidMat(new Color(0.05f, 0.05f, 0.05f));

            // Floor
            SpawnPanel("Floor",
                new Vector3(0, -0.01f, 0),
                new Vector3(HallWidth, 0.02f, HallDepth),
                floorMat);

            // Ceiling
            SpawnPanel("Ceiling",
                new Vector3(0, HallHeight, 0),
                new Vector3(HallWidth, 0.02f, HallDepth),
                ceilMat);

            // Back wall (behind seats)
            SpawnPanel("WallBack",
                new Vector3(0, HallHeight * 0.5f, -HallDepth * 0.5f),
                new Vector3(HallWidth, HallHeight, 0.2f),
                wallMat);

            // Side walls
            SpawnPanel("WallLeft",
                new Vector3(-HallWidth * 0.5f, HallHeight * 0.5f, 0),
                new Vector3(0.2f, HallHeight, HallDepth),
                wallMat);

            SpawnPanel("WallRight",
                new Vector3( HallWidth * 0.5f, HallHeight * 0.5f, 0),
                new Vector3(0.2f, HallHeight, HallDepth),
                wallMat);

            // Front wall / screen surround (screen gap left open for the actual
            // CinemaScreen quad which is placed by StereoScreenRenderer)
            SpawnPanel("WallFront",
                new Vector3(0, HallHeight * 0.5f, HallDepth * 0.5f),
                new Vector3(HallWidth, HallHeight, 0.2f),
                wallMat);
        }

        private void SpawnPanel(string label, Vector3 localPos, Vector3 scale, Material mat)
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
            go.name = label;
            go.transform.SetParent(_hallRoot.transform, false);
            go.transform.localPosition = localPos;
            go.transform.localScale    = scale;

            // Disable collision (we only want visual geometry)
            var col = go.GetComponent<Collider>();
            if (col != null) col.enabled = false;

            if (mat != null)
                go.GetComponent<MeshRenderer>().material = mat;
        }

        private void BuildSeats()
        {
            for (int i = 0; i < 4; i++)
            {
                var seat = new GameObject($"Seat_{i}");
                seat.transform.SetParent(transform, false);
                seat.transform.localPosition = SeatLocalPositions[i];
                seat.transform.localRotation = Quaternion.identity; // faces +Z (screen)

                // Simple placeholder: a flat box for the seat base
                var base_ = GameObject.CreatePrimitive(PrimitiveType.Cube);
                base_.name = "SeatBase";
                base_.transform.SetParent(seat.transform, false);
                base_.transform.localPosition = new Vector3(0, 0.25f, 0);
                base_.transform.localScale    = new Vector3(0.55f, 0.5f, 0.55f);
                Destroy(base_.GetComponent<Collider>());

                // Back rest
                var back = GameObject.CreatePrimitive(PrimitiveType.Cube);
                back.name = "SeatBack";
                back.transform.SetParent(seat.transform, false);
                back.transform.localPosition = new Vector3(0, 0.75f, -0.22f);
                back.transform.localScale    = new Vector3(0.55f, 0.55f, 0.1f);
                Destroy(back.GetComponent<Collider>());

                _seats[i] = seat;
            }
        }

        private void SetupAmbientAudio()
        {
            _ambientSource = gameObject.AddComponent<AudioSource>();
            _ambientSource.clip        = CrowdHumClip;
            _ambientSource.loop        = true;
            _ambientSource.spatialBlend = 0f;   // 2D for constant ambient level
            _ambientSource.volume      = AmbientVolume;

            if (CrowdHumClip != null)
                _ambientSource.Play();
        }

        private void ApplyPassthroughMode()
        {
            if (_hallRoot != null)
                _hallRoot.SetActive(!PassthroughFlatScreen);

            foreach (var seat in _seats)
                if (seat != null)
                    seat.SetActive(!PassthroughFlatScreen);

            if (_ambientSource != null)
                _ambientSource.mute = PassthroughFlatScreen;
        }

        #endregion

        // ─────────────────────────────────────────────────────────────────────
        #region Public API

        /// <summary>Toggle between cinema hall and passthrough flat-screen mode.</summary>
        public void SetPassthroughMode(bool enabled)
        {
            PassthroughFlatScreen = enabled;
            ApplyPassthroughMode();
        }

        /// <summary>Returns the world-space position of a seat (0-3).</summary>
        public Vector3 GetSeatPosition(int index)
        {
            if (index < 0 || index >= _seats.Length || _seats[index] == null)
                return transform.position;
            return _seats[index].transform.position;
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

        private static Material CreateSolidMat(Color color)
        {
            var mat = new Material(Shader.Find("Universal Render Pipeline/Lit"));
            if (mat != null) mat.color = color;
            return mat;
        }

        #endregion
    }
}
