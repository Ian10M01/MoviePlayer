# ─────────────────────────────────────────────────────────────────────────────
# MoviePlayer VR Cinema – Clarity Worker
#
# Upscales VR video frames with Real-ESRGAN and re-encodes to HEVC Main10.
# GPU acceleration is selected at runtime via torch.cuda.is_available().
# ─────────────────────────────────────────────────────────────────────────────

FROM python:3.11-slim-bookworm

# ── System packages ───────────────────────────────────────────────────────────
RUN apt-get update && apt-get install -y --no-install-recommends \
        # FFmpeg from official backport — needed for HEVC encoding + ffprobe
        ffmpeg \
        # Build tools (some PyTorch wheels need them)
        build-essential \
        libgomp1 \
        # Image codec support for Pillow
        libjpeg-dev \
        libpng-dev \
        libwebp-dev \
        # General utilities
        curl \
        git \
    && rm -rf /var/lib/apt/lists/*

# ── Python packages ───────────────────────────────────────────────────────────
WORKDIR /app

# Install PyTorch first (separate step for better layer caching).
# CPU build by default; NVIDIA runtime provides CUDA libs when a GPU is present.
# The install command tries the CUDA 12.1 index first and falls back gracefully.
RUN pip install --no-cache-dir \
        torch torchvision \
        --index-url https://download.pytorch.org/whl/cu121 \
    || pip install --no-cache-dir torch torchvision

# Real-ESRGAN and friends
RUN pip install --no-cache-dir \
        basicsr==1.4.2 \
        realesrgan==0.3.0 \
        facexlib \
        gfpgan \
        ffmpeg-python==0.2.0 \
        Pillow==10.3.0 \
        numpy

# ── Application code ──────────────────────────────────────────────────────────
COPY bake.py ./

# Real-ESRGAN model weights are downloaded on first run by the library.
# To pre-bake them into the image (optional, for offline use):
# RUN python -c "from realesrgan import RealESRGANer; from basicsr.archs.rrdbnet_arch import RRDBNet; \
#     import torch; model=RRDBNet(num_in_ch=3,num_out_ch=3,num_feat=64,num_block=23,num_grow_ch=32,scale=4); \
#     RealESRGANer(scale=4,model_path='https://github.com/xinntao/Real-ESRGAN/releases/download/v0.1.0/RealESRGAN_x4plus.pth',model=model)"

# ── Entrypoint ────────────────────────────────────────────────────────────────
CMD ["python", "-u", "bake.py"]
