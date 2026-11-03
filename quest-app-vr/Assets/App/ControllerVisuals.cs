// ControllerVisuals.cs
// Makes the two Quest controllers visible and tracked at runtime.
// Creates LeftController / RightController under the rig's "Camera Offset",
// drives them with TrackedPoseDriver (new Input System), and draws a simple
// body plus a laser ray. Each controller's visuals hide while it is untracked.

using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.XR;

namespace MoviePlayer.App
{
    public class ControllerVisuals : MonoBehaviour
    {
        // ---- Public API ----

        // Controller transforms. Other scripts can raycast from these (the ray points along local +Z).
        public Transform Left { get; private set; }
        public Transform Right { get; private set; }

        public bool LeftTracked  => leftVisuals  != null && leftVisuals.gameObject.activeSelf;
        public bool RightTracked => rightVisuals != null && rightVisuals.gameObject.activeSelf;

        // Shortens a controller's laser so it ends where it hits the UI (length in metres).
        public void SetRayLength(Transform controller, float length)
        {
            var line = controller != null ? controller.GetComponentInChildren<LineRenderer>() : null;
            if (line != null) line.SetPosition(1, Vector3.forward * Mathf.Min(length, RayLength));
        }

        // ---- Look ----

        private static readonly Color BodyColor = new Color(0.12f, 0.12f, 0.13f);  // dark grey
        private static readonly Color RayColor = new Color(0.4f, 0.95f, 1f);       // soft cyan
        private static readonly Color RayGradientTint = new Color(0.8f, 1f, 1f);   // near white, cyan tint
        private const float RayLength = 3f;
        private const float RayWidth = 0.004f;

        // ---- Runtime state ----

        private Material bodyMaterial;
        private Material rayMaterial;
        private InputAction leftTracked;
        private InputAction rightTracked;
        private Transform leftVisuals;
        private Transform rightVisuals;

        // Every standalone InputAction we create, so each one can be disabled and disposed.
        private readonly List<InputAction> ownedActions = new List<InputAction>();

        private void Awake()
        {
            // Find the rig: Camera.main's parent is the "Camera Offset" transform.
            Camera cam = Camera.main;
            if (cam == null || cam.transform.parent == null)
            {
                Debug.LogWarning("[ControllerVisuals] Camera.main or its Camera Offset parent was not found. Controllers not created.");
                enabled = false;
                return;
            }

            Transform cameraOffset = cam.transform.parent;

            // Shared materials for both controllers.
            bodyMaterial = CreateMaterial(BodyColor, transparent: false);
            rayMaterial = CreateMaterial(RayColor, transparent: true);

            // Build both controllers under Camera Offset.
            Left = BuildController(cameraOffset, "LeftController", "LeftHand");
            Right = BuildController(cameraOffset, "RightController", "RightHand");
            leftVisuals = Left.Find("Visuals");
            rightVisuals = Right.Find("Visuals");

            // Tracking state for visibility, read each frame in Update.
            leftTracked = MakeTrackedAction("LeftHand");
            rightTracked = MakeTrackedAction("RightHand");
        }

        private void Update()
        {
            // Hide a controller's visuals while it is not tracked.
            SetVisualsActive(leftVisuals, leftTracked.ReadValue<float>() > 0.5f);
            SetVisualsActive(rightVisuals, rightTracked.ReadValue<float>() > 0.5f);
        }

        private void OnDestroy()
        {
            // Release the input actions we created in code.
            foreach (InputAction action in ownedActions)
            {
                if (action == null) continue;
                action.Disable();
                action.Dispose();
            }
            ownedActions.Clear();

            // Release the runtime materials.
            if (bodyMaterial != null) Destroy(bodyMaterial);
            if (rayMaterial != null) Destroy(rayMaterial);
        }

        // ---- Controller construction ----

        // Creates one controller root with its pose driver and visuals. Returns its transform.
        private Transform BuildController(Transform parent, string objectName, string hand)
        {
            var root = new GameObject(objectName).transform;
            root.SetParent(parent, false);

            // Pose driver: position and rotation from the XR controller, bound in code.
            var tpd = root.gameObject.AddComponent<TrackedPoseDriver>();
            tpd.trackingType = TrackedPoseDriver.TrackingType.RotationAndPosition;
            tpd.updateType = TrackedPoseDriver.UpdateType.UpdateAndBeforeRender;

            var pos = new InputAction("Position", InputActionType.Value,
                "<XRController>{" + hand + "}/devicePosition", expectedControlType: "Vector3");
            pos.AddBinding("<XRController>{" + hand + "}/pointerPosition");

            var rot = new InputAction("Rotation", InputActionType.Value,
                "<XRController>{" + hand + "}/deviceRotation", expectedControlType: "Quaternion");
            rot.AddBinding("<XRController>{" + hand + "}/pointerRotation");

            tpd.positionInput = new InputActionProperty(pos);
            tpd.rotationInput = new InputActionProperty(rot);
            pos.Enable();
            rot.Enable();
            ownedActions.Add(pos);
            ownedActions.Add(rot);

            // Visuals live under a child so they can be hidden as one unit.
            var visuals = new GameObject("Visuals").transform;
            visuals.SetParent(root, false);
            BuildBody(visuals);
            BuildRay(visuals);

            return root;
        }

