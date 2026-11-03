import { Router, Request, Response, NextFunction } from 'express';
import jwt from 'jsonwebtoken';
import { config } from './config';
import { prisma, redis, redisKeys } from './db';

// ─── Types ────────────────────────────────────────────────────────────────────

export interface JwtPayload {
  roomId: string;
  deviceId: string;
  role: 'HOST' | 'GUEST';
  iat?: number;
  exp?: number;
}

declare global {
  // eslint-disable-next-line @typescript-eslint/no-namespace
  namespace Express {
    interface Request {
      user?: JwtPayload;
    }
  }
}

// ─── Auth Middleware ───────────────────────────────────────────────────────────

/**
 * Express middleware that validates a Bearer JWT and attaches the decoded
 * payload to `req.user`. Optionally restricts to specific roles.
 */
export function requireAuth(roles?: Array<'HOST' | 'GUEST'>) {
  return async (req: Request, res: Response, next: NextFunction): Promise<void> => {
    const authHeader = req.headers.authorization;
    if (!authHeader || !authHeader.startsWith('Bearer ')) {
      res.status(401).json({ error: 'Missing or invalid Authorization header' });
      return;
    }

    const token = authHeader.slice(7);
    try {
      const payload = jwt.verify(token, config.JWT_SECRET) as JwtPayload;

      // Verify the room still exists and is not expired
      const room = await prisma.room.findUnique({
        where: { id: payload.roomId },
        select: { expiresAt: true },
      });

      if (!room || room.expiresAt < new Date()) {
        res.status(401).json({ error: 'Room expired or not found' });
        return;
      }

      if (roles && roles.length > 0 && !roles.includes(payload.role)) {
        res.status(403).json({ error: 'Insufficient permissions' });
        return;
      }

      req.user = payload;
      next();
    } catch (err) {
      res.status(401).json({ error: 'Invalid or expired token' });
    }
  };
}

// ─── HLS Auth Router ──────────────────────────────────────────────────────────

const RATE_LIMIT_WINDOW_S = 60;
const RATE_LIMIT_MAX_REQUESTS = 120;

export const authRouter = Router();

/**
 * POST /auth/hls
 *
 * Called by MediaMTX for every HLS reader. The client's token is forwarded
 * as a Bearer in the Authorization header by MediaMTX, or passed in `token`
 * query param.  Returns 200 to allow, 403 to deny.
 */
authRouter.post('/hls', async (req: Request, res: Response): Promise<void> => {
  // Extract IP for rate limiting
  const ip =
    (req.headers['x-forwarded-for'] as string | undefined)?.split(',')[0]?.trim() ??
    req.socket.remoteAddress ??
    'unknown';

  // Rate limiting check
  const rateLimitKey = redisKeys.rateLimitAuth(ip);
  try {
    const current = await redis.incr(rateLimitKey);
    if (current === 1) {
      await redis.expire(rateLimitKey, RATE_LIMIT_WINDOW_S);
    }
    if (current > RATE_LIMIT_MAX_REQUESTS) {
      console.warn(`[Auth/HLS] Rate limit exceeded for IP: ${ip}`);
      res.status(429).json({ error: 'Too many requests' });
      return;
    }
  } catch (redisErr) {
    // Non-fatal: if Redis is down, allow the request through
    console.error('[Auth/HLS] Redis rate-limit check failed:', redisErr);
  }

  // Extract token: MediaMTX passes it as Bearer in Authorization header,
  // or some setups pass it in the query string forwarded in the body.
  let token: string | undefined;

  const authHeader = req.headers.authorization;
  if (authHeader?.startsWith('Bearer ')) {
    token = authHeader.slice(7);
  } else if (typeof req.body?.token === 'string') {
    token = req.body.token as string;
  } else if (typeof req.query['token'] === 'string') {
    token = req.query['token'] as string;
  }

  if (!token) {
    res.status(403).json({ error: 'No token provided' });
    return;
  }

  try {
    const payload = jwt.verify(token, config.JWT_SECRET) as JwtPayload;

    // Verify the room exists and is not expired
    const room = await prisma.room.findUnique({
      where: { id: payload.roomId },
      select: { expiresAt: true },
    });

    if (!room) {
      res.status(403).json({ error: 'Room not found' });
      return;
    }

    if (room.expiresAt < new Date()) {
      res.status(403).json({ error: 'Room expired' });
      return;
    }

    // All good — allow
    res.status(200).json({});
  } catch {
    res.status(403).json({ error: 'Invalid or expired token' });
  }
});
