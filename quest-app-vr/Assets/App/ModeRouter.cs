using System;
using System.Collections;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.XR.Management;

namespace MoviePlayer.App
{
    /// <summary>
    /// Scene 0 is the 2D panel. The immersive activity is a second process whose
    /// current activity is TheaterActivity; this router starts OpenXR and loads
    /// the VR scene. The panel process never starts XR, so it needs no headset tracking.
    /// </summary>
    [DefaultExecutionOrder(-5000)]
    public class ModeRouter : MonoBehaviour
    {
        public static bool TheaterSession { get; private set; }
        bool _routing;

        void Awake()
        {
            if (IsTheaterActivity())
                TryRoute();
            else
                StartCoroutine(ReleaseImmersiveDisplay());
        }

        void Start() => TryRoute();

        /// <summary>
        /// The panel process must not keep an OpenXR session. XR plug-in management
        /// can claim the display before the first scene, which leaves Quest's 2D
        /// window black. XR starts later, only from the VR theater action.
        /// </summary>
        IEnumerator ReleaseImmersiveDisplay()
        {
            for (int i = 0; i < 10; i++)
            {
                if (IsTheaterActivity())
                {
                    TryRoute();
                    yield break;
                }
                StopImmersiveSession();
                yield return null;
            }
        }

        static void StopImmersiveSession()
        {
            var manager = XRGeneralSettings.Instance != null ? XRGeneralSettings.Instance.Manager : null;
            if (manager == null) return;
            if (!manager.isInitializationComplete && manager.activeLoader == null) return;
            try { manager.StopSubsystems(); }
            catch (Exception e) { Debug.LogWarning("[XR] Panel stop failed: " + e.Message); }
            try { manager.DeinitializeLoader(); }
            catch (Exception e) { Debug.LogWarning("[XR] Panel deinit failed: " + e.Message); }
            Debug.Log("[XR] Immersive session stopped on the 2D panel.");
        }

        void TryRoute()
        {
            if (_routing || TheaterSession) return;
            if (!IsTheaterActivity()) return;
            TheaterSession = true;
            _routing = true;
            var panel = GetComponent<PanelPlayer>();
            if (panel != null) panel.enabled = false;
            StartCoroutine(Boot());
        }

        IEnumerator Boot()
        {
            yield return XRSession.EnsureStarted();
            SceneManager.LoadScene("MainScene");
        }

        public static bool IsTheaterActivity()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            try
            {
                using var player = new AndroidJavaClass("com.unity3d.player.UnityPlayer");
                using var activity = player.GetStatic<AndroidJavaObject>("currentActivity");
                if (activity == null) return false;
                string name = activity.Call<AndroidJavaObject>("getClass").Call<string>("getName");
                if (!string.IsNullOrEmpty(name) && name.IndexOf("TheaterActivity", StringComparison.Ordinal) >= 0)
                    return true;
                using var intent = activity.Call<AndroidJavaObject>("getIntent");
                string mode = intent != null ? intent.Call<string>("getStringExtra", "mp_mode") : null;
                return mode == "theater";
            }
            catch (Exception e)
            {
                Debug.LogWarning("[Mode] Activity check failed: " + e.Message);
                return false;
            }
#else
            return false;
#endif
        }
    }

    public static class XRSession
    {
        public static IEnumerator EnsureStarted()
        {
            var general = XRGeneralSettings.Instance;
            if (general == null || general.Manager == null)
            {
                Debug.LogError("[XR] XR settings are not preloaded. Immersive theater cannot start.");
                yield break;
            }

            var manager = general.Manager;
            if (!manager.isInitializationComplete)
                yield return manager.InitializeLoader();

            if (manager.activeLoader == null)
            {
                Debug.LogError("[XR] OpenXR loader did not start. The 2D panel still plays without a headset.");
                yield break;
            }

            manager.StartSubsystems();
            Debug.Log("[XR] OpenXR session started.");
        }
    }
}
