<#
.SYNOPSIS
    Configures Windows to automatically launch PrintAway Station & Tunnel whenever the PC boots up.
    The shopkeeper only needs to turn on the PC and printer!
#>

$ErrorActionPreference = "Stop"
$rootDir = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$batPath = Join-Path $rootDir "start-station.bat"
$startupDir = [Environment]::GetFolderPath('Startup')
$shortcutPath = Join-Path $startupDir "PrintAway-Station.lnk"

$wsh = New-Object -ComObject WScript.Shell
$sc = $wsh.CreateShortcut($shortcutPath)
$sc.TargetPath = $batPath
$sc.WorkingDirectory = $rootDir
$sc.WindowStyle = 7 # Minimized so it doesn't clutter the shopkeeper's screen
$sc.Description = "Auto-starts PrintAway Local Printer Server & Cloud Tunnel on Windows login"
$sc.Save()

Write-Host "======================================================================" -ForegroundColor Green
Write-Host "  WINDOWS AUTO-START ENABLED SUCCESSFULLY!                            " -ForegroundColor Green
Write-Host "======================================================================" -ForegroundColor Green
Write-Host "Startup Shortcut Created:" -ForegroundColor Cyan
Write-Host "  $shortcutPath" -ForegroundColor White
Write-Host ""
Write-Host "What this means for the Shopkeeper:" -ForegroundColor Yellow
Write-Host "  1. Turn on the Computer and Printer in the morning."
Write-Host "  2. PrintAway Server & Cloud Tunnel start automatically in the background."
Write-Host "  3. Your permanent QR code (https://printitaway.netlify.app) connects"
Write-Host "     automatically to the new tunnel within 5 seconds - no Git or clicks needed!"
Write-Host "======================================================================" -ForegroundColor Green

