'use strict';

// ─────────────────────────────────────────────────────────────────────────────
// MoviePlayer – In-Memory Watch-Party Sync Server  (v2 – contract-correct)
//
// Wire format (both directions)
// ──────────────────────────────
// Every WebSocket message is a JSON "envelope":
//   { "type": "<string>", "payload": "<JSON-string>" }
//
// The payload is a JSON-encoded string (not an inline object) so Unity's
// JsonUtility.FromJson<T>(env.payload) works without an extra parse step.
//
// REST endpoints return snake_case to match C# [Serializable] DTOs.
// ─────────────────────────────────────────────────────────────────────────────

const express  = require('express');
const http     = require('http');
const { WebSocketServer } = require('ws');
const { nanoid } = require('nanoid');

const PORT = process.env.PORT || 3000;

// ── In-memory state ───────────────────────────────────────────────────────────
// rooms: Map<code, Room>
const rooms = new Map();

function makeCode() {
  // 6 uppercase alphanumeric characters, easy to read aloud.
  return nanoid(6).toUpperCase().replace(/[^A-Z0-9]/g, () =>
    'ABCDEFGHJKLMNPQRSTUVWXYZ23456789'[Math.floor(Math.random() * 32)]
  ).slice(0, 6);
}

function makeDeviceId() {
  return nanoid(12);
}

// ── Express ───────────────────────────────────────────────────────────────────
const app = express();
app.use(express.json());

// Health check
app.get('/health', (_req, res) => res.json({ ok: true, rooms: rooms.size }));

// POST /rooms — host creates a new room
// Body: { hls_url?: string }
// Returns: { room_code, room_token }  ← matches C# CreateRoomResponse
app.post('/rooms', (req, res) => {
  const code   = makeCode();
  // Accept both snake_case (hls_url) and camelCase (hlsUrl) from callers.
  const hlsUrl = (req.body && (req.body.hls_url || req.body.hlsUrl)) || '';
  const room   = {
    code,
    hlsUrl,
    hostWs:        null,
    guestWs:       null,
    hostDeviceId:  null,
    guestDeviceId: null,
    state: { position_ms: 0, playing: false, wall_start_ms: Date.now() },
  };
  rooms.set(code, room);
  console.log(`[room] created  code=${code}  hlsUrl=${hlsUrl || '(none yet)'}`);
  res.status(201).json({ room_code: code, room_token: code, hls_url: hlsUrl });
});

// POST /rooms/:code/join — guest joins
// Returns: { room_token, hls_url }  ← matches C# JoinRoomResponse
app.post('/rooms/:code/join', (req, res) => {
  const room = rooms.get(req.params.code.toUpperCase());
  if (!room) return res.status(404).json({ error: 'Room not found' });
  console.log(`[room] join     code=${room.code}`);
  res.json({ room_token: room.code, hls_url: room.hlsUrl, media_id: '', media_name: '' });
});

// GET /rooms/:code — debug / poll state
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

// POST /auth/hls — MediaMTX external-auth webhook
//
// MediaMTX POSTs a JSON body with this shape:
//   { "ip": "…", "user": "…", "password": "…", "path": "…",
//     "protocol": "…", "id": "…", "action": "…",
//     "query": "token=ROOMCODE" }
//
// The room code (= token) lives inside body.query as a URL-query string.
// We also accept it as req.query.token or body.token for flexibility.
app.post('/auth/hls', (req, res) => {
  let token = (req.query.token || '').toUpperCase();

  if (!token && req.body) {
    if (req.body.query) {
      // Parse "token=xxx[&…]" from the query field MediaMTX sends.
      const m = String(req.body.query).match(/(?:^|&)token=([^&]+)/);
      if (m) token = decodeURIComponent(m[1]).toUpperCase();
    }
    if (!token && req.body.token) {
      token = String(req.body.token).toUpperCase();
    }
  }

  if (!token) return res.status(403).json({ error: 'Missing token' });
  if (rooms.has(token)) return res.status(200).json({ ok: true });

  console.warn(`[auth] rejected token=${token}`);
  res.status(403).json({ error: 'Invalid token' });
});

// ── WebSocket helpers ─────────────────────────────────────────────────────────

