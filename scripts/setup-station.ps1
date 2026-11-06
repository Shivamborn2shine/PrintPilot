<#
.SYNOPSIS
    PrintAway 1-Click New PC / New Printer Setup & Migration Wizard
    Run this script whenever moving PrintAway to a new computer or changing printers.
#>

param(
    [switch]$NonInteractive
)

$ErrorActionPreference = "Stop"
$rootDir = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
Set-Location $rootDir

Write-Host "======================================================================" -ForegroundColor Cyan
Write-Host "      PRINTAWAY NEW PC / NEW PRINTER SETUP & MIGRATION WIZARD         " -ForegroundColor Cyan
Write-Host "======================================================================" -ForegroundColor Cyan

# Step 1: Verify Windows Native Dependencies
Write-Host "`n[Step 1/4] Verifying Windows Host Requirements..." -ForegroundColor Yellow
$cscPath = "C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
$sqliteDll = "C:\Windows\System32\winsqlite3.dll"

if (-not (Test-Path $cscPath)) {
    Write-Host "  [ERROR] Native .NET 4.8 C# compiler not found at $cscPath" -ForegroundColor Red
    exit 1
}
Write-Host "  [OK] .NET 4.8 Native Compiler (csc.exe) present." -ForegroundColor Green

if (-not (Test-Path $sqliteDll)) {
    Write-Host "  [ERROR] Windows native winsqlite3.dll not found at $sqliteDll" -ForegroundColor Red
    exit 1
}
Write-Host "  [OK] Windows Native SQLite (winsqlite3.dll) present." -ForegroundColor Green

$spooler = Get-Service -Name Spooler -ErrorAction SilentlyContinue
if ($spooler -and $spooler.Status -ne 'Running') {
    Write-Host "  [!] Starting Windows Print Spooler service..." -ForegroundColor DarkYellow
    Start-Service -Name Spooler -ErrorAction SilentlyContinue
}
Write-Host "  [OK] Windows Print Spooler service is running." -ForegroundColor Green

# Step 2: Compile & Run Self-Tests
Write-Host "`n[Step 2/4] Compiling PrintAway.exe & Running Verification Suite..." -ForegroundColor Yellow
powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $rootDir "scripts\build-and-test.ps1")
if ($LASTEXITCODE -ne 0) {
    Write-Host "  [ERROR] Build or self-test failed." -ForegroundColor Red
    exit 1
}

# Step 3: Discover Installed Windows Printers
Write-Host "`n[Step 3/4] Discovering Installed Windows Printers..." -ForegroundColor Yellow
$printers = @(Get-CimInstance -ClassName Win32_Printer | Select-Object Name, DriverName, PortName, WorkOffline, PrinterStatus)

$virtualRegex = "Microsoft Print to PDF|XPS Document Writer|OneNote|Fax|Send To"
$physicalPrinters = @()
$allPrinters = @()

$idx = 1
foreach ($p in $printers) {
    $isVirt = $p.Name -match $virtualRegex -or $p.PortName -match "PORTPROMPT:|FILE:|nul:"
    $tag = if ($isVirt) { "[VIRTUAL]" } else { "[PHYSICAL HARDWARE]" }
    $color = if ($isVirt) { "DarkGray" } else { "Green" }
    Write-Host ("  [{0}] {1} {2} (Port: {3}, Driver: {4})" -f $idx, $tag, $p.Name, $p.PortName, $p.DriverName) -ForegroundColor $color
    $obj = [PSCustomObject]@{
        Index = $idx
        Name = $p.Name
        Port = $p.PortName
        Driver = $p.DriverName
        IsVirtual = $isVirt
    }
    $allPrinters += $obj
    if (-not $isVirt) { $physicalPrinters += $obj }
    $idx++
}

$defaultPrinter = if ($physicalPrinters.Count -gt 0) { $physicalPrinters[0].Name } else { "auto" }
$selectedQueue = $defaultPrinter

if (-not $NonInteractive -and $allPrinters.Count -gt 0) {
    Write-Host ""
    $choice = Read-Host "Enter printer number [1-$($allPrinters.Count)] to set as active printer (or press Enter for '$defaultPrinter')"
    if ($choice -match "^\d+$") {
        $num = [int]$choice
        $matched = $allPrinters | Where-Object { $_.Index -eq $num } | Select-Object -First 1
        if ($matched) {
            $selectedQueue = $matched.Name
        }
    }
}

Write-Host "  -> Selected Active Printer Queue: $selectedQueue" -ForegroundColor Cyan

# Step 4: Save config/station.json
Write-Host "`n[Step 4/4] Saving Portable Configuration to config\station.json..." -ForegroundColor Yellow
$configDir = Join-Path $rootDir "config"
if (-not (Test-Path $configDir)) {
    New-Item -ItemType Directory -Path $configDir -Force | Out-Null
}
$configPath = Join-Path $configDir "station.json"

$netlifyUrl = "https://printitaway.netlify.app"
if (Test-Path $configPath) {
    try {
        $oldCfg = Get-Content $configPath -Raw | ConvertFrom-Json
        if ($oldCfg.netlifySiteUrl) { $netlifyUrl = $oldCfg.netlifySiteUrl }
    } catch { }
}

$newConfig = [ordered]@{
    shopName = "PrintAway Pilot Shop"
    tagline = "Printing ke liye pareshan ho? Just PrintAway."
    operatingHours = "09:00 AM - 09:00 PM IST"
    turnaroundText = "3-5 mins after counter admin approval"
    port = 8080
    activePrinterQueue = $selectedQueue
    autoFallbackToPhysicalPrinter = $true
    netlifySiteUrl = $netlifyUrl
    autoPushTunnelToGit = $true
}

$utf8NoBom = New-Object System.Text.UTF8Encoding($false)
[System.IO.File]::WriteAllText($configPath, ($newConfig | ConvertTo-Json -Depth 5), $utf8NoBom)
Write-Host "  [OK] Saved $configPath" -ForegroundColor Green

Write-Host "`n======================================================================" -ForegroundColor Green
Write-Host "  SETUP COMPLETE! Your PC and Printer ($selectedQueue) are ready." -ForegroundColor Green
Write-Host "======================================================================" -ForegroundColor Green

if (-not $NonInteractive) {
    $launchNow = Read-Host "Would you like to start the PrintAway Server & Cloudflare Tunnel now? (Y/n)"
    if ($launchNow -eq "" -or $launchNow -match "^[Yy]") {
        powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $rootDir "scripts\start-tunnel.ps1") -PrinterQueue $selectedQueue
    }
}
