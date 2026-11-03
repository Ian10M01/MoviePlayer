import 'dotenv/config';
import { config } from './config';

import express from 'express';
import http from 'http';
import { WebSocketServer } from 'ws';
import { URL } from 'url';

import { roomsRouter } from './rooms';
import { authRouter } from './auth';
import { setupSyncHub } from './sync';
import { prisma, redis } from './db';

const app = express();

// ─── Middleware ───────────────────────────────────────────────────────────────

app.use(express.json());
app.use(express.urlencoded({ extended: false }));

// Health check
app.get('/health', (_req, res) => {
  res.json({ status: 'ok', ts: new Date().toISOString() });
});

// ─── Routes ───────────────────────────────────────────────────────────────────

app.use('/rooms', roomsRouter);
app.use('/auth', authRouter);

// 404 handler
app.use((_req, res) => {
  res.status(404).json({ error: 'Not found' });
});

// Global error handler
app.use((err: Error, _req: express.Request, res: express.Response, _next: express.NextFunction) => {
  console.error('[Express] Unhandled error:', err);
  res.status(500).json({ error: 'Internal server error' });
});

// ─── HTTP + WebSocket Server ──────────────────────────────────────────────────

const server = http.createServer(app);

// Attach WebSocket server to the same HTTP server.
// Only upgrade requests to /ws are handled by the WebSocket server.
const wss = new WebSocketServer({ noServer: true });

server.on('upgrade', (req, socket, head) => {
  const baseUrl = `http://${req.headers.host ?? 'localhost'}`;
  const url = new URL(req.url ?? '/', baseUrl);

  if (url.pathname === '/ws') {
    wss.handleUpgrade(req, socket, head, (ws) => {
      wss.emit('connection', ws, req);
    });
  } else {
    socket.destroy();
  }
});

setupSyncHub(wss);

// ─── Startup ──────────────────────────────────────────────────────────────────

const PORT = config.PORT;

server.listen(PORT, () => {
  console.log(`[Server] MoviePlayer Cloud API listening on port ${PORT}`);
  console.log(`[Server] REST:      http://localhost:${PORT}`);
  console.log(`[Server] WebSocket: ws://localhost:${PORT}/ws`);
});

// ─── Graceful Shutdown ────────────────────────────────────────────────────────

let isShuttingDown = false;

async function gracefulShutdown(signal: string): Promise<void> {
  if (isShuttingDown) return;
  isShuttingDown = true;

  console.log(`[Server] ${signal} received — shutting down gracefully...`);

  // Stop accepting new connections
  server.close(async () => {
    console.log('[Server] HTTP server closed');

    // Close WebSocket connections
    wss.clients.forEach((client) => {
      client.close(1001, 'Server shutting down');
    });
    wss.close();

    // Disconnect DB and Redis
    try {
      await prisma.$disconnect();
      console.log('[Server] Prisma disconnected');
    } catch (err) {
      console.error('[Server] Error disconnecting Prisma:', err);
    }

    try {
      await redis.quit();
      console.log('[Server] Redis disconnected');
    } catch (err) {
      console.error('[Server] Error disconnecting Redis:', err);
    }

    console.log('[Server] Shutdown complete');
    process.exit(0);
  });

  // Force exit if graceful shutdown takes too long
  setTimeout(() => {
    console.error('[Server] Forced shutdown after timeout');
    process.exit(1);
  }, 10_000).unref();
}

process.on('SIGTERM', () => void gracefulShutdown('SIGTERM'));
process.on('SIGINT', () => void gracefulShutdown('SIGINT'));