/**
 * Send an envelope to a single WebSocket connection.
 * envelope = { type, payload: JSON.stringify(payloadObj) }
 */
function sendEnvelope(ws, type, payloadObj) {
  if (ws && ws.readyState === 1 /* OPEN */) {
    ws.send(JSON.stringify({ type, payload: JSON.stringify(payloadObj) }));
  }
}

/**
 * Broadcast an envelope to all room peers except the sender.
 */
function broadcastEnvelope(room, senderWs, type, payloadObj) {
  const raw = JSON.stringify({ type, payload: JSON.stringify(payloadObj) });
  for (const peer of [room.hostWs, room.guestWs]) {
    if (peer && peer !== senderWs && peer.readyState === 1) {
      peer.send(raw);
    }
  }
}

/**
 * Relay a raw message string (already an envelope) to all peers except sender.
 * Used for play_state, play, pause, seek, rtc_offer/answer/ice, etc., where
 * the client already sent a correctly-shaped envelope and we just forward it.
 */
function relay(room, senderWs, rawStr) {
  for (const peer of [room.hostWs, room.guestWs]) {
    if (peer && peer !== senderWs && peer.readyState === 1) {
      peer.send(rawStr);
    }
  }
}

/** Safely parse a payload string (or return {} on failure). */
function safeParsePayload(p) {
  if (!p) return {};
  try { return typeof p === 'string' ? JSON.parse(p) : p; }
  catch { return {}; }
}

/**
 * Build a RoomState-compatible object for the 'joined' envelope.
 * Matches the C# RoomState class (room_code, device_id, host_device_id,
 * seats[], hls_url, manifest_id, manifest, turn).
 */
function buildRoomState(room, myDeviceId) {
  const seats = [];
  if (room.hostDeviceId) {
    seats.push({
      device_id:    room.hostDeviceId,
      display_name: 'Host',
      role:         'host',
      seat_index:   0,
      connected:    true,
    });
  }
  if (room.guestDeviceId) {
    seats.push({
      device_id:    room.guestDeviceId,
      display_name: 'Guest',
      role:         'guest',
      seat_index:   1,
      connected:    true,
    });
  }
  return {
    room_code:      room.code,
    device_id:      myDeviceId,
    host_device_id: room.hostDeviceId || myDeviceId,
    seats,
    hls_url:        room.hlsUrl,
    manifest_id:    null,
    play_state: {
      position_ms:   room.state.position_ms,
      playing:       room.state.playing,
      wall_start_ms: room.state.wall_start_ms,
    },
    turn:           null,
  };
}

// ── HTTP + WebSocket server ───────────────────────────────────────────────────
const server = http.createServer(app);
const wss    = new WebSocketServer({ server, path: '/ws' });

