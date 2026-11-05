<#
.SYNOPSIS
    Starts the PrintAway Local Host Web Server & Print Worker on http://127.0.0.1:8080/
.PARAMETER Simulation
    When specified, runs in safe spooler simulation mode (dry-run without physical paper).
    By default, LIVE PHYSICAL PRINTING to 'Xerox B1025 Multifunction Printer PCL6' (USB001) is ENABLED.
.PARAMETER Port
    Local loopback port to bind (default: 8080).
#>

[CmdletBinding()]
param(
    [switch]$Simulation,
    [int]$Port = 8080
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent $PSScriptRoot
$exePath = Join-Path $repoRoot "bin\PrintAway.exe"

if (-not (Test-Path $exePath)) {
    & (Join-Path $PSScriptRoot "build-and-test.ps1") -SkipTests
}

$argsList = @("--port", $Port.ToString())
if ($Simulation) {
    $argsList += "--simulation"
}

& $exePath @argsList

