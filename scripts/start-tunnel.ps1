<#
.SYNOPSIS
    PrintAway Cloudflare Quick Tunnel Launcher
    Exposes http://127.0.0.1:8080 over a free, zero-config Cloudflare HTTPS Tunnel
    and outputs the exact URLs ready to paste or use with Netlify.
#>

param(
    [string]$NetlifyUrl = "",
    [int]$LocalPort = 8080
)

$ErrorActionPreference = "Stop"
$rootDir = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
Set-Location $rootDir

Write-Host "==========================================================" -ForegroundColor Cyan
Write-Host "          PRINTAWAY ZERO-CONFIG SECURE TUNNEL            " -ForegroundColor Cyan
Write-Host "==========================================================" -ForegroundColor Cyan

# 1. Check if local PrintAway server is running
$localApiUrl = "http://127.0.0.1:$LocalPort/api/shop/status"
Write-Host "Checking local PrintAway server at $localApiUrl ..." -ForegroundColor Yellow
$serverRunning = $false
try {
    $res = Invoke-RestMethod -Uri $localApiUrl -TimeoutSec 3 -ErrorAction Stop
    if ($res.shopName) {
        $serverRunning = $true
        Write-Host "  [OK] PrintAway server is running (`"$($res.shopName)`")" -ForegroundColor Green
    }
} catch {
    Write-Host "  [!] PrintAway server not responding on port $LocalPort." -ForegroundColor DarkYellow
}

if (-not $serverRunning) {
    Write-Host "Starting local PrintAway server in background..." -ForegroundColor Yellow
    $serverExe = Join-Path $rootDir "bin\PrintAway.exe"
    if (-not (Test-Path $serverExe)) {
        Write-Host "Building PrintAway first..." -ForegroundColor Yellow
        powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $rootDir "scripts\build.ps1")
    }
    Start-Process -FilePath $serverExe -ArgumentList "--port $LocalPort" -WindowStyle Minimized
    Start-Sleep -Seconds 2
    try {
        $res = Invoke-RestMethod -Uri $localApiUrl -TimeoutSec 5 -ErrorAction Stop
        Write-Host "  [OK] Local server started successfully." -ForegroundColor Green
    } catch {
        Write-Host "  [ERROR] Failed to start local PrintAway server: $_" -ForegroundColor Red
        exit 1
    }
}

# 2. Check cloudflared binary
$cloudflared = Join-Path $rootDir "bin\cloudflared.exe"
if (-not (Test-Path $cloudflared)) {
    Write-Host "Downloading cloudflared.exe from Cloudflare official releases..." -ForegroundColor Yellow
    [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
    Invoke-WebRequest -Uri "https://github.com/cloudflare/cloudflared/releases/latest/download/cloudflared-windows-amd64.exe" -OutFile $cloudflared
    Write-Host "  [OK] Downloaded cloudflared." -ForegroundColor Green
}

# 3. Launch tunnel and parse tunnel URL
$tunnelLog = Join-Path $rootDir "data\tunnel.log"
if (-not (Test-Path (Join-Path $rootDir "data"))) {
    New-Item -ItemType Directory -Path (Join-Path $rootDir "data") -Force | Out-Null
}
if (Test-Path $tunnelLog) {
    Remove-Item -Force $tunnelLog -ErrorAction SilentlyContinue
}

Write-Host "Launching Cloudflare Tunnel for http://127.0.0.1:$LocalPort ..." -ForegroundColor Yellow

$procInfo = New-Object System.Diagnostics.ProcessStartInfo
$procInfo.FileName = $cloudflared
$procInfo.Arguments = "tunnel --url http://127.0.0.1:$LocalPort"
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

Write-Host ""
Write-Host "==========================================================" -ForegroundColor Green
Write-Host "                 TUNNEL IS LIVE!                          " -ForegroundColor Green
Write-Host "==========================================================" -ForegroundColor Green
Write-Host "Public Station Tunnel URL:" -ForegroundColor Cyan
Write-Host "  $tunnelUrl" -ForegroundColor White -BackgroundColor DarkBlue
Write-Host ""
Write-Host "Test Local Connectivity:" -ForegroundColor Yellow
Write-Host "  curl $tunnelUrl/api/shop/status"
Write-Host ""

if ($NetlifyUrl) {
    $cleanNetlify = $NetlifyUrl.TrimEnd('/')
    Write-Host "Your Ready-to-Use Netlify Customer URL:" -ForegroundColor Cyan
    Write-Host "  $cleanNetlify/?station=$tunnelUrl" -ForegroundColor Green
    Write-Host ""
    Write-Host "Your Ready-to-Use Netlify Admin URL:" -ForegroundColor Cyan
    Write-Host "  $cleanNetlify/admin?station=$tunnelUrl" -ForegroundColor Green
} else {
    Write-Host "HOW TO CONNECT WITH NETLIFY:" -ForegroundColor Cyan
    Write-Host "Option 1 (Zero-Config URL Parameter):" -ForegroundColor White
    Write-Host "  Simply append ?station=$tunnelUrl to your Netlify link:"
    Write-Host "  https://YOUR-SITE.netlify.app/?station=$tunnelUrl" -ForegroundColor Green
    Write-Host ""
    Write-Host "Option 2 (Persistent UI Setting):" -ForegroundColor White
    Write-Host "  Open your Netlify site, click '⚙️ Station URL' in the top right,"
    Write-Host "  and paste: $tunnelUrl" -ForegroundColor Green
}
Write-Host "==========================================================" -ForegroundColor Green
Write-Host "Tunnel process PID: $($tunnelProcess.Id). Keep this window open." -ForegroundColor Yellow
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
