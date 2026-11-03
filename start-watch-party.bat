@echo off
setlocal EnableDelayedExpansion
title MoviePlayer Watch Party Launcher

echo.
echo  ╔══════════════════════════════════════════════════════╗
echo  ║      🎬  MoviePlayer Watch Party Launcher  🎬        ║
echo  ║         Free  •  No account  •  No credit card       ║
echo  ╚══════════════════════════════════════════════════════╝
echo.

REM ── Pre-flight check: is Docker running? ─────────────────────────────────────
docker info >nul 2>&1
if errorlevel 1 (
    echo  [ERROR] Docker Desktop is not running.
    echo  Please start Docker Desktop, wait for it to fully load,
    echo  then run this script again.
    echo.
    pause
    exit /b 1
)

REM ── Check .env exists ─────────────────────────────────────────────────────────
if not exist "home-node\.env" (
    echo  [ERROR] home-node\.env not found.
    echo  Please copy home-node\.env.example to home-node\.env
    echo  and set MEDIA_DIR to your movies folder.
    echo.
    pause
    exit /b 1
)

REM ── Start all Docker services ─────────────────────────────────────────────────
echo  Starting Docker services (this may take a minute on first run
echo  while images are downloaded and built)...
echo.
cd home-node
docker compose up -d --build
if errorlevel 1 (
    echo.
    echo  [ERROR] docker compose up failed.  Check the output above.
    cd ..
    pause
    exit /b 1
)
cd ..

echo.
echo  Services started.  Waiting 20 seconds for tunnel URLs to appear...
timeout /t 20 /nobreak >nul

echo.
echo  ╔══════════════════════════════════════════════════════╗
echo  ║               YOUR TUNNEL URLS                       ║
echo  ╚══════════════════════════════════════════════════════╝
echo.
echo  ── Media (HLS stream) URL ──────────────────────────────
echo  Enter this as "Media URL" in Quest app Settings:
echo.
docker logs cinema-cloudflared-media 2>&1 | findstr "trycloudflare.com"
echo.
echo  ── Sync API URL ────────────────────────────────────────
echo  Enter this as "API URL" in Quest app Settings:
echo.
docker logs cinema-cloudflared-api 2>&1 | findstr "trycloudflare.com"
echo.

REM ── If URLs didn't appear yet, wait a bit more and retry ─────────────────────
echo  (If the URLs above are blank, waiting 15 more seconds...)
timeout /t 15 /nobreak >nul
echo.
echo  ── Media URL (retry) ───────────────────────────────────
docker logs cinema-cloudflared-media 2>&1 | findstr "trycloudflare.com"
echo.
echo  ── API URL (retry) ─────────────────────────────────────
docker logs cinema-cloudflared-api 2>&1 | findstr "trycloudflare.com"
echo.

echo  ╔══════════════════════════════════════════════════════╗
echo  ║                  NEXT STEPS                          ║
echo  ╚══════════════════════════════════════════════════════╝
echo.
echo   1. Copy the two *.trycloudflare.com URLs above.
echo.
echo   2. Put on your Meta Quest and open the MoviePlayer app.
echo.
echo   3. Go to Settings and enter:
echo       Media URL : (the first  trycloudflare.com URL above)
echo       API URL   : (the second trycloudflare.com URL above)
echo.
echo   4. Pick a movie from the library, then tap  Watch .
echo      A 6-letter room code will appear on screen.
echo.
echo   5. Tell your friend the code (text / voice).
echo      They open MoviePlayer on their Quest → tap  Join 
echo      → type the code → tap  Connect .
echo.
echo   6. Both Quests will buffer.  When both show  Ready ,
echo      tap  Play  on either headset — you'll be in sync!
echo.
echo  ── Useful commands ─────────────────────────────────────
echo   View live logs  : docker compose -f home-node\docker-compose.yml logs -f
echo   Stop everything : docker compose -f home-node\docker-compose.yml down
echo   Media URL again : docker logs cinema-cloudflared-media
echo   API URL again   : docker logs cinema-cloudflared-api
echo.
echo  NOTE: Tunnel URLs change every time you restart Docker.
echo        Just re-run this script and enter the new URLs.
echo.
pause
