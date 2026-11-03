# MoviePlayer — Quest Standalone 3D VR Cinema Plan

**Goal:** Beat 4XVR on perceived clarity for 3D movies, and beat Bigscreen/Skybox on *easy private watch-together* for up to **4 friends**. Friends-only for now (invite codes, no public lobby).

**Primary platform:** Meta Quest 3 / 3S standalone (OpenXR / Meta XR SDK).

---

## 1. Competitive wedge

| Competitor | Strength | Gap we fill |
|---|---|---|
| **4XVR** | MVC 3D, “4X Clarity”, USB/NAS, format breadth | No watch-together yet; clarity is proprietary, not a full AI/pre-cache pipeline |
| **Bigscreen** | Social rooms | Weak local/USB/SMB library + format depth vs dedicated players |
| **Skybox** | Solid player; multiplayer in preview | Clarity/MVC/upscale not the focus |

**Our product promise:**  
“Drop any 3D movie in → auto-detect stereo → pre-cache + upscale pack → friends join a private room in under 30 seconds.”

---

## 2. Product scope (v1 — friends only)

### In scope
- Local + USB + SMB/NAS library
- Auto stereo / projection detection (2D, FSBS, HSBS, FOU, HOU, 180/360)
- Built-in clarity stack (see §4)
- Pre-cache + sampling pipeline (see §5)
- Private rooms, max **4** users
- Host playback sync (play/pause/seek) + spatial voice
- Invite code / deep link join
- One cinematic environment (simple theater) + flat screen mode

### Out of scope (later)
- Public lobbies, avatars cosmetics shop
- Licensed streaming catalogs
- MVC Blu-ray ISO parity with 4XVR (phase 2 stretch)
- Cloud AI upscale for strangers (cost/abuse)

---

## 3. System architecture

```
┌─────────────────────────────────────────────────────────────┐
│                     QUEST APP (client)                      │
│  Library UI · Probe/Classify · Player · Clarity · Room UX   │
└───────────────┬───────────────────────────┬─────────────────┘
                │                           │
        media I/O                    realtime sync
                │                           │
┌───────────────▼──────────────┐   ┌────────▼────────────────┐
│     HOME MEDIA NODE          │   │   CLOUD CONTROL PLANE   │
│  (PC or NAS Docker)          │   │   (friends-only, tiny)  │
│  - library index             │   │   - auth / invite codes │
│  - FFmpeg probe/transcode    │   │   - room signaling      │
│  - AI upscale bake jobs      │   │   - WebRTC TURN assist  │
│  - HLS/fMP4 packager         │   │   - presence (≤4)       │
│  - optional LAN relay        │   └─────────────────────────┘
└──────────────────────────────┘
```

### Why this split
- **Quest alone cannot win a pure AI upscale war** against PC tools; thermal/GPU headroom is too thin for sustained neural video SR.
- **Winning path = hybrid clarity:** on-device Meta Super Resolution + custom sharpening/temporal + **home-node offline AI bake** into a VR-optimized cache that the headset plays natively.
- **Social sync stays light:** cloud only does rooms/signaling; heavy video stays on LAN/home node or each friend’s local copy.

---

## 4. Clarity / upscaling strategy (compete with 4XVR)

Treat “upscaling” as a **Clarity Stack**, not one slider.

### Tier A — Always-on (Quest realtime)
1. **Meta Quest Super Resolution** (compositor path) when eyebuffer < panel res
2. Custom **edge-aware RCAS / CAS** sharpen tuned for cinema (less haloing than game sharpen)
3. **Chroma restore** + mild deband for 8-bit streams
4. **HDR tone map** with luminance curve (4XVR’s HDR Luminance competitor)
5. Per-eye **resolution bias** (render screen quad sharper than environment)

### Tier B — Pre-cache bake (Home Media Node) — *our real 4XVR killer*
Offline (or overnight) job per title:

| Step | Output |
|---|---|
| Probe + stereo classify | sidecar JSON |
| Optional denoise | intermediate |
| AI spatial upscale (e.g. Real-ESRGAN / Anime4K / commercial SR) to target | `*_clarity.mp4/mkv` |
| Remux to Quest-friendly profile | HEVC Main/Main10, 4K–6K class, high bitrate, correct SAR |
| Build seek index + thumb strip | `.idx` + `.thumbs` |
| Package as **Clarity Pack** | folder or single container + manifest |

**Targets (Quest 3 practical):**
- Flat 3D SBS/OU: upscale each eye toward **~2K–3K per eye** effective (pack as HSBS/HOU HEVC)
- Prefer **one bake quality preset**: `Quality` (slow) and `Fast` (quick preview bake)

