using System;
using System.IO;
using UnityEngine;

namespace MoviePlayer.App
{
    /// <summary>
    /// Playback position shared from the 2D panel process to the immersive theater process.
    /// persistentDataPath is the same directory for both processes.
    /// </summary>
    [Serializable]
    public class PlaybackHandoffData
    {
        public string url;
        public long timeMs;
        public int layout;
        public int projection;
        public int audioTrack;
        public int eyeSwap;
    }

    public static class PlaybackHandoff
    {
        static string FilePath => Path.Combine(Application.persistentDataPath, "playback_handoff.json");

        public static void Write(string url, long timeMs, int layout, int projection, int audioTrack, bool eyeSwap = false)
        {
            var data = new PlaybackHandoffData
            {
                url = url ?? "",
                timeMs = timeMs,
                layout = layout,
                projection = projection,
                audioTrack = audioTrack,
                eyeSwap = eyeSwap ? 1 : 0,
            };
            try
            {
                File.WriteAllText(FilePath, JsonUtility.ToJson(data));
            }
            catch (Exception e)
            {
                Debug.LogWarning("[Handoff] Could not save playback: " + e.Message);
            }
        }

        public static bool TryConsume(out PlaybackHandoffData data)
        {
            data = null;
            try
            {
                if (!File.Exists(FilePath)) return false;
                data = JsonUtility.FromJson<PlaybackHandoffData>(File.ReadAllText(FilePath));
                File.Delete(FilePath);
            }
            catch (Exception e)
            {
                Debug.LogWarning("[Handoff] Could not read playback: " + e.Message);
                return false;
            }
            return data != null && !string.IsNullOrEmpty(data.url);
        }
    }

    public static class TheaterLaunch
    {
        public const string ActivityClass = "com.movieplayer.vrcinema.TheaterActivity";

        /// <summary>Starts the immersive activity. The panel process keeps running.</summary>
        public static bool TryStart()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            try
            {
                using var player = new AndroidJavaClass("com.unity3d.player.UnityPlayer");
                using var activity = player.GetStatic<AndroidJavaObject>("currentActivity");
                using var intent = new AndroidJavaObject("android.content.Intent");
                intent.Call<AndroidJavaObject>("setClassName", activity, ActivityClass);
                intent.Call<AndroidJavaObject>("putExtra", "mp_mode", "theater");
                intent.Call<AndroidJavaObject>("addFlags", 0x10000000); // FLAG_ACTIVITY_NEW_TASK
                activity.Call("startActivity", intent);
                Debug.Log("[Theater] Started " + ActivityClass);
                return true;
            }
            catch (Exception e)
            {
                Debug.LogWarning("[Theater] VR activity failed to start: " + e.Message);
                return false;
            }
#else
            return false;
#endif
        }
    }
}
