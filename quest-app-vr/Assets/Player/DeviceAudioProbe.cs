using System;
using System.Collections.Generic;
using UnityEngine;

namespace MoviePlayer.Player
{
    /// <summary>
    /// Reads audio track mime types from a local file or URL via Android MediaExtractor.
    /// Unity VideoPlayer does not expose the codec. Quest still cannot decode TrueHD,
    /// DTS, FLAC, Opus, Vorbis or PCM inside an MKV; the UI uses this to say so.
    /// </summary>
    public static class DeviceAudioProbe
    {
        public struct Track
        {
            public int index;
            public string mime;
            public string codec;
            public string language;
            public string label;
        }

        public static List<Track> Probe(string url)
        {
            var list = new List<Track>();
#if UNITY_ANDROID && !UNITY_EDITOR
            AndroidJavaObject extractor = null;
            try
            {
                string src = ToSource(url);
                if (string.IsNullOrEmpty(src)) return list;
                extractor = new AndroidJavaObject("android.media.MediaExtractor");
                extractor.Call("setDataSource", src);
                int count = extractor.Call<int>("getTrackCount");
                int audioIndex = 0;
                for (int i = 0; i < count; i++)
                {
                    AndroidJavaObject format = null;
                    try
                    {
                        format = extractor.Call<AndroidJavaObject>("getTrackFormat", i);
                        string mime = ReadString(format, "mime");
                        if (string.IsNullOrEmpty(mime) || !mime.StartsWith("audio/", StringComparison.OrdinalIgnoreCase))
                            continue;
                        string language = ReadString(format, "language");
                        string codec = MimeToCodec(mime);
                        string name = string.IsNullOrEmpty(language) || language == "und"
                            ? $"Track {audioIndex + 1}"
                            : language;
                        list.Add(new Track
                        {
                            index = audioIndex,
                            mime = mime,
                            codec = codec,
                            language = language ?? "",
                            label = $"{name} · {codec}",
                        });
                        audioIndex++;
                    }
                    catch (Exception e)
                    {
                        Debug.LogWarning($"[Audio] Track {i} unreadable: {e.Message}");
                    }
                    finally
                    {
                        format?.Dispose();
                    }
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning("[Audio] MediaExtractor probe failed: " + e.Message);
            }
            finally
            {
                try { extractor?.Call("release"); } catch { /* already released */ }
                extractor?.Dispose();
            }
#endif
            return list;
        }

        public static int PreferIndex(List<Track> tracks)
        {
            if (tracks == null || tracks.Count == 0) return 0;
            int best = 0;
            int bestRank = 99;
            for (int i = 0; i < tracks.Count; i++)
            {
                int rank = Rank(tracks[i].codec);
                if (rank < bestRank)
                {
                    bestRank = rank;
                    best = i;
                }
            }
            return best;
        }

        public static string MimeToCodec(string mime)
        {
            mime = (mime ?? "").ToLowerInvariant();
            if (mime.Contains("mp4a") || mime.Contains("aac")) return "aac";
            if (mime.Contains("mpeg") || mime.Contains("mp3")) return "mp3";
            if (mime.Contains("eac3") || mime.Contains("e-ac-3") || mime.Contains("ec-3")) return "eac3";
            if (mime.Contains("ac3") || mime.Contains("ac-3")) return "ac3";
            if (mime.Contains("truehd") || mime.Contains("true-hd")) return "truehd";
            if (mime.Contains("dts-hd") || mime.Contains("dtshd")) return "dtshd";
            if (mime.Contains("dts")) return "dts";
            if (mime.Contains("flac")) return "flac";
            if (mime.Contains("opus")) return "opus";
            if (mime.Contains("vorbis")) return "vorbis";
            if (mime.Contains("atmos")) return "atmos";
            if (mime.Contains("raw") || mime.Contains("pcm")) return "pcm";
            const string prefix = "audio/";
            return mime.StartsWith(prefix) ? mime.Substring(prefix.Length) : mime;
        }

        static int Rank(string codec)
        {
            switch ((codec ?? "").ToLowerInvariant())
            {
                case "aac":
                case "mp4a": return 0;
                case "mp3": return 1;
                case "ac3": return 2;
                default: return 9;
            }
        }

#if UNITY_ANDROID && !UNITY_EDITOR
        static string ToSource(string url)
        {
            if (string.IsNullOrEmpty(url)) return url;
            if (url.StartsWith("file://", StringComparison.OrdinalIgnoreCase))
            {
                try { return new Uri(url).LocalPath; }
                catch { return url.Substring("file://".Length); }
            }
            return url;
        }

        static string ReadString(AndroidJavaObject format, string key)
        {
            if (format == null) return "";
            try
            {
                if (!format.Call<bool>("containsKey", key)) return "";
                return format.Call<string>("getString", key) ?? "";
            }
            catch { return ""; }
        }
#endif
    }
}