wss.on('connection', (ws, req) => {
  const url   = new URL(req.url, 'http://localhost');
  const token = (url.searchParams.get('token') || '').toUpperCase();
  const room  = rooms.get(token);

  if (!room) {
    sendEnvelope(ws, 'error', { msg: 'Invalid room code' });
    ws.close(4001, 'Invalid room code');
    return;
  }

  // Assign role and generate a unique device_id for this connection.
  let role;
  const deviceId = makeDeviceId();

  if (!room.hostWs) {
    role = 'host';
    room.hostWs       = ws;
    room.hostDeviceId = deviceId;
  } else if (!room.guestWs) {
    role = 'guest';
    room.guestWs       = ws;
    room.guestDeviceId = deviceId;
  } else {
    sendEnvelope(ws, 'error', { msg: 'Room full (max 2 peers)' });
    ws.close(4002, 'Room full');
    return;
  }

  console.log(`[ws]  connected    code=${room.code}  role=${role}  device=${deviceId}`);

  // ── Send 'joined' with a full RoomState payload ───────────────────────────
  // Matches C# RoomState and what RoomClient.DispatchMessage case "joined" expects.
  sendEnvelope(ws, 'joined', buildRoomState(room, deviceId));

  // Notify the other peer that a new peer joined.
  const otherWs = role === 'host' ? room.guestWs : room.hostWs;
  if (otherWs && otherWs.readyState === 1) {
    sendEnvelope(otherWs, 'peer_joined', { device_id: deviceId, role });
  }

  // ── Message handler ───────────────────────────────────────────────────────
  ws.on('message', (data) => {
    const rawStr = data.toString();
    let msg;
    try { msg = JSON.parse(rawStr); } catch { return; }

    // Parse the inner payload (a JSON string) into a JS object for state updates.
    const payload = safeParsePayload(msg.payload);

    switch (msg.type) {

      // ── Playback control (host → guests) ─────────────────────────────────
      // Update room.state for late-joiners, then relay the original envelope.

      case 'play': {
        room.state.playing       = true;
        room.state.position_ms   = payload.position_ms  ?? room.state.position_ms;
        room.state.wall_start_ms = payload.wall_start_ms ?? Date.now();
        relay(room, ws, rawStr);
        break;
      }

      case 'pause': {
        room.state.playing     = false;
        room.state.position_ms = payload.position_ms ?? room.state.position_ms;
        relay(room, ws, rawStr);
        break;
      }

      case 'seek': {
        room.state.playing     = false;
        room.state.position_ms = payload.position_ms ?? room.state.position_ms;
        relay(room, ws, rawStr);
        break;
      }

      // ── Buffer status (guest → host, relabelled as peer_buffer) ──────────
      // Client sends type="buffer_status" (BufferStatusMsg: buffered_s, ready).
      // Server injects device_id and re-broadcasts as type="peer_buffer" (PeerBufferMsg).
      case 'buffer_status': {
        broadcastEnvelope(room, ws, 'peer_buffer', {
          device_id:  deviceId,
          buffered_s: payload.buffered_s ?? 0,
          ready:      payload.ready      ?? false,
        });
        break;
      }

      // ── State request (guest asks for current position on reconnect) ──────
      case 'state_request': {
        // Response uses PlayStateMsg format (host_wall_ms field name for SyncEngine compat)
        sendEnvelope(ws, 'play_state', {
          position_ms:  room.state.position_ms,
          playing:      room.state.playing,
          host_wall_ms: room.state.wall_start_ms,
        });
        break;
      }

      // ── HLS URL update (host may change URL mid-session) ─────────────────
      case 'hlsUrl': {
        if (role === 'host' && payload.hlsUrl) {
          room.hlsUrl = payload.hlsUrl;
          relay(room, ws, rawStr);
          console.log(`[room] hlsUrl set  code=${room.code}  url=${room.hlsUrl}`);
        }
        break;
      }

      // ── Heartbeat ─────────────────────────────────────────────────────────
      case 'ping': {
        sendEnvelope(ws, 'pong', { ts: payload.ts ?? Date.now() });
        break;
      }

      default:
        // Relay all other enveloped messages transparently:
        //   play_state, rtc_offer, rtc_answer, rtc_ice, etc.
        // VoiceChat.cs filters rtc_* by to_device_id on the client side.
        relay(room, ws, rawStr);
    }
  });

  // ── Disconnect ─────────────────────────────────────────────────────────────
  ws.on('close', () => {
    if (room.hostWs  === ws) { room.hostWs  = null; room.hostDeviceId  = null; }
    if (room.guestWs === ws) { room.guestWs = null; room.guestDeviceId = null; }
    console.log(`[ws]  disconnected  code=${room.code}  role=${role}  device=${deviceId}`);

    const remaining = room.hostWs || room.guestWs;
    if (remaining && remaining.readyState === 1) {
      // device_id from closure — correctly identifies the peer that left.
      sendEnvelope(remaining, 'peer_left', { device_id: deviceId });
    }

    // Clean up empty rooms after 5 minutes.
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

// ── Start ─────────────────────────────────────────────────────────────────────
server.listen(PORT, () => {
  console.log(`\n🎬 MoviePlayer Sync Server running on :${PORT}`);
  console.log(`   POST /rooms            → create room → { room_code, room_token }`);
  console.log(`   POST /rooms/:code/join → guest join  → { room_token, hls_url, media_id, media_name }`);
  console.log(`   POST /auth/hls         → MediaMTX auth webhook`);
  console.log(`   WS   /ws?token=CODE   → real-time sync\n`);
});
