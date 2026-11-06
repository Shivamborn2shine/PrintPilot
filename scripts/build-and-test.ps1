<#
.SYNOPSIS
    Compiles PrintAway native Windows server/worker executable and runs the PRD §14 Automated Test Matrix.
#>

[CmdletBinding()]
param(
    [switch]$SkipTests
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent $PSScriptRoot
$binDir = Join-Path $repoRoot "bin"
if (-not (Test-Path $binDir)) {
    New-Item -ItemType Directory -Path $binDir -Force | Out-Null
}

$csc = "C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if (-not (Test-Path $csc)) {
    throw "64-bit C# compiler not found at $csc"
}

$outExe = Join-Path $binDir "PrintAway.exe"

# Stop any running station instance so file lock on bin\PrintAway.exe is released
Get-Process -Name "PrintAway" -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
Start-Sleep -Milliseconds 500

$sources = @(
    Join-Path $repoRoot "src\SqliteInterop.cs"
    Join-Path $repoRoot "src\PdfAndPrinterEngine.cs"
    Join-Path $repoRoot "src\DatabaseSchemaAndRepo.cs"
    Join-Path $repoRoot "src\PrintWorker.cs"
    Join-Path $repoRoot "src\HttpServerAndRoutes.cs"
    Join-Path $repoRoot "src\TunnelManager.cs"
    Join-Path $repoRoot "src\AutoUpdater.cs"
    Join-Path $repoRoot "src\SupabaseSyncClient.cs"
    Join-Path $repoRoot "src\StationGuiForm.cs"
    Join-Path $repoRoot "src\SelfTestSuite.cs"
    Join-Path $repoRoot "src\Program.cs"
)

Write-Host "=== Compiling PrintAway.exe (64-bit Native Windows GUI & Host Engine) ===" -ForegroundColor Cyan
& $csc /nologo /target:winexe /platform:x64 /optimize+ /out:$outExe `
    /r:System.dll `
    /r:System.Core.dll `
    /r:System.Drawing.dll `
    /r:System.Management.dll `
    /r:System.Web.Extensions.dll `
    /r:System.Windows.Forms.dll `
    $sources

if ($LASTEXITCODE -ne 0) {
    throw "Compilation failed with exit code $LASTEXITCODE"
}
Write-Host "Compiled successfully -> $outExe" -ForegroundColor Green

# Copy binary to root for 1-click execution directly from project folder
Copy-Item -Path $outExe -Destination (Join-Path $repoRoot "PrintAway.exe") -Force
Write-Host "Created root executable -> $(Join-Path $repoRoot 'PrintAway.exe')" -ForegroundColor Green

if (-not $SkipTests) {
    Write-Host "`n=== Running Automated PRD §14 Integration & Critical Test Matrix ===" -ForegroundColor Cyan
    & $outExe --self-test | Out-Host
    if ($LASTEXITCODE -ne 0) {
        throw "Self-test suite failed with exit code $LASTEXITCODE"
    }
}

