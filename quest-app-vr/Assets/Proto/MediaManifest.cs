using System;

namespace MoviePlayer.Proto
{
    [Serializable]
    public class MediaManifest
    {
        public string id;
        public string filename;
        /// <summary>Stereo mode: mono | fsbs | hsbs | fou | hou</summary>
        public string stereo;
        /// <summary>Projection: flat | equirect180 | equirect360</summary>
        public string projection;
        public bool eye_swap;
        public bool native_playable;
        /// <summary>native | remux | transcode</summary>
        public string compat_action;
        public int width;
        public int height;
        public float fps;
        public float duration_s;
        public float sample_score;
        public string codec;
        public string hls_path;
        public string thumbs_path;
        /// <summary>null if not baked</summary>
        public string clarity_pack;
        /// <summary>ready | bake_recommended | baking | baked</summary>
        public string clarity_status;
        public AudioTrack[] audio_tracks;
    }

    [Serializable]
    public class AudioTrack
    {
        public int index;
        public int channels;
        public string codec;
        public string language;
    }
}
