<#
.SYNOPSIS
    PrintAway Multi-Shop & Multi-Printer Zero-Config Station & Secure Tunnel Launcher
    1. Compiles PrintAway.exe if missing (using built-in Windows .NET 4.8 csc.exe)
    2. Starts local PrintAway server on port 8080 (with physical printer auto-discovery)
    3. Downloads cloudflared.exe if missing and starts an HTTP/2 TLS tunnel
    4. Publishes the live tunnel URL to the shop's isolated cloud registry topic (ntfy.sh)
       so the shop's permanent QR code (https://printitaway.netlify.app/?shop=<shopId>) connects in 0.1s!
#>

param(
    [string]$NetlifyUrl = "",
    [int]$LocalPort = 0,
    [string]$PrinterQueue = "",
    [string]$ShopId = ""
)

$ErrorActionPreference = "Stop"
$rootDir = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
Set-Location $rootDir

function Get-RegistryTopicForShop([string]$sid) {
    $clean = ($sid -replace '[^a-zA-Z0-9_-]', '').ToLower()
    if (-not $clean -or $clean -eq "pilot") {
        return "printaway_station_registry_pilot_9f8a7b6c5d4e"
    }
    return "printaway_station_registry_${clean}_9f8a7b"
}

# Load portable config/station.json if present
$configPath = Join-Path $rootDir "config\station.json"
$activeShopId = "pilot"
$registryTopic = ""
if (Test-Path $configPath) {
    try {
        $cfg = Get-Content $configPath -Raw | ConvertFrom-Json
        if (-not $NetlifyUrl -and $cfg.netlifySiteUrl) { $NetlifyUrl = $cfg.netlifySiteUrl }
        if ($LocalPort -le 0 -and $cfg.port) { $LocalPort = [int]$cfg.port }
        if ($cfg.shopId) { $activeShopId = [string]$cfg.shopId }
        if ($cfg.stationRegistryTopic) { $registryTopic = [string]$cfg.stationRegistryTopic }
    } catch {
        Write-Host "[Config] Warning reading config/station.json: $_" -ForegroundColor DarkYellow
    }
}
if ($ShopId) { $activeShopId = $ShopId }
$activeShopId = ($activeShopId -replace '[^a-zA-Z0-9_-]', '').ToLower()
if (-not $activeShopId) { $activeShopId = "pilot" }
if (-not $registryTopic -or ($activeShopId -ne "pilot" -and $registryTopic -eq "printaway_station_registry_pilot_9f8a7b6c5d4e")) {
    $registryTopic = Get-RegistryTopicForShop $activeShopId
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
        if ($res.shopId) {
            $activeShopId = [string]$res.shopId
            $registryTopic = Get-RegistryTopicForShop $activeShopId
        }
        Write-Host "  [OK] PrintAway server is running (Shop: [$activeShopId] `"$($res.shopName)`" -> Primary Queue: $($res.activeQueueName))" -ForegroundColor Green
    }
} catch {
    Write-Host "  [!] PrintAway server not running yet on port $LocalPort." -ForegroundColor DarkYellow
}

if (-not $serverRunning) {
    $serverExe = if (Test-Path (Join-Path $rootDir "bin\PrintAway.exe")) {
        Join-Path $rootDir "bin\PrintAway.exe"
    } elseif (Test-Path (Join-Path $rootDir "PrintAway.exe")) {
        Join-Path $rootDir "PrintAway.exe"
    } else {
        $null
    }

    if (-not $serverExe) {
        if (Test-Path (Join-Path $rootDir "src\Program.cs")) {
            Write-Host "  Compiling PrintAway.exe using built-in Windows .NET compiler..." -ForegroundColor Yellow
            powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $rootDir "scripts\build-and-test.ps1")
            $serverExe = Join-Path $rootDir "bin\PrintAway.exe"
        } else {
            Write-Host "  [ERROR] PrintAway.exe not found in directory." -ForegroundColor Red
            exit 1
        }
    }
    $argList = "--port $LocalPort"
    if ($PrinterQueue) {
        $argList += " --printer `"$PrinterQueue`""
    }
    Write-Host "  Starting PrintAway.exe ($argList)..." -ForegroundColor Yellow
    Start-Process -FilePath $serverExe -ArgumentList $argList
    for ($w = 0; $w -lt 15; $w++) {
        Start-Sleep -Seconds 1
        try {
            $res = Invoke-RestMethod -Uri $localApiUrl -TimeoutSec 2 -ErrorAction Stop
            if ($res.shopName) { $serverRunning = $true; break }
        } catch { }
    }
    try {
        $res = Invoke-RestMethod -Uri $localApiUrl -TimeoutSec 5 -ErrorAction Stop
        if ($res.shopId) {
            $activeShopId = [string]$res.shopId
            $registryTopic = Get-RegistryTopicForShop $activeShopId
        }
        Write-Host "  [OK] Local server started (Shop: [$activeShopId] `"$($res.shopName)`" -> Queue: $($res.activeQueueName))" -ForegroundColor Green
    } catch {
        Write-Host "  [ERROR] Failed to start local PrintAway server: $_" -ForegroundColor Red
        exit 1
    }
}

# 2. Check cloudflared binary
$cloudflared = if (Test-Path (Join-Path $rootDir "bin\cloudflared.exe")) {
    Join-Path $rootDir "bin\cloudflared.exe"
} elseif (Test-Path (Join-Path $rootDir "cloudflared.exe")) {
    Join-Path $rootDir "cloudflared.exe"
} else {
    Join-Path $rootDir "bin\cloudflared.exe"
}

if (-not (Test-Path $cloudflared)) {
    Write-Host "[2/4] Downloading cloudflared.exe from Cloudflare official GitHub releases..." -ForegroundColor Yellow
    $parentBin = Split-Path -Parent $cloudflared
    if (-not (Test-Path $parentBin)) {
        New-Item -ItemType Directory -Path $parentBin -Force | Out-Null
    }
    $ProgressPreference = 'SilentlyContinue'
    [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
    Invoke-WebRequest -Uri "https://github.com/cloudflare/cloudflared/releases/latest/download/cloudflared-windows-amd64.exe" -OutFile $cloudflared
    Write-Host "  [OK] Downloaded cloudflared.exe." -ForegroundColor Green
} else {
    Write-Host "[2/4] Found cloudflared.exe ($cloudflared)." -ForegroundColor Green
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

$tunnelUrl = $null
$maxRetries = 3

for ($attempt = 1; $attempt -le $maxRetries; $attempt++) {
    if ($attempt -gt 1) {
        Write-Host "  Retrying Cloudflare Quick Tunnel connection (Attempt $attempt of $maxRetries)..." -ForegroundColor Yellow
    }

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

    $deadline = (Get-Date).AddSeconds(25)
    $logWriter = [System.IO.File]::AppendText($tunnelLog)
    $got429 = $false

    try {
        while ((Get-Date) -lt $deadline -and -not $tunnelUrl -and -not $tunnelProcess.HasExited) {
            $line = $tunnelProcess.StandardError.ReadLine()
            if ($line) {
                $logWriter.WriteLine($line)
                $logWriter.Flush()
                if ($line -match "https://([a-zA-Z0-9-]+\.trycloudflare\.com)") {
                    $tunnelUrl = "https://" + $matches[1]
                    break
                }
                if ($line -match "status 429") {
                    $got429 = $true
                    break
                }
            }
            Start-Sleep -Milliseconds 100
        }
    } finally {
        $logWriter.Close()
    }

    if ($tunnelUrl) { break }

    if ($got429) {
        Write-Host "  [!] Cloudflare rate-limit active (status 429). Waiting 15 seconds before retry..." -ForegroundColor DarkYellow
        Start-Sleep -Seconds 15
    }

    try { if (-not $tunnelProcess.HasExited) { $tunnelProcess.Kill() } } catch { }
}

if (-not $tunnelUrl) {
    Write-Host "[!] Cloudflare Quick Tunnel returned HTTP 429 (Rate-limited by trycloudflare.com)." -ForegroundColor DarkYellow
    Write-Host "Activating resilient built-in OpenSSH Tunnel fallback (localhost.run)..." -ForegroundColor Yellow

    $sshExe = "C:\Windows\System32\OpenSSH\ssh.exe"
    if (-not (Test-Path $sshExe)) {
        $sshExe = "ssh.exe"
    }

    $sshInfo = New-Object System.Diagnostics.ProcessStartInfo
    $sshInfo.FileName = $sshExe
    $sshInfo.Arguments = "-o StrictHostKeyChecking=no -o ServerAliveInterval=30 -R 80:127.0.0.1:$LocalPort nokey@localhost.run"
    $sshInfo.RedirectStandardOutput = $true
    $sshInfo.RedirectStandardError = $true
    $sshInfo.UseShellExecute = $false
    $sshInfo.CreateNoWindow = $true

    $tunnelProcess = New-Object System.Diagnostics.Process
    $tunnelProcess.StartInfo = $sshInfo
    $null = $tunnelProcess.Start()

    $deadline = (Get-Date).AddSeconds(15)
    $logWriter = [System.IO.File]::AppendText($tunnelLog)
    try {
        while ((Get-Date) -lt $deadline -and -not $tunnelUrl -and -not $tunnelProcess.HasExited) {
            $line = $tunnelProcess.StandardOutput.ReadLine()
            if ($line) {
                $logWriter.WriteLine($line)
                $logWriter.Flush()
                if ($line -match "https://([a-zA-Z0-9-]+\.lhr\.life)") {
                    $tunnelUrl = "https://" + $matches[1]
                    Write-Host "  [OK] Resilient OpenSSH Fallback Tunnel Live: $tunnelUrl" -ForegroundColor Green
                    break
                }
            }
            Start-Sleep -Milliseconds 100
        }
    } finally {
        $logWriter.Close()
    }
}

if (-not $tunnelUrl) {
    Write-Host "[NOTICE] Tunnel could not be established immediately." -ForegroundColor DarkYellow
    Write-Host "Your station is running locally at http://127.0.0.1:$LocalPort/admin" -ForegroundColor White
    Write-Host "PrintAway.exe GUI has automatic retry & manual restart controls." -ForegroundColor DarkYellow
    Write-Host "Check log at: $tunnelLog" -ForegroundColor Gray
}

# 4. Publish live tunnel URL to Shop's Isolated Cloud Registry Topic & Supabase Control Plane
if ($tunnelUrl) {
    Write-Host "[4/4] Broadcasting live tunnel URL for Shop [$activeShopId] to Cloud Registry..." -ForegroundColor Yellow
    [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
    try {
        $null = Invoke-RestMethod -Uri "https://ntfy.sh/$registryTopic" -Method Post -Body $tunnelUrl -TimeoutSec 10
        Write-Host "  [OK] Instant Cloud Registry updated for Shop [$activeShopId]!" -ForegroundColor Green
    } catch {
        Write-Host "  [!] Warning: Cloud Registry broadcast failed: $_" -ForegroundColor DarkYellow
    }

    try {
        $sbUrl = "https://zduoifzakiojfqbtxqqm.supabase.co/rest/v1/shops?shop_id=eq.$activeShopId"
        $sbKey = "sb_publishable_rhmUZZbKhXiJxrChcPTbsg_gFIH9FdO"
        $body = @{ live_tunnel_url = $tunnelUrl; last_heartbeat_utc = (Get-Date).ToUniversalTime().ToString("o") } | ConvertTo-Json
        $sbHeaders = @{ "apikey" = $sbKey; "Authorization" = "Bearer $sbKey"; "Content-Type" = "application/json" }
        $null = Invoke-RestMethod -Uri $sbUrl -Method Patch -Headers $sbHeaders -Body $body -TimeoutSec 5
        Write-Host "  [OK] Supabase Control Plane updated with live tunnel URL!" -ForegroundColor Green
    } catch { }
}

$cleanNetlify = $NetlifyUrl.TrimEnd('/')
$shopQrUrl = if ($activeShopId -eq "pilot") { "$cleanNetlify/?shop=pilot" } else { "$cleanNetlify/?shop=$activeShopId" }
$shopAdminUrl = "$cleanNetlify/admin?shop=$activeShopId"

Write-Host ""
Write-Host "==========================================================" -ForegroundColor Green
Write-Host "        STATION & TUNNEL LIVE FOR SHOP: [$activeShopId]" -ForegroundColor Green
Write-Host "==========================================================" -ForegroundColor Green
Write-Host "Permanent Shop QR URL:       $shopQrUrl" -ForegroundColor Green
Write-Host "Permanent Shop Admin URL:    $shopAdminUrl" -ForegroundColor Green
Write-Host "Local Counter Admin URL:     http://127.0.0.1:$LocalPort/admin" -ForegroundColor White
Write-Host "Active Tunnel Endpoint:      $tunnelUrl" -ForegroundColor Cyan
Write-Host "==========================================================" -ForegroundColor Green
Write-Host "Shopkeeper Instructions: Keep this window minimized while the shop is open." -ForegroundColor Yellow
Write-Host "Customers can scan your permanent QR ($shopQrUrl) right now!" -ForegroundColor Green
Write-Host ""

# Keep running, stream logs, and re-broadcast heartbeat every 30 minutes
$lastHeartbeat = Get-Date
try {
    while (-not $tunnelProcess.HasExited) {
        $l = $null
        try {
            if (-not $tunnelProcess.StandardError.EndOfStream) {
                $l = $tunnelProcess.StandardError.ReadLine()
            } elseif (-not $tunnelProcess.StandardOutput.EndOfStream) {
                $l = $tunnelProcess.StandardOutput.ReadLine()
            } else {
                Start-Sleep -Seconds 1
            }
        } catch { Start-Sleep -Seconds 1 }

        if ($l) {
            try { [System.IO.File]::AppendAllText($tunnelLog, $l + [Environment]::NewLine) } catch { }
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
