using UnityEngine;

#if UNITY_ANDROID && !UNITY_EDITOR
using Unity.XR.Oculus;          // OVRPlugin lives here in Meta XR SDK ≥ 60
#endif

namespace MoviePlayer.Player
{
    /// <summary>
    /// Wires up the Meta Quest Super Resolution compositor hint and drives
    /// the ClarityShader each frame with inspector-tunable parameters.
    ///
    /// Super Resolution
    /// ────────────────
    /// OVRPlugin.SetEyeBufferSharpenType(OVRPlugin.LayerSharpenType.Quality)
    /// requests that the compositor run AMD FidelityFX Super Resolution (FSR1)
    /// on the eye buffer before ATW warp.  This is free GPU work done by the
    /// compositor driver — no shader overhead on the app side.
    ///
    /// RCAS (in shader)
    /// ────────────────
    /// On top of compositor SR we run a per-pixel RCAS pass in ClarityShader
    /// for fine-grained control over sharpening strength and to protect film
    /// grain from ringing.
    /// </summary>
    [RequireComponent(typeof(StereoScreenRenderer))]
    public class ClarityStack : MonoBehaviour
    {
        // ── Inspector ─────────────────────────────────────────────────────────
        [Header("Sharpening")]
        [Range(0f, 1f)] public float SharpenStrength = 0.5f;

        [Header("Chroma")]
        [Range(0f, 1f)] public float ChromaBoost = 0.1f;

        [Header("HDR")]
        public float HdrPeakNits = 500f;

        [Header("Deband")]
        public bool Deband = true;

        [Header("Material")]
        [Tooltip("Drag the ClarityShader material here, or leave null to auto-find it on the screen renderer.")]
        public Material ClarityMaterial;

        // ── Shader property IDs ───────────────────────────────────────────────
        private static readonly int PropSharpenStrength = Shader.PropertyToID("_SharpenStrength");
        private static readonly int PropChromaBoost     = Shader.PropertyToID("_ChromaBoost");
        private static readonly int PropHdrPeakNits     = Shader.PropertyToID("_HdrPeakNits");
        private static readonly int PropDebandEnabled   = Shader.PropertyToID("_DebandEnabled");

        // ─────────────────────────────────────────────────────────────────────
        #region Unity Lifecycle

        private void Start()
        {
            EnableCompositorSuperResolution();
            FindOrAssignMaterial();
            PushShaderProperties();
        }

        private void Update()
        {
            PushShaderProperties();
        }

        #endregion

        // ─────────────────────────────────────────────────────────────────────
        #region Super Resolution

        private void EnableCompositorSuperResolution()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            try
            {
                // Enable premultiplied-alpha compositing (required for SR)
                OVRManager.eyeFovPremultipliedAlphaModeEnabled = false;

                // Request Quality-level FSR sharpening from the Meta compositor
                // LayerSharpenType.Quality = 2 in current Meta XR SDK
                bool ok = OVRPlugin.SetEyeBufferSharpenType(
                    OVRPlugin.LayerSharpenType.Quality);

                if (ok)
                    Debug.Log("[ClarityStack] Compositor Super Resolution (Quality) enabled.");
                else
                    Debug.LogWarning("[ClarityStack] SetEyeBufferSharpenType returned false — " +
                        "check Meta XR SDK version (≥53 required).");
            }
            catch (System.Exception e)
            {
                Debug.LogWarning($"[ClarityStack] SR init exception: {e.Message}");
            }
#else
            Debug.Log("[ClarityStack] EDITOR — Super Resolution skipped (Quest only).");
#endif
        }

        #endregion

        // ─────────────────────────────────────────────────────────────────────
        #region Material Management

        private void FindOrAssignMaterial()
        {
            if (ClarityMaterial != null) return;

            // Try to pull it from the StereoScreenRenderer's screen mesh
            var renderer = GetComponentInChildren<MeshRenderer>();
            if (renderer != null)
            {
                ClarityMaterial = renderer.material;
                Debug.Log("[ClarityStack] Auto-assigned ClarityMaterial from child MeshRenderer.");
            }
            else
            {
                Debug.LogWarning("[ClarityStack] No ClarityMaterial assigned and none found on children.");
            }
        }

        private void PushShaderProperties()
        {
            if (ClarityMaterial == null) return;

            ClarityMaterial.SetFloat(PropSharpenStrength, SharpenStrength);
            ClarityMaterial.SetFloat(PropChromaBoost,     ChromaBoost);
            ClarityMaterial.SetFloat(PropHdrPeakNits,     HdrPeakNits);
            ClarityMaterial.SetFloat(PropDebandEnabled,   Deband ? 1f : 0f);
        }

        #endregion
    }
}
