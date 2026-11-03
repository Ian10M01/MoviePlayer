"""
package.py
──────────
Background service that scans MEDIA_DIR for video files, classifies them,
generates HLS segments + thumbnail filmstrips, and maintains library.json.

Runs as an infinite loop; intended to be launched from api.py in a thread.
"""

from __future__ import annotations

import json
import logging
import os
import subprocess
import threading
import time
from pathlib import Path
from typing import Any

from stereo_classifier import classify
from format_compat import assess_compatibility, audio_output_layouts

logger = logging.getLogger(__name__)

# ─────────────────────────────────────────────────────────────────────────────
# Configuration (read once from env at import time)
# ─────────────────────────────────────────────────────────────────────────────

MEDIA_DIR = Path(os.environ.get("MEDIA_DIR", "/media"))
CLARITY_PACK_DIR = Path(os.environ.get("CLARITY_PACK_DIR", "/clarity"))
HLS_ROOT = Path(os.environ.get("HLS_ROOT", "/hls"))

LIBRARY_PATH = MEDIA_DIR / "library.json"
BAKE_QUEUE_PATH = MEDIA_DIR / "bake_queue.json"

SCAN_INTERVAL_S = int(os.environ.get("SCAN_INTERVAL_S", "60"))
SEGMENT_DURATION = int(os.environ.get("HLS_SEGMENT_DURATION", "2"))
THUMB_INTERVAL_S = int(os.environ.get("THUMB_INTERVAL_S", "60"))

SUPPORTED_EXTENSIONS = {
    ".mp4", ".mkv", ".mov", ".avi", ".m2ts", ".ts", ".mts",
    ".webm", ".m4v", ".vob", ".flv",
}

# ─────────────────────────────────────────────────────────────────────────────
# Library helpers
# ─────────────────────────────────────────────────────────────────────────────

_library_lock = threading.Lock()


def load_library() -> dict[str, Any]:
    """Load library.json from disk, or return empty dict."""
    if LIBRARY_PATH.exists():
        try:
            with open(LIBRARY_PATH, "r", encoding="utf-8") as fh:
                return json.load(fh)
        except (json.JSONDecodeError, OSError) as exc:
            logger.warning("Failed to read library.json: %s", exc)
    return {}


def save_library(library: dict[str, Any]) -> None:
    """Atomically write library.json."""
    tmp = LIBRARY_PATH.with_suffix(".json.tmp")
    with open(tmp, "w", encoding="utf-8") as fh:
        json.dump(library, fh, indent=2)
    tmp.replace(LIBRARY_PATH)
    logger.debug("Library saved (%d entries)", len(library))


# ─────────────────────────────────────────────────────────────────────────────
# FFprobe helper
# ─────────────────────────────────────────────────────────────────────────────

def run_ffprobe(file_path: Path) -> dict[str, Any] | None:
    cmd = [
        "ffprobe",
        "-v", "quiet",
        "-print_format", "json",
        "-show_streams",
        "-show_format",
        str(file_path),
    ]
    try:
        result = subprocess.run(cmd, capture_output=True, text=True, timeout=60)
        if result.returncode != 0:
            logger.error("ffprobe failed for %s: %s", file_path, result.stderr[:300])
            return None
        return json.loads(result.stdout)
    except (subprocess.TimeoutExpired, json.JSONDecodeError, OSError) as exc:
        logger.error("ffprobe error for %s: %s", file_path, exc)
        return None


# ─────────────────────────────────────────────────────────────────────────────
# HLS packaging
# ─────────────────────────────────────────────────────────────────────────────

def _run_ffmpeg(cmd: list[str], timeout: int) -> tuple[bool, str]:
    try:
        result = subprocess.run(cmd, capture_output=True, text=True, timeout=timeout)
    except subprocess.TimeoutExpired:
        return False, "timed out"
    except OSError as exc:
        return False, str(exc)
    if result.returncode != 0:
        return False, (result.stderr or result.stdout or "")[-500:]
    return True, ""


