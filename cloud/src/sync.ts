import { IncomingMessage } from 'http';
import { WebSocket, WebSocketServer } from 'ws';
import { URL } from 'url';
import jwt from 'jsonwebtoken';
import { config } from './config';
import { prisma, redis, redisKeys } from './db';
import { JwtPayload } from './auth';

// ─── Types ────────────────────────────────────────────────────────────────────

interface RoomClient {
  ws: WebSocket;
  deviceId: string;
  role: 'HOST' | 'GUEST';
  displayName: string;
}

interface PlayState {
  playing: boolean;
  t_ms: number;
  host_wall_ms: number;
  updatedAt: number;
}

type IncomingMessage_ =
  | { type: 'state'; t_ms: number; playing: boolean; host_wall_ms: number }
  | { type: 'play' }
  | { type: 'pause'; t_ms: number }
  | { type: 'seek'; t_ms: number }
  | { type: 'buffer'; buffered_s: number; ready: boolean }
  | { type: 'ping' };

// ─── In-memory room map ───────────────────────────────────────────────────────
// roomId → Set of connected clients
const rooms = new Map<string, Set<RoomClient>>();

function getRoomClients(roomId: string): Set<RoomClient> {
  let clients = rooms.get(roomId);
  if (!clients) {
    clients = new Set();
    rooms.set(roomId, clients);
  }
  return clients;
}

function broadcast(
  roomId: string,
  message: unknown,
  exclude?: WebSocket
): void {
  const clients = rooms.get(roomId);
  if (!clients) return;
  const payload = JSON.stringify(message);
  for (const client of clients) {
    if (client.ws !== exclude && client.ws.readyState === WebSocket.OPEN) {
      client.ws.send(payload);
    }
  }
}

function broadcastAll(roomId: string, message: unknown): void {
  broadcast(roomId, message, undefined);
}

async function savePlayState(roomId: string, state: PlayState): Promise<void> {
  try {
    await redis.set(redisKeys.playState(roomId), JSON.stringify(state), 'EX', 86400);
  } catch (err) {
    console.error('[Sync] Failed to save play state to Redis:', err);
  }
}

async function getPlayState(roomId: string): Promise<PlayState | null> {
  try {
    const raw = await redis.get(redisKeys.playState(roomId));
    return raw ? (JSON.parse(raw) as PlayState) : null;
  } catch {
    return null;
  }
}

// ─── WebSocket connection handler ─────────────────────────────────────────────

