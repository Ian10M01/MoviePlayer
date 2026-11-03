"""
format_compat.py
────────────────
Compatibility matrix for codec/container combinations on Meta Quest headsets.

Public API
----------
assess_compatibility(ffprobe_json, clarity_pack_path=None) -> dict
"""

from __future__ import annotations

import os
from typing import Any

# ─────────────────────────────────────────────────────────────────────────────
# Codec / container support tables
# ─────────────────────────────────────────────────────────────────────────────

# Meta Quest native video codec support (all Quest models with latest firmware)
CODEC_SUPPORT: dict[str, dict[str, Any]] = {
    # Fully native – hardware decode
    "h264": {
        "supported": True,
        "hw_decode": True,
        "max_profile": "High",
        "max_level": "5.2",
        "max_width": 3840,
        "max_height": 2160,
        "max_fps": 120,
        "notes": "H.264 / AVC – widest compatibility",
    },
    "hevc": {
        "supported": True,
        "hw_decode": True,
        "max_profile": "Main10",
        "max_level": "5.1",
        "max_width": 7680,
        "max_height": 4320,
        "max_fps": 60,
        "notes": "H.265 / HEVC – preferred for VR content",
    },
    "av1": {
        "supported": True,
        "hw_decode": True,   # Quest 3+ only; Quest 2 is SW decode
        "max_profile": "Main",
        "max_level": "5.1",
        "max_width": 3840,
        "max_height": 2160,
        "max_fps": 60,
        "notes": "AV1 – Quest 3+ hw decode; Quest 2 SW only",
    },
    # Software decode / remux candidates
    "vp9": {
        "supported": True,
        "hw_decode": False,
        "max_width": 3840,
        "max_height": 2160,
        "max_fps": 30,
        "notes": "VP9 – SW decode only; may stutter at 4K",
    },
    # Must transcode
    "vp8": {
        "supported": False,
        "hw_decode": False,
        "notes": "VP8 – not supported; transcode to H.264/HEVC",
    },
    "vc1": {
        "supported": False,
        "hw_decode": False,
        "notes": "VC-1 – not supported; transcode to HEVC",
    },
    "mpeg2video": {
        "supported": False,
        "hw_decode": False,
        "notes": "MPEG-2 Video – not supported; transcode to HEVC",
    },
    "mpeg4": {
        "supported": False,
        "hw_decode": False,
        "notes": "MPEG-4 Part 2 (DivX/Xvid) – not supported; transcode",
    },
    "theora": {
        "supported": False,
        "hw_decode": False,
        "notes": "Theora – not supported; transcode to H.264/HEVC",
    },
    "wmv2": {
        "supported": False,
        "hw_decode": False,
        "notes": "WMV2 – not supported; transcode to HEVC",
    },
    "wmv3": {
        "supported": False,
        "hw_decode": False,
        "notes": "WMV3 (VC-1) – not supported; transcode to HEVC",
    },
}

# Container / mux format support
CONTAINER_SUPPORT: dict[str, dict[str, Any]] = {
    # Fully native
    "mp4":  {"native": True,  "action": "native", "notes": "MP4 – native"},
    "m4v":  {"native": True,  "action": "native", "notes": "M4V – native (MP4 alias)"},
    "mov":  {"native": True,  "action": "native", "notes": "QuickTime MOV – native"},
    "mkv":  {"native": True,  "action": "native", "notes": "Matroska – native"},
    "webm": {"native": True,  "action": "native", "notes": "WebM – native"},
    # Needs remux
    "avi":  {"native": False, "action": "remux",  "notes": "AVI – remux to MP4"},
    "m2ts": {"native": False, "action": "remux",  "notes": "MPEG-2 TS (Blu-ray) – remux to MKV"},
    "mts":  {"native": False, "action": "remux",  "notes": "AVCHD MTS – remux to MP4"},
    "ts":   {"native": False, "action": "remux",  "notes": "MPEG-TS – remux to MP4"},
    "vob":  {"native": False, "action": "remux",  "notes": "DVD VOB – remux to MKV"},
    "flv":  {"native": False, "action": "remux",  "notes": "FLV – remux to MP4"},
    "asf":  {"native": False, "action": "remux",  "notes": "ASF/WMV container – transcode required"},
    "rm":   {"native": False, "action": "remux",  "notes": "RealMedia – transcode required"},
}

# Per-eye resolution thresholds for Clarity bake recommendation
CLARITY_MIN_WIDTH_PER_EYE = 1920   # below this → bake_recommended


def _normalise_codec(codec_name: str) -> str:
    aliases = {
        "h264": "h264", "avc": "h264",
        "h265": "hevc", "hevc": "hevc",
        "av1": "av1",
        "vp9": "vp9", "vp8": "vp8",
        "vc-1": "vc1", "vc1": "vc1", "wmv3": "vc1",
        "mpeg2video": "mpeg2video",
        "mpeg4": "mpeg4",
        "theora": "theora",
        "wmv2": "wmv2",
    }
    return aliases.get(codec_name.lower().strip(), codec_name.lower().strip())


