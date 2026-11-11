<#
.SYNOPSIS
    PrintAway 1-Click Multi-Shop & Multi-Printer Setup & Migration Wizard
    Run this script whenever onboarding a new franchise shop, moving PrintAway to a new PC, or changing printers.
#>

param(
    [switch]$NonInteractive,
    [string]$ShopId = "",
    [string]$ShopName = ""
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

Write-Host "======================================================================" -ForegroundColor Cyan
Write-Host "   PRINTAWAY MULTI-SHOP & MULTI-PRINTER SETUP & MIGRATION WIZARD      " -ForegroundColor Cyan
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

# Step 2: Verify or Compile PrintAway.exe & Run Self-Tests
Write-Host "`n[Step 2/4] Verifying PrintAway Engine..." -ForegroundColor Yellow
$srcProgram = Join-Path $rootDir "src\Program.cs"
$binExe = Join-Path $rootDir "bin\PrintAway.exe"
$rootExe = Join-Path $rootDir "PrintAway.exe"

if (Test-Path $srcProgram) {
    # Developer environment: Compile & run verification test matrix
    powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $rootDir "scripts\build-and-test.ps1")
    if ($LASTEXITCODE -ne 0) {
        Write-Host "  [ERROR] Build or self-test failed." -ForegroundColor Red
        exit 1
    }
} elseif ((Test-Path $binExe) -or (Test-Path $rootExe)) {
    Write-Host "  [OK] Standalone pre-compiled PrintAway.exe present and ready." -ForegroundColor Green
} else {
    Write-Host "  [ERROR] Neither source code nor pre-compiled PrintAway.exe found in directory." -ForegroundColor Red
    exit 1
}

# Step 3: Shop Identity & Multi-Printer Discovery
Write-Host "`n[Step 3/4] Configuring Shop Identity & Discovering Windows Printers..." -ForegroundColor Yellow

$configDir = Join-Path $rootDir "config"
$configPath = Join-Path $configDir "station.json"
$netlifyUrl = "https://printitaway.netlify.app"
$defaultShopId = if ($ShopId) { $ShopId } else { "pilot" }
$defaultShopName = if ($ShopName) { $ShopName } else { "PrintAway Pilot Shop" }
$defaultBwQueue = ""
$defaultColorQueue = ""

if (Test-Path $configPath) {
    try {
        $oldCfg = Get-Content $configPath -Raw | ConvertFrom-Json
        if ($oldCfg.netlifySiteUrl) { $netlifyUrl = $oldCfg.netlifySiteUrl }
        if (-not $ShopId -and $oldCfg.shopId) { $defaultShopId = $oldCfg.shopId }
        if (-not $ShopName -and $oldCfg.shopName) { $defaultShopName = $oldCfg.shopName }
        if ($oldCfg.bwPrinterQueue) { $defaultBwQueue = $oldCfg.bwPrinterQueue }
        if ($oldCfg.colorPrinterQueue) { $defaultColorQueue = $oldCfg.colorPrinterQueue }
    } catch { }
}

$selectedShopId = $defaultShopId
$selectedShopName = $defaultShopName

if (-not $NonInteractive) {
    $inId = Read-Host "Enter unique Shop ID for this location (e.g. 'pilot', 'shop-02', 'campus-01') [Default: $defaultShopId]"
    if ($inId -and $inId.Trim()) {
        $selectedShopId = ($inId.Trim() -replace '[^a-zA-Z0-9_-]', '').ToLower()
    }
    $inName = Read-Host "Enter Shop Display Name [Default: $defaultShopName]"
    if ($inName -and $inName.Trim()) {
        $selectedShopName = $inName.Trim()
    }
}
Write-Host "  -> Active Shop Identity: [$selectedShopId] $selectedShopName" -ForegroundColor Cyan

Write-Host "`n  Installed Windows Printer Queues:" -ForegroundColor Yellow
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
$selectedBwQueue = $defaultBwQueue
$selectedColorQueue = $defaultColorQueue

