using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using UnityEngine;
using UnityEngine.Networking;

namespace MoviePlayer.Player
{
    /// <summary>
    /// Offline playback for files Android MediaPlayer cannot decode.
    /// A bundled static ffmpeg probes the file, then either plays it directly,
    /// copies the video and transcodes the chosen audio to AAC, or transcodes
    /// the video to H.264. Results are cached under persistentDataPath.
    /// </summary>
    public static class OnDeviceTranscoder
    {
        public const string BundledRelative = "ffmpeg/arm64-v8a/ffmpeg";
        const long BundledBytes = 28118768;

        public class Choice
        {
            public int Ordinal;
            public string Language;
            public string Display;
            public string Codec;
            public int Channels;
            public bool Selected;
        }

        public class ProbeResult
        {
            public string VideoCodec = "";
            public int VideoMap;
            public double DurationSec;
            public readonly List<Choice> Audio = new List<Choice>();
        }

        static readonly Regex StreamRe = new Regex(
            @"Stream #\d+:(\d+)(?:\[[^\]]+\])?(?:\(([^)]+)\))?:\s*(Video|Audio):\s*([^,\s]+)",
            RegexOptions.Compiled);
        static readonly Regex DurationRe = new Regex(
            @"Duration:\s*(\d+):(\d+):(\d+(?:\.\d+)?)",
            RegexOptions.Compiled);
        static readonly Regex OutTimeRe = new Regex(
            @"out_time=(\d+):(\d+):(\d+(?:\.\d+)?)",
            RegexOptions.Compiled);

        static readonly Dictionary<string, string> LanguageNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            {"en","English"},{"eng","English"},
            {"ja","Japanese"},{"jpn","Japanese"},
            {"es","Spanish"},{"spa","Spanish"},
            {"fr","French"},{"fra","French"},{"fre","French"},
            {"de","German"},{"deu","German"},{"ger","German"},
            {"it","Italian"},{"ita","Italian"},
            {"pt","Portuguese"},{"por","Portuguese"},
            {"ru","Russian"},{"rus","Russian"},
            {"ko","Korean"},{"kor","Korean"},
            {"zh","Chinese"},{"zho","Chinese"},{"chi","Chinese"},{"cmn","Chinese"},{"yue","Cantonese"},
            {"ar","Arabic"},{"ara","Arabic"},
            {"hi","Hindi"},{"hin","Hindi"},
            {"th","Thai"},{"tha","Thai"},
            {"vi","Vietnamese"},{"vie","Vietnamese"},
            {"pl","Polish"},{"pol","Polish"},
            {"nl","Dutch"},{"nld","Dutch"},{"dut","Dutch"},
            {"sv","Swedish"},{"swe","Swedish"},
            {"no","Norwegian"},{"nor","Norwegian"},{"nb","Norwegian"},{"nn","Norwegian"},
            {"da","Danish"},{"dan","Danish"},
            {"fi","Finnish"},{"fin","Finnish"},
            {"tr","Turkish"},{"tur","Turkish"},
            {"he","Hebrew"},{"heb","Hebrew"},
            {"cs","Czech"},{"ces","Czech"},{"cze","Czech"},
            {"hu","Hungarian"},{"hun","Hungarian"},
            {"ro","Romanian"},{"ron","Romanian"},{"rum","Romanian"},
            {"uk","Ukrainian"},{"ukr","Ukrainian"},
            {"el","Greek"},{"ell","Greek"},{"gre","Greek"},
            {"id","Indonesian"},{"ind","Indonesian"},
            {"ms","Malay"},{"msa","Malay"},{"may","Malay"},
            {"fa","Persian"},{"fas","Persian"},{"per","Persian"},
        };

#if UNITY_ANDROID && !UNITY_EDITOR
        static EncodeJob _job;
#endif

        public static bool OnQuest
        {
            get
            {
#if UNITY_ANDROID && !UNITY_EDITOR
                return true;
#else
                return false;
#endif
            }
        }

        public static bool IsLocalFile(string url)
        {
            if (string.IsNullOrEmpty(url)) return false;
            if (url.StartsWith("file:", StringComparison.OrdinalIgnoreCase)) return true;
            if (url.StartsWith("/", StringComparison.Ordinal)) return true;
            return false;
        }

