using System;
using System.IO;
using System.Text.RegularExpressions;
using MoviePlayer.Proto;
using UnityEngine;
using Layout = MoviePlayer.Player.StereoScreenRenderer.StereoLayout;
using Projection = MoviePlayer.Player.StereoScreenRenderer.ProjectionType;

namespace MoviePlayer.Player
{
    /// <summary>
    /// Picks a stereo layout and projection. Callers apply an explicit UI
    /// override first; this class handles manifest, filename, ffprobe tags, then aspect.
    /// </summary>
    public struct StereoGuess
    {
        public Layout Layout;
        public Projection Projection;
        public bool EyeSwap;
        public bool FrameSequential;
        public bool Locked;
        public string Source;

        public static StereoGuess MonoDefault() => new StereoGuess
        {
            Layout = Layout.Mono,
            Projection = Projection.Flat,
            Source = "default"
        };
    }

    public static class StereoDetect
    {
        public static void Log(StereoGuess guess)
        {
            string layout = guess.Layout.ToString();
            string projection = guess.Projection == Projection.Flat ? "" : " projection=" + guess.Projection;
            string note = guess.FrameSequential ? " note=frame-sequential" : "";
            string swap = guess.EyeSwap ? " swap=1" : "";
            Debug.Log($"[stereo] layout={layout}{projection} source={guess.Source}{swap}{note}");
        }

        /// <summary>Manifest, then filename, then probe tags, then aspect. Not the UI override.</summary>
        public static StereoGuess Resolve(string path, MediaManifest manifest, OnDeviceTranscoder.ProbeResult probe)
        {
            if (manifest != null &&
                (!string.IsNullOrWhiteSpace(manifest.stereo) || !string.IsNullOrWhiteSpace(manifest.projection)))
                return FromManifest(manifest);

            var named = FromFilename(path);
            if (named.Locked) return named;

            var tagged = FromProbe(probe);
            if (tagged.Locked) return tagged;

            int width = probe != null ? probe.Width : 0;
            int height = probe != null ? probe.Height : 0;
            return FromAspect(width, height);
        }

        public static StereoGuess FromManifest(MediaManifest manifest)
        {
            var guess = StereoGuess.MonoDefault();
            if (manifest == null) return guess;
            guess.Source = "manifest";
            guess.EyeSwap = manifest.eye_swap;
            if (TryParseLayout(manifest.stereo, out var layout, out bool swap, out bool sequential))
            {
                guess.Layout = layout;
                guess.EyeSwap |= swap;
                guess.FrameSequential = sequential;
                if (sequential) guess.Layout = Layout.Mono;
            }
            if (TryParseProjection(manifest.projection, out var projection))
                guess.Projection = projection;
            guess.Locked = true;
            return guess;
        }

        public static StereoGuess FromFilename(string path)
        {
            var guess = StereoGuess.MonoDefault();
            string name = Normalize(path);
            if (name.Length == 0) return guess;

            bool layoutSet = false;
            bool sequential = name.Contains(" frame sequential ") || name.Contains(" framesequential ");
            if (sequential)
            {
                guess.FrameSequential = true;
                guess.Layout = Layout.Mono;
                layoutSet = true;
            }
            else if (Has(name, "fsbs") || name.Contains(" full sbs ") || Has(name, "fullsbs"))
            {
                guess.Layout = Layout.FSBS;
                layoutSet = true;
            }
            else if (Has(name, "hsbs") || name.Contains(" half sbs ") || Has(name, "sbs") || name.Contains(" side by side "))
            {
                guess.Layout = Layout.HSBS;
                layoutSet = true;
            }
            else if (Has(name, "fou") || name.Contains(" full ou ") || Has(name, "fullou") || Has(name, "ftab"))
            {
                guess.Layout = Layout.FOU;
                layoutSet = true;
            }
            else if (Has(name, "hou") || name.Contains(" half ou ") || Has(name, "ou") || Has(name, "tab")
                     || name.Contains(" over under ") || name.Contains(" top bottom ") || Has(name, "tb"))
            {
                guess.Layout = Layout.HOU;
                layoutSet = true;
            }
            else if (Has(name, "lr"))
            {
                guess.Layout = Layout.HSBS;
                layoutSet = true;
            }
            else if (Has(name, "3d"))
            {
                guess.Layout = Layout.HSBS;
                layoutSet = true;
            }

            bool projectionSet = false;
            if (Has(name, "vr180") || Has(name, "180"))
            {
                guess.Projection = Projection.Equirect180;
                projectionSet = true;
            }
            else if (Has(name, "360"))
            {
                guess.Projection = Projection.Equirect360;
                projectionSet = true;
            }

            if (Has(name, "swap") || Has(name, "eyeswap") || Has(name, "rl"))
                guess.EyeSwap = true;

            guess.Locked = layoutSet || projectionSet || sequential;
            guess.Source = guess.Locked ? "filename" : "default";
            return guess;
        }