def _get_container(format_name: str) -> str:
    parts = [p.strip() for p in format_name.split(",")]
    priority = ["mp4", "mkv", "mov", "webm", "m4v", "avi", "m2ts", "mts", "ts", "vob", "flv"]
    for p in priority:
        if p in parts:
            return p
    # matroska → mkv
    if "matroska" in parts:
        return "mkv"
    return parts[0]


def assess_compatibility(
    ffprobe_json: dict[str, Any],
    clarity_pack_path: str | None = None,
) -> dict[str, Any]:
    """
    Assess playback compatibility and Clarity Pack status for a video file.

    Parameters
    ----------
    ffprobe_json : dict
        Parsed output of ffprobe -show_streams -show_format -print_format json
    clarity_pack_path : str | None
        Absolute path to the Clarity Pack file, if it has been generated.

    Returns
    -------
    dict with keys:
        codec            – normalised codec name
        codec_info       – full codec support entry from the matrix
        container        – detected container
        container_info   – full container support entry from the matrix
        compat_action    – 'native' | 'remux' | 'transcode'
        native_playable  – bool
        clarity_status   – 'ready' | 'bake_recommended' | 'baking' | 'baked'
        width            – video width
        height           – video height
        per_eye_width    – estimated width per eye (halved for HSBS/HSBS-family)
        notes            – human-readable summary
    """
    streams: list[dict] = ffprobe_json.get("streams", [])
    fmt: dict = ffprobe_json.get("format", {})

    # ── Find primary video stream ──────────────────────────────────────────
    video_stream: dict = {}
    for s in streams:
        if s.get("codec_type") == "video":
            video_stream = s
            break

    codec_raw = video_stream.get("codec_name", "unknown")
    codec = _normalise_codec(codec_raw)
    width = int(video_stream.get("width", 0))
    height = int(video_stream.get("height", 0))
    format_name = fmt.get("format_name", "")
    container = _get_container(format_name)

    codec_info = CODEC_SUPPORT.get(codec, {
        "supported": False,
        "hw_decode": False,
        "notes": f"Unknown codec '{codec}' – transcode required",
    })
    container_info = CONTAINER_SUPPORT.get(container, {
        "native": True,
        "action": "native",
        "notes": f"Unknown container '{container}' – assuming native",
    })

    # ── Determine compat_action ────────────────────────────────────────────
    if not codec_info.get("supported", False):
        compat_action = "transcode"
    elif not container_info.get("native", True):
        compat_action = "remux"
    else:
        compat_action = "native"

    # ── native_playable ───────────────────────────────────────────────────
    max_w = codec_info.get("max_width", 3840)
    max_h = codec_info.get("max_height", 2160)
    native_playable = (
        codec_info.get("supported", False)
        and width <= max_w
        and height <= max_h
    )

    # ── Estimate per-eye width (HSBS detection from AR) ───────────────────
    # Very wide AR (>3) → assume HSBS → per-eye width is half
    ar = (width / height) if height else 1.0
    is_stereo_side_by_side = ar >= 3.0
    per_eye_width = (width // 2) if is_stereo_side_by_side else width

    # ── Clarity Pack status ───────────────────────────────────────────────
    if clarity_pack_path and os.path.isfile(clarity_pack_path):
        clarity_status = "baked"
    elif per_eye_width < CLARITY_MIN_WIDTH_PER_EYE:
        clarity_status = "bake_recommended"
    else:
        clarity_status = "ready"

    # Build notes string
    notes_parts = [
        codec_info.get("notes", ""),
        container_info.get("notes", ""),
        f"compat_action={compat_action}",
        f"clarity_status={clarity_status}",
    ]
    notes = " | ".join(p for p in notes_parts if p)

    return {
        "codec": codec,
        "codec_info": codec_info,
        "container": container,
        "container_info": container_info,
        "compat_action": compat_action,
        "native_playable": native_playable,
        "clarity_status": clarity_status,
        "width": width,
        "height": height,
        "per_eye_width": per_eye_width,
        "notes": notes,
    }


# Quest HLS audio is always re-encoded to AAC-LC. These names are recognised;
# anything else (including atmos beds reported as truehd/eac3) takes the same path.
AUDIO_TRANSCODE_CODECS = {
    "aac", "mp4a", "mp3",
    "ac3", "eac3", "truehd", "atmos",
    "dts", "dts_hd", "dtshd",
    "flac", "opus", "vorbis",
    "pcm_s16le", "pcm_s16be", "pcm_s24le", "pcm_s32le", "pcm_f32le", "pcm_u8",
}


def audio_output_layouts(channels: int) -> list[str]:
    """Layouts to emit for one source stream.

    Stereo AAC is always produced (``-ac 2`` downmix). A 5.1 AAC track is
    added when the source has more than two channels; the packager drops it
    if the encoder rejects that layout.
    """
    try:
        count = int(channels or 0)
    except (TypeError, ValueError):
        count = 0
    if count > 2:
        return ["stereo", "5.1"]
    return ["stereo"]
