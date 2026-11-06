<#
.SYNOPSIS
    PrintAway Zero-Config Station & Secure Tunnel Launcher
    1. Compiles PrintAway.exe if missing (using built-in Windows .NET 4.8 csc.exe)
    2. Starts local PrintAway server on port 8080 (with physical printer auto-discovery)
    3. Downloads cloudflared.exe if missing and starts an HTTP/2 TLS tunnel
    4. Publishes the live tunnel URL to the instant cloud registry (ntfy.sh) so the
       permanent QR code (https://printitaway.netlify.app) connects in 0.1s with ZERO Git or manual setup!
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
$registryTopic = "printaway_station_registry_pilot_9f8a7b6c5d4e"
if (Test-Path $configPath) {
    try {
        $cfg = Get-Content $configPath -Raw | ConvertFrom-Json
        if (-not $NetlifyUrl -and $cfg.netlifySiteUrl) { $NetlifyUrl = $cfg.netlifySiteUrl }
        if ($LocalPort -le 0 -and $cfg.port) { $LocalPort = [int]$cfg.port }
        if ($cfg.stationRegistryTopic) { $registryTopic = [string]$cfg.stationRegistryTopic }
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
    for ($w = 0; $w -lt 15; $w++) {
        Start-Sleep -Seconds 1
        try {
            $res = Invoke-RestMethod -Uri $localApiUrl -TimeoutSec 2 -ErrorAction Stop
            if ($res.shopName) { $serverRunning = $true; break }
        } catch { }
    }
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

# 4. Publish live tunnel URL to Instant Cloud Registry (Zero Git / Zero Rebuild required!)
Write-Host "[4/4] Broadcasting live tunnel URL to Cloud Station Registry..." -ForegroundColor Yellow
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
try {
    $null = Invoke-RestMethod -Uri "https://ntfy.sh/$registryTopic" -Method Post -Body $tunnelUrl -TimeoutSec 10
    Write-Host "  [OK] Instant Cloud Registry updated! Your single QR ($NetlifyUrl) is now LIVE!" -ForegroundColor Green
} catch {
    Write-Host "  [!] Warning: Cloud Registry broadcast failed: $_" -ForegroundColor DarkYellow
}

# Also update local web files and optionally push if git is configured on dev machine
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

$cleanNetlify = $NetlifyUrl.TrimEnd('/')
Write-Host ""
Write-Host "==========================================================" -ForegroundColor Green
Write-Host "                 STATION & TUNNEL LIVE!                   " -ForegroundColor Green
Write-Host "==========================================================" -ForegroundColor Green
Write-Host "Permanent Customer QR URL:   $cleanNetlify" -ForegroundColor Green
Write-Host "Permanent Staff Admin URL:   $cleanNetlify/admin" -ForegroundColor Green
Write-Host "Local Counter Admin URL:     http://127.0.0.1:$LocalPort/admin" -ForegroundColor White
Write-Host "Active Tunnel Endpoint:      $tunnelUrl" -ForegroundColor Cyan
Write-Host "==========================================================" -ForegroundColor Green
Write-Host "Shopkeeper Instructions: Keep this window minimized while the shop is open." -ForegroundColor Yellow
Write-Host "Customers can scan your permanent QR ($cleanNetlify) right now!" -ForegroundColor Green
Write-Host ""

# Keep running, stream logs, and re-broadcast heartbeat every 30 minutes
$lastHeartbeat = Get-Date
try {
    while (-not $tunnelProcess.HasExited) {
        $l = $tunnelProcess.StandardError.ReadLine()
        if ($l) {
            [System.IO.File]::AppendAllText($tunnelLog, $l + [Environment]::NewLine)
        }
        if (((Get-Date) - $lastHeartbeat).TotalMinutes -ge 30) {
            try {
                $null = Invoke-RestMethod -Uri "https://ntfy.sh/$registryTopic" -Method Post -Body $tunnelUrl -TimeoutSec 10
                $lastHeartbeat = Get-Date
            } catch { }
        }
    }
} finally {
    if (-not $tunnelProcess.HasExited) {
        $tunnelProcess.Kill()
    }
}
