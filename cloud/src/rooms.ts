import { Router, Request, Response } from 'express';
import jwt from 'jsonwebtoken';
import { customAlphabet } from 'nanoid';
import { z } from 'zod';
import { config } from './config';
import { prisma, redis, redisKeys } from './db';
import { requireAuth, JwtPayload } from './auth';

const router = Router();

// 6-char alphanumeric invite code (uppercase only for readability in VR)
const generateCode = customAlphabet('ABCDEFGHJKLMNPQRSTUVWXYZ23456789', 6);

const ROOM_EXPIRY_HOURS = 24;
const MAX_SEATS = 4;

function signToken(payload: Omit<JwtPayload, 'iat' | 'exp'>): string {
  return jwt.sign(payload, config.JWT_SECRET, { expiresIn: '24h' });
}

// ─── POST /rooms ──────────────────────────────────────────────────────────────

const CreateRoomSchema = z.object({
  hostDeviceId: z.string().min(1).max(128),
  displayName: z.string().min(1).max(64),
  hlsBaseUrl: z.string().url(),
  mediaId: z.string().min(1).max(256),
  mediaName: z.string().min(1).max(256),
});

router.post('/', async (req: Request, res: Response): Promise<void> => {
  const parsed = CreateRoomSchema.safeParse(req.body);
  if (!parsed.success) {
    res.status(400).json({ error: 'Invalid request body', details: parsed.error.flatten() });
    return;
  }

  const { hostDeviceId, displayName, hlsBaseUrl, mediaId, mediaName } = parsed.data;

  const expiresAt = new Date();
  expiresAt.setHours(expiresAt.getHours() + ROOM_EXPIRY_HOURS);

  // Generate a unique code (retry up to 5 times on collision)
  let code: string = '';
  let attempts = 0;
  while (attempts < 5) {
    code = generateCode();
    const existing = await prisma.room.findUnique({ where: { code } });
    if (!existing) break;
    attempts++;
  }

  if (!code) {
    res.status(500).json({ error: 'Failed to generate unique room code' });
    return;
  }

  try {
    const room = await prisma.room.create({
      data: {
        code,
        hostDeviceId,
        hlsBaseUrl,
        mediaId,
        mediaName,
        expiresAt,
        state: {},
        seats: {
          create: {
            deviceId: hostDeviceId,
            displayName,
            role: 'HOST',
          },
        },
      },
      include: { seats: true },
    });

    const token = signToken({ roomId: room.id, deviceId: hostDeviceId, role: 'HOST' });
    const hlsUrl = `${hlsBaseUrl}?token=${token}`;

    res.status(201).json({
      code: room.code,
      token,
      roomId: room.id,
      hlsUrl,
    });
  } catch (err) {
    console.error('[POST /rooms] Error:', err);
    res.status(500).json({ error: 'Failed to create room' });
  }
});

// ─── POST /rooms/:code/join ───────────────────────────────────────────────────

const JoinRoomSchema = z.object({
  deviceId: z.string().min(1).max(128),
  displayName: z.string().min(1).max(64),
});

router.post('/:code/join', async (req: Request, res: Response): Promise<void> => {
  const parsed = JoinRoomSchema.safeParse(req.body);
  if (!parsed.success) {
    res.status(400).json({ error: 'Invalid request body', details: parsed.error.flatten() });
    return;
  }

  const { deviceId, displayName } = parsed.data;
  const { code } = req.params;

  if (!code) {
    res.status(400).json({ error: 'Missing room code' });
    return;
  }

  const room = await prisma.room.findUnique({
    where: { code },
    include: { seats: true },
  });

  if (!room) {
    res.status(404).json({ error: 'Room not found' });
    return;
  }

  if (room.expiresAt < new Date()) {
    res.status(410).json({ error: 'Room has expired' });
    return;
  }

  if (room.seats.length >= MAX_SEATS) {
    res.status(409).json({ error: 'Room is full (max 4 seats)' });
    return;
  }

  // If this device already has a seat (reconnect), return existing token
  const existingSeat = room.seats.find((s) => s.deviceId === deviceId);
  if (existingSeat) {
    const token = signToken({ roomId: room.id, deviceId, role: existingSeat.role });
    const hlsUrl = `${room.hlsBaseUrl}?token=${token}`;
    res.status(200).json({
      token,
      roomId: room.id,
      hlsUrl,
      mediaId: room.mediaId,
      mediaName: room.mediaName,
    });
    return;
  }

  try {
    await prisma.seat.create({
      data: {
        roomId: room.id,
        deviceId,
        displayName,
        role: 'GUEST',
      },
    });

    const token = signToken({ roomId: room.id, deviceId, role: 'GUEST' });
    const hlsUrl = `${room.hlsBaseUrl}?token=${token}`;

    res.status(201).json({
      token,
      roomId: room.id,
      hlsUrl,
      mediaId: room.mediaId,
      mediaName: room.mediaName,
    });
  } catch (err) {
    console.error('[POST /rooms/:code/join] Error:', err);
    res.status(500).json({ error: 'Failed to join room' });
  }
});

// ─── GET /rooms/:code ─────────────────────────────────────────────────────────

router.get('/:code', async (req: Request, res: Response): Promise<void> => {
  const { code } = req.params;

  if (!code) {
    res.status(400).json({ error: 'Missing room code' });
    return;
  }

  const room = await prisma.room.findUnique({
    where: { code },
    include: { seats: true },
  });

  if (!room) {
    res.status(404).json({ error: 'Room not found' });
    return;
  }

  // Fetch current play state from Redis (override DB if present)
  let playState: unknown = room.state;
  try {
    const cached = await redis.get(redisKeys.playState(room.id));
    if (cached) {
      playState = JSON.parse(cached);
    }
  } catch {
    // Ignore Redis errors — fall back to DB state
  }

  res.json({
    roomId: room.id,
    code: room.code,
    mediaId: room.mediaId,
    mediaName: room.mediaName,
    hlsBaseUrl: room.hlsBaseUrl,
    expiresAt: room.expiresAt,
    createdAt: room.createdAt,
    seats: room.seats.map((s) => ({
      deviceId: s.deviceId,
      displayName: s.displayName,
      role: s.role,
      joinedAt: s.joinedAt,
    })),
    playState,
  });
});

// ─── DELETE /rooms/:code ──────────────────────────────────────────────────────

router.delete(
  '/:code',
  requireAuth(['HOST']),
  async (req: Request, res: Response): Promise<void> => {
    const { code } = req.params;
    const user = req.user as JwtPayload;

    if (!code) {
      res.status(400).json({ error: 'Missing room code' });
      return;
    }

    const room = await prisma.room.findUnique({ where: { code } });
    if (!room) {
      res.status(404).json({ error: 'Room not found' });
      return;
    }

    // Ensure the requesting user is the host of this specific room
    if (room.id !== user.roomId || room.hostDeviceId !== user.deviceId) {
      res.status(403).json({ error: 'Only the host can delete this room' });
      return;
    }

    try {
      // Mark room as expired (set expiresAt to now)
      await prisma.room.update({
        where: { id: room.id },
        data: { expiresAt: new Date() },
      });

      // Clean up Redis state
      await redis.del(redisKeys.playState(room.id));

      res.status(200).json({ message: 'Room closed' });
    } catch (err) {
      console.error('[DELETE /rooms/:code] Error:', err);
      res.status(500).json({ error: 'Failed to delete room' });
    }
  }
);

export { router as roomsRouter };
