using UnityEngine;
using MoviePlayer.Proto;

namespace MoviePlayer.Player
{
    /// <summary>
    /// Configures the cinema screen quad for correct stereo layout and projection.
    ///
    /// Stereo modes
    /// ────────────
    /// HSBS  Half Side-By-Side   — left  = UV.x ∈ [0, 0.5],  right = [0.5, 1.0]
    /// FSBS  Full Side-By-Side   — same UV logic (source is already full-width halves)
    /// HOU   Half Over-Under     — left  = UV.y ∈ [0.5, 1.0], right = [0, 0.5]
    /// FOU   Full Over-Under     — same UV logic
    /// Mono  — both eyes full UV [0,1]×[0,1]
    ///
    /// For Equirect projections a sphere with inverted normals replaces the quad.
    /// Per-eye UV is written into the material's _LeftEyeScaleOffset /
    /// _RightEyeScaleOffset vectors (xy=scale, zw=offset).
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
        public float ScreenHeight   = 6f;   // only used for Flat; auto-computed from aspect otherwise

        [Header("Materials")]
        public Material ScreenMaterial;     // assign ClarityShader material
        public Mesh     SphereMesh;         // inverted-normals sphere for 180/360

        [Header("Current Layout (runtime)")]
        [SerializeField] private StereoLayout _stereoLayout = StereoLayout.HSBS;
        [SerializeField] private ProjectionType _projection  = ProjectionType.Flat;
        [SerializeField] private bool _eyeSwap = false;

        // ── Private ───────────────────────────────────────────────────────────
        private GameObject _screenObject;
        private MeshRenderer _meshRenderer;
        private MeshFilter   _meshFilter;

        // Shader property IDs
        private static readonly int LeftScaleOffset  = Shader.PropertyToID("_LeftEyeScaleOffset");
        private static readonly int RightScaleOffset = Shader.PropertyToID("_RightEyeScaleOffset");
        private static readonly int EyeSide          = Shader.PropertyToID("_EyeSide");

        // ─────────────────────────────────────────────────────────────────────
        #region Unity Lifecycle

        private void Awake()
        {
            BuildScreenObject();
            Apply(_stereoLayout, _projection, _eyeSwap);
        }

        #endregion

        // ─────────────────────────────────────────────────────────────────────
        #region Public API

        /// <summary>Reconfigures screen from a MediaManifest received from the server.</summary>
        public void ApplyManifest(MediaManifest manifest)
        {
            StereoLayout layout = ParseStereo(manifest.stereo);
            ProjectionType proj = ParseProjection(manifest.projection);
            Apply(layout, proj, manifest.eye_swap);
        }

        /// <summary>Assign a video RenderTexture to the screen mesh's material.</summary>
        public void SetMainTexture(Texture tex)
        {
            if (_meshRenderer == null) BuildScreenObject();
            var mat = _meshRenderer != null ? _meshRenderer.material : null;
            if (mat == null && ScreenMaterial != null) mat = ScreenMaterial;
            if (mat != null) mat.SetTexture("_MainTex", tex);
        }

        public void Apply(StereoLayout layout, ProjectionType proj, bool eyeSwap = false)
        {
            _stereoLayout = layout;
            _projection   = proj;
            _eyeSwap      = eyeSwap;

            ReconfigureMesh(proj);
            ConfigureUVs(layout, eyeSwap);
        }

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

