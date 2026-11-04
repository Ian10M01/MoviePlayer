using UnityEngine;
using MoviePlayer.Proto;

namespace MoviePlayer.Player
{
    /// <summary>
    /// Builds and configures the cinema screen: size, distance, curvature, aspect ratio,
    /// stereo layout and projection.
    ///
    /// Stereo modes
    /// ────────────
    /// HSBS  Half Side-By-Side   — each eye gets half the width, shown at the full frame aspect
    /// FSBS  Full Side-By-Side   — each half is a full-resolution picture (aspect = half width / height)
    /// HOU   Half Over-Under     — top half = left eye, shown at the full frame aspect
    /// FOU   Full Over-Under     — each half is a full picture (aspect = width / half height)
    /// Mono  — both eyes see the whole frame
    ///
    /// Equirect 180/360 use an inverted sphere around the viewer. Per-eye UV is written into
    /// the material's _LeftEyeScaleOffset / _RightEyeScaleOffset (xy = scale, zw = offset).
    ///
    /// The screen is sized from the video's real aspect ratio, so nothing is stretched or
    /// cropped. An optional environment constraint (the theater) pins the screen to its
    /// screen wall, caps its size and keeps its bottom edge above the stage.
    /// </summary>
    public class StereoScreenRenderer : MonoBehaviour
    {
        // ── Enums ─────────────────────────────────────────────────────────────
        public enum StereoLayout { Mono, FSBS, HSBS, FOU, HOU }
        public enum ProjectionType { Flat, Equirect180, Equirect360 }

        // ── Inspector ─────────────────────────────────────────────────────────
        [Header("Screen")]
        public float ScreenDistance = 8f;
        public float ScreenWidth    = 12f;
        public float ScreenHeight   = 6.75f;   // derived from width and the video's aspect ratio
        [Range(0f, 1f)]
        [Tooltip("0 = flat, 1 = curved around the viewer.")]
        public float Curvature      = 0f;
        [Tooltip("Vertical offset of the screen centre from eye level (free mode only).")]
        public float HeightOffset   = 0f;

        [Header("Materials")]
        public Material ScreenMaterial;     // assign ClarityShader material
        public Mesh     SphereMesh;         // inverted-normals sphere for 180/360

        [Header("Current Layout (runtime)")]
        [SerializeField] private StereoLayout _stereoLayout = StereoLayout.Mono;
        [SerializeField] private ProjectionType _projection  = ProjectionType.Flat;
        [SerializeField] private bool _eyeSwap = false;

        public StereoLayout   Layout     => _stereoLayout;
        public ProjectionType Projection => _projection;
        public bool           EyeSwap    => _eyeSwap;

        /// <summary>The material instance actually drawn on the screen.</summary>
        public Material ActiveMaterial => _meshRenderer != null ? _meshRenderer.sharedMaterial : null;

        /// <summary>Width/height ratio of one eye's picture as displayed.</summary>
        public float DisplayAspect { get; private set; } = 16f / 9f;

        // ── Private ───────────────────────────────────────────────────────────
        private GameObject   _screenObject;
        private MeshRenderer _meshRenderer;
        private MeshFilter   _meshFilter;
        private Material     _materialInstance;
        private Texture      _mainTex;
        private int          _videoWidth, _videoHeight;

        // Environment constraint (theater screen wall)
        private bool    _constrained;
        private float   _constraintPlaneZ;
        private float   _constraintMinBottomY;
        private Vector2 _constraintMaxSize;

        private static readonly int LeftScaleOffset  = Shader.PropertyToID("_LeftEyeScaleOffset");
        private static readonly int RightScaleOffset = Shader.PropertyToID("_RightEyeScaleOffset");
        private static readonly int MainTexId        = Shader.PropertyToID("_MainTex");

        private const int CurveSegments = 48;

        // ─────────────────────────────────────────────────────────────────────
        #region Unity Lifecycle

        private void Awake()
        {
            BuildScreenObject();
            Apply(_stereoLayout, _projection, _eyeSwap);
        }

        private void OnDestroy()
        {
            if (_materialInstance != null) Destroy(_materialInstance);
        }

        #endregion

        // ─────────────────────────────────────────────────────────────────────
        #region Public API

        /// <summary>Reconfigures screen from a MediaManifest received from the server.</summary>
        public void ApplyManifest(MediaManifest manifest)
        {
            if (manifest.width > 0 && manifest.height > 0) SetVideoSize(manifest.width, manifest.height);
            var guess = StereoDetect.FromManifest(manifest);
            Apply(guess.Layout, guess.Projection, guess.EyeSwap);
            StereoDetect.Log(guess);
        }

        /// <summary>Assign the (processed) video texture to the screen.</summary>
        public void SetMainTexture(Texture tex)
        {
            if (_meshRenderer == null) BuildScreenObject();
            _mainTex = tex;
            if (_materialInstance != null) _materialInstance.SetTexture(MainTexId, tex);
        }

        /// <summary>Decoded frame size; drives the screen's aspect ratio.</summary>
        public void SetVideoSize(int width, int height)
        {
            if (width <= 0 || height <= 0) return;
            if (width == _videoWidth && height == _videoHeight) return;
            _videoWidth = width;
            _videoHeight = height;
            Rebuild();
        }

