# ─────────────────────────────────────────────────────────────────────────────
# MoviePlayer VR Cinema – Packager Service
# Based on jrottenberg/ffmpeg for a slim image with FFmpeg + ffprobe built in.
# ─────────────────────────────────────────────────────────────────────────────
FROM jrottenberg/ffmpeg:6.1-ubuntu2204 AS ffmpeg-base

FROM python:3.11-slim-bookworm

# Copy FFmpeg binaries from the dedicated image
COPY --from=ffmpeg-base /usr/local/bin/ffmpeg  /usr/local/bin/ffmpeg
COPY --from=ffmpeg-base /usr/local/bin/ffprobe /usr/local/bin/ffprobe
COPY --from=ffmpeg-base /usr/local/lib         /usr/local/lib

# System deps
RUN apt-get update && apt-get install -y --no-install-recommends \
        libgomp1 \
        curl \
    && rm -rf /var/lib/apt/lists/*

# Python deps
WORKDIR /app
COPY requirements.txt ./
RUN pip install --no-cache-dir -r requirements.txt

# Application code
COPY package.py stereo_classifier.py format_compat.py api.py ./

# Expose the micro-API port
EXPOSE 8080

# The packager runs both the background scanner loop and the API in one process.
# api.py starts the background thread internally.
CMD ["python", "-u", "api.py"]