def _video_codec_name(probe: dict[str, Any]) -> str:
    for stream in probe.get("streams") or []:
        if stream.get("codec_type") == "video":
            return (stream.get("codec_name") or "").lower()
    return ""


def _audio_streams(probe: dict[str, Any]) -> list[dict[str, Any]]:
    return [s for s in (probe.get("streams") or []) if s.get("codec_type") == "audio"]


def _stream_channels(stream: dict[str, Any]) -> int:
    try:
        count = int(stream.get("channels") or 0)
    except (TypeError, ValueError):
        count = 0
    if count > 0:
        return count
    layout = (stream.get("channel_layout") or "").lower()
    if "7.1" in layout:
        return 8
    if "5.1" in layout or "6.1" in layout:
        return 6
    return 2


def _stream_language(stream: dict[str, Any]) -> str:
    tags = stream.get("tags") or {}
    return tags.get("language") or tags.get("LANGUAGE") or "und"


def _encode_aac(source: Path, ordinal: int, dest: Path, layout: str) -> bool:
    """Transcode one source audio stream to AAC-LC.

    Stereo always retries with ``-ac 2``, which downmixes 5.1, 7.1 and
    Atmos beds. 5.1 is best-effort and may be skipped.
    """
    if layout == "5.1":
        attempts = [
            ["-af", "aresample=48000,aformat=channel_layouts=5.1", "-ac", "6", "-b:a", "384k"],
            ["-af", "aresample=48000", "-ac", "6", "-ar", "48000", "-b:a", "384k"],
            ["-ac", "6", "-ar", "48000", "-b:a", "384k"],
        ]
    else:
        attempts = [
            ["-af", "aresample=48000", "-ac", "2", "-ar", "48000", "-b:a", "192k"],
            ["-ac", "2", "-ar", "48000", "-b:a", "192k"],
        ]

    for extra in attempts:
        if dest.exists():
            dest.unlink()
        cmd = [
            "ffmpeg", "-y", "-hide_banner", "-loglevel", "error",
            "-i", str(source),
            "-map", f"0:a:{ordinal}",
            "-vn",
            "-c:a", "aac",
            "-profile:a", "aac_low",
            *extra,
            str(dest),
        ]
        ok, err = _run_ffmpeg(cmd, timeout=3600)
        if ok and dest.is_file() and dest.stat().st_size > 0:
            return True
        logger.warning(
            "AAC %s encode failed for audio stream %s (%s)",
            layout, ordinal, err.strip() or "empty output",
        )
    return False


def _silent_stereo(dest: Path, duration_s: float) -> bool:
    duration = max(float(duration_s or 0), 1.0)
    if dest.exists():
        dest.unlink()
    cmd = [
        "ffmpeg", "-y", "-hide_banner", "-loglevel", "error",
        "-f", "lavfi",
        "-i", "anullsrc=channel_layout=stereo:sample_rate=48000",
        "-t", f"{duration:.3f}",
        "-c:a", "aac",
        "-profile:a", "aac_low",
        "-b:a", "64k",
        "-ac", "2",
        str(dest),
    ]
    ok, err = _run_ffmpeg(cmd, timeout=600)
    if not ok:
        logger.error("Silent AAC fallback failed: %s", err.strip())
    return ok and dest.is_file() and dest.stat().st_size > 0


