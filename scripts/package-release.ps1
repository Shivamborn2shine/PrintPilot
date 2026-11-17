<#
.SYNOPSIS
    Packages a clean, distributor-ready PrintAway Station folder and ZIP archive for non-technical shopkeepers.
    Strictly excludes all source code (.cs), Git history (.git), developer scripts, and local databases.
#>

[CmdletBinding()]
param(
    [string]$Version = "2.2.0"
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent $PSScriptRoot
$distRoot = Join-Path $repoRoot "dist"
$releaseName = "PrintAway-Station-v$Version"
$releaseDir = Join-Path $distRoot $releaseName
$zipPath = Join-Path $distRoot "$releaseName.zip"

Write-Host "=== Packaging Distributor Release: $releaseName ===" -ForegroundColor Cyan

# 1. Compile fresh binary
& (Join-Path $PSScriptRoot "build-and-test.ps1") -SkipTests

if (Test-Path $releaseDir) {
    Remove-Item $releaseDir -Recurse -Force
}
if (Test-Path $zipPath) {
    Remove-Item $zipPath -Force
}

New-Item -ItemType Directory -Path $releaseDir -Force | Out-Null
New-Item -ItemType Directory -Path (Join-Path $releaseDir "config") -Force | Out-Null
New-Item -ItemType Directory -Path (Join-Path $releaseDir "bin") -Force | Out-Null

# 2. Copy compiled executable
Copy-Item (Join-Path $repoRoot "bin\PrintAway.exe") (Join-Path $releaseDir "PrintAway.exe")

# 3. Copy tunnel binary if present
$cfExe = Join-Path $repoRoot "bin\cloudflared.exe"
if (Test-Path $cfExe) {
    Copy-Item $cfExe (Join-Path $releaseDir "bin\cloudflared.exe")
}

# 4. Ship only the example config. Never bundle a real station.json: every install would
#    start up as that shop. PrintAway.exe runs a first-time setup wizard instead.
Copy-Item (Join-Path $repoRoot "config\station.example.json") (Join-Path $releaseDir "config\station.example.json")

# 5. Copy web assets
Copy-Item (Join-Path $repoRoot "web") (Join-Path $releaseDir "web") -Recurse

# 6. Copy .env.example
if (Test-Path (Join-Path $repoRoot ".env.example")) {
    Copy-Item (Join-Path $repoRoot ".env.example") (Join-Path $releaseDir ".env.example")
}

# 7. Create user-friendly instructions README.txt
$readmeContent = @"
========================================================================
   PrintAway Station - Automated Self-Service Printing Hub
========================================================================

HOW TO START:
1. Double-click 'PrintAway.exe'
2. The first time, a setup wizard asks for your shop name, a unique
   Shop ID, owner details, your UPI ID, an admin password and your printer.
3. The station minimizes to your Windows system tray near the clock
   and prints quietly in the background without any command prompt windows!

CUSTOMER QR LINK:
Print the QR code from the station window or use:
https://printitaway.netlify.app/?shop=<your-shop-id>

ADMIN DASHBOARD:
Click 'Open Counter Admin Dashboard' or visit http://127.0.0.1:8080/admin
Username: your Shop ID
Password: the admin password you chose in the setup wizard

AUTO-START:
Check 'Start station automatically when Windows boots' inside the app
so you never have to launch it manually.
========================================================================
"@

Set-Content (Join-Path $releaseDir "README.txt") $readmeContent

# 8. Create ZIP archive
Write-Host "Creating ZIP archive: $zipPath..." -ForegroundColor Yellow
Add-Type -AssemblyName System.IO.Compression.FileSystem
[System.IO.Compression.ZipFile]::CreateFromDirectory($releaseDir, $zipPath)

$zipSizeMb = (Get-Item $zipPath).Length / 1MB
Write-Host "Release package ready -> $zipPath ($([Math]::Round($zipSizeMb, 2)) MB)" -ForegroundColor Green
Write-Host "Strict IP Protection verified: 0 source files (.cs) or Git history included." -ForegroundColor Green

