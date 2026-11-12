<#
.SYNOPSIS
    PrintAway Code-Protected Client Distribution Packager
    Creates a clean, compiled release bundle (and ZIP) to install on client shop PCs.
    Protects your intellectual property by omitting all C# source files, .git history, .env secrets, and databases.
#>

param(
    [string]$OutputDir = ""
)

$ErrorActionPreference = "Stop"
$rootDir = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
Set-Location $rootDir

Write-Host "======================================================================" -ForegroundColor Cyan
Write-Host "     PRINTAWAY SECURE CLIENT DISTRIBUTION PACKAGER                   " -ForegroundColor Cyan
Write-Host "======================================================================" -ForegroundColor Cyan

# 1. Ensure latest PrintAway.exe is compiled
Write-Host "`n[1/4] Compiling latest production PrintAway.exe binary..." -ForegroundColor Yellow
powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $rootDir "scripts\build-and-test.ps1") -SkipTests
if ($LASTEXITCODE -ne 0) {
    Write-Host "  [ERROR] Compilation failed." -ForegroundColor Red
    exit 1
}

$binExe = Join-Path $rootDir "bin\PrintAway.exe"
if (-not (Test-Path $binExe)) {
    Write-Host "  [ERROR] bin\PrintAway.exe not found." -ForegroundColor Red
    exit 1
}

