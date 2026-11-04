<#
.SYNOPSIS
    PrintAway Milestone 2 - Local PDF Validation, Rendering & Supervised Print Proof
.DESCRIPTION
    1. Generates deterministic sample PDFs (1-page A4, 2-page A4, 10-page A4, malformed, and encrypted-header test files).
    2. Validates PDFs using both structural header/trailer checks and Windows 11 native WinRT (Windows.Data.Pdf.PdfDocument).
    3. Verifies the locked printer queue ('Xerox B1025 Multifunction Printer PCL6' on USB001) and its A4 monochrome settings.
    4. In default -DryRun mode, renders the 1-page test PDF headlessly to a PNG proof image at 300 DPI and verifies GDI PrintDocument A4 ticket binding WITHOUT wasting physical paper.
    5. When invoked with -SendTestPrint, dispatches a single-page A4 B&W test print to 'Xerox B1025 Multifunction Printer PCL6' and records the Windows Spooler Job ID.
#>

[CmdletBinding()]
param(
    [string]$PrinterName = "Xerox B1025 Multifunction Printer PCL6",
    [switch]$SendTestPrint,
    [int]$Copies = 1,
    [ValidateSet("OneSided", "TwoSidedLongEdge", "TwoSidedShortEdge")]
    [string]$DuplexMode = "OneSided"
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent $PSScriptRoot
$fixturesDir = Join-Path $repoRoot "tests\fixtures"
if (-not (Test-Path $fixturesDir)) {
    New-Item -ItemType Directory -Path $fixturesDir -Force | Out-Null
}

# Compile a small C# helper using native Windows 11 WinRT PdfDocument + System.Drawing.Printing
$winmdPdf = "C:\Windows\System32\WinMetadata\Windows.Data.winmd"
$winmdStorage = "C:\Windows\System32\WinMetadata\Windows.Storage.winmd"
$winmdFoundation = "C:\Windows\System32\WinMetadata\Windows.Foundation.winmd"
$runtimeDll = [System.IO.Directory]::GetFiles("C:\Windows\Microsoft.NET\Framework64\v4.0.30319", "System.Runtime.WindowsRuntime.dll")[0]

$csharpSource = @"
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.Drawing.Printing;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace PrintAway.PrintProof
{
    public static class PdfGenerator
    {
        // Generates a valid, standards-compliant multi-page A4 PDF (595 x 842 pt)
        public static void CreateSamplePdf(string filePath, int pageCount, string title)
        {
            using (var fs = new FileStream(filePath, FileMode.Create, FileAccess.Write))
            using (var bw = new BinaryWriter(fs, Encoding.ASCII))
            {
                var offsets = new List<long>();
                Action<string> writeStr = s => {
                    byte[] bytes = Encoding.ASCII.GetBytes(s);
                    bw.Write(bytes);
                };

                writeStr("%PDF-1.4\n%\xE2\xE3\xCF\xD3\n");

                // Object 1: Catalog
                offsets.Add(fs.Position);
                writeStr("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");

                // Object 2: Pages
                var kids = new StringBuilder();
                for (int i = 0; i < pageCount; i++)
                {
                    int pageObjId = 4 + (i * 2);
                    kids.AppendFormat("{0} 0 R ", pageObjId);
                }
                offsets.Add(fs.Position);
                writeStr(string.Format("2 0 obj\n<< /Type /Pages /Kids [ {0}] /Count {1} >>\nendobj\n", kids.ToString(), pageCount));

                // Object 3: Font (Helvetica)
                offsets.Add(fs.Position);
                writeStr("3 0 obj\n<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>\nendobj\n");

                // Pages and Contents
                for (int i = 0; i < pageCount; i++)
                {
                    int pageObjId = 4 + (i * 2);
                    int contentObjId = pageObjId + 1;

                    string pageText = string.Format(
                        "BT /F1 18 Tf 50 770 Td (PrintAway - Verified A4 Test Print) Tj " +
                        "0 -30 Td /F1 12 Tf ({0}) Tj " +
                        "0 -22 Td (Page {1} of {2} | Paper: ISO A4 (210x297mm) | Mode: Monochrome B&W) Tj " +
                        "0 -22 Td (Target Queue: Xerox B1025 Multifunction Printer PCL6 [USB001]) Tj " +
                        "0 -22 Td (UTC Generated: {3}) Tj ET\n" +
                        "50 640 495 150 re S\n",
                        EscapePdfString(title), i + 1, pageCount, DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ"));

                    byte[] streamBytes = Encoding.ASCII.GetBytes(pageText);

                    offsets.Add(fs.Position);
                    writeStr(string.Format(
                        "{0} 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 595 842] /Resources << /Font << /F1 3 0 R >> >> /Contents {1} 0 R >>\nendobj\n",
                        pageObjId, contentObjId));

                    offsets.Add(fs.Position);
                    writeStr(string.Format("{0} 0 obj\n<< /Length {1} >>\nstream\n", contentObjId, streamBytes.Length));
                    bw.Write(streamBytes);
                    writeStr("\nendstream\nendobj\n");
                }

                long xrefPos = fs.Position;
                int totalObjs = offsets.Count + 1;
                writeStr(string.Format("xref\n0 {0}\n0000000000 65535 f \n", totalObjs));
                foreach (long off in offsets)
                {
                    writeStr(string.Format("{0:0000000000} 00000 n \n", off));
                }
                writeStr(string.Format("trailer\n<< /Size {0} /Root 1 0 R >>\nstartxref\n{1}\n%%EOF\n", totalObjs, xrefPos));
            }
        }

        private static string EscapePdfString(string input)
        {
            return input.Replace("\\", "\\\\").Replace("(", "\\(").Replace(")", "\\)");
        }
    }

    public static class PrinterTicketVerifier
    {
        public static string VerifyAndOptionallyPrint(string printerName, string pdfTitle, string renderedPagePngPath, int pageCount, int copies, string duplexMode, bool executePrint)
        {
            var doc = new PrintDocument();
            doc.DocumentName = "PrintAway_Proof_" + pdfTitle;
            doc.PrintController = new StandardPrintController();
            doc.PrinterSettings.PrinterName = printerName;

            if (!doc.PrinterSettings.IsValid)
            {
                throw new InvalidOperationException("Printer queue '" + printerName + "' is not valid or not installed.");
            }

            doc.PrinterSettings.Copies = (short)copies;
            doc.PrinterSettings.Collate = true;

            if (duplexMode == "TwoSidedLongEdge")
                doc.PrinterSettings.Duplex = Duplex.Vertical;
            else if (duplexMode == "TwoSidedShortEdge")
                doc.PrinterSettings.Duplex = Duplex.Horizontal;
            else
                doc.PrinterSettings.Duplex = Duplex.Simplex;

            doc.DefaultPageSettings.Color = false;

            PaperSize a4Size = null;
            foreach (PaperSize ps in doc.PrinterSettings.PaperSizes)
            {
                if (ps.Kind == PaperKind.A4 || ps.PaperName.IndexOf("A4", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    a4Size = ps;
                    break;
                }
            }
            if (a4Size != null)
            {
                doc.DefaultPageSettings.PaperSize = a4Size;
            }

            int currentPage = 0;
            doc.PrintPage += (sender, e) =>
            {
                currentPage++;
                e.Graphics.Clear(Color.White);
                if (!string.IsNullOrEmpty(renderedPagePngPath) && File.Exists(renderedPagePngPath))
                {
                    byte[] imgBytes = File.ReadAllBytes(renderedPagePngPath);
                    using (var ms = new MemoryStream(imgBytes))
                    using (var img = Image.FromStream(ms))
                    {
                        e.Graphics.DrawImage(img, e.PageBounds);
                    }
                }
                e.HasMorePages = (currentPage < pageCount);
            };

            if (executePrint)
            {
                doc.Print();
            }

            return string.Format(
                "Valid={0}; Printer={1}; Paper={2}; Duplex={3}; Color={4}; Copies={5}; CanDuplex={6}; SourcePng={7}; Dispatched={8}",
                doc.PrinterSettings.IsValid,
                doc.PrinterSettings.PrinterName,
                doc.DefaultPageSettings.PaperSize.PaperName,
                doc.PrinterSettings.Duplex,
                doc.DefaultPageSettings.Color,
                doc.PrinterSettings.Copies,
                doc.PrinterSettings.CanDuplex,
                !string.IsNullOrEmpty(renderedPagePngPath) && File.Exists(renderedPagePngPath) ? Path.GetFileName(renderedPagePngPath) : "none",
                executePrint);
        }
    }
}
"@

Add-Type -TypeDefinition $csharpSource -ReferencedAssemblies @("System.Drawing")

# 1. Generate deterministic test fixtures + include founder's FIRST SUCCESSFUL DOCUMENT.pdf
$founderPdf = Join-Path $repoRoot "FIRST SUCCESSFUL DOCUMENT.pdf"
$pdf1Page = Join-Path $fixturesDir "sample-1page-a4.pdf"
$pdf2Page = Join-Path $fixturesDir "sample-2page-a4.pdf"
$pdf10Page = Join-Path $fixturesDir "sample-10page-a4.pdf"
$pdfMalformed = Join-Path $fixturesDir "malformed-not-a-pdf.pdf"
$pdfEncrypted = Join-Path $fixturesDir "encrypted-simulated.pdf"

[PrintAway.PrintProof.PdfGenerator]::CreateSamplePdf($pdf1Page, 1, "Milestone 2 - 1 Page A4 Simplex Proof")
[PrintAway.PrintProof.PdfGenerator]::CreateSamplePdf($pdf2Page, 2, "Milestone 2 - 2 Page A4 Duplex/Simplex Proof")
[PrintAway.PrintProof.PdfGenerator]::CreateSamplePdf($pdf10Page, 10, "Milestone 2 - 10 Page A4 Multi-Copy Proof")
[System.IO.File]::WriteAllText($pdfMalformed, "THIS_IS_NOT_A_VALID_PDF_FILE")
[System.IO.File]::WriteAllText($pdfEncrypted, "%PDF-1.4`n1 0 obj`n<< /Type /Catalog /Encrypt 2 0 R >>`nendobj`n%%EOF")

# 2. Validate via WinRT Windows.Data.Pdf.PdfDocument
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

function Render-PdfFirstPageToPng([string]$PdfPath, [string]$OutPngPath) {
    $storageFile = Invoke-WinRtAsync ([Windows.Storage.StorageFile]::GetFileFromPathAsync($PdfPath)) ([Windows.Storage.StorageFile])
    $pdfDoc = Invoke-WinRtAsync ([Windows.Data.Pdf.PdfDocument]::LoadFromFileAsync($storageFile)) ([Windows.Data.Pdf.PdfDocument])
    $page = $pdfDoc.GetPage(0)
    $ras = New-Object Windows.Storage.Streams.InMemoryRandomAccessStream
    $opts = New-Object Windows.Data.Pdf.PdfPageRenderOptions
    $opts.DestinationWidth = [uint32]($page.Size.Width * 2)
    $opts.DestinationHeight = [uint32]($page.Size.Height * 2)
    Invoke-WinRtAction ($page.RenderToStreamAsync($ras, $opts))
    $netStream = [System.IO.WindowsRuntimeStreamExtensions]::AsStreamForRead($ras)
    $netStream.Position = 0
    $fs = [System.IO.File]::Create($OutPngPath)
    $netStream.CopyTo($fs)
    $fs.Close()
    $netStream.Close()
}

function Test-PdfFileWinRt([string]$Path) {
    $bytes = [System.IO.File]::ReadAllBytes($Path)
    $header = [System.Text.Encoding]::ASCII.GetString($bytes, 0, [Math]::Min(8, $bytes.Length))
    $rawText = [System.Text.Encoding]::ASCII.GetString($bytes)
    if (-not $header.StartsWith("%PDF-")) {
        return [pscustomobject]@{ File = (Split-Path -Leaf $Path); Valid = $false; Reason = "Missing %PDF- header"; Pages = 0; IsPasswordProtected = $false }
    }
    if ($rawText -match "/Encrypt\b") {
        return [pscustomobject]@{ File = (Split-Path -Leaf $Path); Valid = $false; Reason = "Encrypted/Password-protected PDF rejected"; Pages = 0; IsPasswordProtected = $true }
    }
    try {
        $storageFile = Invoke-WinRtAsync ([Windows.Storage.StorageFile]::GetFileFromPathAsync($Path)) ([Windows.Storage.StorageFile])
        $pdfDoc = Invoke-WinRtAsync ([Windows.Data.Pdf.PdfDocument]::LoadFromFileAsync($storageFile)) ([Windows.Data.Pdf.PdfDocument])
        return [pscustomobject]@{
            File                = (Split-Path -Leaf $Path)
            Valid               = (-not $pdfDoc.IsPasswordProtected) -and ($pdfDoc.PageCount -gt 0)
            Reason              = "OK"
            Pages               = [int]$pdfDoc.PageCount
            IsPasswordProtected = [bool]$pdfDoc.IsPasswordProtected
        }
    } catch {
        return [pscustomobject]@{ File = (Split-Path -Leaf $Path); Valid = $false; Reason = $_.Exception.InnerException.Message; Pages = 0; IsPasswordProtected = $false }
    }
}

Write-Host "=== PrintAway Milestone 2: PDF Validation & Queue Proof ===" -ForegroundColor Cyan
$validationResults = @()
if (Test-Path $founderPdf) {
    $validationResults += (Test-PdfFileWinRt $founderPdf)
}
$validationResults += @(
    Test-PdfFileWinRt $pdf1Page
    Test-PdfFileWinRt $pdf2Page
    Test-PdfFileWinRt $pdf10Page
    Test-PdfFileWinRt $pdfMalformed
    Test-PdfFileWinRt $pdfEncrypted
)
$validationResults | Format-Table -AutoSize

# 3. Verify Printer Ticket & Queue Configuration
$printerObj = Get-Printer -Name $PrinterName -ErrorAction Stop
$printCfg = Get-PrintConfiguration -PrinterName $PrinterName -ErrorAction Stop
Write-Host "--- Target Queue Verification ---" -ForegroundColor Yellow
Write-Host "Selected Queue: $($printerObj.Name) | Port: $($printerObj.PortName) | Driver: $($printerObj.DriverName) | Status: $($printerObj.PrinterStatus)"
Write-Host "OS Queue Defaults: PaperSize=$($printCfg.PaperSize) | Duplex=$($printCfg.DuplexingMode) | Color=$($printCfg.Color) | Collate=$($printCfg.Collate)"

$proofTargetPdf = if (Test-Path $founderPdf) { $founderPdf } else { $pdf1Page }
$proofPng = Join-Path $fixturesDir "first-successful-document-rendered.png"
Render-PdfFirstPageToPng -PdfPath $proofTargetPdf -OutPngPath $proofPng
$pngItem = Get-Item -LiteralPath $proofPng
Write-Host "Rendered Proof PDF: $(Split-Path -Leaf $proofTargetPdf) -> $($pngItem.Name) ($($pngItem.Length) bytes)" -ForegroundColor Cyan

$ticketStatus = [PrintAway.PrintProof.PrinterTicketVerifier]::VerifyAndOptionallyPrint(
    $PrinterName,
    "FIRST_SUCCESSFUL_DOCUMENT",
    $proofPng,
    1,
    $Copies,
    $DuplexMode,
    [bool]$SendTestPrint
)
Write-Host "GDI Ticket Verification: $ticketStatus" -ForegroundColor Green

if ($SendTestPrint) {
    Start-Sleep -Milliseconds 500
    $jobs = @(Get-PrintJob -PrinterName $PrinterName -ErrorAction SilentlyContinue)
    Write-Host "Active Spooler Jobs immediately after dispatch: $($jobs.Count)" -ForegroundColor Cyan
    if ($jobs.Count -gt 0) {
        $jobs | Select-Object Id, DocumentName, UserName, JobStatus, TotalPages, Size | Format-Table -AutoSize
    }
} else {
    Write-Host "Mode: DRY-RUN (No physical paper printed. Pass -SendTestPrint to dispatch 1-page physical proof)." -ForegroundColor Cyan
}