        // Dark grey capsule that lies along the controller's forward, with a slight tilt.
        private void BuildBody(Transform visuals)
        {
            GameObject body = GameObject.CreatePrimitive(PrimitiveType.Capsule);
            body.name = "Body";
            Destroy(body.GetComponent<Collider>());   // visuals only, no physics

            body.transform.SetParent(visuals, false);
            body.transform.localScale = new Vector3(0.035f, 0.05f, 0.035f);
            // Capsule axis is Y by default. Rotating 90 on X lays it along Z (forward), 10 more for a tilt.
            body.transform.localRotation = Quaternion.Euler(80f, 0f, 0f);

            if (bodyMaterial != null)
            {
                body.GetComponent<Renderer>().sharedMaterial = bodyMaterial;
            }
        }

        // Thin laser from the controller forward for RayLength metres, fading out at the end.
        private void BuildRay(Transform visuals)
        {
            var rayObject = new GameObject("Ray");
            rayObject.transform.SetParent(visuals, false);

            var line = rayObject.AddComponent<LineRenderer>();
            line.useWorldSpace = false;   // local space, so the ray follows the controller
            line.positionCount = 2;
            line.SetPosition(0, Vector3.zero);
            line.SetPosition(1, Vector3.forward * RayLength);
            line.startWidth = RayWidth;
            line.endWidth = RayWidth;
            line.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            line.receiveShadows = false;

            // Gradient: semi-opaque at the controller, fully transparent at the far end.
            var gradient = new Gradient();
            gradient.SetKeys(
                new[]
                {
                    new GradientColorKey(RayGradientTint, 0f),
                    new GradientColorKey(RayGradientTint, 1f)
                },
                new[]
                {
                    new GradientAlphaKey(0.6f, 0f),
                    new GradientAlphaKey(0f, 1f)
                });
            line.colorGradient = gradient;

            if (rayMaterial != null)
            {
                line.sharedMaterial = rayMaterial;
            }
        }

        // ---- Tracking ----

        // Reads the controller's isTracked button each frame (see Update).
        private InputAction MakeTrackedAction(string hand)
        {
            var action = new InputAction("IsTracked", InputActionType.Button,
                "<XRController>{" + hand + "}/isTracked");
            action.Enable();
            ownedActions.Add(action);
            return action;
        }

        private static void SetVisualsActive(Transform visuals, bool active)
        {
            if (visuals != null && visuals.gameObject.activeSelf != active)
            {
                visuals.gameObject.SetActive(active);
            }
        }

        // ---- Materials ----

        // Unlit material with the given colour. Uses URP Unlit, falls back to Unlit/Color.
        // Transparent materials use Sprites/Default: vertex-colour alpha blending with no
        // shader keywords, so the ray's fade survives variant stripping in the build.
        private static Material CreateMaterial(Color color, bool transparent)
        {
            if (transparent)
            {
                Shader sprite = Shader.Find("Sprites/Default");
                if (sprite != null) return new Material(sprite) { color = color };
            }

            Shader shader = Shader.Find("Universal Render Pipeline/Unlit");
            if (shader == null)
            {
                shader = Shader.Find("Unlit/Color");
            }
            if (shader == null)
            {
                Debug.LogWarning("[ControllerVisuals] No Unlit shader found. Controller visuals use default materials.");
                return null;
            }

            var material = new Material(shader);
            material.SetColor("_BaseColor", color);
            material.color = color;

            // Transparent setup for URP Unlit so the ray's alpha gradient shows.
            if (transparent)
            {
                material.SetFloat("_Surface", 1f);
                material.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.SrcAlpha);
                material.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
                material.SetInt("_ZWrite", 0);
                material.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
                material.renderQueue = (int)UnityEngine.Rendering.RenderQueue.Transparent;
            }

            return material;
        }
    }
}