def _collect_aac_tracks(source: Path, probe: dict[str, Any], work: Path) -> list[dict[str, Any]]:
    """Encode every audio stream to AAC. One failure does not abort the movie."""
    stereo: list[dict[str, Any]] = []
    surround: list[dict[str, Any]] = []
    streams = _audio_streams(probe)

    for ordinal, stream in enumerate(streams):
        codec = (stream.get("codec_name") or "unknown").lower()
        channels = _stream_channels(stream)
        language = _stream_language(stream)
        logger.info(
            "Audio stream %s codec=%s channels=%s -> AAC-LC",
            ordinal, codec, channels,
        )
        for layout in audio_output_layouts(channels):
            dest = work / f"a{ordinal}_{'51' if layout == '5.1' else 'st'}.m4a"
            if not _encode_aac(source, ordinal, dest, layout):
                logger.warning("Skipping audio stream %s layout %s", ordinal, layout)
                continue
            out_channels = 6 if layout == "5.1" else 2
            title = f"{language} AAC {'5.1' if layout == '5.1' else 'stereo'}"
            entry = {
                "path": str(dest),
                "channels": out_channels,
                "codec": "aac",
                "language": language,
                "title": title,
                "source_codec": codec,
                "default": False,
            }
            (surround if layout == "5.1" else stereo).append(entry)

    if not stereo:
        logger.warning("No stereo AAC track encoded; generating silent AAC stereo.")
        duration = 0.0
        try:
            duration = float((probe.get("format") or {}).get("duration") or 0)
        except (TypeError, ValueError):
            duration = 0.0
        dest = work / "silent_stereo.m4a"
        if _silent_stereo(dest, duration):
            stereo.append({
                "path": str(dest),
                "channels": 2,
                "codec": "aac",
                "language": "und",
                "title": "Silent AAC stereo",
                "source_codec": "none",
                "default": False,
            })

    # Stereo is the default Quest playback track; 5.1 follows it.
    ordered = stereo + surround
    if ordered:
        ordered[0]["default"] = True
    return ordered


def _video_args(copy_h264: bool) -> list[str]:
    if copy_h264:
        return ["-c:v", "copy", "-tag:v", "avc1"]
    return [
        "-c:v", "libx264",
        "-preset", "fast",
        "-crf", "20",
        "-profile:v", "high",
        "-pix_fmt", "yuv420p",
        "-tag:v", "avc1",
    ]


def _mux_hls(source: Path, tracks: list[dict[str, Any]], hls_dir: Path, copy_h264: bool) -> bool:
    """H.264 video plus AAC tracks in one fMP4 VOD playlist. Stereo is first."""
    index_path = str(hls_dir / "index.m3u8")
    segment_pattern = str(hls_dir / "seg%05d.m4s")
    cmd = ["ffmpeg", "-y", "-hide_banner", "-loglevel", "warning", "-i", str(source)]
    for track in tracks:
        cmd += ["-i", track["path"]]
    cmd += ["-map", "0:v:0"]
    for i in range(len(tracks)):
        cmd += ["-map", f"{i + 1}:a:0"]
    cmd += _video_args(copy_h264)
    cmd += ["-c:a", "copy"]
    for i, track in enumerate(tracks):
        cmd += [f"-disposition:a:{i}", "default" if track.get("default") else "0"]
        cmd += [f"-metadata:s:a:{i}", f"title={track.get('title') or 'AAC'}"]
    cmd += [
        "-f", "hls",
        "-hls_time", str(SEGMENT_DURATION),
        "-hls_segment_type", "fmp4",
        "-hls_segment_filename", segment_pattern,
        "-hls_playlist_type", "vod",
        "-hls_flags", "independent_segments",
        "-movflags", "+faststart",
        index_path,
    ]
    ok, err = _run_ffmpeg(cmd, timeout=3600)
    if not ok:
        logger.error("FFmpeg HLS mux failed:\n%s", err)
    return ok


def _remember_packaged_audio(manifest: dict[str, Any], tracks: list[dict[str, Any]]) -> None:
    manifest["source_audio_tracks"] = list(manifest.get("audio_tracks") or [])
    manifest["audio_tracks"] = [
        {
            "index": i,
            "channels": track["channels"],
            "codec": "aac",
            "language": track.get("language") or "und",
            "title": track.get("title") or "AAC",
            "source_codec": track.get("source_codec") or "",
        }
        for i, track in enumerate(tracks)
    ]