        public static StereoGuess FromProbe(OnDeviceTranscoder.ProbeResult probe)
        {
            var guess = StereoGuess.MonoDefault();
            if (probe == null) return guess;

            bool hit = false;
            string mode = (probe.StereoTag ?? "").Trim();
            if (mode.Length > 0)
            {
                hit = true;
                if (int.TryParse(mode, out int mkv))
                    ApplyMatroska(mkv, ref guess);
                else if (TryParseLayout(mode, out var layout, out bool swap, out bool sequential))
                {
                    guess.Layout = sequential ? Layout.Mono : layout;
                    guess.EyeSwap |= swap;
                    guess.FrameSequential = sequential;
                }
                else
                {
                    string lower = mode.ToLowerInvariant();
                    if (lower.Contains("frame") && (lower.Contains("seq") || lower.Contains("alternate")))
                    {
                        guess.FrameSequential = true;
                        guess.Layout = Layout.Mono;
                    }
                    else if (lower.Contains("top") || lower.Contains("over") || lower.Contains("bottom"))
                        guess.Layout = lower.Contains("full") ? Layout.FOU : Layout.HOU;
                    else if (lower.Contains("side") || lower.Contains("left") || lower.Contains("sbs"))
                        guess.Layout = lower.Contains("full") ? Layout.FSBS : Layout.HSBS;
                    else
                        hit = false;
                }
            }

            string projection = (probe.ProjectionTag ?? "").ToLowerInvariant();
            if (projection.Contains("180"))
            {
                guess.Projection = Projection.Equirect180;
                hit = true;
            }
            else if (projection.Contains("equirect") || projection.Contains("360") || projection.Contains("spherical"))
            {
                guess.Projection = Projection.Equirect360;
                hit = true;
            }
            else if (probe.Spherical)
            {
                guess.Projection = Projection.Equirect360;
                hit = true;
            }

            guess.Locked = hit;
            guess.Source = hit ? "tag" : "default";
            return guess;
        }

        /// <summary>Last resort. Never infers 180 or 360 from the frame shape.</summary>
        public static StereoGuess FromAspect(int width, int height)
        {
            var guess = StereoGuess.MonoDefault();
            if (width <= 0 || height <= 0) return guess;
            float wide = (float)width / height;
            float tall = (float)height / width;
            if (wide >= 3.2f)
            {
                guess.Layout = Layout.HSBS;
                guess.Locked = true;
                guess.Source = "aspect";
            }
            else if (tall >= 1.6f && width >= 1920)
            {
                guess.Layout = Layout.HOU;
                guess.Locked = true;
                guess.Source = "aspect";
            }
            return guess;
        }

        public static Rect EyeRect(Layout layout, bool leftEye, bool eyeSwap)
        {
            if (layout == Layout.Mono) return new Rect(0f, 0f, 1f, 1f);
            if (eyeSwap) leftEye = !leftEye;
            bool overUnder = layout == Layout.HOU || layout == Layout.FOU;
            if (overUnder)
                return leftEye ? new Rect(0f, 0.5f, 1f, 0.5f) : new Rect(0f, 0f, 1f, 0.5f);
            return leftEye ? new Rect(0f, 0f, 0.5f, 1f) : new Rect(0.5f, 0f, 0.5f, 1f);
        }

