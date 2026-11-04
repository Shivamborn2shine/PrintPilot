<#
.SYNOPSIS
    Starts the PrintAway Local Host Web Server & Print Worker on http://127.0.0.1:8080/
.PARAMETER RealPrint
    When specified, approved print jobs are physically dispatched to 'Xerox B1025 Multifunction Printer PCL6' (USB001).
    By default, runs in safe spooler simulation mode so testing the UI does not waste physical paper unless requested.
.PARAMETER Port
    Local loopback port to bind (default: 8080).
#>

[CmdletBinding()]
param(
    [switch]$RealPrint,
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
if ($RealPrint) {
    $argsList += "--real-print"
}

& $exePath @argsList

