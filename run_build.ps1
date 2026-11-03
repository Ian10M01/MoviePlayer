$ErrorActionPreference = "Continue"

# ── STEP 1: Poll for Unity ────────────────────────────────────────────────────
$unityExe      = $null
$maxWaitMin    = 60
$elapsed       = 0
$checkInterval = 30

Write-Host "$(Get-Date -f 'HH:mm:ss') | Waiting for Unity 6000.0.23f1 to finish installing..."

while ($elapsed -lt ($maxWaitMin * 60)) {
    $hubEditorBase = "C:\Program Files\Unity\Hub\Editor"
    if (Test-Path $hubEditorBase) {
        $unity6 = Get-ChildItem $hubEditorBase -Filter "Unity.exe" -Recurse -ErrorAction SilentlyContinue |
                  Where-Object { $_.FullName -match "6000" } | Select-Object -First 1
        if ($unity6) {
            $unityExe = $unity6.FullName
            Write-Host "$(Get-Date -f 'HH:mm:ss') | Unity found: $unityExe"
            break
        }
    }
    foreach ($p in @(
        "C:\Program Files\Unity\Hub\Editor\6000.0.23f1\Editor\Unity.exe",
        "C:\Program Files\Unity\6000.0.23f1\Editor\Unity.exe"
    )) {
        if (Test-Path $p) {
            $unityExe = $p
            Write-Host "$(Get-Date -f 'HH:mm:ss') | Unity found: $unityExe"
            break
        }
    }
    if ($unityExe) { break }

    Write-Host "$(Get-Date -f 'HH:mm:ss') | Unity not ready. Elapsed: $([int]($elapsed/60)) min. Checking in ${checkInterval}s..."
    Start-Sleep -Seconds $checkInterval
    $elapsed += $checkInterval
}

if (-not $unityExe) {
    Write-Host "ERROR: Unity not found after $maxWaitMin minutes."
    exit 1
}

# ── STEP 2: Verify Android module ────────────────────────────────────────────
$unityDir      = Split-Path (Split-Path $unityExe)
$androidModule = Join-Path $unityDir "PlaybackEngines\AndroidPlayer"
Write-Host "$(Get-Date -f 'HH:mm:ss') | Checking Android module: $androidModule"
if (Test-Path $androidModule) {
    Write-Host "$(Get-Date -f 'HH:mm:ss') | Android Build Support confirmed."
} else {
    Write-Host "$(Get-Date -f 'HH:mm:ss') | WARNING: Android module not found. Proceeding anyway..."
}

# ── STEP 3: Verify BuildScript.cs ────────────────────────────────────────────
$buildScript = "C:\Users\milat\Desktop\MoviePlayer\quest-app\Assets\Editor\BuildScript.cs"
if (Test-Path $buildScript) {
    Write-Host "$(Get-Date -f 'HH:mm:ss') | BuildScript.cs confirmed: $buildScript"
} else {
    Write-Host "ERROR: BuildScript.cs not found at $buildScript"
    exit 1
}

# ── STEP 4: Unity batch build ────────────────────────────────────────────────
$logFile     = "C:\Users\milat\Desktop\MoviePlayer\unity_build.log"
$projectPath = "C:\Users\milat\Desktop\MoviePlayer\quest-app"

Write-Host "$(Get-Date -f 'HH:mm:ss') | Starting Unity batch build..."

$proc = Start-Process -FilePath $unityExe -ArgumentList @(
    "-batchmode", "-nographics", "-quit",
    "-projectPath", $projectPath,
    "-buildTarget", "Android",
    "-executeMethod", "BuildScript.BuildAPK",
    "-logFile", $logFile
) -PassThru -NoNewWindow

$timeout = 2700
$waited  = 0
while (-not $proc.HasExited -and $waited -lt $timeout) {
    Start-Sleep -Seconds 30
    $waited += 30
    Write-Host "$(Get-Date -f 'HH:mm:ss') | Build running... $([int]($waited/60)) min elapsed"
}

if (-not $proc.HasExited) {
    Write-Host "$(Get-Date -f 'HH:mm:ss') | Build timed out after 45 min. Killing process."
    $proc | Stop-Process -Force
    exit 2
}

$exitCode = $proc.ExitCode
Write-Host "$(Get-Date -f 'HH:mm:ss') | Unity exited with code: $exitCode"

if (Test-Path $logFile) {
    Write-Host "`n=== BUILD LOG (last 60 lines) ==="
    Get-Content $logFile -Tail 60
}

# ── STEP 5: Check APK and sideload ───────────────────────────────────────────
$apkPath = "C:\Users\milat\Desktop\MoviePlayer\MoviePlayer.apk"

if (Test-Path $apkPath) {
    $apkSize = [math]::Round((Get-Item $apkPath).Length / 1MB, 1)
    Write-Host "`n=== APK BUILT SUCCESSFULLY ==="
    Write-Host "Location : $apkPath"
    Write-Host "Size     : $apkSize MB"

    $env:PATH = [System.Environment]::GetEnvironmentVariable('PATH','Machine') + ';' +
                [System.Environment]::GetEnvironmentVariable('PATH','User')

    $adbCmd = Get-Command adb -ErrorAction SilentlyContinue
    $adbExe = $null
    if ($adbCmd) { $adbExe = $adbCmd.Source }
    if (-not $adbExe -or -not (Test-Path $adbExe)) { $adbExe = "C:\Users\milat\Downloads\platform-tools\adb.exe" }
    if (-not (Test-Path $adbExe)) { $adbExe = "$env:LOCALAPPDATA\Android\Sdk\platform-tools\adb.exe" }
    if (-not (Test-Path $adbExe)) { $adbExe = "C:\Android\platform-tools\adb.exe" }

    if (Test-Path $adbExe) {
        Write-Host "`nADB found: $adbExe"
        Write-Host "Checking for connected Quest..."
        $devices = & $adbExe devices 2>&1
        Write-Host $devices

        if ($devices -match "\tdevice") {
            Write-Host "Quest connected! Installing APK..."
            $installOut = & $adbExe install -r $apkPath 2>&1
            Write-Host $installOut
            if ($installOut -match "Success") {
                Write-Host "SIDELOAD COMPLETE! MoviePlayer is installed on your Quest."
            } else {
                Write-Host "Install may have had issues. See output above."
            }
        } else {
            Write-Host "`nQuest not connected via USB."
            Write-Host "When ready run:  & `"$adbExe`" install -r `"$apkPath`""
        }
    } else {
        Write-Host "`nADB not found. To sideload manually:"
        Write-Host "  adb install -r `"$apkPath`""
    }
} else {
    Write-Host "APK not found at $apkPath (build likely failed, exit code $exitCode)."
    if (Test-Path $logFile) {
        $lines = (Get-Content $logFile).Count
        Write-Host "`n=== BUILD LOG ($lines lines total, last 100) ==="
        Get-Content $logFile | Select-Object -Last 100
    }
    exit $exitCode
}

Write-Host "`nAll done at $(Get-Date -f 'HH:mm:ss')."