        /// <summary>Aspect of one displayed eye. HSBS/HOU undo the packed squeeze; FSBS/FOU do not.</summary>
        public static float EyeAspect(float frameAspect, Layout layout, bool oneEye)
        {
            if (!oneEye || layout == Layout.Mono) return frameAspect;
            if (layout == Layout.FSBS) return frameAspect * 0.5f;
            if (layout == Layout.FOU) return frameAspect * 2f;
            return frameAspect;
        }

        public static bool TryParseLayout(string text, out Layout layout, out bool eyeSwap, out bool frameSequential)
        {
            layout = Layout.Mono;
            eyeSwap = false;
            frameSequential = false;
            if (string.IsNullOrWhiteSpace(text)) return false;
            string token = text.Trim().ToLowerInvariant().Replace('_', '-').Replace(' ', '-');
            switch (token)
            {
                case "mono":
                case "2d":
                case "none":
                    layout = Layout.Mono;
                    return true;
                case "hsbs":
                case "sbs":
                case "half-sbs":
                case "halfsbs":
                case "3d":
                    layout = Layout.HSBS;
                    return true;
                case "fsbs":
                case "full-sbs":
                case "fullsbs":
                    layout = Layout.FSBS;
                    return true;
                case "hou":
                case "ou":
                case "tab":
                case "half-ou":
                case "over-under":
                    layout = Layout.HOU;
                    return true;
                case "fou":
                case "full-ou":
                case "fullou":
                    layout = Layout.FOU;
                    return true;
                case "frame-sequential":
                case "framesequential":
                case "frame-seq":
                    frameSequential = true;
                    layout = Layout.Mono;
                    return true;
                default:
                    return false;
            }
        }

        public static bool TryParseProjection(string text, out Projection projection)
        {
            projection = Projection.Flat;
            if (string.IsNullOrWhiteSpace(text)) return false;
            string token = text.Trim().ToLowerInvariant().Replace('_', '-').Replace(' ', '-');
            if (token is "180" or "vr180" or "equirect180" or "equirect-180")
            {
                projection = Projection.Equirect180;
                return true;
            }
            if (token is "360" or "vr360" or "equirect360" or "equirect-360" or "equirectangular" or "spherical")
            {
                projection = Projection.Equirect360;
                return true;
            }
            if (token is "flat" or "2d" or "screen" or "none")
            {
                projection = Projection.Flat;
                return true;
            }
            return false;
        }

        static void ApplyMatroska(int mode, ref StereoGuess guess)
        {
            // Matroska StereoMode. Top of an over-under frame is the left eye unless the mode says otherwise.
            switch (mode)
            {
                case 1: // side by side, left eye first
                    guess.Layout = Layout.FSBS;
                    break;
                case 11: // side by side, right eye first
                    guess.Layout = Layout.FSBS;
                    guess.EyeSwap = true;
                    break;
                case 3: // top-bottom, left eye first
                    guess.Layout = Layout.FOU;
                    break;
                case 2: // top-bottom, right eye first
                    guess.Layout = Layout.FOU;
                    guess.EyeSwap = true;
                    break;
                case 13: // both eyes laced in one block, left first
                case 14: // both eyes laced in one block, right first
                    guess.FrameSequential = true;
                    guess.Layout = Layout.Mono;
                    break;
                default:
                    guess.Layout = Layout.Mono;
                    break;
            }
        }

        static string Normalize(string path)
        {
            if (string.IsNullOrEmpty(path)) return "";
            string name;
            try { name = Path.GetFileNameWithoutExtension(Uri.UnescapeDataString(path)); }
            catch { name = Path.GetFileNameWithoutExtension(path); }
            if (string.IsNullOrEmpty(name)) return "";
            name = name.ToLowerInvariant();
            name = Regex.Replace(name, @"[^a-z0-9]+", " ");
            return " " + name.Trim() + " ";
        }

        static bool Has(string normalized, string token) => normalized.Contains(" " + token + " ");
    }
}
