"""
api.py
──────
FastAPI micro-API for the MoviePlayer Home Node packager.
Runs on :8080 inside Docker.

Endpoints:
  GET  /health                       Liveness probe
  GET  /library                      Full library.json
  GET  /library/{id}                 Single movie manifest
  POST /library/{id}/bake            Enqueue Clarity bake job
  GET  /hls/{id}/index.m3u8         Serve HLS playlist
  GET  /thumbs/{id}                  Serve WebVTT thumbs
  GET  /thumbs/{id}/{filename}       Serve individual thumb image
"""

from __future__ import annotations

import json
import logging
import os
import threading
from pathlib import Path
from typing import Any

from fastapi import FastAPI, HTTPException, Request, Response
from fastapi.middleware.cors import CORSMiddleware
from fastapi.responses import FileResponse, JSONResponse, PlainTextResponse
import uvicorn

# ─────────────────────────────────────────────────────────────────────────────
# Config
# ─────────────────────────────────────────────────────────────────────────────

MEDIA_DIR = Path(os.environ.get("MEDIA_DIR", "/media"))
CLARITY_PACK_DIR = Path(os.environ.get("CLARITY_PACK_DIR", "/clarity"))
HLS_ROOT = Path(os.environ.get("HLS_ROOT", "/hls"))
LIBRARY_PATH = MEDIA_DIR / "library.json"
BAKE_QUEUE_PATH = MEDIA_DIR / "bake_queue.json"
API_SECRET = os.environ.get("API_SECRET", "")

logging.basicConfig(
    level=logging.INFO,
    format="%(asctime)s %(levelname)s %(name)s: %(message)s",
)
logger = logging.getLogger(__name__)

# ─────────────────────────────────────────────────────────────────────────────
# App
# ─────────────────────────────────────────────────────────────────────────────

app = FastAPI(
    title="MoviePlayer Home Node Packager API",
    version="1.0.0",
    description="Serves the local VR cinema library and manages packaging jobs.",
)

app.add_middleware(
    CORSMiddleware,
    allow_origins=["*"],
    allow_methods=["*"],
    allow_headers=["*"],
)

# ─────────────────────────────────────────────────────────────────────────────
# Helpers
# ─────────────────────────────────────────────────────────────────────────────

_library_lock = threading.Lock()


def _load_library() -> dict[str, Any]:
    if not LIBRARY_PATH.exists():
        return {}
    try:
        with open(LIBRARY_PATH, "r", encoding="utf-8") as fh:
            return json.load(fh)
    except (json.JSONDecodeError, OSError) as exc:
        logger.error("Failed to read library.json: %s", exc)
        return {}


def _load_bake_queue() -> list[dict]:
    if not BAKE_QUEUE_PATH.exists():
        return []
    try:
        with open(BAKE_QUEUE_PATH, "r", encoding="utf-8") as fh:
            return json.load(fh)
    except (json.JSONDecodeError, OSError):
        return []


def _save_bake_queue(queue: list[dict]) -> None:
    tmp = BAKE_QUEUE_PATH.with_suffix(".json.tmp")
    with open(tmp, "w", encoding="utf-8") as fh:
        json.dump(queue, fh, indent=2)
    tmp.replace(BAKE_QUEUE_PATH)


def _check_secret(request: Request) -> None:
    """Validate the API secret header if one is configured."""
    if not API_SECRET:
        return  # No secret configured → open access (dev mode)
    token = request.headers.get("X-API-Secret") or request.query_params.get("secret")
    if token != API_SECRET:
        raise HTTPException(status_code=401, detail="Unauthorized")


# ─────────────────────────────────────────────────────────────────────────────
# Routes
# ─────────────────────────────────────────────────────────────────────────────

@app.get("/health")
async def health() -> dict:
    return {"status": "ok", "library_entries": len(_load_library())}


@app.get("/library")
async def get_library(request: Request) -> JSONResponse:
    _check_secret(request)
    with _library_lock:
        library = _load_library()
    return JSONResponse(content=list(library.values()))


@app.get("/library/{movie_id}")
async def get_movie(movie_id: str, request: Request) -> JSONResponse:
    _check_secret(request)
    with _library_lock:
        library = _load_library()
    entry = library.get(movie_id)
    if entry is None:
        raise HTTPException(status_code=404, detail=f"Movie '{movie_id}' not found")
    return JSONResponse(content=entry)


