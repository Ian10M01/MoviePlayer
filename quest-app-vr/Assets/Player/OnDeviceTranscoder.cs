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
        /// <summary>The static ffmpeg executable, packaged as a "native library" so Android lets it run.</summary>
        public const string NativeLibName = "libffmpeg.so";
        public const string DamagedPrefix = "Can't play: ";

        public class Choice
        {
            public int Ordinal;
            public string Language;
            public string Display;
            public string Codec;
            public int Channels;
            public bool Selected;
        }

        public enum Plan
        {
            Direct,
            AudioOnly,
            Full
        }

        public class ProbeResult
        {
            public string VideoCodec = "";
            public int VideoMap;
            public int Width;
            public int Height;
            public double DurationSec;
            public string StereoTag = "";
            public string ProjectionTag = "";
            public bool Spherical;
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

        /// <summary>file:// URL with spaces escaped so Android VideoPlayer can open the path.</summary>
        public static string ToFileUrl(string filesystemPath)
        {
            if (string.IsNullOrEmpty(filesystemPath)) return filesystemPath;
            if (filesystemPath.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
                filesystemPath = ToFilesystemPath(filesystemPath);
            string path = filesystemPath.Replace('\\', '/');
#pragma warning disable SYSLIB0013
            string escaped = Uri.EscapeUriString(path);
#pragma warning restore SYSLIB0013
            return "file://" + escaped;
        }

        public static string EscapeFileUrl(string url)
        {
            if (string.IsNullOrEmpty(url) || !url.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
                return url;
            return ToFileUrl(url);
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
            // Android 10+ (targetSdk >= 29) refuses to exec anything the app wrote to its own
            // storage (error=13). ffmpeg therefore ships as lib/arm64-v8a/libffmpeg.so; with
            // extractNativeLibs=true the installer unpacks it to nativeLibraryDir, which apps
            // may execute from.
            ExecutablePath = null;
            try
            {
                using var player = new AndroidJavaClass("com.unity3d.player.UnityPlayer");
                using var activity = player.GetStatic<AndroidJavaObject>("currentActivity");
                using var info = activity.Call<AndroidJavaObject>("getApplicationInfo");
                string libDir = info.Get<string>("nativeLibraryDir");
                string candidate = Path.Combine(libDir, NativeLibName);
                if (File.Exists(candidate)) ExecutablePath = candidate;
                else Debug.LogError("[ffmpeg] " + candidate + " missing; check the APK has lib/arm64-v8a/" + NativeLibName);
            }
            catch (Exception e) { Debug.LogError("[ffmpeg] nativeLibraryDir lookup failed: " + e.Message); }
            if (string.IsNullOrEmpty(ExecutablePath))
                onError?.Invoke("ffmpeg is not installed with the app.");
            yield break;
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
            Debug.Log($"[ffmpeg] probe exit={code} lines={lines.Count} first={(lines.Count > 0 ? lines[0] : "-")}");
            var result = ParseProbe(path, lines);
            if (string.IsNullOrEmpty(result.VideoCodec) && result.Audio.Count == 0)
            {
                bool damaged = lines.Exists(l => l.Contains("moov atom not found") || l.Contains("Invalid data found"));
                onError?.Invoke(damaged
                    ? DamagedPrefix + Path.GetFileName(path) + " is incomplete or damaged. Download it again."
                    : "Could not read streams in " + Path.GetFileName(path));
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
            Func<bool> cancelled,
            Plan? force = null)
        {
            if (probe == null) { onError?.Invoke("Nothing to play."); yield break; }
            // Stereo packing (SBS / OU / 180 / 360) is not a reason to convert.
            Plan plan = force ?? ChoosePlan(path, probe, ordinal);
            LogPlan(plan, path, probe, ordinal);
            if (plan == Plan.Direct)
            {
                // A cached transcode from an older build is ignored. Play the original.
                onPlayable?.Invoke(path);
                yield break;
            }

            bool full = plan == Plan.Full;
            string label = full ? "Converting…" : "Converting audio…";
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
            bool ok = false;
            string err = null;
            yield return Encode(path, probe.VideoMap, ordinal, part, full, probe.DurationSec, label, progress, cancelled, success => ok = success, e => err = e);
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

        public static Plan ChoosePlan(string path, ProbeResult probe, int ordinal)
        {
            if (probe == null) return Plan.Full;
            Choice audio = AudioAt(probe, ordinal);
            bool videoHw = IsHwVideo(probe.VideoCodec);
            bool audioDirect = audio == null || IsDirectAudio(audio.Codec);
            string ext = Ext(path);
            bool safeContainer = ext == ".mp4" || ext == ".m4v" || ext == ".mov" || ext == ".3gp";
            bool tryLoose = ext == ".mkv" || ext == ".webm";
            if (videoHw && audioDirect && (safeContainer || tryLoose)) return Plan.Direct;
            if (videoHw) return Plan.AudioOnly;
            return Plan.Full;
        }

        public static void LogPlan(Plan plan, string path, ProbeResult probe, int ordinal)
        {
            string video = string.IsNullOrEmpty(probe?.VideoCodec) ? "unknown" : probe.VideoCodec;
            Choice audio = AudioAt(probe, ordinal);
            string audioCodec = audio == null ? "none" : (string.IsNullOrEmpty(audio.Codec) ? "unknown" : audio.Codec);
            string kind = plan == Plan.Direct ? "direct play"
                : plan == Plan.AudioOnly ? "convert audio only"
                : "convert video+audio";
            string container = Ext(path).TrimStart('.');
            if (container.Length == 0) container = "unknown";
            Debug.Log($"[compat] {kind} video={video} audio={audioCodec} container={container}");
        }

        public static Plan FallbackPlan(Plan current, string path, ProbeResult probe)
        {
            if (current == Plan.Full) return Plan.Full;
            if (current == Plan.AudioOnly) return Plan.Full;
            string codec = probe != null ? probe.VideoCodec : "";
            if (IsHevc(codec) || !IsHwVideo(codec)) return Plan.Full;
            string ext = Ext(path);
            bool safe = ext == ".mp4" || ext == ".m4v" || ext == ".mov" || ext == ".3gp";
            return safe ? Plan.Full : Plan.AudioOnly;
        }

        static IEnumerator Encode(
            string path, int videoMap, int ordinal, string part, bool full, double duration,
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
                    "-b:a", "192k",
                    "-ac", "2",
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
            ReadTags(lines, result);
            _ = path;
            return result;
        }

        static void ReadTags(List<string> lines, ProbeResult result)
        {
            foreach (var line in lines)
            {
                if (line.IndexOf("Stream #", StringComparison.Ordinal) >= 0 &&
                    line.IndexOf("Video:", StringComparison.Ordinal) >= 0 &&
                    result.Width == 0)
                {
                    var size = Regex.Match(line, @"\b(\d{3,5})x(\d{3,5})\b");
                    if (size.Success)
                    {
                        result.Width = int.Parse(size.Groups[1].Value, CultureInfo.InvariantCulture);
                        result.Height = int.Parse(size.Groups[2].Value, CultureInfo.InvariantCulture);
                    }
                }

                var tag = Regex.Match(line,
                    @"(stereo_mode|spherical(?:-video)?|projection(?:-type)?)\s*[:=]\s*(\S+)",
                    RegexOptions.IgnoreCase);
                if (!tag.Success) continue;
                string key = tag.Groups[1].Value.ToLowerInvariant();
                string value = tag.Groups[2].Value.Trim().Trim('"');
                if (key.StartsWith("stereo", StringComparison.Ordinal)) result.StereoTag = value;
                else if (key.StartsWith("spherical", StringComparison.Ordinal))
                    result.Spherical = value != "0" && !value.Equals("false", StringComparison.OrdinalIgnoreCase);
                else if (string.IsNullOrEmpty(result.ProjectionTag)) result.ProjectionTag = value;
            }

            var text = string.Join("\n", lines);
            var side = Regex.Match(text, @"Stereo 3D:\s*([^\r\n]+)", RegexOptions.IgnoreCase);
            if (side.Success && string.IsNullOrEmpty(result.StereoTag))
                result.StereoTag = side.Groups[1].Value.Trim();
            if (text.IndexOf("equirectangular", StringComparison.OrdinalIgnoreCase) >= 0 &&
                string.IsNullOrEmpty(result.ProjectionTag))
                result.ProjectionTag = "equirectangular";
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

        public static bool IsHevc(string codec)
        {
            codec = NormCodec(codec);
            return codec == "hevc" || codec == "h265" || codec == "hvc1" || codec == "hev1";
        }

        public static bool IsHwVideo(string codec)
        {
            codec = NormCodec(codec);
            return codec == "h264" || codec == "avc" || codec == "avc1" || IsHevc(codec);
        }

        public static bool IsDirectAudio(string codec)
        {
            codec = NormCodec(codec);
            return codec == "aac" || codec == "mp3" || codec == "mp4a";
        }

        static string NormCodec(string codec) => (codec ?? "").Trim().ToLowerInvariant();

        static Choice AudioAt(ProbeResult probe, int ordinal)
        {
            if (probe == null || ordinal < 0 || ordinal >= probe.Audio.Count) return null;
            return probe.Audio[ordinal];
        }

        static string Ext(string path)
        {
            try { return Path.GetExtension(path ?? "").ToLowerInvariant(); }
            catch { return ""; }
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
            // 0755. Os.chmod is the reliable path; File and Runtime.exec are backups.
            // A string[] passed to Call is covariant with params object[] and becomes
            // three String arguments, which Unity then invokes as Object.exec.
            try
            {
                using var os = new AndroidJavaClass("android.system.Os");
                os.CallStatic("chmod", path, 493);
            }
            catch (Exception e) { Debug.LogWarning("[ffmpeg] Os.chmod: " + e.Message); }

            try
            {
                using var file = new AndroidJavaObject("java.io.File", path);
                file.Call<bool>("setExecutable", true, false);
                file.Call<bool>("setReadable", true, false);
            }
            catch (Exception e) { Debug.LogWarning("[ffmpeg] setExecutable: " + e.Message); }

            try
            {
                using var runtimeClass = new AndroidJavaClass("java.lang.Runtime");
                using var runtime = runtimeClass.CallStatic<AndroidJavaObject>("getRuntime");
                using var proc = runtime.Call<AndroidJavaObject>("exec", "chmod 755 " + path);
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