if (-not $NonInteractive -and $allPrinters.Count -gt 0) {
    Write-Host ""
    $choice = Read-Host "Select Primary / Default Printer [1-$($allPrinters.Count)] (or press Enter for '$defaultPrinter')"
    if ($choice -match "^\d+$") {
        $matched = $allPrinters | Where-Object { $_.Index -eq [int]$choice } | Select-Object -First 1
        if ($matched) { $selectedQueue = $matched.Name }
    }

    if ($physicalPrinters.Count -gt 1 -or $allPrinters.Count -gt 1) {
        $bwChoice = Read-Host "Optional: Select Dedicated B&W Printer [1-$($allPrinters.Count)] (or press Enter to use Primary '$selectedQueue')"
        if ($bwChoice -match "^\d+$") {
            $matchedBw = $allPrinters | Where-Object { $_.Index -eq [int]$bwChoice } | Select-Object -First 1
            if ($matchedBw) { $selectedBwQueue = $matchedBw.Name }
        }

        $colorChoice = Read-Host "Optional: Select Dedicated Color Printer [1-$($allPrinters.Count)] (or press Enter to use Primary '$selectedQueue')"
        if ($colorChoice -match "^\d+$") {
            $matchedColor = $allPrinters | Where-Object { $_.Index -eq [int]$colorChoice } | Select-Object -First 1
            if ($matchedColor) { $selectedColorQueue = $matchedColor.Name }
        }
    }
}

Write-Host "  -> Primary Printer Queue:         $selectedQueue" -ForegroundColor Cyan
Write-Host "  -> Dedicated B&W Printer Queue:   $(if ($selectedBwQueue) { $selectedBwQueue } else { '(Uses Primary)' })" -ForegroundColor Cyan
Write-Host "  -> Dedicated Color Printer Queue: $(if ($selectedColorQueue) { $selectedColorQueue } else { '(Uses Primary)' })" -ForegroundColor Cyan

# Step 4: Save config/station.json
Write-Host "`n[Step 4/4] Saving Portable Configuration to config\station.json..." -ForegroundColor Yellow
if (-not (Test-Path $configDir)) {
    New-Item -ItemType Directory -Path $configDir -Force | Out-Null
}

$topic = Get-RegistryTopicForShop $selectedShopId
$newConfig = [ordered]@{
    shopId = $selectedShopId
    shopName = $selectedShopName
    tagline = "Printing ke liye pareshan ho? Just PrintAway."
    operatingHours = "09:00 AM - 09:00 PM IST"
    turnaroundText = "3-5 mins after counter admin approval"
    port = 8080
    activePrinterQueue = $selectedQueue
    bwPrinterQueue = $selectedBwQueue
    colorPrinterQueue = $selectedColorQueue
    autoFallbackToPhysicalPrinter = $true
    netlifySiteUrl = $netlifyUrl
    stationRegistryTopic = $topic
    autoPushTunnelToGit = $false
}

$utf8NoBom = New-Object System.Text.UTF8Encoding($false)
[System.IO.File]::WriteAllText($configPath, ($newConfig | ConvertTo-Json -Depth 5), $utf8NoBom)
Write-Host "  [OK] Saved $configPath" -ForegroundColor Green

$cleanNetlify = $netlifyUrl.TrimEnd('/')
$qrUrl = "$cleanNetlify/?shop=$selectedShopId"
$adminUrl = "$cleanNetlify/admin?shop=$selectedShopId"

if (-not $NonInteractive) {
    $autoBoot = Read-Host "`nEnable Auto-Start on Windows Boot so the shopkeeper only needs to turn on the PC? (Y/n)"
    if ($autoBoot -eq "" -or $autoBoot -match "^[Yy]") {
        powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $rootDir "scripts\enable-autostart.ps1")
    }
}

Write-Host "`n======================================================================" -ForegroundColor Green
Write-Host "  SETUP COMPLETE FOR SHOP [$selectedShopId] ($selectedShopName)!" -ForegroundColor Green
Write-Host "  Permanent Customer QR Code URL: $qrUrl" -ForegroundColor Green
Write-Host "  Permanent Shop Admin URL:       $adminUrl" -ForegroundColor Green
Write-Host "======================================================================" -ForegroundColor Green

if (-not $NonInteractive) {
    $launchNow = Read-Host "Would you like to start the PrintAway Station now? (Y/n)"
    if ($launchNow -eq "" -or $launchNow -match "^[Yy]") {
        $serverExe = if (Test-Path (Join-Path $rootDir "bin\PrintAway.exe")) {
            Join-Path $rootDir "bin\PrintAway.exe"
        } else {
            Join-Path $rootDir "PrintAway.exe"
        }
        Start-Process -FilePath $serverExe
        Write-Host "`n  [OK] PrintAway Station launched successfully!" -ForegroundColor Green
    }
}
