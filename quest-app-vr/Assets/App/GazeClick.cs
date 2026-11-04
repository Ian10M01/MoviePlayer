// GazeClick.cs — VR point-and-click UI interactor.
// Attaches to the Main Camera. Points with a tracked controller's laser (trigger clicks,
// thumbstick scrolls) and falls back to head gaze with a 1.5 s dwell when no controller
// is tracked.

using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.UI;

namespace MoviePlayer.App
{
    /// <summary>
    /// Minimal controller-ray + head-gaze UI interactor that works without
    /// XR Interaction Toolkit. Requirements: GraphicRaycaster on a world-space Canvas
    /// and an EventSystem.
    /// </summary>
    [RequireComponent(typeof(Camera))]
    public class GazeClick : MonoBehaviour
    {
        [Header("Gaze / Reticle")]
        [Tooltip("World-space radius (metres) of the gaze spot used for dwell timing.")]
        public float GazeSpotRadius = 0.02f;

        [Tooltip("Seconds of continuous gaze needed to trigger a click (head gaze only).")]
        public float DwellSeconds = 1.5f;

        [Tooltip("Furthest UI a controller ray can reach, in metres.")]
        public float MaxRayDistance = 10f;

        [Tooltip("Thumbstick scroll speed, in UI units per second.")]
        public float ScrollSpeed = 900f;

        /// <summary>Raised when either controller's trigger is pressed (e.g. to wake hidden controls).</summary>
        public static event System.Action TriggerPressed;

        /// <summary>The UI element currently under the pointer, or null.</summary>
        public static GameObject HoveredObject { get; private set; }

        [Header("Reticle Visuals")]
        [Tooltip("Optional world-space reticle transform (auto-created if null).")]
        public Transform Reticle;

        // ── Runtime state ─────────────────────────────────────────────────────
        private Camera           _cam;
        private ControllerVisuals _controllers;
        private Transform        _activeHand;        // controller currently pointing, null = head gaze
        private GameObject       _hovered;           // object that received pointer-enter
        private GameObject       _pressed;           // object that received pointer-down
        private GameObject       _dwellTarget;
        private float            _dwellElapsed;
        private PointerEventData _pointerData;
        private readonly List<RaycastResult> _raycastResults = new List<RaycastResult>();

        private InputAction _leftTrigger, _rightTrigger, _leftStick, _rightStick;
        private readonly List<InputAction> _actions = new List<InputAction>();

        // ── Lifecycle ─────────────────────────────────────────────────────────

        private void Awake()
        {
            _cam = GetComponent<Camera>();
            if (Reticle == null) Reticle = CreateReticle();

            _leftTrigger  = MakeButton("LeftHand");
            _rightTrigger = MakeButton("RightHand");
            _leftStick    = MakeStick("LeftHand");
            _rightStick   = MakeStick("RightHand");
        }

        private void OnDestroy()
        {
            foreach (var a in _actions) { a.Disable(); a.Dispose(); }
            _actions.Clear();
        }

        private void Update()
        {
            if (EventSystem.current == null) return;
            if (_pointerData == null) _pointerData = new PointerEventData(EventSystem.current);
            if (_controllers == null) _controllers = FindFirstObjectByType<ControllerVisuals>();

            if (_leftTrigger.WasPressedThisFrame() || _rightTrigger.WasPressedThisFrame())
                TriggerPressed?.Invoke();

            ChooseActiveHand();

            Ray ray = _activeHand != null
                ? new Ray(_activeHand.position, _activeHand.forward)
                : new Ray(_cam.transform.position, _cam.transform.forward);

            bool hasHit = TryRaycastUI(ray, out RaycastResult hit, out float distance);

            if (_controllers != null)
            {
                _controllers.SetRayLength(_controllers.Left,  _activeHand == _controllers.Left  && hasHit ? distance : float.MaxValue);
                _controllers.SetRayLength(_controllers.Right, _activeHand == _controllers.Right && hasHit ? distance : float.MaxValue);
            }

            UpdateHover(hasHit ? hit.gameObject : null);
            HoveredObject = hasHit ? hit.gameObject : null;

            if (hasHit && Reticle != null)
            {
                Reticle.position = hit.worldPosition;
                Reticle.rotation = Quaternion.LookRotation(-ray.direction);
                Reticle.gameObject.SetActive(true);
            }
            else if (Reticle != null)
            {
                Reticle.gameObject.SetActive(false);
            }

            if (_activeHand != null)
                HandleController(hasHit, hit);
            else
                HandleGaze(hasHit, hit);
        }

