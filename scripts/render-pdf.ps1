[CmdletBinding()]
param(
    [Parameter(Mandatory=$true)]
    [string]$PdfPath,
    [Parameter(Mandatory=$true)]
    [string]$OutDir,
    [string]$Pages = "all"
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

if (-not (Test-Path $PdfPath)) {
    throw "PDF file not found: $PdfPath"
}
if (-not (Test-Path $OutDir)) {
    New-Item -ItemType Directory -Path $OutDir -Force | Out-Null
}

$runtimeDll = [System.IO.Directory]::GetFiles("C:\Windows\Microsoft.NET\Framework64\v4.0.30319", "System.Runtime.WindowsRuntime.dll")[0]
$null = [Windows.Data.Pdf.PdfDocument, Windows.Data.Pdf, ContentType = WindowsRuntime]
$null = [Windows.Data.Pdf.PdfPageRenderOptions, Windows.Data.Pdf, ContentType = WindowsRuntime]
$null = [Windows.Storage.StorageFile, Windows.Storage, ContentType = WindowsRuntime]
$null = [Windows.Storage.Streams.InMemoryRandomAccessStream, Windows.Storage.Streams, ContentType = WindowsRuntime]
Add-Type -Path $runtimeDll

function Invoke-WinRtAsync($AsyncOp, [Type]$ResultType) {
    $asTaskGeneric = ([System.WindowsRuntimeSystemExtensions].GetMethods() | Where-Object {
        $_.Name -eq 'AsTask' -and $_.GetParameters().Count -eq 1 -and $_.GetParameters()[0].ParameterType.Name -eq 'IAsyncOperation`1'
    })[0]
    $asTask = $asTaskGeneric.MakeGenericMethod($ResultType)
    $netTask = $asTask.Invoke($null, @($AsyncOp))
    $netTask.Wait()
    return $netTask.Result
}

function Invoke-WinRtAction($AsyncAction) {
    $asTaskMethod = ([System.WindowsRuntimeSystemExtensions].GetMethods() | Where-Object {
        $_.Name -eq 'AsTask' -and $_.GetParameters().Count -eq 1 -and $_.GetParameters()[0].ParameterType.Name -eq 'IAsyncAction'
    })[0]
    $netTask = $asTaskMethod.Invoke($null, @($AsyncAction))
    $netTask.Wait()
}

$fullPdfPath = [System.IO.Path]::GetFullPath($PdfPath)
$sf = Invoke-WinRtAsync ([Windows.Storage.StorageFile]::GetFileFromPathAsync($fullPdfPath)) ([Windows.Storage.StorageFile])
$pdfDoc = Invoke-WinRtAsync ([Windows.Data.Pdf.PdfDocument]::LoadFromFileAsync($sf)) ([Windows.Data.Pdf.PdfDocument])

$targetPages = @()
if ($Pages -eq "all" -or [string]::IsNullOrWhiteSpace($Pages)) {
    for ($i = 1; $i -le $pdfDoc.PageCount; $i++) { $targetPages += $i }
} else {
    foreach ($p in $Pages.Split(',')) {
        $val = 0
        if ([int]::TryParse($p.Trim(), [ref]$val) -and $val -ge 1 -and $val -le $pdfDoc.PageCount) {
            $targetPages += $val
        }
    }
}

foreach ($pageNum in $targetPages) {
    $zeroIndex = [uint32]($pageNum - 1)
    $page = $pdfDoc.GetPage($zeroIndex)
    $ras = New-Object Windows.Storage.Streams.InMemoryRandomAccessStream
    $opts = New-Object Windows.Data.Pdf.PdfPageRenderOptions
    # Render at 2x page dimensions for crisp laser print output (approx 150-300 DPI)
    $opts.DestinationWidth = [uint32]($page.Size.Width * 2)
    $opts.DestinationHeight = [uint32]($page.Size.Height * 2)
    Invoke-WinRtAction ($page.RenderToStreamAsync($ras, $opts))
    
    $netStream = [System.IO.WindowsRuntimeStreamExtensions]::AsStreamForRead($ras)
    $netStream.Position = 0
    $outPng = Join-Path $OutDir "page_$pageNum.png"
    $fs = [System.IO.File]::Create($outPng)
    $netStream.CopyTo($fs)
    $fs.Close()
    $netStream.Close()
    Write-Host "RENDERED: page_$pageNum.png ($($outPng))"
}
[Environment]::Exit(0)