        public static string ToFilesystemPath(string url)
        {
            if (string.IsNullOrEmpty(url)) return url;
            if (url.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
            {
                try { return new Uri(url).LocalPath; }
                catch { return url.Substring(url.IndexOf("://", StringComparison.Ordinal) + 3); }
            }
            return url;
        }

        public static int ChooseDefault(List<Choice> audio)
        {
            if (audio == null || audio.Count == 0) return 0;
            string pref = PlayerPrefs.GetString("audio.lang", "");
            if (!string.IsNullOrEmpty(pref))
            {
                for (int i = 0; i < audio.Count; i++)
                    if (SameLanguage(audio[i].Language, pref)) return i;
            }
            for (int i = 0; i < audio.Count; i++)
                if (SameLanguage(audio[i].Language, "en")) return i;
            return 0;
        }

        public static void Cancel()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            _job?.Kill();
            _job = null;
#endif
        }

        public static IEnumerator EnsureBinary(Action<string> onError)
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            string stored = Path.Combine(Application.persistentDataPath, "bin", "ffmpeg");
            Directory.CreateDirectory(Path.GetDirectoryName(stored));
            if (!File.Exists(stored) || new FileInfo(stored).Length != BundledBytes)
            {
                string src = Application.streamingAssetsPath + "/" + BundledRelative;
                using (var req = UnityWebRequest.Get(src))
                {
                    yield return req.SendWebRequest();
                    if (req.result != UnityWebRequest.Result.Success || req.downloadHandler.data == null)
                    {
                        onError?.Invoke("Could not unpack ffmpeg: " + req.error);
                        yield break;
                    }
                    File.WriteAllBytes(stored, req.downloadHandler.data);
                }
            }
            MakeExecutable(stored);
            string exec = CopyToExecutableDir(stored);
            MakeExecutable(exec);
            ExecutablePath = exec;
            if (string.IsNullOrEmpty(ExecutablePath) || !File.Exists(ExecutablePath))
                onError?.Invoke("ffmpeg did not extract.");
#else
            onError?.Invoke("On-device ffmpeg runs on the Quest.");
            yield break;
#endif
        }

        public static string ExecutablePath { get; private set; }

        public static IEnumerator Probe(string path, Action<ProbeResult> onOk, Action<string> onError)
        {
            var lines = new List<string>();
            int code = -1;
            yield return Run(new[] { ExecutablePath, "-hide_banner", "-i", path }, lines.Add, c => code = c, () => false);
            var result = ParseProbe(path, lines);
            if (string.IsNullOrEmpty(result.VideoCodec) && result.Audio.Count == 0)
            {
                onError?.Invoke("Could not read streams in " + Path.GetFileName(path));
                yield break;
            }
            _ = code; // ffmpeg exits 1 when used only to probe
            onOk?.Invoke(result);
        }

        public static IEnumerator PreparePlayback(
            string path,
            ProbeResult probe,
            int ordinal,
            Action<string, float> progress,
            Action<string> onPlayable,
            Action<string> onError,
            Func<bool> cancelled)
        {
            if (probe == null) { onError?.Invoke("Nothing to play."); yield break; }
            Choice audio = ordinal >= 0 && ordinal < probe.Audio.Count ? probe.Audio[ordinal] : null;
            string ext = Path.GetExtension(path).ToLowerInvariant();
            bool containerDirect = ext == ".mp4" || ext == ".m4v" || ext == ".mov";
            bool videoHw = IsHwVideo(probe.VideoCodec);
            bool audioDirect = audio == null || IsDirectAudio(audio.Codec);

            if (videoHw && audioDirect && containerDirect)
            {
                progress?.Invoke("Preparing audio…", 1f);
                onPlayable?.Invoke(path);
                yield break;
            }

            bool full = !videoHw;
            string label = full ? "Converting…" : "Preparing audio…";
            long mtime = 0;
            try { mtime = File.GetLastWriteTimeUtc(path).Ticks; } catch { mtime = 0; }
            string key = Hash(path + "|" + mtime + "|" + ordinal + "|" + (full ? "v" : "a"));
            string dir = Path.Combine(Application.persistentDataPath, "cache");
            Directory.CreateDirectory(dir);
            string dest = Path.Combine(dir, key + ".mp4");
            if (File.Exists(dest) && new FileInfo(dest).Length > 4096)
            {
                progress?.Invoke(label, 1f);
                onPlayable?.Invoke(dest);
                yield break;
            }

            string part = dest + "." + Guid.NewGuid().ToString("N") + ".part";
            bool surround = audio != null && audio.Channels > 2;
            bool ok = false;
            string err = null;
            if (surround)
            {
                yield return Encode(path, probe.VideoMap, ordinal, part, full, 6, probe.DurationSec, label, progress, cancelled, success => ok = success, e => err = e);
                if (cancelled()) yield break;
            }
            if (!ok)
            {
                if (File.Exists(part)) try { File.Delete(part); } catch { /* retry clean */ }
                yield return Encode(path, probe.VideoMap, ordinal, part, full, 2, probe.DurationSec, label, progress, cancelled, success => ok = success, e => err = e);
            }
            if (cancelled()) yield break;
            if ((!ok || !File.Exists(part) || new FileInfo(part).Length < 4096) && !full)
            {
                if (File.Exists(part)) try { File.Delete(part); } catch { /* leftover */ }
                full = true;
                label = "Converting…";
                key = Hash(path + "|" + mtime + "|" + ordinal + "|v");
                dest = Path.Combine(dir, key + ".mp4");
                if (File.Exists(dest) && new FileInfo(dest).Length > 4096)
                {
                    progress?.Invoke(label, 1f);
                    onPlayable?.Invoke(dest);
                    yield break;
                }
                part = dest + "." + Guid.NewGuid().ToString("N") + ".part";
                ok = false;
                err = null;
                yield return Encode(path, probe.VideoMap, ordinal, part, true, 2, probe.DurationSec, label, progress, cancelled, success => ok = success, e => err = e);
            }
            if (cancelled()) yield break;
            if (!ok || !File.Exists(part) || new FileInfo(part).Length < 4096)
            {
                if (File.Exists(part)) try { File.Delete(part); } catch { /* leftover */ }
                onError?.Invoke(string.IsNullOrEmpty(err) ? "Conversion failed." : err);
                yield break;
            }
            if (File.Exists(dest)) try { File.Delete(dest); } catch { /* replace */ }
            File.Move(part, dest);
            progress?.Invoke(label, 1f);
            onPlayable?.Invoke(dest);
        }

        static IEnumerator Encode(
            string path, int videoMap, int ordinal, string part, bool full, int channels, double duration,
            string label, Action<string, float> progress, Func<bool> cancelled,
            Action<bool> onOk, Action<string> onError)
        {
            var args = new List<string>
            {
                ExecutablePath, "-y", "-hide_banner", "-progress", "pipe:1", "-nostats",
                "-i", path, "-map", "0:v:" + videoMap.ToString(CultureInfo.InvariantCulture)
            };
            if (ordinal >= 0)
                args.AddRange(new[] { "-map", "0:a:" + ordinal.ToString(CultureInfo.InvariantCulture) });
            else
                args.Add("-an");

            if (full)
            {
                args.AddRange(new[]
                {
                    "-c:v", "libx264", "-preset", "veryfast", "-crf", "20",
                    "-profile:v", "high", "-pix_fmt", "yuv420p",
                    "-vf", "scale=min(3840\\,iw):-2"
                });
            }
            else args.AddRange(new[] { "-c:v", "copy" });

            if (ordinal >= 0)
            {
                args.AddRange(new[]
                {
                    "-c:a", "aac", "-profile:a", "aac_low",
                    "-b:a", channels > 2 ? "384k" : "192k",
                    "-ac", channels.ToString(CultureInfo.InvariantCulture),
                    "-ar", "48000"
                });
            }
            args.AddRange(new[] { "-movflags", "+faststart", part });

            var tail = new Queue<string>();
            int code = -1;
            yield return Run(args.ToArray(), line =>
            {
                tail.Enqueue(line);
                while (tail.Count > 8) tail.Dequeue();
                var match = OutTimeRe.Match(line);
                if (!match.Success || duration <= 0.5) return;
                double sec = int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture) * 3600
                    + int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture) * 60
                    + double.Parse(match.Groups[3].Value, CultureInfo.InvariantCulture);
                progress?.Invoke(label, Mathf.Clamp01((float)(sec / duration)));
            }, c => code = c, cancelled);

            if (cancelled != null && cancelled()) { onOk?.Invoke(false); yield break; }
            if (code != 0)
            {
                onError?.Invoke(tail.Count > 0 ? string.Join(" ", tail) : "ffmpeg exited " + code);
                onOk?.Invoke(false);
                yield break;
            }
            onOk?.Invoke(true);
        }

        static ProbeResult ParseProbe(string path, List<string> lines)
        {
            var result = new ProbeResult();
            var text = string.Join("\n", lines);
            var dur = DurationRe.Match(text);
            if (dur.Success)
            {
                result.DurationSec = int.Parse(dur.Groups[1].Value, CultureInfo.InvariantCulture) * 3600
                    + int.Parse(dur.Groups[2].Value, CultureInfo.InvariantCulture) * 60
                    + double.Parse(dur.Groups[3].Value, CultureInfo.InvariantCulture);
            }

            int videoSeen = 0;
            int ordinal = 0;
            foreach (Match match in StreamRe.Matches(text))
            {
                string kind = match.Groups[3].Value;
                string codec = match.Groups[4].Value.ToLowerInvariant();
                if (kind == "Video")
                {
                    bool attachment = codec == "mjpeg" || codec == "png" || codec == "gif"
                        || match.Value.IndexOf("attached pic", StringComparison.OrdinalIgnoreCase) >= 0;
                    if (!attachment && string.IsNullOrEmpty(result.VideoCodec))
                    {
                        result.VideoCodec = codec;
                        result.VideoMap = videoSeen;
                    }
                    videoSeen++;
                    continue;
                }
                string lang = match.Groups[2].Success ? match.Groups[2].Value.Trim().ToLowerInvariant() : "";
                int channels = ChannelsIn(match.Value);
                result.Audio.Add(new Choice
                {
                    Ordinal = ordinal,
                    Language = lang,
                    Codec = codec,
                    Channels = channels,
                    Display = PrettyLanguage(lang, ordinal)
                });
                ordinal++;
            }
            Disambiguate(result.Audio);
            _ = path;
            return result;
        }

        static void Disambiguate(List<Choice> audio)
        {
            var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var choice in audio)
            {
                counts.TryGetValue(choice.Display, out int n);
                counts[choice.Display] = n + 1;
            }
            var seen = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var choice in audio)
            {
                if (counts[choice.Display] < 2) continue;
                seen.TryGetValue(choice.Display, out int n);
                n++;
                seen[choice.Display] = n;
                choice.Display = choice.Display + " " + n;
            }
        }

        static int ChannelsIn(string line)
        {
            if (line.Contains("7.1")) return 8;
            if (line.Contains("6.1")) return 7;
            if (line.Contains("5.1") || line.Contains("5.0")) return 6;
            if (line.IndexOf("mono", StringComparison.OrdinalIgnoreCase) >= 0) return 1;
            var match = Regex.Match(line, @"(\d+)\s+channels");
            if (match.Success && int.TryParse(match.Groups[1].Value, out int n)) return n;
            return 2;
        }

        public static string PrettyLanguage(string code, int ordinal)
        {
            if (string.IsNullOrEmpty(code) || code == "und" || code == "unk")
                return "Track " + (ordinal + 1);
            if (LanguageNames.TryGetValue(code, out string name)) return name;
            return code.Length <= 3 ? code.ToUpperInvariant() : code;
        }

        static bool SameLanguage(string a, string b)
        {
            if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b)) return false;
            if (string.Equals(a, b, StringComparison.OrdinalIgnoreCase)) return true;
            string na = PrettyLanguage(a, 0);
            string nb = PrettyLanguage(b, 0);
            return string.Equals(na, nb, StringComparison.OrdinalIgnoreCase);
        }

        static bool IsHwVideo(string codec)
        {
            codec = (codec ?? "").ToLowerInvariant();
            return codec == "h264" || codec == "avc" || codec == "hevc" || codec == "h265";
        }

        static bool IsDirectAudio(string codec)
        {
            codec = (codec ?? "").ToLowerInvariant();
            return codec == "aac" || codec == "mp3" || codec == "mp4a";
        }

        static string Hash(string text)
        {
            using var sha = SHA256.Create();
            byte[] bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(text));
            var sb = new StringBuilder(16);
            for (int i = 0; i < 8; i++) sb.Append(bytes[i].ToString("x2"));
            return sb.ToString();
        }

        static IEnumerator Run(string[] args, Action<string> onLine, Action<int> onExit, Func<bool> cancelled)
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            Cancel();
            var job = new EncodeJob();
            _job = job;
            job.Start(args);
            while (!job.Done)
            {
                if (cancelled != null && cancelled())
                {
                    job.Kill();
                    onExit?.Invoke(-1);
                    yield break;
                }
                while (job.Lines.TryDequeue(out string line)) onLine?.Invoke(line);
                yield return null;
            }
            while (job.Lines.TryDequeue(out string rest)) onLine?.Invoke(rest);
            onExit?.Invoke(job.ExitCode);
