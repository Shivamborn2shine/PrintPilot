<#
.SYNOPSIS
    PrintAway Zero-Config Station & Secure Tunnel Launcher
    1. Compiles PrintAway.exe if missing (using built-in Windows .NET 4.8 csc.exe)
    2. Starts local PrintAway server on port 8080 (with physical printer auto-discovery)
    3. Downloads cloudflared.exe if missing and starts an HTTP/2 TLS tunnel
    4. Automatically updates web/index.html & web/admin.html with the live tunnel URL
       and pushes to GitHub so Netlify (https://printitaway.netlify.app) connects automatically!
#>

param(
    [string]$NetlifyUrl = "",
    [int]$LocalPort = 0,
    [string]$PrinterQueue = ""
)

$ErrorActionPreference = "Stop"
$rootDir = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
Set-Location $rootDir

# Load portable config/station.json if present
$configPath = Join-Path $rootDir "config\station.json"
$autoPushGit = $true
if (Test-Path $configPath) {
    try {
        $cfg = Get-Content $configPath -Raw | ConvertFrom-Json
        if (-not $NetlifyUrl -and $cfg.netlifySiteUrl) { $NetlifyUrl = $cfg.netlifySiteUrl }
        if ($LocalPort -le 0 -and $cfg.port) { $LocalPort = [int]$cfg.port }
        if ($null -ne $cfg.autoPushTunnelToGit) { $autoPushGit = [bool]$cfg.autoPushTunnelToGit }
    } catch {
        Write-Host "[Config] Warning reading config/station.json: $_" -ForegroundColor DarkYellow
    }
}
if (-not $NetlifyUrl) { $NetlifyUrl = "https://printitaway.netlify.app" }
if ($LocalPort -le 0) { $LocalPort = 8080 }

Write-Host "==========================================================" -ForegroundColor Cyan
Write-Host "       PRINTAWAY 1-CLICK STATION & TUNNEL LAUNCHER        " -ForegroundColor Cyan
Write-Host "==========================================================" -ForegroundColor Cyan

# 1. Check if local PrintAway server is running
$localApiUrl = "http://127.0.0.1:$LocalPort/api/shop/status"
Write-Host "[1/4] Checking local PrintAway server at $localApiUrl ..." -ForegroundColor Yellow
$serverRunning = $false
try {
    $res = Invoke-RestMethod -Uri $localApiUrl -TimeoutSec 3 -ErrorAction Stop
    if ($res.shopName) {
        $serverRunning = $true
        Write-Host "  [OK] PrintAway server is running (`"$($res.shopName)`" -> Queue: $($res.activeQueueName))" -ForegroundColor Green
    }
} catch {
    Write-Host "  [!] PrintAway server not running yet on port $LocalPort." -ForegroundColor DarkYellow
}

if (-not $serverRunning) {
    $serverExe = Join-Path $rootDir "bin\PrintAway.exe"
    if (-not (Test-Path $serverExe)) {
        Write-Host "  Compiling PrintAway.exe using built-in Windows .NET compiler..." -ForegroundColor Yellow
        powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $rootDir "scripts\build-and-test.ps1")
    }
    $argList = "--port $LocalPort"
    if ($PrinterQueue) {
        $argList += " --printer `"$PrinterQueue`""
    }
    Write-Host "  Starting PrintAway.exe ($argList)..." -ForegroundColor Yellow
    Start-Process -FilePath $serverExe -ArgumentList $argList -WindowStyle Minimized
    Start-Sleep -Seconds 2
    try {
        $res = Invoke-RestMethod -Uri $localApiUrl -TimeoutSec 5 -ErrorAction Stop
        Write-Host "  [OK] Local server started. Active Printer Queue: $($res.activeQueueName)" -ForegroundColor Green
    } catch {
        Write-Host "  [ERROR] Failed to start local PrintAway server: $_" -ForegroundColor Red
        exit 1
    }
}

# 2. Check cloudflared binary
$cloudflared = Join-Path $rootDir "bin\cloudflared.exe"
if (-not (Test-Path $cloudflared)) {
    Write-Host "[2/4] Downloading cloudflared.exe from Cloudflare official GitHub releases..." -ForegroundColor Yellow
    if (-not (Test-Path (Join-Path $rootDir "bin"))) {
        New-Item -ItemType Directory -Path (Join-Path $rootDir "bin") -Force | Out-Null
    }
    $ProgressPreference = 'SilentlyContinue'
    [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
    Invoke-WebRequest -Uri "https://github.com/cloudflare/cloudflared/releases/latest/download/cloudflared-windows-amd64.exe" -OutFile $cloudflared
    Write-Host "  [OK] Downloaded cloudflared.exe." -ForegroundColor Green
} else {
    Write-Host "[2/4] Found cloudflared.exe in bin\." -ForegroundColor Green
}

# 3. Launch tunnel (HTTP/2 over TCP for zero UDP packet loss)
$tunnelLog = Join-Path $rootDir "data\tunnel.log"
if (-not (Test-Path (Join-Path $rootDir "data"))) {
    New-Item -ItemType Directory -Path (Join-Path $rootDir "data") -Force | Out-Null
}
if (Test-Path $tunnelLog) {
    Remove-Item -Force $tunnelLog -ErrorAction SilentlyContinue
}

Write-Host "[3/4] Launching Cloudflare HTTP/2 Tunnel for http://127.0.0.1:$LocalPort ..." -ForegroundColor Yellow

$procInfo = New-Object System.Diagnostics.ProcessStartInfo
$procInfo.FileName = $cloudflared
$procInfo.Arguments = "tunnel --protocol http2 --url http://127.0.0.1:$LocalPort"
$procInfo.RedirectStandardError = $true
$procInfo.RedirectStandardOutput = $true
$procInfo.UseShellExecute = $false
$procInfo.CreateNoWindow = $true

$tunnelProcess = New-Object System.Diagnostics.Process
$tunnelProcess.StartInfo = $procInfo
$null = $tunnelProcess.Start()

$tunnelUrl = $null
$deadline = (Get-Date).AddSeconds(30)
$logWriter = [System.IO.File]::AppendText($tunnelLog)

try {
    while ((Get-Date) -lt $deadline -and -not $tunnelUrl) {
        $line = $tunnelProcess.StandardError.ReadLine()
        if ($line) {
            $logWriter.WriteLine($line)
            $logWriter.Flush()
            if ($line -match "https://([a-zA-Z0-9-]+\.trycloudflare\.com)") {
                $tunnelUrl = "https://" + $matches[1]
                break
            }
        }
        Start-Sleep -Milliseconds 100
    }
} finally {
    $logWriter.Close()
}

if (-not $tunnelUrl) {
    Write-Host "[ERROR] Could not detect Cloudflare Tunnel URL within 30 seconds." -ForegroundColor Red
    Write-Host "Check log at: $tunnelLog" -ForegroundColor Yellow
    exit 1
}

# 4. Auto-update web/index.html & web/admin.html with new DEFAULT_STATION_URL
Write-Host "[4/4] Syncing tunnel URL ($tunnelUrl) to web frontend..." -ForegroundColor Yellow
$utf8NoBom = New-Object System.Text.UTF8Encoding($false)
foreach ($webFile in @("web\index.html", "web\admin.html")) {
    $fullWebPath = Join-Path $rootDir $webFile
    if (Test-Path $fullWebPath) {
        $content = [System.IO.File]::ReadAllText($fullWebPath, [System.Text.Encoding]::UTF8)
        $updated = [regex]::Replace($content, "const DEFAULT_STATION_URL = '[^']*';", "const DEFAULT_STATION_URL = '$tunnelUrl';")
        if ($updated -ne $content) {
            [System.IO.File]::WriteAllText($fullWebPath, $updated, $utf8NoBom)
        }
    }
}

if ($autoPushGit) {
    try {
        git add web/index.html web/admin.html 2>$null
        $diff = git status --porcelain web/index.html web/admin.html 2>$null
        if ($diff) {
            git commit -m "Auto-sync active station tunnel URL: $tunnelUrl" 2>$null | Out-Null
            git push origin main 2>$null | Out-Null
            Write-Host "  [OK] Pushed new tunnel URL to GitHub -> Netlify will auto-update in ~15s!" -ForegroundColor Green
        } else {
            Write-Host "  [OK] Web files already synced." -ForegroundColor Green
        }
    } catch {
        Write-Host "  [!] Git auto-push skipped (use the ?station= URL below or push manually)." -ForegroundColor DarkYellow
    }
}

$cleanNetlify = $NetlifyUrl.TrimEnd('/')
Write-Host ""
Write-Host "==========================================================" -ForegroundColor Green
Write-Host "                 STATION & TUNNEL LIVE!                   " -ForegroundColor Green
Write-Host "==========================================================" -ForegroundColor Green
Write-Host "Local Customer URL:      http://127.0.0.1:$LocalPort/" -ForegroundColor White
Write-Host "Local Admin URL:         http://127.0.0.1:$LocalPort/admin" -ForegroundColor White
Write-Host "Public Tunnel URL:       $tunnelUrl" -ForegroundColor Cyan
Write-Host ""
Write-Host "Netlify Customer URL (Direct):" -ForegroundColor Cyan
Write-Host "  $cleanNetlify/?station=$tunnelUrl" -ForegroundColor Green
Write-Host ""
Write-Host "Netlify Admin URL (Direct):" -ForegroundColor Cyan
Write-Host "  $cleanNetlify/admin?station=$tunnelUrl" -ForegroundColor Green
Write-Host "==========================================================" -ForegroundColor Green
Write-Host "Tunnel process PID: $($tunnelProcess.Id). Keep this window open while shop is open." -ForegroundColor Yellow
Write-Host "Press Ctrl+C to stop tunnel." -ForegroundColor DarkGray
Write-Host ""

# Keep running and streaming logs to logfile
try {
    while (-not $tunnelProcess.HasExited) {
        $l = $tunnelProcess.StandardError.ReadLine()
        if ($l) {
            [System.IO.File]::AppendAllText($tunnelLog, $l + [Environment]::NewLine)
        }
    }
} finally {
    if (-not $tunnelProcess.HasExited) {
        $tunnelProcess.Kill()
    }
}