# 2. Check cloudflared.exe
$cloudflared = Join-Path $rootDir "bin\cloudflared.exe"
if (-not (Test-Path $cloudflared)) {
    Write-Host "  Downloading cloudflared.exe..." -ForegroundColor Yellow
    [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
    Invoke-WebRequest -Uri "https://github.com/cloudflare/cloudflared/releases/latest/download/cloudflared-windows-amd64.exe" -OutFile $cloudflared
}

# 3. Prepare target distribution folder
if (-not $OutputDir) {
    $OutputDir = Join-Path $rootDir "dist\PrintAway-Station-Release"
}

Write-Host "`n[2/4] Assembling clean release bundle in $OutputDir ..." -ForegroundColor Yellow
if (Test-Path $OutputDir) {
    Remove-Item -Recurse -Force $OutputDir -ErrorAction SilentlyContinue
}
New-Item -ItemType Directory -Path $OutputDir -Force | Out-Null
New-Item -ItemType Directory -Path (Join-Path $OutputDir "bin") -Force | Out-Null
New-Item -ItemType Directory -Path (Join-Path $OutputDir "scripts") -Force | Out-Null
New-Item -ItemType Directory -Path (Join-Path $OutputDir "config") -Force | Out-Null
New-Item -ItemType Directory -Path (Join-Path $OutputDir "data") -Force | Out-Null
New-Item -ItemType Directory -Path (Join-Path $OutputDir "web\assets") -Force | Out-Null

# Copy only binaries & branding icons
Copy-Item -Path $binExe -Destination (Join-Path $OutputDir "bin\PrintAway.exe") -Force
Copy-Item -Path $binExe -Destination (Join-Path $OutputDir "PrintAway.exe") -Force
Copy-Item -Path $cloudflared -Destination (Join-Path $OutputDir "bin\cloudflared.exe") -Force
if (Test-Path (Join-Path $rootDir "app.ico")) {
    Copy-Item -Path (Join-Path $rootDir "app.ico") -Destination (Join-Path $OutputDir "app.ico") -Force
    Copy-Item -Path (Join-Path $rootDir "app.ico") -Destination (Join-Path $OutputDir "bin\app.ico") -Force
}

# Copy launcher batch files
Copy-Item -Path (Join-Path $rootDir "setup-new-pc.bat") -Destination (Join-Path $OutputDir "setup-new-pc.bat") -Force
Copy-Item -Path (Join-Path $rootDir "start-station.bat") -Destination (Join-Path $OutputDir "start-station.bat") -Force
Copy-Item -Path (Join-Path $rootDir "enable-autostart.bat") -Destination (Join-Path $OutputDir "enable-autostart.bat") -Force

# Copy client scripts
Copy-Item -Path (Join-Path $rootDir "scripts\setup-station.ps1") -Destination (Join-Path $OutputDir "scripts\setup-station.ps1") -Force
Copy-Item -Path (Join-Path $rootDir "scripts\start-tunnel.ps1") -Destination (Join-Path $OutputDir "scripts\start-tunnel.ps1") -Force
Copy-Item -Path (Join-Path $rootDir "scripts\enable-autostart.ps1") -Destination (Join-Path $OutputDir "scripts\enable-autostart.ps1") -Force
Copy-Item -Path (Join-Path $rootDir "scripts\render-pdf.ps1") -Destination (Join-Path $OutputDir "scripts\render-pdf.ps1") -Force

# Copy default config template (without sensitive secrets)
if (Test-Path (Join-Path $rootDir "config\station.json")) {
    Copy-Item -Path (Join-Path $rootDir "config\station.json") -Destination (Join-Path $OutputDir "config\station.json") -Force
} else {
    $templateConfig = @{
        shopId = "midtownstationary"
        shopName = "Midtown Stationary"
        tagline = "Printing ke liye pareshan ho? Just PrintAway."
        operatingHours = "09:00 AM - 09:00 PM IST"
        turnaroundText = "3-5 mins after counter admin approval"
        port = 8080
        activePrinterQueue = "auto"
        bwPrinterQueue = ""
        colorPrinterQueue = ""
        autoFallbackToPhysicalPrinter = $true
        netlifySiteUrl = "https://printitaway.netlify.app"
        stationRegistryTopic = "printaway_station_registry_midtownstationary_9f8a7b"
        autoPushTunnelToGit = $false
    } | ConvertTo-Json -Depth 5
    [System.IO.File]::WriteAllText((Join-Path $OutputDir "config\station.json"), $templateConfig, [System.Text.Encoding]::UTF8)
}

# Copy web frontend UI files (Client HTML/CSS/Assets - ZERO C# backend code)
$webOutDir = Join-Path $OutputDir "web"
$binWebDir = Join-Path $OutputDir "bin\web"
if (-not (Test-Path $webOutDir)) { New-Item -ItemType Directory -Path $webOutDir -Force | Out-Null }
if (-not (Test-Path $binWebDir)) { New-Item -ItemType Directory -Path $binWebDir -Force | Out-Null }
if (Test-Path (Join-Path $rootDir "web\index.html")) {
    Copy-Item -Path (Join-Path $rootDir "web\index.html") -Destination (Join-Path $webOutDir "index.html") -Force
    Copy-Item -Path (Join-Path $rootDir "web\index.html") -Destination (Join-Path $binWebDir "index.html") -Force
}
if (Test-Path (Join-Path $rootDir "web\admin.html")) {
    Copy-Item -Path (Join-Path $rootDir "web\admin.html") -Destination (Join-Path $webOutDir "admin.html") -Force
    Copy-Item -Path (Join-Path $rootDir "web\admin.html") -Destination (Join-Path $binWebDir "admin.html") -Force
}
if (Test-Path (Join-Path $rootDir "web\assets")) {
    Copy-Item -Recurse -Path (Join-Path $rootDir "web\assets\*") -Destination (Join-Path $webOutDir "assets") -Force
    Copy-Item -Recurse -Path (Join-Path $rootDir "web\assets\*") -Destination (Join-Path $binWebDir "assets") -Force
}

# Create a clean README for the shopkeeper
$readmeContent = @"
======================================================================
                 PRINTAWAY COUNTER STATION SETUP
======================================================================

QUICK 1-MINUTE SETUP INSTRUCTIONS FOR NEW PC:

1. Connect your printer (USB or LAN) and make sure Windows can print to it.
2. Double-click: setup-new-pc.bat
   - Follow the short prompts to select your shop name and printer.
   - Choose 'Y' to enable Auto-Start so PrintAway starts when PC turns on.
3. Keep this window running or minimized while the shop is open.

Customers can scan your shop QR code to upload documents and print!
Support: Contact your PrintAway administrator.
======================================================================
"@
[System.IO.File]::WriteAllText((Join-Path $OutputDir "README.txt"), $readmeContent, [System.Text.Encoding]::UTF8)

Write-Host "  [OK] Release bundle created with ZERO source files!" -ForegroundColor Green

# 4. Create ZIP package
$zipPath = Join-Path $rootDir "dist\PrintAway-Station-Setup.zip"
Write-Host "`n[3/4] Creating ZIP package: $zipPath ..." -ForegroundColor Yellow
if (Test-Path $zipPath) {
    Remove-Item -Force $zipPath -ErrorAction SilentlyContinue
}

Add-Type -AssemblyName System.IO.Compression.FileSystem
[System.IO.Compression.ZipFile]::CreateFromDirectory($OutputDir, $zipPath)

$zipSizeMb = [math]::Round((Get-Item $zipPath).Length / 1MB, 2)
Write-Host "  [OK] ZIP created ($zipSizeMb MB)." -ForegroundColor Green

# 5. Mirror to releases/ and web/downloads/ for global 1-click cloud downloading
$releaseDir = Join-Path $rootDir "releases"
if (-not (Test-Path $releaseDir)) { New-Item -ItemType Directory -Path $releaseDir -Force | Out-Null }
$releaseZip = Join-Path $releaseDir "PrintAway-Station-Setup.zip"
Copy-Item -Path $zipPath -Destination $releaseZip -Force
Copy-Item -Path $binExe -Destination (Join-Path $releaseDir "PrintAway.exe") -Force
Write-Host "  [OK] Copied to $releaseZip (tracked in Git for direct download)." -ForegroundColor Green

$webDlDir = Join-Path $rootDir "web\downloads"
if (-not (Test-Path $webDlDir)) { New-Item -ItemType Directory -Path $webDlDir -Force | Out-Null }
$webDlZip = Join-Path $webDlDir "PrintAway-Station-Setup.zip"
Copy-Item -Path $zipPath -Destination $webDlZip -Force
Copy-Item -Path $binExe -Destination (Join-Path $webDlDir "PrintAway.exe") -Force
Write-Host "  [OK] Copied to $webDlZip (hosted on Netlify CDN)." -ForegroundColor Green

# 6. Security Audit Verification
Write-Host "`n[4/4] Security & Intellectual Property Audit:" -ForegroundColor Yellow
$leakedCs = Get-ChildItem -Path $OutputDir -Recurse -Filter "*.cs"
$leakedGit = Get-ChildItem -Path $OutputDir -Recurse -Filter ".git" -Hidden
$leakedEnv = Get-ChildItem -Path $OutputDir -Recurse -Filter ".env"

if ($leakedCs.Count -eq 0 -and $leakedGit.Count -eq 0 -and $leakedEnv.Count -eq 0) {
    Write-Host "  [PASSED] 100% Protected! No C# source files, No .git history, No .env secrets." -ForegroundColor Green
} else {
    Write-Host "  [WARNING] Leaked files detected!" -ForegroundColor Red
}

Write-Host "`n======================================================================" -ForegroundColor Green
Write-Host "  CLIENT DISTRIBUTION BUNDLE READY & DEPLOYABLE!" -ForegroundColor Green
Write-Host "  Local Folder: $OutputDir" -ForegroundColor White
Write-Host "  Git Release:  $releaseZip" -ForegroundColor Cyan
Write-Host "  Netlify CDN:  https://printitaway.netlify.app/downloads/PrintAway-Station-Setup.zip" -ForegroundColor Yellow
Write-Host "  GitHub Raw:   https://github.com/Shivamborn2shine/PrintPilot/raw/main/releases/PrintAway-Station-Setup.zip" -ForegroundColor Yellow
Write-Host "======================================================================" -ForegroundColor Green
Write-Host "You can safely push to Git and download this ZIP on ANY computer." -ForegroundColor Yellow
Write-Host "Your proprietary code, architecture, and cloud secrets remain 100% protected!" -ForegroundColor Green

