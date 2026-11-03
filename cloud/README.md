# MoviePlayer Cloud API

Backend service for the **MoviePlayer VR Cinema** project. Provides:

- **Room management** – create / join / close viewing rooms with invite codes
- **HLS token auth** – signs JWT-gated HLS URLs; MediaMTX calls the auth webhook to validate viewers
- **WebSocket sync hub** – real-time play / pause / seek synchronisation with wall-clock timestamps
- **Pre-cache lobby** – guests report buffer status; host sees per-friend ready state before playback

---

## Tech Stack

| Layer | Technology |
|---|---|
| Runtime | Node 20 |
| Framework | Express 4 |
| WebSockets | ws |
| ORM | Prisma 5 (PostgreSQL) |
| Cache / pub-sub | Redis (ioredis) |
| Auth | jsonwebtoken (HS256) |
| Hosting | Fly.io |

---

## Local Development

```bash
# 1. Install dependencies
cd cloud
npm install

# 2. Copy and fill in environment variables
cp .env.example .env

# 3. Start PostgreSQL and Redis (Docker is easiest)
docker run -d --name pg -e POSTGRES_PASSWORD=password -p 5432:5432 postgres:16-alpine
docker run -d --name redis -p 6379:6379 redis:7-alpine

# 4. Run Prisma migrations
npx prisma migrate dev --name init

# 5. Start dev server (hot-reload)
npm run dev
```

The API will be available at `http://localhost:3000`.

---

## Deploy to Fly.io

### Prerequisites

```bash
# Install flyctl
curl -L https://fly.io/install.sh | sh   # macOS / Linux
# or: iwr https://fly.io/install.ps1 -useb | iex  # Windows PowerShell

# Sign in
fly auth login
```

### First-time launch

```bash
cd cloud
fly launch --no-deploy
```

Accept the generated `fly.toml` or use the one already committed.

### Set secrets

```bash
fly secrets set \
  JWT_SECRET="$(node -e "console.log(require('crypto').randomBytes(64).toString('hex'))")" \
  DATABASE_URL="postgresql://user:pass@host:5432/movieplayer" \
  REDIS_URL="redis://default:pass@host:6379"
```

> **Tip:** Use [Fly Postgres](https://fly.io/docs/postgres/) and [Upstash Redis](https://upstash.com/) for managed databases with zero ops overhead.

### Deploy

```bash
fly deploy
```

### Run database migrations after deploy

```bash
fly ssh console --command "npx prisma migrate deploy"
```

---

## REST API Reference

All protected endpoints require `Authorization: Bearer <token>`.

### `POST /rooms`

Create a new viewing room.

**Body**
```json
{
  "hostDeviceId": "device-uuid",
  "displayName": "Alice",
  "hlsBaseUrl": "https://media.example.com/live/movie/index.m3u8",
  "mediaId": "tt1234567",
  "mediaName": "My Movie (2024)"
}
```

**Response `201`**
```json
{
  "code": "XK9PQ2",
  "token": "<jwt>",
  "roomId": "clxyz...",
  "hlsUrl": "https://media.example.com/live/movie/index.m3u8?token=<jwt>"
}
```

---

### `POST /rooms/:code/join`

Join an existing room as a guest.

**Body**
```json
{
  "deviceId": "device-uuid",
  "displayName": "Bob"
}
```

**Response `201`**
```json
{
  "token": "<jwt>",
  "roomId": "clxyz...",
  "hlsUrl": "https://media.example.com/live/movie/index.m3u8?token=<jwt>",
  "mediaId": "tt1234567",
  "mediaName": "My Movie (2024)"
}
```

**Error codes**
- `404` – room not found
- `409` – room is full (max 4 seats)
- `410` – room has expired

---

### `GET /rooms/:code`

Get room info, seat list, and current play state.

**Response `200`**
```json
{
  "roomId": "clxyz...",
  "code": "XK9PQ2",
  "mediaId": "tt1234567",
  "mediaName": "My Movie (2024)",
  "hlsBaseUrl": "https://...",
  "expiresAt": "2026-11-03T19:47:00.000Z",
  "seats": [
    { "deviceId": "...", "displayName": "Alice", "role": "HOST", "joinedAt": "..." },
    { "deviceId": "...", "displayName": "Bob",   "role": "GUEST", "joinedAt": "..." }
  ],
  "playState": { "playing": false, "t_ms": 12345, "host_wall_ms": 1762130820000 }
}
```

---

### `DELETE /rooms/:code` *(HOST only)*

Close the room. Requires `Authorization: Bearer <host-token>`.

**Response `200`**
```json
{ "message": "Room closed" }
```

---

### `POST /auth/hls`

**MediaMTX auth webhook.** Called by the media server for every HLS subscriber. The client's JWT must be passed as a Bearer token in the `Authorization` header, or in the `token` body / query field.

Returns `200 {}` to allow, `403` to deny, `429` if rate-limited.

---

## WebSocket Sync API

Connect to `wss://<host>/ws?token=<jwt>`.

### Server → Client messages

| Type | Payload | Description |
|---|---|---|
| `joined` | `{ roomId, seats, playState }` | Sent immediately on connect |
| `peer_joined` | `{ deviceId, displayName, role }` | A new peer joined |
| `peer_left` | `{ deviceId, displayName, role }` | A peer disconnected |
| `state` | `{ t_ms, playing, host_wall_ms, from }` | Full state sync from host |
| `play` | `{ t_ms, host_wall_ms, from }` | Host started playback |
| `pause` | `{ t_ms, from }` | Host paused |
| `seek` | `{ t_ms, from }` | Host seeked (guests should rebuffer) |
| `buffer` | `{ deviceId, displayName, buffered_s, ready }` | Guest buffer status (relayed) |
| `pong` | — | Reply to `ping` |

### Client → Server messages

| Type | Payload | Who | Description |
|---|---|---|---|
| `ping` | — | Any | Keep-alive |
| `state` | `{ t_ms, playing, host_wall_ms }` | HOST | Full state broadcast |
| `play` | — | HOST | Start playback |
| `pause` | `{ t_ms }` | HOST | Pause at position |
| `seek` | `{ t_ms }` | HOST | Seek to position |
| `buffer` | `{ buffered_s, ready }` | GUEST | Report buffer status |

### Connection close codes

| Code | Reason |
|---|---|
| `4001` | Missing or invalid token |
| `4004` | Room not found |
| `4010` | Room expired |
| `4500` | Internal server error |

---

## Environment Variables

| Variable | Required | Description |
|---|---|---|
| `JWT_SECRET` | ✅ | HS256 signing secret (min 32 chars recommended) |
| `DATABASE_URL` | ✅ | PostgreSQL connection string |
| `REDIS_URL` | ✅ | Redis connection string |
| `PORT` | ❌ | HTTP port (default: `3000`) |
