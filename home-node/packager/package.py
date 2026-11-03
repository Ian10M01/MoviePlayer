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
from format_compat import assess_compatibility

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

def _build_ffmpeg_hls_cmd(
    source: Path,
    hls_dir: Path,
    manifest: dict[str, Any],
    compat: dict[str, Any],
) -> list[str]:
    """
    Build the FFmpeg command that produces H.264/AAC fMP4 VOD HLS.
    Copies an existing H.264 or AAC stream; otherwise transcodes to
    libx264 High + AAC so Quest MediaPlayer can play the playlist.
    """
    codec = (manifest.get("codec") or "").lower()
    _ = compat  # retained for callers; output is always H.264/AAC

    if codec in ("h264", "avc"):
        video_args = ["-c:v", "copy", "-tag:v", "avc1"]
    else:
        video_args = [
            "-c:v", "libx264",
            "-preset", "fast",
            "-crf", "20",
            "-profile:v", "high",
            "-pix_fmt", "yuv420p",
            "-tag:v", "avc1",
        ]

    tracks = manifest.get("audio_tracks") or []
    audio_is_aac = bool(tracks) and all(
        (t.get("codec") or "").lower() in ("aac", "mp4a") for t in tracks
    )
    audio_args = ["-c:a", "copy"] if audio_is_aac else ["-c:a", "aac", "-b:a", "192k", "-ac", "2"]

    index_path = str(hls_dir / "index.m3u8")
    segment_pattern = str(hls_dir / "seg%05d.m4s")

    cmd = (
        ["ffmpeg", "-y", "-hide_banner", "-loglevel", "warning"]
        + ["-i", str(source)]
        + video_args
        + audio_args
        + [
            "-f", "hls",
            "-hls_time", str(SEGMENT_DURATION),
            "-hls_segment_type", "fmp4",
            "-hls_segment_filename", segment_pattern,
            "-hls_playlist_type", "vod",
            "-hls_flags", "independent_segments",
            "-movflags", "+faststart",
            index_path,
        ]
    )
    return cmd


def package_hls(source: Path, manifest: dict[str, Any], compat: dict[str, Any]) -> bool:
    """
    Run FFmpeg to produce HLS fmp4 segments.
    Returns True on success.
    """
    movie_id = manifest["id"]

    # Prefer Clarity Pack if available
    clarity_pack = CLARITY_PACK_DIR / f"{movie_id}_clarity.mp4"
    actual_source = clarity_pack if clarity_pack.is_file() else source

    hls_dir = HLS_ROOT / movie_id
    hls_dir.mkdir(parents=True, exist_ok=True)

    index_path = hls_dir / "index.m3u8"
    if index_path.exists():
        logger.debug("HLS already packaged for %s, skipping.", movie_id)
        return True

    logger.info("Packaging HLS for %s (source: %s)", movie_id, actual_source.name)
    cmd = _build_ffmpeg_hls_cmd(actual_source, hls_dir, manifest, compat)

    try:
        result = subprocess.run(cmd, capture_output=True, text=True, timeout=3600)
        if result.returncode != 0:
            logger.error("FFmpeg HLS failed for %s:\n%s", movie_id, result.stderr[-500:])
            return False
    except subprocess.TimeoutExpired:
        logger.error("FFmpeg HLS timed out for %s", movie_id)
        return False

    logger.info("HLS packaging complete for %s", movie_id)
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