async function handleConnection(ws: WebSocket, req: IncomingMessage): Promise<void> {
  // Extract token from query string: /ws?token=<jwt>
  const baseUrl = `http://localhost`;
  const url = new URL(req.url ?? '/', baseUrl);
  const token = url.searchParams.get('token');

  if (!token) {
    ws.close(4001, 'Missing token');
    return;
  }

  // Validate JWT
  let payload: JwtPayload;
  try {
    payload = jwt.verify(token, config.JWT_SECRET) as JwtPayload;
  } catch {
    ws.close(4001, 'Invalid token');
    return;
  }

  const { roomId, deviceId, role } = payload;

  // Verify room exists and is not expired
  const room = await prisma.room.findUnique({
    where: { id: roomId },
    include: { seats: true },
  });

  if (!room) {
    ws.close(4004, 'Room not found');
    return;
  }

  if (room.expiresAt < new Date()) {
    ws.close(4010, 'Room expired');
    return;
  }

  // Find display name from seats
  const seat = room.seats.find((s) => s.deviceId === deviceId);
  const displayName = seat?.displayName ?? deviceId;

  // Register client
  const client: RoomClient = { ws, deviceId, role, displayName };
  const clients = getRoomClients(roomId);
  clients.add(client);

  console.log(`[Sync] ${role} ${deviceId} joined room ${roomId} (${clients.size} total)`);

  // Send join confirmation with current state
  const playState = await getPlayState(roomId);
  const seatsInfo = room.seats.map((s) => ({
    deviceId: s.deviceId,
    displayName: s.displayName,
    role: s.role,
  }));

  ws.send(
    JSON.stringify({
      type: 'joined',
      roomId,
      seats: seatsInfo,
      playState,
    })
  );

  // Notify others that a peer joined
  broadcast(
    roomId,
    {
      type: 'peer_joined',
      deviceId,
      displayName,
      role,
    },
    ws
  );

  // ─── Message handler ─────────────────────────────────────────────────────

  ws.on('message', (data) => {
    let msg: IncomingMessage_;
    try {
      msg = JSON.parse(data.toString()) as IncomingMessage_;
    } catch {
      return; // Ignore malformed messages
    }

    switch (msg.type) {
      case 'ping':
        ws.send(JSON.stringify({ type: 'pong' }));
        break;

      case 'state': {
        // Only HOST can broadcast authoritative state
        if (role !== 'HOST') return;
        const state: PlayState = {
          playing: msg.playing,
          t_ms: msg.t_ms,
          host_wall_ms: msg.host_wall_ms,
          updatedAt: Date.now(),
        };
        void savePlayState(roomId, state);
        broadcastAll(roomId, {
          type: 'state',
          t_ms: msg.t_ms,
          playing: msg.playing,
          host_wall_ms: msg.host_wall_ms,
          from: deviceId,
        });
        break;
      }

      case 'play': {
        if (role !== 'HOST') return;
        void getPlayState(roomId).then((existing) => {
          const state: PlayState = {
            playing: true,
            t_ms: existing?.t_ms ?? 0,
            host_wall_ms: Date.now(),
            updatedAt: Date.now(),
          };
          void savePlayState(roomId, state);
          broadcastAll(roomId, {
            type: 'play',
            t_ms: state.t_ms,
            host_wall_ms: state.host_wall_ms,
            from: deviceId,
          });
        });
        break;
      }

      case 'pause': {
        if (role !== 'HOST') return;
        const state: PlayState = {
          playing: false,
          t_ms: msg.t_ms,
          host_wall_ms: Date.now(),
          updatedAt: Date.now(),
        };
        void savePlayState(roomId, state);
        broadcastAll(roomId, {
          type: 'pause',
          t_ms: msg.t_ms,
          from: deviceId,
        });
        break;
      }

      case 'seek': {
        if (role !== 'HOST') return;
        const state: PlayState = {
          playing: false, // Paused while seeking; host sends play when ready
          t_ms: msg.t_ms,
          host_wall_ms: Date.now(),
          updatedAt: Date.now(),
        };
        void savePlayState(roomId, state);
        broadcastAll(roomId, {
          type: 'seek',
          t_ms: msg.t_ms,
          from: deviceId,
        });
        break;
      }

      case 'buffer': {
        // GUEST reports buffer status — broadcast to all (host sees readiness)
        broadcastAll(roomId, {
          type: 'buffer',
          deviceId,
          displayName,
          buffered_s: msg.buffered_s,
          ready: msg.ready,
        });
        break;
      }

      default:
        // Unknown message type — ignore
        break;
    }
  });

  // ─── Disconnect handler ───────────────────────────────────────────────────

  ws.on('close', () => {
    clients.delete(client);
    console.log(`[Sync] ${role} ${deviceId} left room ${roomId} (${clients.size} remaining)`);

    if (clients.size === 0) {
      rooms.delete(roomId);
    } else {
      broadcastAll(roomId, {
        type: 'peer_left',
        deviceId,
        displayName,
        role,
      });
    }
  });

  ws.on('error', (err) => {
    console.error(`[Sync] WebSocket error for ${deviceId} in room ${roomId}:`, err);
  });
}

// ─── Setup function ───────────────────────────────────────────────────────────

export function setupSyncHub(wss: WebSocketServer): void {
  wss.on('connection', (ws: WebSocket, req: IncomingMessage) => {
    handleConnection(ws, req).catch((err) => {
      console.error('[Sync] Unhandled error in connection handler:', err);
      ws.close(4500, 'Internal server error');
    });
  });

  console.log('[Sync] WebSocket sync hub initialized');
}