def package_hls(source: Path, manifest: dict[str, Any], compat: dict[str, Any]) -> bool:
    """
    Produce H.264 High + AAC-LC fMP4 VOD HLS.

    Every audio codec (aac, mp3, ac3, eac3, truehd, dts, dtshd, flac, opus,
    pcm, vorbis, atmos, and anything unknown) is transcoded to AAC-LC.
    Multichannel sources also get an AAC 5.1 track when the encoder accepts
    the layout. The first audio track is always stereo AAC. A single bad
    audio stream is skipped; the movie still ships with one stereo track.
    """
    _ = compat  # video decision uses the file actually packaged
    movie_id = manifest["id"]

    clarity_pack = CLARITY_PACK_DIR / f"{movie_id}_clarity.mp4"
    actual_source = clarity_pack if clarity_pack.is_file() else source

    hls_dir = HLS_ROOT / movie_id
    hls_dir.mkdir(parents=True, exist_ok=True)

    index_path = hls_dir / "index.m3u8"
    if index_path.exists():
        logger.debug("HLS already packaged for %s, skipping.", movie_id)
        return True

    probe = run_ffprobe(actual_source)
    if probe is None:
        logger.error("ffprobe failed for HLS source %s", actual_source)
        return False

    video_codec = _video_codec_name(probe)
    copy_h264 = video_codec in ("h264", "avc")
    if not copy_h264:
        logger.info("Transcoding video %s to H.264 High for %s", video_codec or "unknown", movie_id)

    work = hls_dir / "_audio"
    work.mkdir(parents=True, exist_ok=True)
    logger.info("Packaging HLS for %s (source: %s)", movie_id, actual_source.name)

    try:
        tracks = _collect_aac_tracks(actual_source, probe, work)
        if not tracks:
            logger.error("No AAC audio track could be produced for %s", movie_id)
            return False

        ok = _mux_hls(actual_source, tracks, hls_dir, copy_h264)
        if not ok and copy_h264:
            logger.warning("H.264 copy mux failed for %s; transcoding video.", movie_id)
            if index_path.exists():
                index_path.unlink()
            ok = _mux_hls(actual_source, tracks, hls_dir, copy_h264=False)
        if not ok:
            if index_path.exists():
                index_path.unlink()
            return False
        _remember_packaged_audio(manifest, tracks)
    finally:
        for leftover in work.glob("*"):
            try:
                leftover.unlink()
            except OSError:
                pass
        try:
            work.rmdir()
        except OSError:
            pass

    logger.info("HLS packaging complete for %s (%d AAC track(s))", movie_id, len(tracks))
    return True


# ─────────────────────────────────────────────────────────────────────────────
# Thumbnail filmstrip (WebVTT)
# ─────────────────────────────────────────────────────────────────────────────

