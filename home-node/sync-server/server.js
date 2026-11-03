'use strict';

// ─────────────────────────────────────────────────────────────────────────────
// MoviePlayer – In-Memory Watch-Party Sync Server
// No database, no Redis, no JWT.  Room code IS the token.
// Max 2 peers: one host + one guest.
// ─────────────────────────────────────────────────────────────────────────────

const express  = require('express');
const http     = require('http');
const { WebSocketServer } = require('ws');
const { nanoid } = require('nanoid');

const PORT = process.env.PORT || 3000;

// ── In-memory state ───────────────────────────────────────────────────────────
// rooms: Map<code, Room>
// Room = { code, hlsUrl, hostWs, guestWs, state: { t_ms, playing, host_wall_ms } }
const rooms = new Map();

function makeCode() {
  // 6 uppercase alphanumeric characters, easy to read aloud
  return nanoid(6).toUpperCase().replace(/[^A-Z0-9]/g, () =>
    'ABCDEFGHJKLMNPQRSTUVWXYZ23456789'[Math.floor(Math.random() * 32)]
  ).slice(0, 6);
}

// ── Express app ───────────────────────────────────────────────────────────────
const app = express();
app.use(express.json());

// Health check
app.get('/health', (_req, res) => res.json({ ok: true, rooms: rooms.size }));

// POST /rooms — host creates a new room
// Body: { hlsUrl: string }  (optional; can be set later via WS)
app.post('/rooms', (req, res) => {
  const code   = makeCode();
  const hlsUrl = (req.body && req.body.hlsUrl) || '';
  const room   = {
    code,
    hlsUrl,
    hostWs:  null,
    guestWs: null,
    state: { t_ms: 0, playing: false, host_wall_ms: Date.now() },
  };
  rooms.set(code, room);

  console.log(`[room] created  code=${code}  hlsUrl=${hlsUrl || '(none yet)'}`);
  res.status(201).json({ code, token: code });
});

// POST /rooms/:code/join — guest joins
app.post('/rooms/:code/join', (req, res) => {
  const room = rooms.get(req.params.code.toUpperCase());
  if (!room) return res.status(404).json({ error: 'Room not found' });
  console.log(`[room] join     code=${room.code}`);
  res.json({ token: room.code, hlsUrl: room.hlsUrl });
});

// GET /rooms/:code — poll room state (useful for debugging)
app.get('/rooms/:code', (req, res) => {
  const room = rooms.get(req.params.code.toUpperCase());
  if (!room) return res.status(404).json({ error: 'Room not found' });
  res.json({
    code:    room.code,
    hlsUrl:  room.hlsUrl,
    peers:   (room.hostWs ? 1 : 0) + (room.guestWs ? 1 : 0),
    state:   room.state,
  });
});

// POST /auth/hls — MediaMTX publishUser/readUser webhook
// MediaMTX calls this with query ?token=<code> to gate HLS access.
app.post('/auth/hls', (req, res) => {
  // MediaMTX sends JSON body or query param depending on config
  const token = (req.query.token || (req.body && req.body.token) || '').toUpperCase();
  if (!token) return res.status(403).json({ error: 'Missing token' });
  if (rooms.has(token)) {
    return res.status(200).json({ ok: true });
  }
  console.warn(`[auth] rejected token=${token}`);
  res.status(403).json({ error: 'Invalid token' });
});

// ── HTTP + WebSocket server ────────────────────────────────────────────────────
const server = http.createServer(app);
const wss    = new WebSocketServer({ server, path: '/ws' });