#else
            onExit?.Invoke(-1);
            yield break;
#endif
        }

#if UNITY_ANDROID && !UNITY_EDITOR
        static void MakeExecutable(string path)
        {
            try
            {
                using var file = new AndroidJavaObject("java.io.File", path);
                file.Call<bool>("setExecutable", true, false);
            }
            catch (Exception e) { Debug.LogWarning("[ffmpeg] setExecutable: " + e.Message); }
            try
            {
                using var runtime = new AndroidJavaClass("java.lang.Runtime");
                using var proc = runtime.CallStatic<AndroidJavaObject>("getRuntime")
                    .Call<AndroidJavaObject>("exec", new[] { "chmod", "755", path });
                proc.Call<int>("waitFor");
            }
            catch (Exception e) { Debug.LogWarning("[ffmpeg] chmod: " + e.Message); }
        }

        static string CopyToExecutableDir(string stored)
        {
            try
            {
                using var player = new AndroidJavaClass("com.unity3d.player.UnityPlayer");
                using var activity = player.GetStatic<AndroidJavaObject>("currentActivity");
                using var cache = activity.Call<AndroidJavaObject>("getCodeCacheDir");
                string dir = cache.Call<string>("getAbsolutePath");
                string dest = Path.Combine(dir, "ffmpeg");
                File.Copy(stored, dest, true);
                return dest;
            }
            catch (Exception e)
            {
                Debug.LogWarning("[ffmpeg] code cache copy failed, using app storage: " + e.Message);
                return stored;
            }
        }

        sealed class EncodeJob
        {
            public readonly System.Collections.Concurrent.ConcurrentQueue<string> Lines =
                new System.Collections.Concurrent.ConcurrentQueue<string>();
            public int ExitCode = -1;
            public volatile bool Done;
            AndroidJavaObject _process;
            readonly object _gate = new object();

            public void Start(string[] args)
            {
                var thread = new Thread(() =>
                {
                    AndroidJNI.AttachCurrentThread();
                    try { Exec(args); }
                    catch (Exception e)
                    {
                        Lines.Enqueue(e.Message);
                        ExitCode = -1;
                    }
                    finally
                    {
                        Done = true;
                        AndroidJNI.DetachCurrentThread();
                    }
                });
                thread.IsBackground = true;
                thread.Start();
            }

            void Exec(string[] args)
            {
                using var list = new AndroidJavaObject("java.util.ArrayList");
                foreach (var arg in args) list.Call<bool>("add", arg);
                using var builder = new AndroidJavaObject("java.lang.ProcessBuilder");
                builder.Call<AndroidJavaObject>("command", list);
                builder.Call<AndroidJavaObject>("redirectErrorStream", true);
                AndroidJavaObject process = builder.Call<AndroidJavaObject>("start");
                lock (_gate) _process = process;
                using var input = process.Call<AndroidJavaObject>("getInputStream");
                using var reader = new AndroidJavaObject("java.io.InputStreamReader", input);
                using var buffered = new AndroidJavaObject("java.io.BufferedReader", reader);
                while (true)
                {
                    string line = buffered.Call<string>("readLine");
                    if (line == null) break;
                    Lines.Enqueue(line);
                }
                ExitCode = process.Call<int>("waitFor");
            }

            public void Kill()
            {
                lock (_gate)
                {
                    try { _process?.Call("destroy"); } catch { /* already gone */ }
                }
            }
        }
#endif
    }
}