def generate_thumbnails(source: Path, manifest: dict[str, Any]) -> bool:
    """
    Extract one thumbnail per THUMB_INTERVAL_S seconds, write a WebVTT file.
    Thumbnails are written to hls/{movie_id}/thumbs/ as thumb_NNNNN.jpg.
    The VTT file is written to hls/{movie_id}/thumbs.vtt.
    Returns True on success.
    """
    movie_id = manifest["id"]
    duration_s = manifest.get("duration_s", 0)
    hls_dir = HLS_ROOT / movie_id
    thumbs_dir = hls_dir / "thumbs"
    thumbs_dir.mkdir(parents=True, exist_ok=True)

    vtt_path = hls_dir / "thumbs.vtt"
    if vtt_path.exists():
        return True  # already generated

    if duration_s <= 0:
        logger.warning("No duration for %s; skipping thumbnails.", movie_id)
        return False

    # For HSBS content, crop to left eye so thumbnails show a normal image
    stereo = manifest.get("stereo", "mono")
    width = manifest.get("width", 0)
    height = manifest.get("height", 0)

    vf_filter = "scale=320:-1"
    if stereo in ("hsbs",) and width > 0 and height > 0:
        # Crop left half
        vf_filter = f"crop=iw/2:ih:0:0,scale=320:-1"

    thumb_pattern = str(thumbs_dir / "thumb_%05d.jpg")

    cmd = [
        "ffmpeg", "-y", "-hide_banner", "-loglevel", "warning",
        "-i", str(source),
        "-vf", f"fps=1/{THUMB_INTERVAL_S},{vf_filter}",
        "-q:v", "3",
        thumb_pattern,
    ]

    try:
        result = subprocess.run(cmd, capture_output=True, text=True, timeout=600)
        if result.returncode != 0:
            logger.error("Thumbnail extraction failed for %s: %s", movie_id, result.stderr[-300:])
            return False
    except subprocess.TimeoutExpired:
        logger.error("Thumbnail extraction timed out for %s", movie_id)
        return False

    # Build WebVTT
    thumb_files = sorted(thumbs_dir.glob("thumb_*.jpg"))
    vtt_lines = ["WEBVTT", ""]
    for i, thumb_file in enumerate(thumb_files):
        start_s = i * THUMB_INTERVAL_S
        end_s = min(start_s + THUMB_INTERVAL_S, int(duration_s))

        def fmt_ts(s: int) -> str:
            hh, rem = divmod(s, 3600)
            mm, ss = divmod(rem, 60)
            return f"{hh:02}:{mm:02}:{ss:02}.000"

        vtt_lines.append(f"{fmt_ts(start_s)} --> {fmt_ts(end_s)}")
        vtt_lines.append(f"thumbs/{thumb_file.name}")
        vtt_lines.append("")

    vtt_path.write_text("\n".join(vtt_lines), encoding="utf-8")
    logger.info("Thumbnails generated for %s (%d frames)", movie_id, len(thumb_files))
    return True


# ─────────────────────────────────────────────────────────────────────────────
# Bake queue helpers
# ─────────────────────────────────────────────────────────────────────────────

def _load_bake_queue() -> list[dict]:
    if BAKE_QUEUE_PATH.exists():
        try:
            with open(BAKE_QUEUE_PATH, "r", encoding="utf-8") as fh:
                return json.load(fh)
        except (json.JSONDecodeError, OSError):
            pass
    return []


def enqueue_bake(movie_id: str, source_path: str, target_path: str) -> None:
    with _library_lock:
        queue = _load_bake_queue()
        existing_ids = {job["movie_id"] for job in queue}
        if movie_id not in existing_ids:
            queue.append({
                "movie_id": movie_id,
                "source_path": source_path,
                "target_path": target_path,
                "status": "queued",
            })
            tmp = BAKE_QUEUE_PATH.with_suffix(".json.tmp")
            with open(tmp, "w", encoding="utf-8") as fh:
                json.dump(queue, fh, indent=2)
            tmp.replace(BAKE_QUEUE_PATH)
            logger.info("Enqueued bake job for %s", movie_id)


# ─────────────────────────────────────────────────────────────────────────────
# Core scan + index loop
# ─────────────────────────────────────────────────────────────────────────────