wss.on('connection', (ws, req) => {
  const url   = new URL(req.url, `http://localhost`);
  const token = (url.searchParams.get('token') || '').toUpperCase();
  const room  = rooms.get(token);

  if (!room) {
    ws.send(JSON.stringify({ type: 'error', msg: 'Invalid room code' }));
    ws.close(4001, 'Invalid room code');
    return;
  }

  // Assign role: first connection is host, second is guest
  let role;
  if (!room.hostWs) {
    role = 'host';
    room.hostWs = ws;
  } else if (!room.guestWs) {
    role = 'guest';
    room.guestWs = ws;
  } else {
    ws.send(JSON.stringify({ type: 'error', msg: 'Room full (max 2 peers)' }));
    ws.close(4002, 'Room full');
    return;
  }

  console.log(`[ws]  connected  code=${room.code}  role=${role}`);
  ws.send(JSON.stringify({ type: 'welcome', role, state: room.state }));

  // Notify the other peer that someone joined
  const other = role === 'host' ? room.guestWs : room.hostWs;
  if (other && other.readyState === 1 /* OPEN */) {
    other.send(JSON.stringify({ type: 'peer_joined', role }));
  }

  ws.on('message', (data) => {
    let msg;
    try { msg = JSON.parse(data); } catch { return; }

    // ── Message handlers ────────────────────────────────────────────────────
    switch (msg.type) {

      case 'play':
        room.state.playing    = true;
        room.state.t_ms       = msg.t_ms       ?? room.state.t_ms;
        room.state.host_wall_ms = msg.wall_ms  ?? Date.now();
        broadcast(room, ws, { type: 'play', t_ms: room.state.t_ms, wall_ms: room.state.host_wall_ms });
        break;

      case 'pause':
        room.state.playing = false;
        room.state.t_ms    = msg.t_ms ?? room.state.t_ms;
        broadcast(room, ws, { type: 'pause', t_ms: room.state.t_ms });
        break;

      case 'seek':
        room.state.t_ms    = msg.t_ms;
        room.state.playing = false;
        broadcast(room, ws, { type: 'seek', t_ms: msg.t_ms });
        break;

      case 'buffer':
        // Host reports buffered ms; forward to guest for display
        broadcast(room, ws, { type: 'buffer', buffered_ms: msg.buffered_ms, from: role });
        break;

      case 'state_request':
        // Guest asks for current state (re-sync)
        ws.send(JSON.stringify({ type: 'state', state: room.state }));
        break;

      case 'hlsUrl':
        // Host sets the HLS URL mid-session
        if (role === 'host') {
          room.hlsUrl = msg.hlsUrl;
          broadcast(room, ws, { type: 'hlsUrl', hlsUrl: msg.hlsUrl });
          console.log(`[room] hlsUrl set code=${room.code} url=${msg.hlsUrl}`);
        }
        break;

      case 'ping':
        ws.send(JSON.stringify({ type: 'pong', ts: msg.ts }));
        break;

      default:
        // Forward unknown messages to the other peer transparently
        broadcast(room, ws, msg);
    }
  });

  ws.on('close', () => {
    if (room.hostWs  === ws) room.hostWs  = null;
    if (room.guestWs === ws) room.guestWs = null;
    console.log(`[ws]  disconnected  code=${room.code}  role=${role}`);

    // Notify the remaining peer
    const remaining = room.hostWs || room.guestWs;
    if (remaining && remaining.readyState === 1) {
      remaining.send(JSON.stringify({ type: 'peer_left', role }));
    }

    // Clean up empty rooms after 5 minutes
    if (!room.hostWs && !room.guestWs) {
      setTimeout(() => {
        if (!room.hostWs && !room.guestWs) {
          rooms.delete(room.code);
          console.log(`[room] removed  code=${room.code}`);
        }
      }, 5 * 60 * 1000);
    }
  });

  ws.on('error', (err) => {
    console.error(`[ws]  error  code=${room.code}  role=${role}  err=${err.message}`);
  });
});

function broadcast(room, senderWs, payload) {
  const msg = JSON.stringify(payload);
  for (const peer of [room.hostWs, room.guestWs]) {
    if (peer && peer !== senderWs && peer.readyState === 1 /* OPEN */) {
      peer.send(msg);
    }
  }
}

// ── Start ─────────────────────────────────────────────────────────────────────
server.listen(PORT, () => {
  console.log(`\n🎬 MoviePlayer Sync Server running on :${PORT}`);
  console.log(`   POST /rooms        → create room (returns { code, token })`);
  console.log(`   POST /rooms/:code/join → guest joins`);
  console.log(`   WS   /ws?token=CODE → real-time sync\n`);
});
