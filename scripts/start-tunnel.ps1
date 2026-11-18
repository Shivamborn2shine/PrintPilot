[CmdletBinding()]
param(
    [string]$NetlifyUrl = "",
    [int]$LocalPort = 0,
    [string]$PrinterQueue = "",
    [string]$ShopId = ""
)

$ErrorActionPreference = "Stop"
$rootDir = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$serverExe = Join-Path $rootDir "bin\PrintAway.exe"
if (-not (Test-Path -LiteralPath $serverExe)) {
    $serverExe = Join-Path $rootDir "PrintAway.exe"
}
if (-not (Test-Path -LiteralPath $serverExe)) {
    $serverExe = Join-Path $rootDir "bin\PrintAway.exe"
    & (Join-Path $PSScriptRoot "build-and-test.ps1") -SkipTests
}

$argsList = @()
if ($LocalPort -gt 0) { $argsList += "--port", $LocalPort.ToString() }
if ($PrinterQueue) { $argsList += "--printer", ('"{0}"' -f $PrinterQueue) }

if ($ShopId) {
    $configPath = Join-Path $rootDir "config\station.json"
    if (-not (Test-Path -LiteralPath $configPath)) { $configPath = Join-Path $rootDir "station.json" }
    if (-not (Test-Path -LiteralPath $configPath) -or
        (Get-Content -LiteralPath $configPath -Raw | ConvertFrom-Json).shopId -ne $ShopId) {
        throw "The requested ShopId does not match the station configuration. Launch without -ShopId to restore or set up this station."
    }
}

$launch = @{ FilePath = $serverExe; WorkingDirectory = $rootDir }
if ($argsList.Count -gt 0) { $launch.ArgumentList = $argsList }
Start-Process @launch

if ($NetlifyUrl -and $ShopId) {
    Write-Host ("Storefront: {0}/?shop={1}" -f $NetlifyUrl.TrimEnd('/'), [Uri]::EscapeDataString($ShopId))
}
Write-Host "PrintAway manages the tunnel and displays its connection status in the station window."