def scan_and_index() -> None:
    """
    Scan MEDIA_DIR for video files, classify new ones, package HLS, generate
    thumbnails, and update library.json.
    """
    logger.info("Scanning %s …", MEDIA_DIR)
    library: dict[str, Any] = {}
    with _library_lock:
        library = load_library()

    indexed_files = {entry["filename"] for entry in library.values()}

    for root, _dirs, files in os.walk(MEDIA_DIR):
        # Skip the HLS output directory if it's inside MEDIA_DIR
        rel_root = Path(root).relative_to(MEDIA_DIR)
        if str(rel_root).startswith("hls"):
            continue

        for fname in files:
            ext = Path(fname).suffix.lower()
            if ext not in SUPPORTED_EXTENSIONS:
                continue

            file_path = Path(root) / fname

            # Skip already-indexed files (by filename; could use mtime too)
            if fname in indexed_files:
                # Still check if clarity pack appeared after initial index
                existing = next(
                    (e for e in library.values() if e["filename"] == fname), None
                )
                if existing:
                    movie_id = existing["id"]
                    clarity_pack = CLARITY_PACK_DIR / f"{movie_id}_clarity.mp4"
                    if clarity_pack.is_file() and existing.get("clarity_pack") is None:
                        existing["clarity_pack"] = str(clarity_pack)
                        logger.info("Updated clarity_pack for %s", fname)
                        with _library_lock:
                            library[movie_id] = existing
                            save_library(library)
                continue

            logger.info("Indexing new file: %s", fname)

            # ── 1. ffprobe ──────────────────────────────────────────────
            probe = run_ffprobe(file_path)
            if probe is None:
                logger.warning("Skipping %s (ffprobe failed)", fname)
                continue

            # ── 2. Classify ─────────────────────────────────────────────
            try:
                manifest = classify(probe, str(file_path))
            except Exception as exc:  # noqa: BLE001
                logger.error("Classifier error for %s: %s", fname, exc)
                continue

            movie_id = manifest["id"]

            # ── 3. Format compat ────────────────────────────────────────
            clarity_pack_file = CLARITY_PACK_DIR / f"{movie_id}_clarity.mp4"
            try:
                compat = assess_compatibility(
                    probe,
                    clarity_pack_path=str(clarity_pack_file) if clarity_pack_file.is_file() else None,
                )
            except Exception as exc:  # noqa: BLE001
                logger.error("format_compat error for %s: %s", fname, exc)
                compat = {"compat_action": "transcode", "clarity_status": "ready"}

            manifest["compat_action"] = compat["compat_action"]
            manifest["native_playable"] = compat.get("native_playable", False)
            manifest["clarity_status"] = compat.get("clarity_status", "ready")

            # ── 4. HLS packaging ────────────────────────────────────────
            hls_ok = package_hls(file_path, manifest, compat)
            if not hls_ok:
                logger.warning("HLS packaging failed for %s; will retry next scan", fname)
                continue

            # ── 5. Thumbnails ───────────────────────────────────────────
            generate_thumbnails(file_path, manifest)

            # ── 6. Auto-enqueue bake if recommended ─────────────────────
            if compat.get("clarity_status") == "bake_recommended":
                target = str(CLARITY_PACK_DIR / f"{movie_id}_clarity.mp4")
                enqueue_bake(movie_id, str(file_path), target)

            # ── 7. Update library ────────────────────────────────────────
            with _library_lock:
                library = load_library()  # re-read to avoid races
                library[movie_id] = manifest
                save_library(library)

            indexed_files.add(fname)
            logger.info("Indexed: %s (id=%s, stereo=%s, action=%s)",
                        fname, movie_id, manifest["stereo"], manifest["compat_action"])

    logger.info("Scan complete. Library has %d entries.", len(library))


def run_scanner_loop() -> None:
    """Infinite loop that calls scan_and_index every SCAN_INTERVAL_S seconds."""
    while True:
        try:
            scan_and_index()
        except Exception as exc:  # noqa: BLE001
            logger.exception("Unhandled error in scanner: %s", exc)
        time.sleep(SCAN_INTERVAL_S)


# ─────────────────────────────────────────────────────────────────────────────
# Module-level convenience
# ─────────────────────────────────────────────────────────────────────────────

def start_background_scanner() -> threading.Thread:
    """Start the scanner loop in a daemon thread and return the thread."""
    t = threading.Thread(target=run_scanner_loop, name="scanner", daemon=True)
    t.start()
    logger.info("Background scanner started (interval=%ds)", SCAN_INTERVAL_S)
    return t


if __name__ == "__main__":
    logging.basicConfig(level=logging.INFO,
                        format="%(asctime)s %(levelname)s %(name)s: %(message)s")
    run_scanner_loop()