        // ── Pointer source ────────────────────────────────────────────────────

        // The right controller points by default; pulling a trigger makes that hand the pointer.
        // With no controller tracked, head gaze takes over.
        private void ChooseActiveHand()
        {
            if (_controllers == null) { _activeHand = null; return; }

            if (_leftTrigger.WasPressedThisFrame() && _controllers.LeftTracked)   _activeHand = _controllers.Left;
            if (_rightTrigger.WasPressedThisFrame() && _controllers.RightTracked) _activeHand = _controllers.Right;

            bool activeTracked = (_activeHand == _controllers.Left  && _controllers.LeftTracked) ||
                                 (_activeHand == _controllers.Right && _controllers.RightTracked);
            if (!activeTracked)
            {
                if (_controllers.RightTracked)     _activeHand = _controllers.Right;
                else if (_controllers.LeftTracked) _activeHand = _controllers.Left;
                else                               _activeHand = null;
            }
        }

        private InputAction ActiveTrigger => _activeHand == _controllers.Left ? _leftTrigger : _rightTrigger;
        private InputAction ActiveStick   => _activeHand == _controllers.Left ? _leftStick   : _rightStick;

        // ── Raycasting ────────────────────────────────────────────────────────

        // GraphicRaycaster only accepts screen positions, so find where the ray meets the nearest
        // world-space canvas, project that point to the screen, and raycast there.
        private bool TryRaycastUI(Ray ray, out RaycastResult best, out float bestDistance)
        {
            best = default;
            bestDistance = float.MaxValue;
            bool found = false;

            foreach (var raycaster in FindObjectsByType<GraphicRaycaster>(FindObjectsSortMode.None))
            {
                if (!raycaster.isActiveAndEnabled) continue;
                var canvas = raycaster.GetComponent<Canvas>();
                if (canvas == null || canvas.renderMode != RenderMode.WorldSpace) continue;

                var rt = (RectTransform)canvas.transform;
                var plane = new Plane(rt.forward, rt.position);
                if (!plane.Raycast(ray, out float d) || d > MaxRayDistance || d >= bestDistance) continue;

                Vector3 point = ray.GetPoint(d);
                Vector3 screen = _cam.WorldToScreenPoint(point);
                if (screen.z <= 0f) continue;

                _pointerData.position = screen;
                _raycastResults.Clear();
                raycaster.Raycast(_pointerData, _raycastResults);
                if (_raycastResults.Count == 0) continue;

                // Results are sorted front to back; take the first that isn't behind the ray origin.
                var r = _raycastResults[0];
                r.worldPosition = point;
                r.distance = d;
                best = r;
                bestDistance = d;
                found = true;
            }

            _pointerData.pointerCurrentRaycast = best;
            return found;
        }

        // ── Interaction ───────────────────────────────────────────────────────

        private void HandleController(bool hasHit, RaycastResult hit)
        {
            _dwellTarget = null;
            _dwellElapsed = 0f;

            var trigger = ActiveTrigger;
            if (trigger.WasPressedThisFrame() || KeyboardClick())
            {
                if (hasHit) PointerDown(hit);
            }
            if (trigger.WasReleasedThisFrame() || KeyboardClick())
            {
                PointerUp(hasHit ? hit.gameObject : null);
            }

            // Thumbstick up/down scrolls the list under the pointer. It does not move
            // language buttons; those are not inside a ScrollRect.
            if (hasHit)
            {
                float y = ActiveStick.ReadValue<Vector2>().y;
                var scroll = hit.gameObject.GetComponentInParent<ScrollRect>();
                if (scroll != null && scroll.vertical)
                {
                    scroll.movementType = ScrollRect.MovementType.Clamped;
                    scroll.inertia = true;
                    scroll.decelerationRate = 0.1f;
                    if (Mathf.Abs(y) > 0.12f)
                        scroll.velocity = new Vector2(scroll.velocity.x, -y * 4500f);
                }
            }
        }

