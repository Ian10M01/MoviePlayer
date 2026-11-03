# Post-reboot: Start Docker daemon & bring up home-node stack
# Run this in PowerShell after restarting Windows

Write-Host "=== Post-Reboot Docker + home-node Startup ===" -ForegroundColor Cyan

# --- Step 1: Add Docker to PATH ---
$env:PATH = [System.Environment]::GetEnvironmentVariable('PATH','Machine') + ';' + [System.Environment]::GetEnvironmentVariable('PATH','User')
if (-not (Get-Command docker -ErrorAction SilentlyContinue)) {
    $env:PATH += ';C:\Program Files\Docker\Docker\resources\bin'
}

# --- Step 2: Ensure WSL2 default is set ---
Write-Host "`n[1] Setting WSL2 as default..." -ForegroundColor Yellow
wsl --set-default-version 2 2>&1 | Write-Host

# --- Step 3: Restart Docker Desktop if not running ---
Write-Host "`n[2] Checking Docker Desktop..." -ForegroundColor Yellow
$dd = Get-Process -Name "Docker Desktop" -ErrorAction SilentlyContinue
if (-not $dd) {
    Write-Host "Launching Docker Desktop..."
    Start-Process "C:\Program Files\Docker\Docker\Docker Desktop.exe"
} else {
    Write-Host "Docker Desktop already running (PID $($dd[0].Id))"
}

# --- Step 4: Wait for Docker daemon ---
Write-Host "`n[3] Waiting for Docker daemon (up to 3 min)..." -ForegroundColor Yellow
$timeout = 180; $elapsed = 0
while ($elapsed -lt $timeout) {
    $r = docker ps 2>&1
    if ($LASTEXITCODE -eq 0) {
        Write-Host "Docker daemon ready!" -ForegroundColor Green
        docker --version
        break
    }
    Write-Host "  ${elapsed}s - $($r | Select-Object -First 1)"
    Start-Sleep -Seconds 5
    $elapsed += 5
}
if ($elapsed -ge $timeout) {
    Write-Host "Docker still not ready after $timeout seconds." -ForegroundColor Red
    Write-Host "Try: Settings > Resources > WSL Integration and restart Docker Desktop manually."
    exit 1
}

# --- Step 5: Bring up home-node stack ---
Write-Host "`n[4] Starting home-node stack..." -ForegroundColor Yellow
Set-Location "C:\Users\milat\Desktop\MoviePlayer\home-node"
docker compose up -d --build 2>&1
Write-Host "Compose exit: $LASTEXITCODE"
docker ps --format "table {{.Names}}`t{{.Status}}"

# --- Step 6: Wait for Cloudflare tunnel URLs ---
Write-Host "`n[5] Waiting 45s for Cloudflare tunnels..." -ForegroundColor Yellow
Start-Sleep -Seconds 45
foreach ($name in @("home-node-cloudflared-media-1","home-node-cloudflared-api-1","cloudflared-media","cloudflared-api")) {
    $log = docker logs $name 2>&1
    if ($log -match "(https://[a-z0-9\-]+\.trycloudflare\.com)") {
        Write-Host "$name -> $($Matches[1])" -ForegroundColor Green
    }
}

Write-Host "`n=== Done ===" -ForegroundColor Cyan
docker ps
