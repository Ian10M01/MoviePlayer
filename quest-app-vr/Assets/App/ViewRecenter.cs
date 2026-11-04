using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;
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
        bool _placed;
        Coroutine _smooth;

        private IEnumerator Start()
        {
            yield return null;
            SubsystemManager.GetSubsystems(_subsystems);
            FixCameraHeight();

            // One placement after tracking has a pose. Later recenters are the hold button only.
            yield return new WaitForSeconds(0.5f);
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
            if (!TryTargets(out var contentPos, out var contentRot, out var uiPos, out var uiRot)) return;
            if (!_placed)
            {
                Apply(contentPos, contentRot, uiPos, uiRot);
                _placed = true;
                return;
            }
            if (_smooth != null) StopCoroutine(_smooth);
            _smooth = StartCoroutine(Smooth(contentPos, contentRot, uiPos, uiRot));
        }

        IEnumerator Smooth(Vector3 contentPos, Quaternion contentRot, Vector3 uiPos, Quaternion uiRot)
        {
            Vector3 fromPos = Content != null ? Content.position : contentPos;
            Quaternion fromRot = Content != null ? Content.rotation : contentRot;
            Vector3 fromUi = UiCanvas != null ? UiCanvas.position : uiPos;
            Quaternion fromUiRot = UiCanvas != null ? UiCanvas.rotation : uiRot;
            float t = 0f;
            const float seconds = 0.35f;
            while (t < seconds)
            {
                t += Time.deltaTime;
                float k = Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(t / seconds));
                Apply(
                    Vector3.Lerp(fromPos, contentPos, k),
                    Quaternion.Slerp(fromRot, contentRot, k),
                    Vector3.Lerp(fromUi, uiPos, k),
                    Quaternion.Slerp(fromUiRot, uiRot, k));
                yield return null;
            }
            Apply(contentPos, contentRot, uiPos, uiRot);
            _smooth = null;
        }

        bool TryTargets(out Vector3 contentPos, out Quaternion contentRot, out Vector3 uiPos, out Quaternion uiRot)
        {
            contentPos = default;
            contentRot = Quaternion.identity;
            uiPos = default;
            uiRot = Quaternion.identity;
            var cam = Camera.main;
            if (cam == null) return false;
            var head = cam.transform;
            Vector3 fwd = head.forward;
            fwd.y = 0f;
            if (fwd.sqrMagnitude < 1e-4f) fwd = Vector3.forward;
            fwd.Normalize();
            var yaw = Quaternion.LookRotation(fwd);
            contentPos = head.position;
            contentRot = yaw;
            uiPos = head.position + fwd * UiDistance + Vector3.down * UiDrop;
            uiRot = yaw;
            Debug.Log($"[Recenter] head={head.position} yaw={yaw.eulerAngles.y:F0}");
            return true;
        }

        void Apply(Vector3 contentPos, Quaternion contentRot, Vector3 uiPos, Quaternion uiRot)
        {
            if (Content != null) Content.SetPositionAndRotation(contentPos, contentRot);
            if (UiCanvas != null) UiCanvas.SetPositionAndRotation(uiPos, uiRot);
        }
    }

    /// <summary>Hold the button briefly. A tap does not snap the screen.</summary>
    public class HoldToRecenter : MonoBehaviour, IPointerDownHandler, IPointerUpHandler
    {
        public ViewRecenter Target;
        public float Seconds = 0.55f;
        float _held;
        bool _down;
        bool _fired;

        public void OnPointerDown(PointerEventData eventData)
        {
            _down = true;
            _fired = false;
            _held = 0f;
        }

        public void OnPointerUp(PointerEventData eventData) => _down = false;

        void Update()
        {
            if (!_down || _fired) return;
            _held += Time.unscaledDeltaTime;
            if (_held < Seconds) return;
            _fired = true;
            Target?.Recenter();
        }
    }
}