        private void HandleGaze(bool hasHit, RaycastResult hit)
        {
            _dwellTarget = null;
            _dwellElapsed = 0f;
            // Dwell clicks steal a scroll. The panel uses touch; VR uses the trigger.
            if (hasHit && KeyboardClick()) Click(hit);
        }

        private void UpdateHover(GameObject target)
        {
            var handler = target != null ? ExecuteEvents.GetEventHandler<IPointerEnterHandler>(target) : null;
            if (handler == _hovered) return;

            if (_hovered != null) ExecuteEvents.Execute(_hovered, _pointerData, ExecuteEvents.pointerExitHandler);
            _hovered = handler;
            _pointerData.pointerEnter = handler;
            if (_hovered != null) ExecuteEvents.Execute(_hovered, _pointerData, ExecuteEvents.pointerEnterHandler);
        }

        private void PointerDown(RaycastResult hit)
        {
            _pointerData.pointerPressRaycast = hit;
            _pointerData.pressPosition = _pointerData.position;
            _pressed = ExecuteEvents.ExecuteHierarchy(hit.gameObject, _pointerData, ExecuteEvents.pointerDownHandler)
                       ?? ExecuteEvents.GetEventHandler<IPointerClickHandler>(hit.gameObject);
            _pointerData.pointerPress = _pressed;
            _pointerData.rawPointerPress = hit.gameObject;
        }

        private void PointerUp(GameObject releasedOver)
        {
            if (_pressed == null) return;

            ExecuteEvents.Execute(_pressed, _pointerData, ExecuteEvents.pointerUpHandler);

            var clickHandler = releasedOver != null ? ExecuteEvents.GetEventHandler<IPointerClickHandler>(releasedOver) : null;
            if (clickHandler == _pressed)
            {
                ExecuteEvents.Execute(_pressed, _pointerData, ExecuteEvents.pointerClickHandler);
                Debug.Log($"[GazeClick] Clicked {_pressed.name}");
            }

            _pressed = null;
            _pointerData.pointerPress = null;
            _pointerData.rawPointerPress = null;
        }

        private void Click(RaycastResult hit)
        {
            PointerDown(hit);
            PointerUp(hit.gameObject);
        }

        // ── Input ─────────────────────────────────────────────────────────────

        private static bool KeyboardClick()
        {
            // Keyboard fallback (editor / flat-screen)
            var kb = Keyboard.current;
            return kb != null && (kb.enterKey.wasPressedThisFrame || kb.spaceKey.wasPressedThisFrame);
        }

        private InputAction MakeButton(string hand)
        {
            var a = new InputAction("Trigger " + hand, InputActionType.Button,
                "<XRController>{" + hand + "}/{TriggerButton}");
            a.AddBinding("<XRController>{" + hand + "}/triggerPressed");
            a.Enable();
            _actions.Add(a);
            return a;
        }

        private InputAction MakeStick(string hand)
        {
            var a = new InputAction("Stick " + hand, InputActionType.Value,
                "<XRController>{" + hand + "}/{Primary2DAxis}", expectedControlType: "Vector2");
            a.Enable();
            _actions.Add(a);
            return a;
        }

        private Transform CreateReticle()
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            go.name = "GazeReticle";
            go.transform.localScale = Vector3.one * 0.02f;

            // Plain white unlit material (Sprites/Default is always in the build)
            var shader = Shader.Find("Sprites/Default") ?? Shader.Find("Universal Render Pipeline/Unlit");
            if (shader != null)
                go.GetComponent<MeshRenderer>().material = new Material(shader) { color = new Color(1f, 1f, 1f, 0.9f) };

            // No collider
            var col = go.GetComponent<Collider>();
            if (col != null) Destroy(col);

            go.SetActive(false);
            return go.transform;
        }
    }
}