        /// <summary>
        /// Pins a flat screen to an environment's screen wall: fixed distance, size capped at
        /// maxSize, bottom edge kept at or above minBottomY. Pass active=false for free placement.
        /// </summary>
        public void SetEnvironmentConstraint(bool active, float planeZ = 0f, float minBottomY = 0f, Vector2 maxSize = default)
        {
            _constrained = active;
            _constraintPlaneZ = planeZ;
            _constraintMinBottomY = minBottomY;
            _constraintMaxSize = maxSize;
            Rebuild();
        }

        public void Apply(StereoLayout layout, ProjectionType proj, bool eyeSwap = false)
        {
            _stereoLayout = layout;
            _projection   = proj;
            _eyeSwap      = eyeSwap;
            Rebuild();
        }

        /// <summary>Re-applies size, distance, curvature and layout after changing the public fields.</summary>
        public void Rebuild()
        {
            if (_screenObject == null) BuildScreenObject();
            DisplayAspect = ComputeAspect();
            ReconfigureMesh(_projection);
            ConfigureUVs(_stereoLayout, _eyeSwap);
        }

        /// <summary>The screen's actual width in metres after constraints.</summary>
        public float EffectiveWidth { get; private set; }

        #endregion

        // ─────────────────────────────────────────────────────────────────────
        #region Screen Construction

        private void BuildScreenObject()
        {
            if (_screenObject != null) Destroy(_screenObject);

            _screenObject = new GameObject("CinemaScreen");
            _screenObject.transform.SetParent(transform, false);
            _screenObject.transform.localPosition = new Vector3(0, 0, ScreenDistance);

            _meshFilter   = _screenObject.AddComponent<MeshFilter>();
            _meshRenderer = _screenObject.AddComponent<MeshRenderer>();
            _meshRenderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            _meshRenderer.receiveShadows = false;

            if (ScreenMaterial != null)
            {
                _materialInstance = new Material(ScreenMaterial) { name = "ScreenMaterial (instance)" };
                _meshRenderer.sharedMaterial = _materialInstance;
                if (_mainTex != null) _materialInstance.SetTexture(MainTexId, _mainTex);
            }
        }

        private float ComputeAspect()
        {
            float frame = (_videoWidth > 0 && _videoHeight > 0) ? (float)_videoWidth / _videoHeight : 16f / 9f;
            switch (_stereoLayout)
            {
                case StereoLayout.FSBS: return frame * 0.5f;
                case StereoLayout.FOU:  return frame * 2f;
                default:                return frame;   // Mono, HSBS, HOU are shown at the full frame aspect
            }
        }

        private void ReconfigureMesh(ProjectionType proj)
        {
            var t = _screenObject.transform;
            switch (proj)
            {
                case ProjectionType.Flat:
                {
                    float width = Mathf.Max(0.5f, ScreenWidth);
                    float height = width / DisplayAspect;
                    float distance = ScreenDistance;
                    float centreY = HeightOffset;

                    if (_constrained)
                    {
                        float fit = Mathf.Min(1f, _constraintMaxSize.x / width, _constraintMaxSize.y / height);
                        width *= fit;
                        height *= fit;
                        distance = _constraintPlaneZ;
                        centreY = Mathf.Max(0f, _constraintMinBottomY + height * 0.5f);
                    }

                    ScreenHeight = height;
                    EffectiveWidth = width;
                    // In the theater the screen hangs on the wall and stays flat.
                    float curve = _constrained ? 0f : Curvature;
                    _meshFilter.mesh = BuildCurvedScreenMesh(width, height, distance, curve);
                    t.localPosition = new Vector3(0, centreY, distance);
                    t.localRotation = Quaternion.identity;
                    t.localScale    = Vector3.one;
                    break;
                }

                case ProjectionType.Equirect180:
                case ProjectionType.Equirect360:
                    _meshFilter.mesh = (SphereMesh != null)
                        ? SphereMesh
                        : BuildInvertedSphereMesh(proj == ProjectionType.Equirect180 ? 180f : 360f);
                    t.localPosition = Vector3.zero;
                    t.localRotation = Quaternion.identity;
                    float radius = 50f;
                    t.localScale    = new Vector3(radius, radius, radius);
                    EffectiveWidth = 0f;
                    break;
            }
        }

        #endregion

        // ─────────────────────────────────────────────────────────────────────
        #region UV / Stereo Configuration

