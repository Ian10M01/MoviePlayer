"""
stereo_classifier.py
────────────────────
Detects stereo layout, projection type, eye-swap, playback compatibility, and
compat action from ffprobe JSON output + file path heuristics.

Returns a fully-populated manifest dict ready to be stored in library.json.
"""

from __future__ import annotations

import hashlib
import logging
import os
import re
from datetime import datetime, timezone
from typing import Any

logger = logging.getLogger(__name__)

# ─────────────────────────────────────────────────────────────────────────────
# Constants
# ─────────────────────────────────────────────────────────────────────────────

# Codecs natively supported by Meta Quest (all generations)
QUEST_NATIVE_CODECS = {"h264", "hevc", "h265", "av1"}

# Codecs that require transcoding
TRANSCODE_CODECS = {"vc1", "mpeg2video", "mpeg4", "vp8", "wmv2", "wmv3", "theora"}

# Containers that need remuxing to mp4/mkv/mov for Quest
REMUX_CONTAINERS = {"avi", "m2ts", "mts", "ts", "vob", "flv", "asf", "rm", "rmvb"}

# Containers natively supported
NATIVE_CONTAINERS = {"mp4", "mkv", "mov", "webm", "m4v"}

# Resolution cap for "native playable" (4K = 3840 width or 2160 height)
MAX_NATIVE_WIDTH = 3840
MAX_NATIVE_HEIGHT = 2160

# ─────────────────────────────────────────────────────────────────────────────
# Stereo layout detection
# ─────────────────────────────────────────────────────────────────────────────

# Filename token → stereo layout
_STEREO_FILENAME_TOKENS: list[tuple[re.Pattern, str]] = [
    (re.compile(r"\b(h?sbs|side.?by.?side|3dh)\b", re.I), "hsbs"),
    (re.compile(r"\bfsbs\b", re.I), "fsbs"),
    (re.compile(r"\b(h?ou|over.?under|top.?bottom|tab)\b", re.I), "hou"),
    (re.compile(r"\bfou\b", re.I), "fou"),
    (re.compile(r"\bmvc\b", re.I), "mvc"),
    (re.compile(r"\b(half.?3d|half.?sbs)\b", re.I), "hsbs"),
    (re.compile(r"\bfull.?3d\b", re.I), "fsbs"),
    (re.compile(r"\b3d\b", re.I), "hsbs"),   # generic "3D" → assume HSBS (most common)
]

# MKV stereo_mode metadata values → layout
_MKV_STEREO_MODE: dict[str, str] = {
    "1": "fsbs",
    "3": "fsbs",        # side by side (left eye first)
    "11": "hsbs",
    "2": "fou",         # top-bottom
    "6": "hou",
    "13": "hou",        # half height top-bottom
    "4": "checkerboard",
    "5": "row_interleaved",
    "7": "col_interleaved",
    "8": "anaglyph_cyan_red",
    "9": "mvc",         # multiview encoding
    "10": "mvc",
}

# ─────────────────────────────────────────────────────────────────────────────
# Projection detection
# ─────────────────────────────────────────────────────────────────────────────

_PROJ_FILENAME_TOKENS: list[tuple[re.Pattern, str]] = [
    (re.compile(r"\b360\b", re.I), "360"),
    (re.compile(r"\b180\b", re.I), "180"),
    (re.compile(r"\beqr\b", re.I), "360"),
    (re.compile(r"\bequirect(angular)?\b", re.I), "360"),
    (re.compile(r"\bfish.?eye\b", re.I), "180"),
]


def _detect_stereo_from_filename(stem: str) -> str | None:
    for pattern, layout in _STEREO_FILENAME_TOKENS:
        if pattern.search(stem):
            return layout
    return None


