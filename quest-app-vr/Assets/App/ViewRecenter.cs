using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.XR;

namespace MoviePlayer.App
{
    /// <summary>
    /// Keeps the cinema screen and menu in front of the viewer.
    /// - Zeroes the camera offset when the runtime tracks from the floor, so eye height is the real height.
    /// - Places content and UI in front of the head at startup, on app focus and on the Quest's recenter.
    /// </summary>
    public class ViewRecenter : MonoBehaviour
    {
        public Transform Content;    // screen + theater, positioned at the viewer facing their yaw
        public Transform UiCanvas;   // world-space menu, placed UiDistance in front of the viewer
        public float UiDistance = 2f;
        public float UiDrop     = 0.15f;   // slightly below eye level is more comfortable

        private readonly List<XRInputSubsystem> _subsystems = new List<XRInputSubsystem>();

        private IEnumerator Start()
        {
            yield return null;
            SubsystemManager.GetSubsystems(_subsystems);
            foreach (var s in _subsystems)
            {
                s.trackingOriginUpdated -= OnTrackingOriginUpdated;
                s.trackingOriginUpdated += OnTrackingOriginUpdated;
            }
            FixCameraHeight();

            // Give head tracking a moment to report a real pose before placing anything.
            yield return new WaitForSeconds(0.5f);
            Recenter();
        }

        private void OnDestroy()
        {
            foreach (var s in _subsystems)
                if (s != null) s.trackingOriginUpdated -= OnTrackingOriginUpdated;
        }

        private void OnApplicationFocus(bool hasFocus)
        {
            if (hasFocus && isActiveAndEnabled) StartCoroutine(RecenterNextFrame());
        }

        // Fired by the Quest's recenter (hold the Meta button / "Reset view").
        private void OnTrackingOriginUpdated(XRInputSubsystem subsystem)
        {
            if (isActiveAndEnabled) StartCoroutine(RecenterNextFrame());
        }

        private IEnumerator RecenterNextFrame()
        {
            yield return null;
            Recenter();
        }

        private void FixCameraHeight()
        {
            var cam = Camera.main;
            if (cam == null || cam.transform.parent == null) return;

            bool floor = false;
            foreach (var s in _subsystems)
                if ((s.GetTrackingOriginMode() & TrackingOriginModeFlags.Floor) != 0) floor = true;

            var offset = cam.transform.parent;
            var p = offset.localPosition;
            p.y = floor ? 0f : 1.6f;
            offset.localPosition = p;
            Debug.Log($"[Recenter] Tracking origin {(floor ? "Floor" : "Device")} → camera offset y={p.y}");
        }

        public void Recenter()
        {
            var cam = Camera.main;
            if (cam == null) return;

            var head = cam.transform;
            Vector3 fwd = head.forward;
            fwd.y = 0f;
            if (fwd.sqrMagnitude < 1e-4f) fwd = Vector3.forward;
            fwd.Normalize();
            var yaw = Quaternion.LookRotation(fwd);

            if (Content != null)
                Content.SetPositionAndRotation(head.position, yaw);
            if (UiCanvas != null)
                UiCanvas.SetPositionAndRotation(head.position + fwd * UiDistance + Vector3.down * UiDrop, yaw);

            Debug.Log($"[Recenter] head={head.position} yaw={yaw.eulerAngles.y:F0}");
        }
    }
}