        /// <summary>
        /// Computes per-eye UV scale+offset and writes them into the material.
        /// Material samples: UV_eye = UV * scale + offset
        /// </summary>
        private void ConfigureUVs(StereoLayout layout, bool eyeSwap)
        {
            Vector4 left, right;

            switch (layout)
            {
                case StereoLayout.HSBS:
                case StereoLayout.FSBS:
                    left  = new Vector4(0.5f, 1f, 0f,   0f);
                    right = new Vector4(0.5f, 1f, 0.5f, 0f);
                    break;

                case StereoLayout.HOU:
                case StereoLayout.FOU:
                    // Top half is the left eye (V ∈ [0.5,1]). Bottom half is the right eye.
                    left  = new Vector4(1f, 0.5f, 0f, 0.5f);
                    right = new Vector4(1f, 0.5f, 0f, 0f);
                    break;

                default: // Mono
                    left  = new Vector4(1f, 1f, 0f, 0f);
                    right = new Vector4(1f, 1f, 0f, 0f);
                    break;
            }

            if (eyeSwap) { var tmp = left; left = right; right = tmp; }

            if (_materialInstance == null) return;
            _materialInstance.SetVector(LeftScaleOffset,  left);
            _materialInstance.SetVector(RightScaleOffset, right);
        }

        #endregion

        // ─────────────────────────────────────────────────────────────────────
        #region Mesh Builders

        /// <summary>
        /// Screen mesh centred on its transform. With curvature > 0 the screen bends into an
        /// arc around the viewer (radius = distance / curvature), so the edges come closer.
        /// </summary>
        private static Mesh BuildCurvedScreenMesh(float width, float height, float distance, float curvature)
        {
            int segs = curvature > 0.001f ? CurveSegments : 1;
            float radius = curvature > 0.001f ? Mathf.Max(distance / curvature, width / (2f * Mathf.PI) + 0.1f) : 0f;
            float hh = height * 0.5f;

            var verts = new Vector3[(segs + 1) * 2];
            var uvs   = new Vector2[verts.Length];
            var tris  = new int[segs * 6];

            for (int i = 0; i <= segs; i++)
            {
                float u = (float)i / segs;
                float x, z;
                if (segs == 1)
                {
                    x = (u - 0.5f) * width;
                    z = 0f;
                }
                else
                {
                    float phi = (u - 0.5f) * width / radius;   // arc length = width
                    x = radius * Mathf.Sin(phi);
                    z = radius * Mathf.Cos(phi) - radius;      // edges curve toward the viewer
                }
                verts[i * 2]     = new Vector3(x, -hh, z);
                verts[i * 2 + 1] = new Vector3(x,  hh, z);
                uvs[i * 2]       = new Vector2(u, 0f);
                uvs[i * 2 + 1]   = new Vector2(u, 1f);
            }

            for (int i = 0, ti = 0; i < segs; i++)
            {
                int b0 = i * 2, t0 = b0 + 1, b1 = b0 + 2, t1 = b0 + 3;
                tris[ti++] = b0; tris[ti++] = t0; tris[ti++] = b1;
                tris[ti++] = t0; tris[ti++] = t1; tris[ti++] = b1;
            }

            var mesh = new Mesh { name = "ScreenMesh" };
            mesh.vertices  = verts;
            mesh.uv        = uvs;
            mesh.triangles = tris;
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();
            return mesh;
        }

        /// <summary>
        /// Builds a UV sphere with inverted normals so the inside surface is
        /// visible. Used for equirectangular projections.
        /// </summary>
        private static Mesh BuildInvertedSphereMesh(float hFovDeg)
        {
            const int latSegments  = 32;
            const int lonSegments  = 64;

            var mesh = new Mesh { name = "InvertedSphere" };
            int vertCount = (latSegments + 1) * (lonSegments + 1);
            Vector3[] verts = new Vector3[vertCount];
            Vector2[] uvs   = new Vector2[vertCount];
            int[]     tris  = new int[latSegments * lonSegments * 6];

            float hFovRad = hFovDeg * Mathf.Deg2Rad;

            int vi = 0;
            for (int lat = 0; lat <= latSegments; lat++)
            {
                float theta = Mathf.PI * lat / latSegments;        // 0 → π
                for (int lon = 0; lon <= lonSegments; lon++)
                {
                    float phi = -hFovRad * 0.5f + hFovRad * lon / lonSegments;
                    float sinT = Mathf.Sin(theta);
                    verts[vi] = new Vector3(
                        sinT * Mathf.Sin(phi),
                        Mathf.Cos(theta),
                        sinT * Mathf.Cos(phi));
                    uvs[vi] = new Vector2(
                        (float)lon / lonSegments,
                        1f - (float)lat / latSegments);
                    vi++;
                }
            }

            int ti = 0;
            for (int lat = 0; lat < latSegments; lat++)
            {
                for (int lon = 0; lon < lonSegments; lon++)
                {
                    int curr = lat * (lonSegments + 1) + lon;
                    int next = curr + lonSegments + 1;
                    tris[ti++] = curr;
                    tris[ti++] = curr + 1;
                    tris[ti++] = next;
                    tris[ti++] = next;
                    tris[ti++] = curr + 1;
                    tris[ti++] = next + 1;
                }
            }

            mesh.vertices  = verts;
            mesh.uv        = uvs;
            mesh.triangles = tris;
            mesh.RecalculateNormals();
            Vector3[] normals = mesh.normals;
            for (int i = 0; i < normals.Length; i++) normals[i] = -normals[i];
            mesh.normals = normals;
            return mesh;
        }

        #endregion

        // ─────────────────────────────────────────────────────────────────────
        #region Parsers

        #endregion
    }
}