### Tier C — Adaptive sampling
- Sample N frames at open → estimate blur / compression → pick sharpen strength + whether to recommend bake
- Stereo sampling: detect SBS vs OU, eye swap, black bars, half vs full
- Bitrate/resolution score shown as “Clarity ready / Bake recommended”

### Honest constraint
Realtime **neural** 4× video SR on Quest during 3D decode is not competitive. Market the hybrid: **instant Clarity A + optional Clarity Pack B**. That is how you out-look 4XVR on prepared libraries while staying usable on raw files.

---

## 5. Pre-cache & format compatibility

### Ingest pipeline (Home Node + on-headset light probe)
1. **ffprobe** → codecs, resolution, fps, audio, chapters  
2. **Layout classifier** → stereo + projection confidence  
3. **Compatibility matrix**
   - Native play if Quest hardware decode OK (H.264/HEVC/AV1 where available)
   - Soft path: remux only
   - Hard path: transcode to HEVC + AAC/Opus (or pass-through multi-channel if device supports)
4. **Pre-cache**
   - Keyframe / GOP index for instant seek
   - Thumbnail filmstrip sampling
   - First 30–60s decode warm
   - Optional Clarity Pack bake queue
5. **Sidecar manifest** (example fields)

```json
{
  "id": "movie-uuid",
  "source": "smb://nas/movies/foo.mkv",
  "stereo": "hsbs",
  "projection": "flat",
  "eye_swap": false,
  "native_playable": true,
  "clarity_pack": "packs/foo_q.hevc.mp4",
  "seek_index": "packs/foo.idx",
  "thumbs": "packs/foo.thumbs",
  "audio_delay_ms": 0,
  "sample_score": 0.74
}
```

### Format ambition (phased)
- **v1:** mp4/mkv/mov/webm + H.264/HEVC/VP9/AV1; SBS/OU; 180/360 equirect  
- **v1.5:** m2ts/ts, better 10-bit HDR tone map, spatial audio binauralize  
- **v2:** MVC / Blu-ray 3D ISO / SSIF (hard; 4XVR’s moat)  
- **v2:** MV-HEVC spatial video soft decode where feasible  

“Every file type” is a **compatibility engine goal**, not a day-one guarantee. Ship a visible “Will play / Needs convert / Needs bake” state so friends aren’t guessing.

---

## 6. Multiplayer (≤4) — friends only

### Join model
- Host creates **Private Room** → 6-character invite code + Quest deep link
- Max 4 seats (host + 3)
- No matchmaking, no public list
- Optional room PIN + expire after 24h

### Sync model (recommended)
**Timeline sync + each client plays media independently** when possible:

1. Prefer each friend has the **same Clarity Pack / file hash** (LAN copy or prior sync)
2. Else Home Node **streams** Quest-optimized fMP4/HLS to guests (LAN first; WAN via relay)
3. Host is authority for `t`, play state, rate
4. Clients slew (small rate adjust) to stay within ~50–100ms

Avoid re-encoding a live “shared desktop” capture for 3D — quality dies and stereo breaks.

### Voice
- WebRTC audio mesh for 4 people (or SFU if WAN flaky)
- Push-to-talk + open mic
- Duck movie audio when someone talks (optional)

### Presence
- Simple seated avatars or nameplates in theater seats (keep cheap)
- Head pose optional; hands optional for v1

### Bandwidth sketch (WAN stream fallback)
- Cap guest stream ~15–25 Mbps HEVC for 4K-class HSBS
- 3 guests × 20 Mbps ≈ 60 Mbps up from home — needs solid upload or pre-share files
- **Friends UX default:** “Sync library” / “Send Clarity Pack” over LAN before movie night

---

## 7. Services & server plan

### A. Cloud Control Plane (always-on, cheap)
**Stack suggestion:** Fly.io / Railway / small VPS + Cloudflare

| Service | Role |
|---|---|
| `api` | accounts (optional), invite codes, room CRUD |
| `signal` | WebSocket room signaling + sync authority relay |
| `turn` | coturn for WebRTC voice / fallback media |
| `postgres` | users, rooms, invites, device ids |
| `redis` | ephemeral room state |

**Auth for friends-only v1:**  
device-linked accounts OR shared household “crew” code; no Meta-only lock-in required beyond Quest identity if you want simpler: **host-generated room codes only** (zero accounts) for alpha.

### B. Home Media Node (Docker on PC/NAS)
Like 4X Link, but with bake + packager:

| Container | Role |
|---|---|
| `indexer` | scan folders / SMB mounts |
| `ffmpeg-worker` | probe, remux, transcode, thumbs |
| `clarity-worker` | AI upscale bake (GPU optional; CPU slower) |
| `media-api` | library JSON, pack download, HLS |
| `relay` (optional) | authenticated WAN stream to friends |

