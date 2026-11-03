# MoviePlayer VR Cinema – Home Node

The Home Node runs on your local machine or NAS and handles:

- **Video library scanning** – auto-detects stereo layout, projection, and codec compatibility
- **HLS packaging** – FFmpeg produces fmp4 segments served by MediaMTX
- **Clarity Pack baking** – Real-ESRGAN 2× AI upscale → HEVC Main10 for crisp VR video
- **Cloudflared tunnel** – exposes the local node securely to your Quest headsets via Cloudflare Zero Trust

---

## Prerequisites

| Requirement | Notes |
|---|---|
| [Docker Desktop](https://www.docker.com/products/docker-desktop/) ≥ 4.25 | Windows / macOS / Linux |
| [NVIDIA Container Toolkit](https://docs.nvidia.com/datacenter/cloud-native/container-toolkit/install-guide.html) | **Optional** – GPU acceleration for Real-ESRGAN; CPU fallback works without it |
| A Cloudflare account | Free tier is sufficient; used only for tunnelling |
| ~10 GB free disk per movie | For HLS segments; Clarity Packs need an additional 20-80 GB per title |

### NVIDIA GPU setup (optional but recommended)

On Windows with Docker Desktop, enable **GPU support** in Docker Desktop settings:
- *Settings → Resources → GPU → Enable GPU Support*

On Linux, follow the [NVIDIA Container Toolkit](https://docs.nvidia.com/datacenter/cloud-native/container-toolkit/install-guide.html) guide, then verify with:
```bash
docker run --rm --gpus all nvidia/cuda:12.1.0-base-ubuntu22.04 nvidia-smi
```

---

## Setup

### 1. Clone the repository

```bash
git clone https://github.com/your-org/movieplayer.git
cd movieplayer/home-node
```

### 2. Configure environment

```bash
cp .env.example .env
```

Open `.env` in a text editor and fill in:

| Variable | What to put |
|---|---|
| `CLOUDFLARE_TUNNEL_TOKEN` | Token from Cloudflare Zero Trust → Tunnels → Create Tunnel → Docker install command |
| `CLOUD_API_URL` | Base URL of the MoviePlayer cloud relay (e.g. `https://api.example.com`) |
| `API_SECRET` | A long random secret shared with the cloud relay |
| `MEDIA_DIR` | **Absolute path** to your movies folder (e.g. `D:/Movies` or `/mnt/nas/movies`) |
| `CLARITY_PACK_DIR` | Where upscaled Clarity Packs will be stored (needs lots of space) |
| `CLARITY_FAST_MODE` | `0` for AI upscale (slow, best), `1` for FFmpeg-only (fast preview) |

### 3. Start the stack

```bash
docker compose up -d
```

Docker will build the packager and clarity-worker images on first run (~5-10 minutes).

### 4. Verify

```bash
# Check all containers are healthy
docker compose ps

# View packager logs (scanning + HLS packaging progress)
docker compose logs -f packager

# Clarity worker logs (bake jobs)
docker compose logs -f clarity-worker

# MediaMTX logs (HLS server)
docker compose logs -f mediamtx
```

The packager API is available at **http://localhost:8080**:

```bash
# Library contents
curl http://localhost:8080/library

# Single movie manifest
curl http://localhost:8080/library/<id>

# Manually trigger a Clarity bake
curl -X POST http://localhost:8080/library/<id>/bake \
     -H "X-API-Secret: <your API_SECRET>"
```

---

## How it works

```
MEDIA_DIR
   └── movie.mkv
          │
          ▼
     [packager]  ──ffprobe──►  stereo_classifier  ──► manifest
          │                    format_compat
          │
          ├── FFmpeg HLS ──► /hls/{id}/ ──► [mediamtx] ──► Quest headset
          │                                    :8888 (HLS)
          │
          └── bake_queue.json ──► [clarity-worker]
                                        │
                              Real-ESRGAN 2× upscale
                              HEVC Main10 encode
                                        │
                              CLARITY_PACK_DIR/{id}_clarity.mp4
                                        │
                              [packager] re-packages clarity as HLS

[cloudflared] tunnels :8080 (API) + :8888 (HLS) to the internet via Cloudflare Zero Trust
```

---

## Clarity Pack baking

Clarity Packs are optional AI-upscaled versions of your movies:

- **Recommended when**: source resolution < 1920 px per eye (e.g. old 1080p SBS content)
- **Full mode** (`CLARITY_FAST_MODE=0`): Real-ESRGAN 4× upscale → 2× output (best quality, GPU recommended)
- **Fast mode** (`CLARITY_FAST_MODE=1`): FFmpeg `unsharp` + `hqdn3d` filters (minutes vs. hours)

Bake jobs are enqueued automatically for `bake_recommended` titles, or manually via the API.

---

## Stopping the stack

```bash
docker compose down
```

HLS segments and the library index are preserved in Docker volumes; your source files in `MEDIA_DIR` are never modified.

---

## Troubleshooting

| Symptom | Fix |
|---|---|
| Packager not indexing files | Check `MEDIA_DIR` is mounted and readable: `docker compose exec packager ls /media` |
| HLS not playing on Quest | Verify the Cloudflare tunnel is connected: `docker compose logs cloudflared` |
| Clarity bake OOM crash | Set `CLARITY_FAST_MODE=1` or add more RAM/VRAM |
| `ffprobe: not found` | Rebuild the packager image: `docker compose build packager` |
| `nvidia-smi` not found in container | Install NVIDIA Container Toolkit on the host |