def _detect_stereo_from_metadata(video_stream: dict) -> str | None:
    tags = video_stream.get("tags", {})
    # MKV stereo_mode tag
    stereo_mode = tags.get("stereo_mode") or tags.get("STEREO_MODE")
    if stereo_mode is not None:
        return _MKV_STEREO_MODE.get(str(stereo_mode))

    # Generic stereo_layout tag
    layout_tag = tags.get("stereo_layout") or tags.get("STEREO_LAYOUT") or ""
    if layout_tag:
        layout_tag = layout_tag.lower()
        if "side" in layout_tag:
            return "hsbs"
        if "top" in layout_tag or "over" in layout_tag:
            return "hou"

    # Side-data (H.264 SEI stereo info)
    for sd in video_stream.get("side_data_list", []):
        sd_type = sd.get("side_data_type", "")
        if "Stereo 3D" in sd_type or "stereo3d" in sd_type.lower():
            layout = sd.get("type", "")
            if "side_by_side" in layout:
                return "hsbs" if sd.get("half", False) else "fsbs"
            if "top_bottom" in layout:
                return "hou"

    return None


def _detect_stereo_from_aspect(width: int, height: int) -> str | None:
    """Heuristic: if AR is very wide (>3.0) it's likely HSBS; if tall (<0.4) it's likely OU."""
    if height == 0:
        return None
    ar = width / height
    if ar >= 3.0:
        return "hsbs"
    if ar <= 0.4:
        return "hou"
    return None


def _detect_projection_from_filename(stem: str) -> str:
    for pattern, proj in _PROJ_FILENAME_TOKENS:
        if pattern.search(stem):
            return proj
    return "flat"


def _detect_projection_from_metadata(video_stream: dict) -> str | None:
    tags = video_stream.get("tags", {})
    proj = tags.get("projection") or tags.get("PROJECTION") or ""
    if "equirect" in proj.lower() or "360" in proj:
        return "360"
    if "180" in proj:
        return "180"
    return None


def _detect_eye_swap(stem: str) -> bool:
    return bool(re.search(r"\b(swap|eyeswap|eye.?swap|rl)\b", stem, re.I))


# ─────────────────────────────────────────────────────────────────────────────
# Compatibility assessment
# ─────────────────────────────────────────────────────────────────────────────

def _normalise_codec(codec_name: str) -> str:
    name = codec_name.lower().strip()
    # Aliases
    aliases = {"h264": "h264", "avc": "h264", "h265": "hevc", "hevc": "hevc",
               "av1": "av1", "vp9": "vp9", "vp8": "vp8",
               "vc-1": "vc1", "vc1": "vc1", "wmv3": "vc1",
               "mpeg2video": "mpeg2video", "mpeg4": "mpeg4", "theora": "theora"}
    return aliases.get(name, name)


def _get_container(format_name: str) -> str:
    """Return the primary container name from ffprobe format_name (e.g. 'mov,mp4,m4a,3gp,3g2,mj2' → 'mp4')."""
    parts = [p.strip() for p in format_name.split(",")]
    for p in parts:
        if p in ("mp4", "m4v"):
            return "mp4"
        if p in ("matroska", "mkv", "webm"):
            return p
        if p == "mov":
            return "mov"
    return parts[0]


def assess_compat(codec: str, container: str, width: int, height: int) -> tuple[str, bool]:
    """
    Returns (compat_action, native_playable).
    compat_action: 'native' | 'remux' | 'transcode'
    native_playable: True if codec + resolution are Quest-supported without conversion.
    """
    norm_codec = _normalise_codec(codec)
    norm_container = container.lower()

    native_playable = (
        norm_codec in QUEST_NATIVE_CODECS
        and width <= MAX_NATIVE_WIDTH
        and height <= MAX_NATIVE_HEIGHT
    )

    if norm_codec in TRANSCODE_CODECS:
        return "transcode", False

    if norm_codec in QUEST_NATIVE_CODECS:
        if norm_container in REMUX_CONTAINERS:
            return "remux", native_playable
        return "native", native_playable

    # Unknown codec – transcode to be safe
    return "transcode", False


# ─────────────────────────────────────────────────────────────────────────────
# Audio track extraction
# ─────────────────────────────────────────────────────────────────────────────