            if (ScreenMaterial != null)
                _meshRenderer.material = ScreenMaterial;
        }

        private void ReconfigureMesh(ProjectionType proj)
        {
            if (_screenObject == null) BuildScreenObject();

            switch (proj)
            {
                case ProjectionType.Flat:
                    _meshFilter.mesh = BuildQuadMesh(ScreenWidth, ScreenHeight);
                    _screenObject.transform.localPosition = new Vector3(0, 0, ScreenDistance);
                    _screenObject.transform.localRotation = Quaternion.identity;
                    _screenObject.transform.localScale    = Vector3.one;
                    break;

                case ProjectionType.Equirect180:
                case ProjectionType.Equirect360:
                    _meshFilter.mesh = (SphereMesh != null)
                        ? SphereMesh
                        : BuildInvertedSphereMesh(proj == ProjectionType.Equirect180 ? 180f : 360f);
                    _screenObject.transform.localPosition = Vector3.zero;
                    _screenObject.transform.localRotation = Quaternion.identity;
                    // Large enough to surround the player
                    float radius = 50f;
                    _screenObject.transform.localScale    = new Vector3(radius, radius, radius);
                    break;
            }
        }

        #endregion

        // ─────────────────────────────────────────────────────────────────────
        #region UV / Stereo Configuration

        /// <summary>
        /// Computes per-eye UV scale+offset and writes them into the material.
        /// Scale = (scaleX, scaleY), Offset = (offsetX, offsetY).
        /// Material samples: UV_eye = UV * scale + offset
        /// </summary>
        private void ConfigureUVs(StereoLayout layout, bool eyeSwap)
        {
            // (scaleX, scaleY, offsetX, offsetY)
            Vector4 left, right;

            switch (layout)
            {
                case StereoLayout.HSBS:
                case StereoLayout.FSBS:
                    // Left = left half, Right = right half
                    left  = new Vector4(0.5f, 1f, 0f,   0f);
                    right = new Vector4(0.5f, 1f, 0.5f, 0f);
                    break;

                case StereoLayout.HOU:
                case StereoLayout.FOU:
                    // Left = top half (V ∈ [0.5,1]), Right = bottom half (V ∈ [0,0.5])
                    // Unity UV origin is bottom-left, so top = higher V
                    left  = new Vector4(1f, 0.5f, 0f, 0.5f);
                    right = new Vector4(1f, 0.5f, 0f, 0f);
                    break;

                default: // Mono
                    left  = new Vector4(1f, 1f, 0f, 0f);
                    right = new Vector4(1f, 1f, 0f, 0f);
                    break;
            }

            if (eyeSwap) { var tmp = left; left = right; right = tmp; }

            if (_meshRenderer == null || _meshRenderer.material == null) return;

            _meshRenderer.material.SetVector(LeftScaleOffset,  left);
            _meshRenderer.material.SetVector(RightScaleOffset, right);
        }

        #endregion

        // ─────────────────────────────────────────────────────────────────────
        #region Mesh Builders

        private static Mesh BuildQuadMesh(float width, float height)
        {
            float hw = width  * 0.5f;
            float hh = height * 0.5f;

            var mesh = new Mesh { name = "ScreenQuad" };
            mesh.vertices  = new[] {
                new Vector3(-hw, -hh, 0), new Vector3( hw, -hh, 0),
                new Vector3(-hw,  hh, 0), new Vector3( hw,  hh, 0)
            };
            mesh.uv = new[] {
                new Vector2(0, 0), new Vector2(1, 0),
                new Vector2(0, 1), new Vector2(1, 1)
            };
            mesh.triangles = new[] { 0, 2, 1, 2, 3, 1 };
            mesh.RecalculateNormals();
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
                    // Invert winding for inside-visible normals
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
            // Flip normals inward
            Vector3[] normals = mesh.normals;
            for (int i = 0; i < normals.Length; i++) normals[i] = -normals[i];
            mesh.normals = normals;
            return mesh;
        }

        #endregion

        // ─────────────────────────────────────────────────────────────────────
        #region Parsers

        private static StereoLayout ParseStereo(string s) => s?.ToLowerInvariant() switch
        {
            "hsbs" => StereoLayout.HSBS,
            "fsbs" => StereoLayout.FSBS,
            "hou"  => StereoLayout.HOU,
            "fou"  => StereoLayout.FOU,
            _      => StereoLayout.Mono
        };

        private static ProjectionType ParseProjection(string s) => s?.ToLowerInvariant() switch
        {
            "equirect180" => ProjectionType.Equirect180,
            "equirect360" => ProjectionType.Equirect360,
            _             => ProjectionType.Flat
        };

        #endregion
    }
}