@app.post("/library/{movie_id}/bake")
async def enqueue_bake(movie_id: str, request: Request) -> JSONResponse:
    """Enqueue a Clarity Pack bake job for a given movie ID."""
    _check_secret(request)
    with _library_lock:
        library = _load_library()
    entry = library.get(movie_id)
    if entry is None:
        raise HTTPException(status_code=404, detail=f"Movie '{movie_id}' not found")

    source_path = str(MEDIA_DIR / entry["filename"])
    target_path = str(CLARITY_PACK_DIR / f"{movie_id}_clarity.mp4")

    with _library_lock:
        queue = _load_bake_queue()
        existing = {job["movie_id"] for job in queue}
        if movie_id in existing:
            return JSONResponse(content={"status": "already_queued", "movie_id": movie_id})

        queue.append({
            "movie_id": movie_id,
            "source_path": source_path,
            "target_path": target_path,
            "status": "queued",
        })
        _save_bake_queue(queue)

    logger.info("Bake enqueued for %s via API", movie_id)
    return JSONResponse(content={"status": "queued", "movie_id": movie_id})


@app.get("/hls/{movie_id}/index.m3u8")
async def serve_hls_playlist(movie_id: str, request: Request) -> Response:
    """Serve the HLS playlist for a given movie ID."""
    _check_secret(request)
    playlist_path = HLS_ROOT / movie_id / "index.m3u8"
    if not playlist_path.exists():
        raise HTTPException(status_code=404, detail=f"HLS playlist not found for '{movie_id}'")
    return FileResponse(
        path=str(playlist_path),
        media_type="application/vnd.apple.mpegurl",
        headers={"Access-Control-Allow-Origin": "*"},
    )


@app.get("/hls/{movie_id}/{segment}")
async def serve_hls_segment(movie_id: str, segment: str, request: Request) -> Response:
    """Serve fmp4 segments or init files."""
    _check_secret(request)
    seg_path = HLS_ROOT / movie_id / segment
    if not seg_path.exists():
        raise HTTPException(status_code=404, detail="Segment not found")
    media_type = "video/mp4" if segment.endswith(".m4s") else "application/octet-stream"
    return FileResponse(path=str(seg_path), media_type=media_type,
                        headers={"Access-Control-Allow-Origin": "*"})


@app.get("/thumbs/{movie_id}")
async def serve_thumbs_vtt(movie_id: str, request: Request) -> Response:
    """Serve the WebVTT thumbnail track."""
    _check_secret(request)
    vtt_path = HLS_ROOT / movie_id / "thumbs.vtt"
    if not vtt_path.exists():
        raise HTTPException(status_code=404, detail=f"Thumbs not ready for '{movie_id}'")
    return PlainTextResponse(
        content=vtt_path.read_text(encoding="utf-8"),
        media_type="text/vtt",
        headers={"Access-Control-Allow-Origin": "*"},
    )


@app.get("/thumbs/{movie_id}/{filename}")
async def serve_thumb_image(movie_id: str, filename: str, request: Request) -> Response:
    """Serve individual thumbnail JPEG."""
    _check_secret(request)
    thumb_path = HLS_ROOT / movie_id / "thumbs" / filename
    if not thumb_path.exists():
        raise HTTPException(status_code=404, detail="Thumbnail not found")
    return FileResponse(path=str(thumb_path), media_type="image/jpeg",
                        headers={"Access-Control-Allow-Origin": "*"})


# ─────────────────────────────────────────────────────────────────────────────
# Startup: launch background scanner
# ─────────────────────────────────────────────────────────────────────────────

@app.on_event("startup")
async def _startup() -> None:
    # Import here to avoid circular issues at module level
    from package import start_background_scanner
    start_background_scanner()
    logger.info("PackagerAPI started on :8080")


# ─────────────────────────────────────────────────────────────────────────────
# Entrypoint
# ─────────────────────────────────────────────────────────────────────────────

if __name__ == "__main__":
    uvicorn.run(
        "api:app",
        host="0.0.0.0",
        port=8080,
        log_level="info",
        access_log=True,
    )
