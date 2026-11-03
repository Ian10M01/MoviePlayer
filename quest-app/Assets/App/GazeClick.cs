// GazeClick.cs — VR gaze / controller-trigger UI interactor.
// Attaches to the Main Camera.  Every frame casts a ray from the camera
// forward into world-space UI; a 1.5 s dwell or right-trigger press clicks.

using UnityEngine;
using UnityEngine.EventSystems;

namespace MoviePlayer.App
{
    /// <summary>
    /// Minimal head-gaze + trigger UI interactor that works without
    /// XR Interaction Toolkit.  Requirements: GraphicRaycaster on Canvas,
    /// EventSystem with InputSystemUIInputModule or StandaloneInputModule.
    /// </summary>
    [RequireComponent(typeof(Camera))]
    public class GazeClick : MonoBehaviour
    {
        [Header("Gaze / Reticle")]
        [Tooltip("World-space radius (metres) of the gaze spot used for dwell timing.")]
        public float GazeSpotRadius = 0.02f;

        [Tooltip("Seconds of continuous gaze needed to trigger a click.")]
        public float DwellSeconds = 1.5f;

        [Header("Reticle Visuals")]
        [Tooltip("Optional world-space reticle transform (auto-created if null).")]
        public Transform Reticle;

        // ── Runtime state ─────────────────────────────────────────────────────
        private Camera          _cam;
        private GameObject      _dwellTarget;
        private float           _dwellElapsed;
        private PointerEventData _pointerData;
        private System.Collections.Generic.List<RaycastResult> _raycastResults
            = new System.Collections.Generic.List<RaycastResult>();

        // ── Lifecycle ─────────────────────────────────────────────────────────

        private void Awake()
        {
            _cam = GetComponent<Camera>();
            if (Reticle == null) Reticle = CreateReticle();
            _pointerData = new PointerEventData(EventSystem.current);
        }

        private void Update()
        {
            if (EventSystem.current == null) return;

            // Cast ray from screen centre
            var screenCentre = new Vector2(_cam.pixelWidth * 0.5f, _cam.pixelHeight * 0.5f);
            _pointerData.position = screenCentre;

            _raycastResults.Clear();
            EventSystem.current.RaycastAll(_pointerData, _raycastResults);

            bool triggered = IsTriggerPressed();

            if (_raycastResults.Count > 0)
            {
                var hit = _raycastResults[0];

                // Move reticle to the hit point
                if (Reticle != null)
                {
                    Reticle.position = hit.worldPosition;
                    Reticle.rotation = Quaternion.LookRotation(-_cam.transform.forward);
                    Reticle.gameObject.SetActive(true);
                }

                // Dwell tracking
                if (hit.gameObject == _dwellTarget)
                {
                    _dwellElapsed += Time.deltaTime;
                    if (_dwellElapsed >= DwellSeconds)
                    {
                        SimulateClick(hit);
                        _dwellElapsed = 0f;
                    }
                }
                else
                {
                    _dwellTarget  = hit.gameObject;
                    _dwellElapsed = 0f;
                }

                // Instant trigger click
                if (triggered) SimulateClick(hit);
            }
            else
            {
                _dwellTarget  = null;
                _dwellElapsed = 0f;
                if (Reticle != null) Reticle.gameObject.SetActive(false);
            }
        }

        // ── Helpers ───────────────────────────────────────────────────────────

        private void SimulateClick(RaycastResult hit)
        {
            if (EventSystem.current == null) return;
            _pointerData.pointerCurrentRaycast = hit;

            // Simulate pointer-down → pointer-up → click
            ExecuteEvents.Execute(hit.gameObject, _pointerData,
                ExecuteEvents.pointerDownHandler);
            ExecuteEvents.Execute(hit.gameObject, _pointerData,
                ExecuteEvents.pointerUpHandler);
            ExecuteEvents.Execute(hit.gameObject, _pointerData,
                ExecuteEvents.pointerClickHandler);

            Debug.Log($"[GazeClick] Clicked {hit.gameObject.name}");
        }

        private bool IsTriggerPressed()
        {
            // Keyboard fallback (editor / flat-screen)
            if (Input.GetKeyDown(KeyCode.Return) || Input.GetKeyDown(KeyCode.Space))
                return true;

            // XR controller trigger via Unity XR Input (no Meta SDK needed)
            var devices = new System.Collections.Generic.List<UnityEngine.XR.InputDevice>();
            UnityEngine.XR.InputDevices.GetDevicesWithRole(
                UnityEngine.XR.InputDeviceRole.RightHanded, devices);
            foreach (var dev in devices)
            {
                if (dev.TryGetFeatureValue(UnityEngine.XR.CommonUsages.triggerButton,
                                           out bool pressed) && pressed)
                    return true;
            }
            return false;
        }

        private Transform CreateReticle()
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            go.name = "GazeReticle";
            go.transform.localScale = Vector3.one * 0.02f;

            // Plain white unlit material
            var mat = new Material(Shader.Find("Unlit/Color") ??
                                   Shader.Find("Universal Render Pipeline/Unlit"));
            if (mat != null) mat.color = new Color(1f, 1f, 1f, 0.8f);
            go.GetComponent<MeshRenderer>().material = mat;

            // No collider
            var col = go.GetComponent<Collider>();
            if (col != null) Destroy(col);

            go.SetActive(false);
            return go.transform;
        }
    }
}