**GPU note:** Clarity bake prefers NVIDIA on the home PC. NAS CPU-only is fine for remux/thumbs; AI bake can be “send to PC overnight.”

### C. Quest Client
- Unity or Unreal + Meta XR + OpenXR  
- Video: ExoPlayer-class path via native plugin / Unity Video + custom demux where needed  
- Clarity A shaders + Super Resolution flag  
- Room UI + voice

**Recommendation:** Unity + custom native decode plugin for format edge cases; don’t rely on Unity Video alone for “every file.”

---

## 8. Sync protocol (minimal)

Host → peers (10–20 Hz when dirty, else 1 Hz heartbeat):

```json
{
  "room": "AB12CD",
  "media_id": "movie-uuid",
  "pack_hash": "sha256:...",
  "t_ms": 3724012,
  "playing": true,
  "host_wall_ms": 1730000000123,
  "ctrl": "host_only"
}
```

Clients compute offset vs host wall clock; correct with pause/seek or ±2% rate until locked.

**Control modes:** Host-only (default) | Anyone can pause/seek.

---

## 9. Security & privacy (friends)
- Rooms private by default; codes single-use or time-limited  
- Home Node behind LAN; WAN requires token from cloud room  
- No public content CDN in v1  
- Don’t log filenames to cloud (media stays local); cloud only sees room ids + hashes if needed  
- Voice P2P/TURN; no recording

---

## 10. Phased delivery

### Phase 0 — Spike (1–2 weeks)
- Quest app plays local HSBS/HOU file in theater
- Enable Super Resolution + custom sharpen
- ffprobe classify stub
- Fake room: 2 Quest devices sync playhead over LAN WebSocket

### Phase 1 — Solo Clarity MVP
- Library (local/USB)
- Sidecar classify + thumbs
- Clarity A stack polished
- Home Node: remux + seek index + thumb cache

### Phase 2 — Clarity Packs
- Bake worker + manifest
- In-app “Enhance for Quest” queue
- A/B compare Raw vs Pack

### Phase 3 — Friends rooms (≤4)
- Cloud signaling + invite codes
- Timeline sync + voice
- LAN pack share / Home Node stream fallback

### Phase 4 — Format moat
- SMB/Jellyfin-like browse
- HDR luminance tools
- MVC / ISO research spike
- 180/360 path parity

---

## 11. Cost estimate (friends-only ops)

| Item | Approx |
|---|---|
| Cloud API + WS + Postgres | $5–20/mo |
| TURN traffic | low if LAN-first; $0–15/mo for 4 friends |
| Home Node | existing PC/NAS electricity |
| AI bake | local GPU time (no API fees if self-hosted) |

Keep cloud dumb and cheap; put intelligence on the Home Node.

---

## 12. Success metrics
- Time-to-first-frame from USB/SMB < 3s for indexed titles  
- Seek scrub without hitch on cached titles  
- Blind A/B: friends prefer our Clarity Pack vs 4XVR on same source ≥70%  
- Room join → in-seat watching < 30s  
- Sync drift < 100ms for 95% of session  
- 4-person voice workable on typical home Wi-Fi 6

---

## 13. Recommended tech choices (lock for v1)

| Layer | Choice |
|---|---|
| Quest app | Unity 6 + Meta XR SDK + OpenXR |
| Decode | Native Android media + FFmpeg fallback plugin |
| Clarity A | Quest Super Resolution + custom cinema RCAS |
| Clarity B | Home Docker + FFmpeg + Real-ESRGAN (or better SR later) |
| Home Node | Docker Compose on Windows/Linux NAS |
| Cloud | Node/Go API + WS + Postgres + Redis + coturn |
| Sync | Custom timeline protocol over WS |
| Voice | WebRTC (Unity websocket signaling) |

---

## 14. Immediate next build order
1. Repo skeleton: `quest-app/`, `home-node/`, `cloud/`, `proto/`  
2. Clarity A prototype on a known HSBS sample  
3. Home Node probe → manifest → thumbs  
4. 2-headset sync spike  
5. Invite-code rooms + voice  

---

## Open decisions (confirm next)
1. **Unity vs Unreal** for Quest client (lean Unity)?  
2. Home Node first on **Windows PC with NVIDIA** or NAS-only?  
3. Alpha join: **accountless room codes** OK?  
4. Must-have day-one sources: **USB only** or USB + SMB?  
5. Stretch: chase **MVC** in phase 4 or accept remuxed SBS/OU as v1 truth?