def _extract_audio_tracks(streams: list[dict]) -> list[dict]:
    tracks = []
    idx = 0
    for s in streams:
        if s.get("codec_type") != "audio":
            continue
        tags = s.get("tags", {})
        tracks.append({
            "index": idx,
            "codec": s.get("codec_name", "unknown"),
            "channels": s.get("channels", 2),
            "language": tags.get("language") or tags.get("LANGUAGE") or "und",
        })
        idx += 1
    return tracks


# ─────────────────────────────────────────────────────────────────────────────
# Public API
# ─────────────────────────────────────────────────────────────────────────────

def classify(ffprobe_json: dict[str, Any], file_path: str) -> dict[str, Any]:
    """
    Given ffprobe JSON and the absolute file path, produce a full manifest dict.

    Parameters
    ----------
    ffprobe_json : dict
        Parsed output of ``ffprobe -v quiet -print_format json -show_streams -show_format``
    file_path : str
        Absolute path to the source video file.

    Returns
    -------
    dict
        Manifest dictionary.
    """
    streams: list[dict] = ffprobe_json.get("streams", [])
    fmt: dict = ffprobe_json.get("format", {})

    # ── Find primary video stream ──────────────────────────────────────────
    video_stream: dict = {}
    for s in streams:
        if s.get("codec_type") == "video":
            video_stream = s
            break

    filename = os.path.basename(file_path)
    stem = os.path.splitext(filename)[0]

    # ── IDs ───────────────────────────────────────────────────────────────
    movie_id = hashlib.sha256(file_path.encode()).hexdigest()[:12]

    # ── Video properties ──────────────────────────────────────────────────
    width: int = int(video_stream.get("width", 0))
    height: int = int(video_stream.get("height", 0))
    codec_raw: str = video_stream.get("codec_name", "unknown")
    codec = _normalise_codec(codec_raw)

    # FPS – r_frame_rate is a fraction string like "24000/1001"
    fps = 0.0
    rfr = video_stream.get("r_frame_rate", "0/1")
    try:
        num, den = (int(x) for x in rfr.split("/"))
        fps = round(num / den, 3) if den else 0.0
    except (ValueError, ZeroDivisionError):
        pass

    duration_s = 0.0
    try:
        duration_s = float(fmt.get("duration") or video_stream.get("duration", 0))
    except (TypeError, ValueError):
        pass

    # ── Container ─────────────────────────────────────────────────────────
    format_name: str = fmt.get("format_name", "")
    container = _get_container(format_name)

    # ── Stereo detection (priority: metadata > filename > aspect ratio) ────
    stereo = (
        _detect_stereo_from_metadata(video_stream)
        or _detect_stereo_from_filename(stem)
        or _detect_stereo_from_aspect(width, height)
        or "mono"
    )

    # ── Projection detection ──────────────────────────────────────────────
    projection = (
        _detect_projection_from_metadata(video_stream)
        or _detect_projection_from_filename(stem)
    )

    # ── Eye swap ──────────────────────────────────────────────────────────
    eye_swap = _detect_eye_swap(stem)

    # ── Compatibility ─────────────────────────────────────────────────────
    compat_action, native_playable = assess_compat(codec, container, width, height)

    # ── Audio tracks ──────────────────────────────────────────────────────
    audio_tracks = _extract_audio_tracks(streams)

    # ── Sample score placeholder (frame-sampling updates this later) ───────
    sample_score: float = 0.0

    hls_path = f"hls/{movie_id}/index.m3u8"
    thumbs_path = f"hls/{movie_id}/thumbs.vtt"

    manifest: dict[str, Any] = {
        "id": movie_id,
        "filename": filename,
        "stereo": stereo,
        "projection": projection,
        "eye_swap": eye_swap,
        "native_playable": native_playable,
        "compat_action": compat_action,
        "width": width,
        "height": height,
        "fps": fps,
        "codec": codec,
        "audio_tracks": audio_tracks,
        "duration_s": round(duration_s, 3),
        "clarity_pack": None,
        "hls_path": hls_path,
        "thumbs_path": thumbs_path,
        "sample_score": sample_score,
        "indexed_at": datetime.now(timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ"),
    }

    logger.debug("Classified %s → %s", filename, manifest)
    return manifest
