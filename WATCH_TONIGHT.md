# 🎬 Watch Tonight — Free VR Watch Party on Meta Quest

**Total cost: $0.  No accounts.  No credit cards.  No cloud subscriptions.**

---

## What you need

| Item | Notes |
|------|-------|
| Windows PC with Docker Desktop | Free download: [docker.com/products/docker-desktop](https://www.docker.com/products/docker-desktop) |
| Your movies folder | Any SBS / OU 3D MKV, MP4, MKV, etc. |
| Two Meta Quest headsets (Quest 2, 3, or Pro) | Both need **Developer Mode** enabled |
| MoviePlayer APK sideloaded on both Quests | See `quest-app/README.md` |
| Both Quests on the same WiFi (or any WiFi) | Cellular hotspot works too |

---

## One-time PC setup (~10 minutes)

### 1. Install Docker Desktop
Download from [docker.com/products/docker-desktop](https://www.docker.com/products/docker-desktop), install, and start it.  
Wait for the whale icon in the system tray to stop animating.

### 2. Get this repo
```
git clone https://github.com/your-username/MoviePlayer.git
```
Or download the ZIP and extract it.

### 3. Create your `.env` file
```powershell
Copy-Item home-node\.env.example home-node\.env
notepad home-node\.env
```
Change these two lines:
```
MEDIA_DIR=C:/Users/YourName/Movies        ← your movies folder (forward slashes!)
CLARITY_PACK_DIR=C:/Users/YourName/ClarityPacks
```
Save and close.

### 4. Build the ExoPlayer AAR (once)
The Quest app needs a small Android library to play HLS video.

**Option A – Pre-built AAR provided (easiest)**  
If `quest-app/Assets/Plugins/Android/ExoPlayerPlugin.aar` already exists, skip to step 5.

**Option B – Build it yourself**  
See `quest-app/ExoPlayerPlugin/BUILD_INSTRUCTIONS.md` for the 5-step process.  
Takes about 3 minutes.

### 5. Build and sideload the Unity APK
See `quest-app/README.md` for Unity build instructions.  
Sideload to **both** Quest headsets:
```powershell
adb install -r MoviePlayer.apk
```

---

## Every watch session (~30 seconds)

### Step 1 — Double-click `start-watch-party.bat`
The script will:
- Start Docker services (packager, media server, sync server)
- Launch two free Cloudflare tunnels
- Print the tunnel URLs on screen

### Step 2 — Wait ~20 seconds for the URLs
You'll see something like:
```
── Media URL ──────────────────────────────────
https://random-words-1234.trycloudflare.com

── API URL ────────────────────────────────────
https://other-random-words-5678.trycloudflare.com
```

### Step 3 — Enter URLs in the Quest app
1. Put on your Quest and open **MoviePlayer**.
2. Tap **Settings** (gear icon).
3. Enter:
   - **Media URL** → the first `.trycloudflare.com` URL
   - **API URL** → the second `.trycloudflare.com` URL
4. Tap **Save**.

### Step 4 — Start a room (you, the host)
1. Tap **Library** → pick a movie.
2. Tap **Watch**.
3. A **6-letter room code** (e.g. `K7MN2P`) appears on screen.
4. Tell your friend the code — text, voice call, whatever.

### Step 5 — Friend joins
1. Friend opens **MoviePlayer** on their Quest.
2. Taps **Join**.
3. Types the 6-letter code → taps **Connect**.
4. Both headsets start buffering the movie.

### Step 6 — Hit Play!
When both headsets show **Ready**, tap **Play** on either headset.  
The sync engine keeps you within ~100 ms of each other automatically.

---

## Free tier notes

- **Cloudflare Quick Tunnels** are free forever for personal, non-commercial use.
- The tunnel URLs are **randomly generated each session** — re-enter them in Settings each time you restart.
- No Cloudflare account or domain required.
- Everything runs on your PC — no data leaves your home except through the tunnel.

---

## Troubleshooting

| Problem | Fix |
|---------|-----|
| Tunnel URL not showing in script output | Run `docker logs cinema-cloudflared-media` manually |
| "Room not found" on join | Make sure you entered the correct 6-letter code |
| Buffering forever | Your upload speed may be slow — try a smaller file, or set `CLARITY_FAST_MODE=1` in `.env` |
| "Can't connect" on Quest | Check WiFi — Quest and tunnel need internet access |
| Docker fails to start | Make sure Docker Desktop is fully running (whale icon, not spinning) |
| `.env` not found | Did you copy `.env.example` to `.env`? Both must be in `home-node/` |
| Tunnel URL shows "ERR_NGROK_…" | That's cloudflared, not ngrok — try restarting: `docker compose restart cloudflared-media cloudflared-api` |

### Quick diagnostic commands (run in PowerShell)
```powershell
# Check all containers are running
docker ps

# Live logs for everything
docker compose -f home-node\docker-compose.yml logs -f

# Re-read the tunnel URLs
docker logs cinema-cloudflared-media
docker logs cinema-cloudflared-api

# Stop everything
docker compose -f home-node\docker-compose.yml down
```

---

## Cost breakdown

| Service | Monthly cost |
|---------|-------------|
| Docker Desktop (personal use) | Free |
| Cloudflare Quick Tunnels | Free |
| MediaMTX (open source) | Free |
| Sync server (runs on your PC) | Free |
| **Total** | **$0** |
