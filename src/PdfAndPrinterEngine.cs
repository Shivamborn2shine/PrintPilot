using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Drawing.Printing;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Management;
using System.Net;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Xml;

namespace PrintAway.Engine
{
    public sealed class PdfValidationResult
    {
        public bool IsValid { get; set; }
        public string ErrorCode { get; set; }
        public string ErrorMessage { get; set; }
        public int PageCount { get; set; }
        public long FileSizeBytes { get; set; }
        public string Sha256Hex { get; set; }
        public bool IsEncrypted { get; set; }
        public bool HasEmbeddedJavaScript { get; set; }
    }

    public sealed class QuoteCalculationResult
    {
        public int DocumentPages { get; set; }
        public int SelectedPageCount { get; set; }
        public string NormalizedPageRange { get; set; }
        public List<int> ResolvedPages { get; set; }
        public int Copies { get; set; }
        public string DuplexMode { get; set; }
        public string ColorMode { get; set; }
        public string PaperSize { get; set; }
        public string Orientation { get; set; }
        public int PagesPerSheet { get; set; }
        public bool Collate { get; set; }
        public int PrintedSidesPerCopy { get; set; }
        public int TotalPrintedSides { get; set; }
        public int SheetsPerCopy { get; set; }
        public int TotalPhysicalSheets { get; set; }
        public double RatePerSideInr { get; set; }
        public double BaseChargeInr { get; set; }
        public double MinimumChargeAppliedInr { get; set; }
        public double TaxAmountInr { get; set; }
        public double TotalAmountInr { get; set; }
        public long TotalAmountPaise { get; set; }
    }

    public sealed class DiscoveredPrinterQueue
    {
        public string QueueName { get; set; }
        public string DriverName { get; set; }
        public string PortName { get; set; }
        public bool IsDefault { get; set; }
        public bool WorkOffline { get; set; }
        public string StatusText { get; set; }
        public int PrinterStatusCode { get; set; }
        public int ExtendedStatus { get; set; }
        public int ActiveJobCount { get; set; }
        public bool IsVirtualExcluded { get; set; }
        public bool IsXeroxCandidate { get; set; }
        public bool SupportsDuplex { get; set; }
        public string DefaultPaperSize { get; set; }
        public bool IsHealthyForDispatch { get; set; }
        public string HealthReason { get; set; }
    }

    public sealed class PrintDispatchResult
    {
        public bool Dispatched { get; set; }
        public int? WindowsSpoolerJobId { get; set; }
        public string SpoolerDocumentName { get; set; }
        public string TargetQueueName { get; set; }
        public int PrintedSides { get; set; }
        public int PhysicalSheets { get; set; }
        public bool SimulatedMode { get; set; }
        public string ErrorMessage { get; set; }
    }

    public static class PdfAndPrinterEngine
    {
        private static readonly Regex PageObjRegex = new Regex(@"/Type\s*/Page\b(?![sS])", RegexOptions.Compiled);
        private static readonly Regex PagesCountRegex = new Regex(@"/Type\s*/Pages\b[^>]*?/Count\s+(\d+)", RegexOptions.Compiled | RegexOptions.Singleline);
        private static readonly Regex EncryptRegex = new Regex(@"/Encrypt\b", RegexOptions.Compiled);
        private static readonly Regex JsRegex = new Regex(@"/(?:JavaScript|JS)\b", RegexOptions.Compiled);
        private static readonly Regex VirtualPrinterRegex = new Regex(@"Microsoft Print to PDF|Microsoft XPS Document Writer|OneNote|Fax|Send To OneNote", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex VirtualPortRegex = new Regex(@"^(?:PORTPROMPT:|nul:|SHRFAX:|FILE:)$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        public static bool IsImageBytes(byte[] bytes)
        {
            if (bytes == null || bytes.Length < 4) return false;
            // JPEG: FF D8 FF
            if (bytes[0] == 0xFF && bytes[1] == 0xD8 && bytes[2] == 0xFF) return true;
            // PNG: 89 50 4E 47
            if (bytes.Length >= 8 && bytes[0] == 0x89 && bytes[1] == 0x50 && bytes[2] == 0x4E && bytes[3] == 0x47) return true;
            // GIF: 47 49 46
            if (bytes[0] == 0x47 && bytes[1] == 0x49 && bytes[2] == 0x46) return true;
            // BMP: 42 4D
            if (bytes[0] == 0x42 && bytes[1] == 0x4D) return true;
            // TIFF: 49 49 2A 00 or 4D 4D 00 2A
            if (bytes.Length >= 4 && ((bytes[0] == 0x49 && bytes[1] == 0x49 && bytes[2] == 0x2A && bytes[3] == 0x00) ||
                                      (bytes[0] == 0x4D && bytes[1] == 0x4D && bytes[2] == 0x00 && bytes[3] == 0x2A))) return true;
            // WEBP: RIFF....WEBP
            if (bytes.Length >= 12 && bytes[0] == 0x52 && bytes[1] == 0x49 && bytes[2] == 0x46 && bytes[3] == 0x46 &&
                bytes[8] == 0x57 && bytes[9] == 0x45 && bytes[10] == 0x42 && bytes[11] == 0x50) return true;
            return false;
        }

        public static bool IsImageExtension(string filename)
        {
            if (string.IsNullOrEmpty(filename)) return false;
            string ext = Path.GetExtension(filename).ToLowerInvariant();
            return ext == ".jpg" || ext == ".jpeg" || ext == ".png" || ext == ".webp" || ext == ".bmp" || ext == ".gif" || ext == ".jfif" || ext == ".tif" || ext == ".tiff";
        }

        public static bool IsTextExtension(string filename)
        {
            if (string.IsNullOrEmpty(filename)) return false;
            string ext = Path.GetExtension(filename).ToLowerInvariant();
            return ext == ".txt" || ext == ".csv" || ext == ".log" || ext == ".tsv" || ext == ".md" || ext == ".json";
        }

        public static bool IsOfficeDocExtension(string filename)
        {
            if (string.IsNullOrEmpty(filename)) return false;
            string ext = Path.GetExtension(filename).ToLowerInvariant();
            return ext == ".docx" || ext == ".doc" || ext == ".rtf" || ext == ".odt" || ext == ".html" || ext == ".htm" || ext == ".pptx" || ext == ".xlsx";
        }

        public static bool IsZipArchiveBytes(byte[] bytes)
        {
            return bytes != null && bytes.Length >= 4 && bytes[0] == 0x50 && bytes[1] == 0x4B && bytes[2] == 0x03 && bytes[3] == 0x04;
        }

        public static bool IsRtfBytes(byte[] bytes)
        {
            if (bytes == null || bytes.Length < 5) return false;
            string head = Encoding.ASCII.GetString(bytes, 0, 5);
            return string.Equals(head, "{\\rtf", StringComparison.OrdinalIgnoreCase);
        }

        private static ImageCodecInfo GetEncoder(ImageFormat format)
        {
            ImageCodecInfo[] codecs = ImageCodecInfo.GetImageDecoders();
            for (int i = 0; i < codecs.Length; i++)
            {
                if (codecs[i].FormatID == format.Guid)
                {
                    return codecs[i];
                }
            }
            return null;
        }

        public static byte[] ConvertImageToA4Pdf(byte[] imageBytes)
        {
            if (imageBytes == null || imageBytes.Length == 0) return null;
            try
            {
                using (var msIn = new MemoryStream(imageBytes))
                using (var originalImg = Image.FromStream(msIn))
                {
                    // Check for mobile camera EXIF orientation tag 0x0112 (274)
                    try
                    {
                        if (Array.IndexOf(originalImg.PropertyIdList, 0x0112) >= 0)
                        {
                            var prop = originalImg.GetPropertyItem(0x0112);
                            if (prop != null && prop.Value != null && prop.Value.Length >= 2)
                            {
                                int orientation = BitConverter.ToUInt16(prop.Value, 0);
                                if (orientation == 6) originalImg.RotateFlip(RotateFlipType.Rotate90FlipNone);
                                else if (orientation == 8) originalImg.RotateFlip(RotateFlipType.Rotate270FlipNone);
                                else if (orientation == 3) originalImg.RotateFlip(RotateFlipType.Rotate180FlipNone);
                            }
                        }
                    }
                    catch { }

                    int imgW = originalImg.Width;
                    int imgH = originalImg.Height;
                    if (imgW <= 0 || imgH <= 0) return null;

                    byte[] jpegBytes;
                    using (var msJpeg = new MemoryStream())
                    {
                        ImageCodecInfo jpgEncoder = GetEncoder(ImageFormat.Jpeg);
                        if (jpgEncoder != null)
                        {
                            using (var encParams = new EncoderParameters(1))
                            {
                                encParams.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, 96L);
                                originalImg.Save(msJpeg, jpgEncoder, encParams);
                            }
                        }
                        else
                        {
                            originalImg.Save(msJpeg, ImageFormat.Jpeg);
                        }
                        jpegBytes = msJpeg.ToArray();
                    }

                    // ISO A4 points (72 points/inch)
                    // Portrait: 595.28 x 841.89 pt | Landscape: 841.89 x 595.28 pt
                    bool isLandscape = imgW > (int)(imgH * 1.15);
                    double pageW = isLandscape ? 841.89 : 595.28;
                    double pageH = isLandscape ? 595.28 : 841.89;

                    double margin = 20.0; // 7mm safe printable margin
                    double safeW = pageW - (margin * 2.0);
                    double safeH = pageH - (margin * 2.0);
                    double scale = Math.Min(safeW / imgW, safeH / imgH);
                    double drawW = imgW * scale;
                    double drawH = imgH * scale;
                    double posX = (pageW - drawW) / 2.0;
                    double posY = (pageH - drawH) / 2.0;

                    return GenerateSingleImageA4Pdf(jpegBytes, imgW, imgH, pageW, pageH, drawW, drawH, posX, posY);
                }
            }
            catch (Exception ex)
            {
                Trace.WriteLine("[PdfAndPrinterEngine] ConvertImageToA4Pdf error: " + ex.Message);
                return null;
            }
        }

        private sealed class DocParagraphBlock
        {
            public string Text;
            public bool IsHeading;
            public bool IsSubHeading;
            public bool IsBold;
        }

        private sealed class A4RenderedPage
        {
            public byte[] JpegBytes;
            public int PixelWidth;
            public int PixelHeight;
            public double PageWidthPt;
            public double PageHeightPt;
            public double DrawWidthPt;
            public double DrawHeightPt;
            public double PosXPt;
            public double PosYPt;
        }

        public static byte[] ConvertTextToA4Pdf(byte[] textBytes)
        {
            if (textBytes == null || textBytes.Length == 0) return null;
            try
            {
                string text = Encoding.UTF8.GetString(textBytes);
                var blocks = new List<DocParagraphBlock>();
                string[] lines = text.Replace("\r\n", "\n").Split('\n');
                for (int i = 0; i < lines.Length; i++)
                {
                    blocks.Add(new DocParagraphBlock { Text = lines[i] });
                }
                return RenderDocBlocksToMultiPageA4Pdf(blocks, true);
            }
            catch (Exception ex)
            {
                Trace.WriteLine("[PdfAndPrinterEngine] ConvertTextToA4Pdf error: " + ex.Message);
                return null;
            }
        }

        public static byte[] ConvertDocumentToA4Pdf(string filename, byte[] docBytes, out string failureReason)
        {
            failureReason = null;
            if (docBytes == null || docBytes.Length == 0)
            {
                failureReason = "Document file is empty.";
                return null;
            }

            string ext = Path.GetExtension(filename ?? "").ToLowerInvariant();
            if (string.IsNullOrEmpty(ext))
            {
                if (IsRtfBytes(docBytes)) ext = ".rtf";
                else if (IsZipArchiveBytes(docBytes)) ext = ".docx";
            }

            // Tier 1: High-fidelity local Office converters if installed on the shop PC (LibreOffice headless or MS Word COM)
            if (ext == ".docx" || ext == ".doc" || ext == ".odt" || ext == ".rtf" || ext == ".pptx" || ext == ".xlsx")
            {
                byte[] loPdf = TryConvertViaLibreOffice(ext, docBytes);
                if (loPdf != null && loPdf.Length > 0) return loPdf;

                if (ext == ".docx" || ext == ".doc" || ext == ".rtf" || ext == ".odt")
                {
                    byte[] wordPdf = TryConvertViaWordCom(ext, docBytes);
                    if (wordPdf != null && wordPdf.Length > 0) return wordPdf;
                }
            }

            // Tier 2: Built-in Zero-Dependency .NET 4.x converters (always available on Windows without Office!)
            try
            {
                if (ext == ".docx" || (ext == "" && IsZipArchiveBytes(docBytes)))
                {
                    List<DocParagraphBlock> docxBlocks = ExtractDocxBlocks(docBytes);
                    if (docxBlocks != null && docxBlocks.Count > 0)
                    {
                        byte[] pdf = RenderDocBlocksToMultiPageA4Pdf(docxBlocks, false);
                        if (pdf != null && pdf.Length > 0) return pdf;
                    }
                    // Maybe an ODT inside a zip?
                    List<DocParagraphBlock> odtFallback = ExtractOdtBlocks(docBytes);
                    if (odtFallback != null && odtFallback.Count > 0)
                    {
                        byte[] pdf = RenderDocBlocksToMultiPageA4Pdf(odtFallback, false);
                        if (pdf != null && pdf.Length > 0) return pdf;
                    }
                }
                else if (ext == ".odt")
                {
                    List<DocParagraphBlock> odtBlocks = ExtractOdtBlocks(docBytes);
                    if (odtBlocks != null && odtBlocks.Count > 0)
                    {
                        byte[] pdf = RenderDocBlocksToMultiPageA4Pdf(odtBlocks, false);
                        if (pdf != null && pdf.Length > 0) return pdf;
                    }
                }
                else if (ext == ".rtf" || IsRtfBytes(docBytes))
                {
                    string plain = ExtractRtfPlainText(docBytes);
                    if (!string.IsNullOrWhiteSpace(plain))
                    {
                        byte[] pdf = ConvertTextToA4Pdf(Encoding.UTF8.GetBytes(plain));
                        if (pdf != null && pdf.Length > 0) return pdf;
                    }
                }
                else if (ext == ".html" || ext == ".htm")
                {
                    string rawHtml = Encoding.UTF8.GetString(docBytes);
                    string plainHtml = ExtractHtmlPlainText(rawHtml);
                    byte[] pdf = ConvertTextToA4Pdf(Encoding.UTF8.GetBytes(plainHtml));
                    if (pdf != null && pdf.Length > 0) return pdf;
                }
                else if (ext == ".doc")
                {
                    failureReason = "Legacy binary .doc files require Microsoft Word or LibreOffice on the shop PC, or please re-save the file as .docx / .pdf before uploading.";
                    return null;
                }
                else if (ext == ".pptx" || ext == ".xlsx")
                {
                    failureReason = "Presentations/spreadsheets (" + ext + ") require LibreOffice on the shop PC, or please export as .pdf / .docx before uploading.";
                    return null;
                }
            }
            catch (Exception ex)
            {
                Trace.WriteLine("[PdfAndPrinterEngine] ConvertDocumentToA4Pdf built-in fallback error: " + ex.Message);
            }

            failureReason = "Could not convert document format (" + ext + ") into a printable PDF. Please save as PDF or DOCX and try again.";
            return null;
        }

        private static byte[] TryConvertViaLibreOffice(string ext, byte[] docBytes)
        {
            try
            {
                string[] candidates = new string[]
                {
                    @"C:\Program Files\LibreOffice\program\soffice.exe",
                    @"C:\Program Files (x86)\LibreOffice\program\soffice.exe"
                };
                string soffice = null;
                for (int i = 0; i < candidates.Length; i++)
                {
                    if (File.Exists(candidates[i])) { soffice = candidates[i]; break; }
                }
                if (soffice == null) return null;

                string tempDir = Path.Combine(Path.GetTempPath(), "printaway_conv_" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(tempDir);
                try
                {
                    string inPath = Path.Combine(tempDir, "input" + ext);
                    string outPdf = Path.Combine(tempDir, "input.pdf");
                    File.WriteAllBytes(inPath, docBytes);

                    var psi = new ProcessStartInfo
                    {
                        FileName = soffice,
                        Arguments = string.Format(CultureInfo.InvariantCulture, "--headless --norestore --convert-to pdf --outdir \"{0}\" \"{1}\"", tempDir, inPath),
                        CreateNoWindow = true,
                        UseShellExecute = false
                    };
                    using (var proc = Process.Start(psi))
                    {
                        if (proc != null && proc.WaitForExit(25000) && proc.ExitCode == 0 && File.Exists(outPdf))
                        {
                            return File.ReadAllBytes(outPdf);
                        }
                        try { if (proc != null && !proc.HasExited) proc.Kill(); } catch { }
                    }
                }
                finally
                {
                    try { Directory.Delete(tempDir, true); } catch { }
                }
            }
            catch { }
            return null;
        }

        private static byte[] TryConvertViaWordCom(string ext, byte[] docBytes)
        {
            Type wordType = null;
            try { wordType = Type.GetTypeFromProgID("Word.Application"); } catch { }
            if (wordType == null) return null;

            byte[] resultBytes = null;
            Exception threadEx = null;
            var t = new Thread(() =>
            {
                object wordApp = null;
                object docs = null;
                object doc = null;
                string tempDir = Path.Combine(Path.GetTempPath(), "printaway_word_" + Guid.NewGuid().ToString("N"));
                try
                {
                    Directory.CreateDirectory(tempDir);
                    string inPath = Path.Combine(tempDir, "input" + ext);
                    string outPdf = Path.Combine(tempDir, "output.pdf");
                    File.WriteAllBytes(inPath, docBytes);

                    wordApp = Activator.CreateInstance(wordType);
                    wordType.InvokeMember("Visible", BindingFlags.SetProperty, null, wordApp, new object[] { false });
                    try { wordType.InvokeMember("DisplayAlerts", BindingFlags.SetProperty, null, wordApp, new object[] { 0 }); } catch { }

                    docs = wordType.InvokeMember("Documents", BindingFlags.GetProperty, null, wordApp, null);
                    doc = docs.GetType().InvokeMember("Open", BindingFlags.InvokeMethod, null, docs, new object[] { inPath, false, true });
                    // 17 = wdExportFormatPDF
                    doc.GetType().InvokeMember("ExportAsFixedFormat", BindingFlags.InvokeMethod, null, doc, new object[] { outPdf, 17 });
                    doc.GetType().InvokeMember("Close", BindingFlags.InvokeMethod, null, doc, new object[] { false });
                    doc = null;

                    if (File.Exists(outPdf))
                    {
                        resultBytes = File.ReadAllBytes(outPdf);
                    }
                }
                catch (Exception ex)
                {
                    threadEx = ex;
                }
                finally
                {
                    try { if (doc != null) doc.GetType().InvokeMember("Close", BindingFlags.InvokeMethod, null, doc, new object[] { false }); } catch { }
                    try { if (wordApp != null) wordType.InvokeMember("Quit", BindingFlags.InvokeMethod, null, wordApp, new object[] { false }); } catch { }
                    try { Directory.Delete(tempDir, true); } catch { }
                }
            });
            t.SetApartmentState(ApartmentState.STA);
            t.IsBackground = true;
            t.Start();
            t.Join(25000);
            if (threadEx != null)
            {
                Trace.WriteLine("[PdfAndPrinterEngine] Word COM fallback error: " + threadEx.Message);
            }
            return resultBytes;
        }

        private static List<DocParagraphBlock> ExtractDocxBlocks(byte[] docxBytes)
        {
            var blocks = new List<DocParagraphBlock>();
            using (var ms = new MemoryStream(docxBytes))
            using (var archive = new ZipArchive(ms, ZipArchiveMode.Read))
            {
                ZipArchiveEntry docEntry = archive.GetEntry("word/document.xml");
                if (docEntry == null) return null;

                var xmlDoc = new XmlDocument();
                using (var entryStream = docEntry.Open())
                {
                    xmlDoc.Load(entryStream);
                }

                var nsmgr = new XmlNamespaceManager(xmlDoc.NameTable);
                nsmgr.AddNamespace("w", "http://schemas.openxmlformats.org/wordprocessingml/2006/main");

                XmlNode bodyNode = xmlDoc.SelectSingleNode("//w:body", nsmgr);
                if (bodyNode == null) return null;

                foreach (XmlNode child in bodyNode.ChildNodes)
                {
                    if (child.LocalName == "p")
                    {
                        blocks.Add(ParseDocxParagraph(child, nsmgr));
                    }
                    else if (child.LocalName == "tbl")
                    {
                        XmlNodeList rows = child.SelectNodes("./w:tr", nsmgr);
                        if (rows != null)
                        {
                            foreach (XmlNode row in rows)
                            {
                                var cellTexts = new List<string>();
                                XmlNodeList cells = row.SelectNodes("./w:tc", nsmgr);
                                if (cells != null)
                                {
                                    foreach (XmlNode cell in cells)
                                    {
                                        var sbCell = new StringBuilder();
                                        XmlNodeList cellParas = cell.SelectNodes(".//w:p", nsmgr);
                                        if (cellParas != null)
                                        {
                                            foreach (XmlNode cp in cellParas)
                                            {
                                                var parsed = ParseDocxParagraph(cp, nsmgr);
                                                if (!string.IsNullOrWhiteSpace(parsed.Text))
                                                {
                                                    if (sbCell.Length > 0) sbCell.Append(" ");
                                                    sbCell.Append(parsed.Text.Trim());
                                                }
                                            }
                                        }
                                        cellTexts.Add(sbCell.ToString());
                                    }
                                }
                                blocks.Add(new DocParagraphBlock
                                {
                                    Text = string.Join("  |  ", cellTexts.ToArray())
                                });
                            }
                            blocks.Add(new DocParagraphBlock { Text = "" });
                        }
                    }
                }
            }
            if (blocks.Count == 0)
            {
                blocks.Add(new DocParagraphBlock { Text = "(Empty Word Document)" });
            }
            return blocks;
        }

        private static DocParagraphBlock ParseDocxParagraph(XmlNode pNode, XmlNamespaceManager nsmgr)
        {
            var block = new DocParagraphBlock();
            XmlNode pStyle = pNode.SelectSingleNode("./w:pPr/w:pStyle/@w:val", nsmgr);
            if (pStyle != null && !string.IsNullOrEmpty(pStyle.Value))
            {
                string st = pStyle.Value;
                if (st.IndexOf("Title", StringComparison.OrdinalIgnoreCase) >= 0 || st.IndexOf("Heading1", StringComparison.OrdinalIgnoreCase) >= 0)
                    block.IsHeading = true;
                else if (st.IndexOf("Heading", StringComparison.OrdinalIgnoreCase) >= 0 || st.IndexOf("Subtitle", StringComparison.OrdinalIgnoreCase) >= 0)
                    block.IsSubHeading = true;
            }

            bool allBold = false;
            int nonEmptyRuns = 0;
            int boldRuns = 0;
            var sb = new StringBuilder();

            XmlNodeList runs = pNode.SelectNodes("./w:r", nsmgr);
            if (runs != null)
            {
                foreach (XmlNode r in runs)
                {
                    bool rBold = r.SelectSingleNode("./w:rPr/w:b", nsmgr) != null;
                    var runSb = new StringBuilder();
                    foreach (XmlNode rChild in r.ChildNodes)
                    {
                        if (rChild.LocalName == "t" || rChild.LocalName == "delText")
                        {
                            runSb.Append(rChild.InnerText);
                        }
                        else if (rChild.LocalName == "tab")
                        {
                            runSb.Append("    ");
                        }
                        else if (rChild.LocalName == "br" || rChild.LocalName == "cr")
                        {
                            runSb.Append("\n");
                        }
                    }
                    string runTxt = runSb.ToString();
                    if (!string.IsNullOrWhiteSpace(runTxt))
                    {
                        nonEmptyRuns++;
                        if (rBold) boldRuns++;
                    }
                    sb.Append(runTxt);
                }
            }
            if (nonEmptyRuns > 0 && boldRuns == nonEmptyRuns) allBold = true;

            // Prefix bullet if paragraph has numbering property
            if (pNode.SelectSingleNode("./w:pPr/w:numPr", nsmgr) != null && sb.Length > 0)
            {
                block.Text = "• " + sb.ToString();
            }
            else
            {
                block.Text = sb.ToString();
            }
            block.IsBold = allBold;
            return block;
        }

        private static List<DocParagraphBlock> ExtractOdtBlocks(byte[] odtBytes)
        {
            var blocks = new List<DocParagraphBlock>();
            using (var ms = new MemoryStream(odtBytes))
            using (var archive = new ZipArchive(ms, ZipArchiveMode.Read))
            {
                ZipArchiveEntry contentEntry = archive.GetEntry("content.xml");
                if (contentEntry == null) return null;

                var xmlDoc = new XmlDocument();
                using (var s = contentEntry.Open())
                {
                    xmlDoc.Load(s);
                }
                XmlNodeList nodes = xmlDoc.SelectNodes("//*[local-name()='h' or local-name()='p']");
                if (nodes != null)
                {
                    foreach (XmlNode n in nodes)
                    {
                        blocks.Add(new DocParagraphBlock
                        {
                            Text = n.InnerText ?? "",
                            IsHeading = (n.LocalName == "h")
                        });
                    }
                }
            }
            return blocks.Count > 0 ? blocks : null;
        }

        private static string ExtractRtfPlainText(byte[] rtfBytes)
        {
            string rtf = Encoding.ASCII.GetString(rtfBytes);
            string extracted = null;
            try
            {
                var t = new Thread(() =>
                {
                    try
                    {
                        using (var rtb = new System.Windows.Forms.RichTextBox())
                        {
                            rtb.Rtf = rtf;
                            extracted = rtb.Text;
                        }
                    }
                    catch { }
                });
                t.SetApartmentState(ApartmentState.STA);
                t.IsBackground = true;
                t.Start();
                t.Join(4000);
            }
            catch { }

            if (!string.IsNullOrEmpty(extracted)) return extracted;

            // Fallback regex RTF stripper
            string stripped = Regex.Replace(rtf, @"\{\\\*[^}]+\}", "");
            stripped = Regex.Replace(stripped, @"\\par\b\s?", "\n");
            stripped = Regex.Replace(stripped, @"\\[a-zA-Z]+-?\d*\s?", "");
            stripped = stripped.Replace("{", "").Replace("}", "");
            return stripped.Trim();
        }

        private static string ExtractHtmlPlainText(string html)
        {
            if (string.IsNullOrEmpty(html)) return "";
            string s = Regex.Replace(html, @"<(script|style)[^>]*>[\s\S]*?</\1>", "", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"<(br|p|div|li|tr|h[1-6])[^>]*>", "\n", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"<[^>]+>", " ");
            s = WebUtility.HtmlDecode(s);
            s = Regex.Replace(s, @"[ \t]+", " ");
            return s.Trim();
        }

        private static byte[] RenderDocBlocksToMultiPageA4Pdf(List<DocParagraphBlock> blocks, bool monospaceMode)
        {
            const int dpi = 240; // Ultra-crisp 240 DPI (~1984 x 2806 px) balances razor sharpness & fast network upload
            int pageW = (int)Math.Round(8.27 * dpi);
            int pageH = (int)Math.Round(11.69 * dpi);
            int marginX = (int)Math.Round(0.55 * dpi);
            int marginTop = (int)Math.Round(0.55 * dpi);
            int marginBottom = (int)Math.Round(0.55 * dpi);
            int printableW = pageW - (marginX * 2);

            string family = monospaceMode ? "Consolas" : "Segoe UI";
            var pages = new List<A4RenderedPage>();

            using (var fontTitle = new Font(family, 15.0f, FontStyle.Bold, GraphicsUnit.Point))
            using (var fontSub = new Font(family, 12.5f, FontStyle.Bold, GraphicsUnit.Point))
            using (var fontBold = new Font(family, 10.5f, FontStyle.Bold, GraphicsUnit.Point))
            using (var fontBody = new Font(family, monospaceMode ? 9.5f : 10.5f, FontStyle.Regular, GraphicsUnit.Point))
            using (var fontFooter = new Font("Segoe UI", 8.0f, FontStyle.Regular, GraphicsUnit.Point))
            using (var brushBlack = new SolidBrush(Color.FromArgb(15, 23, 42)))
            using (var brushGray = new SolidBrush(Color.FromArgb(100, 116, 139)))
            {
                var preppedLines = new List<KeyValuePair<string, Font>>();

                using (var measureBmp = new Bitmap(16, 16))
                {
                    measureBmp.SetResolution(dpi, dpi);
                    using (var measureG = Graphics.FromImage(measureBmp))
                    {
                        measureG.PageUnit = GraphicsUnit.Pixel;

                        for (int i = 0; i < blocks.Count; i++)
                        {
                            DocParagraphBlock blk = blocks[i];
                            Font f = blk.IsHeading ? fontTitle : (blk.IsSubHeading ? fontSub : (blk.IsBold ? fontBold : fontBody));
                            string raw = (blk.Text ?? "").Replace("\r\n", "\n").Replace("\r", "\n");
                            string[] subParas = raw.Split('\n');
                            for (int s = 0; s < subParas.Length; s++)
                            {
                                List<string> wrapped = WrapTextToPixelWidth(measureG, subParas[s], f, printableW);
                                for (int w = 0; w < wrapped.Count; w++)
                                {
                                    preppedLines.Add(new KeyValuePair<string, Font>(wrapped[w], f));
                                }
                            }
                            if (!monospaceMode && !string.IsNullOrEmpty(raw))
                            {
                                // Subtle visual breathing room after paragraphs
                                preppedLines.Add(new KeyValuePair<string, Font>("", fontBody));
                            }
                        }
                    }
                }

                if (preppedLines.Count == 0)
                {
                    preppedLines.Add(new KeyValuePair<string, Font>(" ", fontBody));
                }

                // Paginate lines across A4 pages
                var pageChunks = new List<List<KeyValuePair<string, Font>>>();
                var curChunk = new List<KeyValuePair<string, Font>>();
                float curY = marginTop;
                float maxY = pageH - marginBottom - 40;

                using (var measureBmp2 = new Bitmap(16, 16))
                {
                    measureBmp2.SetResolution(dpi, dpi);
                    using (var g2 = Graphics.FromImage(measureBmp2))
                    {
                        for (int i = 0; i < preppedLines.Count; i++)
                        {
                            var item = preppedLines[i];
                            float lh = string.IsNullOrEmpty(item.Key)
                                ? (g2.MeasureString("Ag", item.Value).Height * 0.45f)
                                : (g2.MeasureString("Ag", item.Value).Height * 1.18f);

                            if (curY + lh > maxY && curChunk.Count > 0)
                            {
                                pageChunks.Add(curChunk);
                                curChunk = new List<KeyValuePair<string, Font>>();
                                curY = marginTop;
                            }
                            curChunk.Add(item);
                            curY += lh;
                            if (pageChunks.Count >= 99) break;
                        }
                        if (curChunk.Count > 0) pageChunks.Add(curChunk);
                    }
                }

                ImageCodecInfo jpgEncoder = GetEncoder(ImageFormat.Jpeg);
                for (int p = 0; p < pageChunks.Count; p++)
                {
                    using (var bmp = new Bitmap(pageW, pageH))
                    {
                        bmp.SetResolution(dpi, dpi);
                        using (var g = Graphics.FromImage(bmp))
                        {
                            g.Clear(Color.White);
                            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
                            float y = marginTop;
                            var chunk = pageChunks[p];
                            for (int l = 0; l < chunk.Count; l++)
                            {
                                string txt = chunk[l].Key;
                                Font f = chunk[l].Value;
                                float baseH = g.MeasureString("Ag", f).Height;
                                if (string.IsNullOrEmpty(txt))
                                {
                                    y += baseH * 0.45f;
                                }
                                else
                                {
                                    g.DrawString(txt, f, brushBlack, marginX, y);
                                    y += baseH * 1.18f;
                                }
                            }

                            if (pageChunks.Count > 1)
                            {
                                string footer = string.Format(CultureInfo.InvariantCulture, "Page {0} of {1}", p + 1, pageChunks.Count);
                                SizeF fSz = g.MeasureString(footer, fontFooter);
                                g.DrawString(footer, fontFooter, brushGray, (pageW - fSz.Width) / 2.0f, pageH - marginBottom + 8);
                            }
                        }

                        using (var msJpeg = new MemoryStream())
                        {
                            if (jpgEncoder != null)
                            {
                                using (var encParams = new EncoderParameters(1))
                                {
                                    encParams.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, 90L);
                                    bmp.Save(msJpeg, jpgEncoder, encParams);
                                }
                            }
                            else
                            {
                                bmp.Save(msJpeg, ImageFormat.Jpeg);
                            }

                            pages.Add(new A4RenderedPage
                            {
                                JpegBytes = msJpeg.ToArray(),
                                PixelWidth = pageW,
                                PixelHeight = pageH,
                                PageWidthPt = 595.28,
                                PageHeightPt = 841.89,
                                DrawWidthPt = 595.28,
                                DrawHeightPt = 841.89,
                                PosXPt = 0,
                                PosYPt = 0
                            });
                        }
                    }
                }
            }

            return GenerateMultiImageA4Pdf(pages);
        }

        private static List<string> WrapTextToPixelWidth(Graphics g, string text, Font font, int maxWidthPx)
        {
            var result = new List<string>();
            if (string.IsNullOrEmpty(text))
            {
                result.Add("");
                return result;
            }

            string[] words = text.Split(' ');
            var currentLine = new StringBuilder();

            for (int i = 0; i < words.Length; i++)
            {
                string word = words[i];
                string candidate = currentLine.Length == 0 ? word : (currentLine.ToString() + " " + word);
                if (g.MeasureString(candidate, font).Width <= maxWidthPx)
                {
                    if (currentLine.Length > 0) currentLine.Append(' ');
                    currentLine.Append(word);
                }
                else
                {
                    if (currentLine.Length > 0)
                    {
                        result.Add(currentLine.ToString());
                        currentLine.Clear();
                    }
                    // Break ultra-long continuous tokens (URLs/hashes) across characters
                    if (g.MeasureString(word, font).Width > maxWidthPx)
                    {
                        var charBuf = new StringBuilder();
                        for (int c = 0; c < word.Length; c++)
                        {
                            charBuf.Append(word[c]);
                            if (g.MeasureString(charBuf.ToString(), font).Width > maxWidthPx && charBuf.Length > 1)
                            {
                                char last = charBuf[charBuf.Length - 1];
                                charBuf.Length -= 1;
                                result.Add(charBuf.ToString());
                                charBuf.Clear();
                                charBuf.Append(last);
                            }
                        }
                        if (charBuf.Length > 0) currentLine.Append(charBuf.ToString());
                    }
                    else
                    {
                        currentLine.Append(word);
                    }
                }
            }
            if (currentLine.Length > 0 || result.Count == 0)
            {
                result.Add(currentLine.ToString());
            }
            return result;
        }

        private static byte[] GenerateSingleImageA4Pdf(byte[] jpegBytes, int imgW, int imgH, double pageW, double pageH, double drawW, double drawH, double posX, double posY)
        {
            var list = new List<A4RenderedPage>
            {
                new A4RenderedPage
                {
                    JpegBytes = jpegBytes,
                    PixelWidth = imgW,
                    PixelHeight = imgH,
                    PageWidthPt = pageW,
                    PageHeightPt = pageH,
                    DrawWidthPt = drawW,
                    DrawHeightPt = drawH,
                    PosXPt = posX,
                    PosYPt = posY
                }
            };
            return GenerateMultiImageA4Pdf(list);
        }

        private static byte[] GenerateMultiImageA4Pdf(List<A4RenderedPage> pages)
        {
            if (pages == null || pages.Count == 0) return null;

            using (var ms = new MemoryStream())
            {
                var offsets = new List<long>();
                var ascii = Encoding.ASCII;

                Action<string> writeAscii = (s) =>
                {
                    byte[] b = ascii.GetBytes(s);
                    ms.Write(b, 0, b.Length);
                };

                writeAscii("%PDF-1.4\n%\xE2\xE3\xCF\xD3\n");

                // 1: Catalog
                offsets.Add(ms.Position);
                writeAscii("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");

                // 2: Pages
                var kids = new StringBuilder();
                for (int i = 0; i < pages.Count; i++)
                {
                    int pageObjId = 3 + (i * 3);
                    if (i > 0) kids.Append(' ');
                    kids.Append(pageObjId.ToString(CultureInfo.InvariantCulture)).Append(" 0 R");
                }
                offsets.Add(ms.Position);
                writeAscii(string.Format(CultureInfo.InvariantCulture, "2 0 obj\n<< /Type /Pages /Kids [{0}] /Count {1} >>\nendobj\n", kids.ToString(), pages.Count));

                // 3..N: Page, Content, Image XObject per page
                for (int i = 0; i < pages.Count; i++)
                {
                    A4RenderedPage pg = pages[i];
                    int pageObjId = 3 + (i * 3);
                    int contentObjId = pageObjId + 1;
                    int imageObjId = pageObjId + 2;

                    // Page object
                    offsets.Add(ms.Position);
                    string mediaBox = string.Format(CultureInfo.InvariantCulture, "[0 0 {0:0.##} {1:0.##}]", pg.PageWidthPt, pg.PageHeightPt);
                    writeAscii(string.Format(CultureInfo.InvariantCulture,
                        "{0} 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox {1} /Contents {2} 0 R /Resources << /XObject << /Im1 {3} 0 R >> /ProcSet [/PDF /ImageC] >> >>\nendobj\n",
                        pageObjId, mediaBox, contentObjId, imageObjId));

                    // Content stream object
                    offsets.Add(ms.Position);
                    string streamContent = string.Format(CultureInfo.InvariantCulture,
                        "q\n{0:0.##} 0 0 {1:0.##} {2:0.##} {3:0.##} cm\n/Im1 Do\nQ\n",
                        pg.DrawWidthPt, pg.DrawHeightPt, pg.PosXPt, pg.PosYPt);
                    byte[] contentBytes = ascii.GetBytes(streamContent);
                    writeAscii(string.Format(CultureInfo.InvariantCulture, "{0} 0 obj\n<< /Length {1} >>\nstream\n", contentObjId, contentBytes.Length));
                    ms.Write(contentBytes, 0, contentBytes.Length);
                    writeAscii("endstream\nendobj\n");

                    // Image XObject
                    offsets.Add(ms.Position);
                    writeAscii(string.Format(CultureInfo.InvariantCulture,
                        "{0} 0 obj\n<< /Type /XObject /Subtype /Image /Width {1} /Height {2} /ColorSpace /DeviceRGB /BitsPerComponent 8 /Filter /DCTDecode /Length {3} >>\nstream\n",
                        imageObjId, pg.PixelWidth, pg.PixelHeight, pg.JpegBytes.Length));
                    ms.Write(pg.JpegBytes, 0, pg.JpegBytes.Length);
                    writeAscii("\nendstream\nendobj\n");
                }

                // xref
                long startXref = ms.Position;
                writeAscii(string.Format(CultureInfo.InvariantCulture, "xref\n0 {0}\n0000000000 65535 f \n", offsets.Count + 1));
                for (int i = 0; i < offsets.Count; i++)
                {
                    writeAscii(string.Format(CultureInfo.InvariantCulture, "{0:D10} 00000 n \n", offsets[i]));
                }

                // trailer
                writeAscii(string.Format(CultureInfo.InvariantCulture,
                    "trailer\n<< /Size {0} /Root 1 0 R >>\nstartxref\n{1}\n%%EOF\n",
                    offsets.Count + 1, startXref));

                return ms.ToArray();
            }
        }

        public static PdfValidationResult ValidatePdf(byte[] fileBytes, long maxFileSizeBytes = 20L * 1024L * 1024L, int maxPageCount = 100)
        {
            var result = new PdfValidationResult();
            if (fileBytes == null || fileBytes.Length == 0)
            {
                result.IsValid = false;
                result.ErrorCode = "EMPTY_FILE";
                result.ErrorMessage = "Uploaded file is empty.";
                return result;
            }

            result.FileSizeBytes = fileBytes.Length;
            if (fileBytes.Length > maxFileSizeBytes)
            {
                result.IsValid = false;
                result.ErrorCode = "FILE_TOO_LARGE";
                result.ErrorMessage = string.Format(CultureInfo.InvariantCulture, "PDF exceeds maximum allowed size of {0:0.0} MB.", maxFileSizeBytes / (1024.0 * 1024.0));
                return result;
            }

            if (fileBytes.Length < 16)
            {
                result.IsValid = false;
                result.ErrorCode = "INVALID_PDF_HEADER";
                result.ErrorMessage = "File is too small to be a valid PDF document.";
                return result;
            }

            string header = Encoding.ASCII.GetString(fileBytes, 0, Math.Min(1024, fileBytes.Length));
            int pdfHeaderIdx = header.IndexOf("%PDF-", StringComparison.Ordinal);
            if (pdfHeaderIdx < 0 || pdfHeaderIdx > 20)
            {
                result.IsValid = false;
                result.ErrorCode = "INVALID_PDF_SIGNATURE";
                result.ErrorMessage = "Rejected: File does not start with a valid %PDF- signature. Only genuine PDF documents are accepted.";
                return result;
            }

            using (var sha = SHA256.Create())
            {
                byte[] hash = sha.ComputeHash(fileBytes);
                var sb = new StringBuilder(hash.Length * 2);
                foreach (byte b in hash) sb.Append(b.ToString("x2", CultureInfo.InvariantCulture));
                result.Sha256Hex = sb.ToString();
            }

            // Use Latin1/ISO-8859-1 to inspect PDF structural tokens without corrupting binary offsets
            string rawContent = Encoding.GetEncoding("ISO-8859-1").GetString(fileBytes);

            int tailOffset = Math.Max(0, rawContent.Length - 2048);
            string tail = rawContent.Substring(tailOffset);
            if (tail.IndexOf("%%EOF", StringComparison.Ordinal) < 0)
            {
                result.IsValid = false;
                result.ErrorCode = "CORRUPTED_PDF_EOF";
                result.ErrorMessage = "Rejected: PDF file is truncated or missing its %%EOF trailer.";
                return result;
            }

            if (EncryptRegex.IsMatch(rawContent))
            {
                result.IsValid = false;
                result.IsEncrypted = true;
                result.ErrorCode = "ENCRYPTED_PDF_UNSUPPORTED";
                result.ErrorMessage = "Rejected: Password-protected or encrypted PDFs cannot be printed. Please upload an unencrypted PDF.";
                return result;
            }

            if (JsRegex.IsMatch(rawContent))
            {
                result.IsValid = false;
                result.HasEmbeddedJavaScript = true;
                result.ErrorCode = "PDF_ACTIVE_CONTENT_REJECTED";
                result.ErrorMessage = "Rejected: PDF contains embedded JavaScript actions which are blocked by security policy.";
                return result;
            }

            // Count pages via /Type /Page leaf nodes, fallback to /Type /Pages /Count
            int leafPages = PageObjRegex.Matches(rawContent).Count;
            int maxReportedPages = 0;
            foreach (Match m in PagesCountRegex.Matches(rawContent))
            {
                int val;
                if (int.TryParse(m.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out val))
                {
                    if (val > maxReportedPages) maxReportedPages = val;
                }
            }

            int detectedPages = leafPages > 0 ? leafPages : maxReportedPages;
            if (detectedPages <= 0)
            {
                result.IsValid = false;
                result.ErrorCode = "ZERO_PAGES_DETECTED";
                result.ErrorMessage = "Rejected: Could not detect any valid printable pages in this PDF.";
                return result;
            }

            if (detectedPages > maxPageCount)
            {
                result.IsValid = false;
                result.PageCount = detectedPages;
                result.ErrorCode = "PAGE_LIMIT_EXCEEDED";
                result.ErrorMessage = string.Format(CultureInfo.InvariantCulture, "PDF has {0} pages, exceeding the pilot maximum of {1} pages per order.", detectedPages, maxPageCount);
                return result;
            }

            result.IsValid = true;
            result.PageCount = detectedPages;
            result.ErrorCode = "OK";
            result.ErrorMessage = "Valid PDF document.";
            return result;
        }

        public static List<int> ParseAndValidatePageRange(string pageRangeSpec, int totalPages, out string normalizedSpec, out string error)
        {
            error = null;
            normalizedSpec = "all";
            var pages = new List<int>();

            if (totalPages <= 0)
            {
                error = "Document has no pages.";
                return pages;
            }

            string rawTrim = string.IsNullOrWhiteSpace(pageRangeSpec) ? "all" : pageRangeSpec.Trim();
            if (string.Equals(rawTrim, "all", StringComparison.OrdinalIgnoreCase))
            {
                for (int i = 1; i <= totalPages; i++) pages.Add(i);
                normalizedSpec = "1-" + totalPages.ToString(CultureInfo.InvariantCulture);
                return pages;
            }

            if (string.Equals(rawTrim, "odd", StringComparison.OrdinalIgnoreCase))
            {
                for (int i = 1; i <= totalPages; i += 2) pages.Add(i);
                normalizedSpec = "odd (" + string.Join(",", pages) + ")";
                return pages;
            }

            if (string.Equals(rawTrim, "even", StringComparison.OrdinalIgnoreCase))
            {
                for (int i = 2; i <= totalPages; i += 2) pages.Add(i);
                if (pages.Count == 0)
                {
                    error = "Document has only 1 page; there are no even pages to print.";
                    return pages;
                }
                normalizedSpec = "even (" + string.Join(",", pages) + ")";
                return pages;
            }

            if (rawTrim.StartsWith("first:", StringComparison.OrdinalIgnoreCase))
            {
                int firstN;
                string numStr = rawTrim.Substring("first:".Length).Trim();
                if (!int.TryParse(numStr, NumberStyles.Integer, CultureInfo.InvariantCulture, out firstN) || firstN < 1 || firstN > totalPages)
                {
                    error = string.Format(CultureInfo.InvariantCulture, "Invalid 'First N Pages' count '{0}'. Must be between 1 and {1}.", numStr, totalPages);
                    return pages;
                }
                for (int i = 1; i <= firstN; i++) pages.Add(i);
                normalizedSpec = firstN == 1 ? "1" : ("1-" + firstN.ToString(CultureInfo.InvariantCulture));
                return pages;
            }

            var seen = new HashSet<int>();
            string[] parts = rawTrim.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0)
            {
                error = "Invalid page range format.";
                return pages;
            }

            foreach (string rawPart in parts)
            {
                string part = rawPart.Trim();
                if (part.IndexOf('-') >= 0)
                {
                    string[] bounds = part.Split('-');
                    int start, end;
                    if (bounds.Length != 2 ||
                        !int.TryParse(bounds[0].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out start) ||
                        !int.TryParse(bounds[1].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out end))
                    {
                        error = "Invalid page range segment '" + part + "'. Use format like '1-5' or '1-3,5'.";
                        return new List<int>();
                    }
                    if (start < 1 || end < start || end > totalPages)
                    {
                        error = string.Format(CultureInfo.InvariantCulture, "Page range '{0}' is outside document bounds (1..{1}).", part, totalPages);
                        return new List<int>();
                    }
                    for (int p = start; p <= end; p++)
                    {
                        if (seen.Add(p)) pages.Add(p);
                    }
                }
                else
                {
                    int p;
                    if (!int.TryParse(part, NumberStyles.Integer, CultureInfo.InvariantCulture, out p) || p < 1 || p > totalPages)
                    {
                        error = string.Format(CultureInfo.InvariantCulture, "Page number '{0}' is outside document bounds (1..{1}).", part, totalPages);
                        return new List<int>();
                    }
                    if (seen.Add(p)) pages.Add(p);
                }
            }

            pages.Sort();
            normalizedSpec = rawTrim;
            return pages;
        }

        public static QuoteCalculationResult CalculateQuote(
            int documentPages,
            string pageRangeSpec,
            int copies,
            string duplexMode,
            double simplexRatePerSideInr,
            double duplexRatePerSideInr,
            double minOrderChargeInr,
            double taxPercent,
            out string validationError,
            string colorMode = "bw",
            double colorSimplexRatePerSideInr = 10.0,
            double colorDuplexRatePerSideInr = 8.0,
            string paperSize = "A4",
            string orientation = "auto",
            int pagesPerSheet = 1,
            bool collate = true,
            int maxCopies = 50)
        {
            validationError = null;
            if (copies < 1 || copies > maxCopies)
            {
                validationError = string.Format(CultureInfo.InvariantCulture, "Copies must be between 1 and {0}.", maxCopies);
                return null;
            }

            if (pagesPerSheet != 1 && pagesPerSheet != 2 && pagesPerSheet != 4)
            {
                validationError = "Pages per sheet (N-Up) must be 1, 2, or 4.";
                return null;
            }

            string normalizedRange;
            string rangeErr;
            List<int> resolvedPages = ParseAndValidatePageRange(pageRangeSpec, documentPages, out normalizedRange, out rangeErr);
            if (rangeErr != null || resolvedPages.Count == 0)
            {
                validationError = rangeErr ?? "No pages selected.";
                return null;
            }

            bool isDuplex = string.Equals(duplexMode, "TwoSidedLongEdge", StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(duplexMode, "TwoSidedShortEdge", StringComparison.OrdinalIgnoreCase);
            string canonicalDuplex = isDuplex
                ? (string.Equals(duplexMode, "TwoSidedShortEdge", StringComparison.OrdinalIgnoreCase) ? "TwoSidedShortEdge" : "TwoSidedLongEdge")
                : "OneSided";

            bool isColor = string.Equals(colorMode, "color", StringComparison.OrdinalIgnoreCase) ||
                           string.Equals(colorMode, "colour", StringComparison.OrdinalIgnoreCase);
            string canonicalColor = isColor ? "color" : "bw";

            string canonicalPaper = string.IsNullOrWhiteSpace(paperSize) ? "A4" : paperSize.Trim();
            string canonicalOrientation = "auto";
            if (string.Equals(orientation, "portrait", StringComparison.OrdinalIgnoreCase)) canonicalOrientation = "portrait";
            else if (string.Equals(orientation, "landscape", StringComparison.OrdinalIgnoreCase)) canonicalOrientation = "landscape";

            int selectedPages = resolvedPages.Count;
            // N-Up support: 1, 2, or 4 document pages tiled per printed side
            int printedSidesPerCopy = (int)Math.Ceiling(selectedPages / (double)pagesPerSheet);
            int totalPrintedSides = printedSidesPerCopy * copies;

            // Per PRD §10: Each copy starts on a fresh physical sheet!
            // Example: 5-page PDF x 2 copies simplex = 10 sides, 10 sheets; duplex = 10 sides, 6 sheets (3 sheets per copy * 2).
            int sheetsPerCopy = isDuplex ? (int)Math.Ceiling(printedSidesPerCopy / 2.0) : printedSidesPerCopy;
            int totalPhysicalSheets = sheetsPerCopy * copies;

            double ratePerSide;
            if (isColor)
            {
                ratePerSide = isDuplex ? colorDuplexRatePerSideInr : colorSimplexRatePerSideInr;
            }
            else
            {
                ratePerSide = isDuplex ? duplexRatePerSideInr : simplexRatePerSideInr;
            }

            double baseCharge = Math.Round(totalPrintedSides * ratePerSide, 2, MidpointRounding.AwayFromZero);
            double subtotalAfterMin = Math.Max(baseCharge, minOrderChargeInr);
            double minDelta = Math.Round(subtotalAfterMin - baseCharge, 2, MidpointRounding.AwayFromZero);
            double taxAmount = Math.Round(subtotalAfterMin * (taxPercent / 100.0), 2, MidpointRounding.AwayFromZero);
            double totalInr = Math.Round(subtotalAfterMin + taxAmount, 2, MidpointRounding.AwayFromZero);
            long totalPaise = (long)Math.Round(totalInr * 100.0, 0, MidpointRounding.AwayFromZero);

            return new QuoteCalculationResult
            {
                DocumentPages = documentPages,
                SelectedPageCount = selectedPages,
                NormalizedPageRange = normalizedRange,
                ResolvedPages = resolvedPages,
                Copies = copies,
                DuplexMode = canonicalDuplex,
                ColorMode = canonicalColor,
                PaperSize = canonicalPaper,
                Orientation = canonicalOrientation,
                PagesPerSheet = pagesPerSheet,
                Collate = collate,
                PrintedSidesPerCopy = printedSidesPerCopy,
                TotalPrintedSides = totalPrintedSides,
                SheetsPerCopy = sheetsPerCopy,
                TotalPhysicalSheets = totalPhysicalSheets,
                RatePerSideInr = ratePerSide,
                BaseChargeInr = baseCharge,
                MinimumChargeAppliedInr = minDelta,
                TaxAmountInr = taxAmount,
                TotalAmountInr = totalInr,
                TotalAmountPaise = totalPaise
            };
        }

        public static List<DiscoveredPrinterQueue> DiscoverWindowsQueues()
        {
            var list = new List<DiscoveredPrinterQueue>();
            try
            {
                using (var searcher = new ManagementObjectSearcher("SELECT Name, DriverName, PortName, Default, WorkOffline, PrinterStatus, ExtendedPrinterStatus, Status FROM Win32_Printer"))
                using (var results = searcher.Get())
                {
                    foreach (ManagementObject mo in results)
                    {
                        string name = Convert.ToString(mo["Name"] ?? "");
                        string driver = Convert.ToString(mo["DriverName"] ?? "");
                        string port = Convert.ToString(mo["PortName"] ?? "");
                        bool isDefault = mo["Default"] != null && Convert.ToBoolean(mo["Default"]);
                        bool workOffline = mo["WorkOffline"] != null && Convert.ToBoolean(mo["WorkOffline"]);
                        int printerStatus = mo["PrinterStatus"] != null ? Convert.ToInt32(mo["PrinterStatus"]) : 3;
                        int extStatus = mo["ExtendedPrinterStatus"] != null ? Convert.ToInt32(mo["ExtendedPrinterStatus"]) : 2;
                        string statusStr = Convert.ToString(mo["Status"] ?? "Unknown");

                        bool isVirtual = VirtualPrinterRegex.IsMatch(name) || VirtualPortRegex.IsMatch(port);
                        bool isXerox = name.IndexOf("Xerox", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                       name.IndexOf("B1025", StringComparison.OrdinalIgnoreCase) >= 0;

                        bool supportsDuplex = false;
                        string defaultPaper = "Unknown";
                        try
                        {
                            var ps = new PrinterSettings { PrinterName = name };
                            if (ps.IsValid)
                            {
                                supportsDuplex = ps.CanDuplex;
                                if (ps.DefaultPageSettings != null && ps.DefaultPageSettings.PaperSize != null)
                                {
                                    defaultPaper = ps.DefaultPageSettings.PaperSize.PaperName;
                                }
                            }
                        }
                        catch { }

                        int activeJobs = CountActiveSpoolerJobs(name);

                        // PrinterStatus 3 = Idle, 4 = Printing, 5 = Warmup
                        bool statusHealthy = !workOffline && (printerStatus == 3 || printerStatus == 4 || printerStatus == 5);
                        string healthReason = statusHealthy
                            ? "Ready (Online, Spooler Normal)"
                            : (workOffline ? "Queue is marked WorkOffline in Windows" : "Printer reported status code " + printerStatus + " (" + statusStr + ")");

                        list.Add(new DiscoveredPrinterQueue
                        {
                            QueueName = name,
                            DriverName = driver,
                            PortName = port,
                            IsDefault = isDefault,
                            WorkOffline = workOffline,
                            StatusText = statusStr,
                            PrinterStatusCode = printerStatus,
                            ExtendedStatus = extStatus,
                            ActiveJobCount = activeJobs,
                            IsVirtualExcluded = isVirtual,
                            IsXeroxCandidate = isXerox,
                            SupportsDuplex = supportsDuplex,
                            DefaultPaperSize = defaultPaper,
                            IsHealthyForDispatch = statusHealthy && !isVirtual,
                            HealthReason = healthReason
                        });
                    }
                }
            }
            catch (Exception ex)
            {
                list.Add(new DiscoveredPrinterQueue
                {
                    QueueName = "ERROR_QUERYING_WMI",
                    DriverName = ex.Message,
                    PortName = "N/A",
                    IsHealthyForDispatch = false,
                    HealthReason = "WMI query error: " + ex.Message
                });
            }
            return list;
        }

        public static int CountActiveSpoolerJobs(string queueName)
        {
            int count = 0;
            try
            {
                using (var searcher = new ManagementObjectSearcher("SELECT Name FROM Win32_PrintJob"))
                using (var results = searcher.Get())
                {
                    foreach (ManagementObject mo in results)
                    {
                        string jobName = Convert.ToString(mo["Name"] ?? "");
                        if (jobName.StartsWith(queueName + ",", StringComparison.OrdinalIgnoreCase))
                        {
                            count++;
                        }
                    }
                }
            }
            catch { }
            return count;
        }

        public static int? FindLatestSpoolerJobId(string queueName, string documentName)
        {
            try
            {
                int? matchedId = null;
                using (var searcher = new ManagementObjectSearcher("SELECT JobId, Name, Document FROM Win32_PrintJob"))
                using (var results = searcher.Get())
                {
                    foreach (ManagementObject mo in results)
                    {
                        string jobName = Convert.ToString(mo["Name"] ?? "");
                        string doc = Convert.ToString(mo["Document"] ?? "");
                        if (jobName.StartsWith(queueName + ",", StringComparison.OrdinalIgnoreCase))
                        {
                            int jid = Convert.ToInt32(mo["JobId"], CultureInfo.InvariantCulture);
                            if (string.IsNullOrEmpty(documentName) || doc.IndexOf(documentName, StringComparison.OrdinalIgnoreCase) >= 0)
                            {
                                matchedId = jid;
                            }
                            else if (!matchedId.HasValue)
                            {
                                matchedId = jid;
                            }
                        }
                    }
                }
                return matchedId;
            }
            catch
            {
                return null;
            }
        }

        public static PrintDispatchResult DispatchPrintJob(
            string targetQueueName,
            string orderCode,
            byte[] pdfBytes,
            List<int> pagesToPrint,
            int copies,
            string duplexMode,
            bool simulateSpoolerOnly,
            string colorMode = "bw",
            string paperSize = "A4",
            string orientation = "auto",
            int pagesPerSheet = 1,
            bool collate = true)
        {
            var result = new PrintDispatchResult
            {
                TargetQueueName = targetQueueName,
                SimulatedMode = simulateSpoolerOnly
            };

            if (VirtualPrinterRegex.IsMatch(targetQueueName))
            {
                result.Dispatched = false;
                result.ErrorMessage = "Refused: Target queue '" + targetQueueName + "' is a virtual printer.";
                return result;
            }

            if (pagesPerSheet != 1 && pagesPerSheet != 2 && pagesPerSheet != 4) pagesPerSheet = 1;
            bool isDuplex = string.Equals(duplexMode, "TwoSidedLongEdge", StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(duplexMode, "TwoSidedShortEdge", StringComparison.OrdinalIgnoreCase);
            bool isColor = string.Equals(colorMode, "color", StringComparison.OrdinalIgnoreCase) ||
                           string.Equals(colorMode, "colour", StringComparison.OrdinalIgnoreCase);

            int selectedCount = (pagesToPrint != null && pagesToPrint.Count > 0) ? pagesToPrint.Count : 1;
            int printedSidesPerCopy = (int)Math.Ceiling(selectedCount / (double)pagesPerSheet);
            result.PrintedSides = printedSidesPerCopy * copies;
            result.PhysicalSheets = (isDuplex ? (int)Math.Ceiling(printedSidesPerCopy / 2.0) : printedSidesPerCopy) * copies;

            string spoolDocName = "PrintAway_" + orderCode;
            result.SpoolerDocumentName = spoolDocName;

            var printDoc = new PrintDocument();
            printDoc.DocumentName = spoolDocName;
            printDoc.PrintController = new StandardPrintController();
            printDoc.PrinterSettings.PrinterName = targetQueueName;

            if (!printDoc.PrinterSettings.IsValid)
            {
                result.Dispatched = false;
                result.ErrorMessage = "Target Windows print queue '" + targetQueueName + "' is not installed or invalid.";
                return result;
            }

            // Enforce Paper Size, Color vs Monochrome, Copies, Collate, Orientation, and Duplex mode
            printDoc.PrinterSettings.Copies = (short)Math.Max(1, Math.Min(50, copies));
            printDoc.PrinterSettings.Collate = collate;
            if (string.Equals(duplexMode, "TwoSidedLongEdge", StringComparison.OrdinalIgnoreCase))
                printDoc.PrinterSettings.Duplex = Duplex.Vertical;
            else if (string.Equals(duplexMode, "TwoSidedShortEdge", StringComparison.OrdinalIgnoreCase))
                printDoc.PrinterSettings.Duplex = Duplex.Horizontal;
            else
                printDoc.PrinterSettings.Duplex = Duplex.Simplex;

            printDoc.DefaultPageSettings.Color = isColor;
            if (string.Equals(orientation, "landscape", StringComparison.OrdinalIgnoreCase))
                printDoc.DefaultPageSettings.Landscape = true;
            else if (string.Equals(orientation, "portrait", StringComparison.OrdinalIgnoreCase))
                printDoc.DefaultPageSettings.Landscape = false;

            string targetPaper = string.IsNullOrWhiteSpace(paperSize) ? "A4" : paperSize.Trim();
            foreach (PaperSize ps in printDoc.PrinterSettings.PaperSizes)
            {
                if (ps.PaperName.IndexOf(targetPaper, StringComparison.OrdinalIgnoreCase) >= 0 ||
                    (string.Equals(targetPaper, "A4", StringComparison.OrdinalIgnoreCase) && ps.Kind == PaperKind.A4) ||
                    (string.Equals(targetPaper, "Letter", StringComparison.OrdinalIgnoreCase) && ps.Kind == PaperKind.Letter) ||
                    (string.Equals(targetPaper, "Legal", StringComparison.OrdinalIgnoreCase) && ps.Kind == PaperKind.Legal) ||
                    (string.Equals(targetPaper, "A3", StringComparison.OrdinalIgnoreCase) && ps.Kind == PaperKind.A3))
                {
                    printDoc.DefaultPageSettings.PaperSize = ps;
                    break;
                }
            }

            if (simulateSpoolerOnly)
            {
                // Safe deterministic simulation mode for automated test suite or dry-run validation:
                int simulatedJobId = Math.Abs((orderCode + DateTime.UtcNow.Ticks.ToString()).GetHashCode() % 9000) + 1000;
                result.Dispatched = true;
                result.WindowsSpoolerJobId = simulatedJobId;
                return result;
            }

            // Real physical dispatch to verified Windows queue:
            // Renders actual PDF pages using Windows 11 native WinRT and draws them onto each printed side (1-up, 2-up, or 4-up)
            // with either full RGB color or true grayscale conversion for Black & White mode!
            string tempPdfPath = Path.Combine(Path.GetTempPath(), "printaway_" + Guid.NewGuid().ToString("N") + ".pdf");
            string tempRenderDir = Path.Combine(Path.GetTempPath(), "printaway_render_" + Guid.NewGuid().ToString("N"));
            try
            {
                File.WriteAllBytes(tempPdfPath, pdfBytes);
                RenderPdfPagesToPng(tempPdfPath, pagesToPrint, tempRenderDir);

                int pageIdx = 0;
                printDoc.PrintPage += (sender, e) =>
                {
                    e.Graphics.Clear(Color.White);
                    Rectangle bounds = e.PageBounds;

                    if (pagesPerSheet == 1)
                    {
                        int pageNum = pagesToPrint[pageIdx];
                        pageIdx++;
                        DrawRenderedPdfPageSlot(e.Graphics, tempRenderDir, pageNum, bounds, isColor, orderCode, targetQueueName, duplexMode, pagesToPrint.Count);
                    }
                    else if (pagesPerSheet == 2)
                    {
                        // 2-in-1: split page into 2 slots (top/bottom in portrait, left/right in landscape)
                        Rectangle slot1, slot2;
                        if (bounds.Width >= bounds.Height)
                        {
                            int halfW = bounds.Width / 2;
                            slot1 = new Rectangle(bounds.X, bounds.Y, halfW, bounds.Height);
                            slot2 = new Rectangle(bounds.X + halfW, bounds.Y, halfW, bounds.Height);
                        }
                        else
                        {
                            int halfH = bounds.Height / 2;
                            slot1 = new Rectangle(bounds.X, bounds.Y, bounds.Width, halfH);
                            slot2 = new Rectangle(bounds.X, bounds.Y + halfH, bounds.Width, halfH);
                        }

                        if (pageIdx < pagesToPrint.Count)
                        {
                            DrawRenderedPdfPageSlot(e.Graphics, tempRenderDir, pagesToPrint[pageIdx++], slot1, isColor, orderCode, targetQueueName, duplexMode, pagesToPrint.Count);
                        }
                        if (pageIdx < pagesToPrint.Count)
                        {
                            DrawRenderedPdfPageSlot(e.Graphics, tempRenderDir, pagesToPrint[pageIdx++], slot2, isColor, orderCode, targetQueueName, duplexMode, pagesToPrint.Count);
                        }
                    }
                    else
                    {
                        // 4-in-1: 2x2 grid
                        int halfW = bounds.Width / 2;
                        int halfH = bounds.Height / 2;
                        Rectangle[] slots = new[]
                        {
                            new Rectangle(bounds.X, bounds.Y, halfW, halfH),
                            new Rectangle(bounds.X + halfW, bounds.Y, halfW, halfH),
                            new Rectangle(bounds.X, bounds.Y + halfH, halfW, halfH),
                            new Rectangle(bounds.X + halfW, bounds.Y + halfH, halfW, halfH)
                        };
                        for (int s = 0; s < 4 && pageIdx < pagesToPrint.Count; s++)
                        {
                            DrawRenderedPdfPageSlot(e.Graphics, tempRenderDir, pagesToPrint[pageIdx++], slots[s], isColor, orderCode, targetQueueName, duplexMode, pagesToPrint.Count);
                        }
                    }

                    e.HasMorePages = (pageIdx < pagesToPrint.Count);
                };

                printDoc.Print();
                int? spoolerId = FindLatestSpoolerJobId(targetQueueName, spoolDocName);
                result.Dispatched = true;
                result.WindowsSpoolerJobId = spoolerId ?? 1;
                return result;
            }
            finally
            {
                try { if (File.Exists(tempPdfPath)) File.Delete(tempPdfPath); } catch { }
                try { if (Directory.Exists(tempRenderDir)) Directory.Delete(tempRenderDir, true); } catch { }
            }
        }

        private static void DrawRenderedPdfPageSlot(
            Graphics g,
            string tempRenderDir,
            int pageNum,
            Rectangle destSlot,
            bool isColor,
            string orderCode,
            string targetQueueName,
            string duplexMode,
            int totalSelected)
        {
            string pngPath = Path.Combine(tempRenderDir, "page_" + pageNum + ".png");
            if (File.Exists(pngPath))
            {
                using (var fs = new FileStream(pngPath, FileMode.Open, FileAccess.Read, FileShare.Read))
                using (var img = Image.FromStream(fs))
                {
                    // Fit proportionally inside destSlot
                    double scaleX = (double)destSlot.Width / img.Width;
                    double scaleY = (double)destSlot.Height / img.Height;
                    double scale = Math.Min(scaleX, scaleY);
                    int drawW = Math.Max(1, (int)Math.Round(img.Width * scale));
                    int drawH = Math.Max(1, (int)Math.Round(img.Height * scale));
                    int drawX = destSlot.X + (destSlot.Width - drawW) / 2;
                    int drawY = destSlot.Y + (destSlot.Height - drawH) / 2;
                    var fitRect = new Rectangle(drawX, drawY, drawW, drawH);

                    // High-quality rendering flags on printer context
                    g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                    g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.HighQuality;
                    g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;
                    g.CompositingQuality = System.Drawing.Drawing2D.CompositingQuality.HighQuality;

                    // Always composite onto an opaque white RGB canvas to eliminate alpha-channel/transparency
                    // which causes Windows V4 PCL6 print filter pipelines (such as Xerox B1025) to drop or fail jobs.
                    // Preserve FULL native pixel resolution (img.Width x img.Height) to prevent downsampling blur:
                    using (var opaqueBmp = new Bitmap(img.Width, img.Height, PixelFormat.Format32bppRgb))
                    {
                        using (var bg = Graphics.FromImage(opaqueBmp))
                        {
                            bg.Clear(Color.White);
                            bg.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                            bg.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.HighQuality;
                            bg.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;
                            bg.CompositingQuality = System.Drawing.Drawing2D.CompositingQuality.HighQuality;
                            bg.DrawImage(img, new Rectangle(0, 0, img.Width, img.Height));
                        }

                        if (isColor)
                        {
                            g.DrawImage(opaqueBmp, fitRect, new Rectangle(0, 0, img.Width, img.Height), GraphicsUnit.Pixel);
                        }
                        else
                        {
                            // Apply ITU-R BT.601 luminance grayscale matrix when Black & White is selected
                            using (var attrs = new ImageAttributes())
                            {
                                var grayMatrix = new ColorMatrix(new float[][]
                                {
                                    new float[] { 0.299f, 0.299f, 0.299f, 0, 0 },
                                    new float[] { 0.587f, 0.587f, 0.587f, 0, 0 },
                                    new float[] { 0.114f, 0.114f, 0.114f, 0, 0 },
                                    new float[] { 0,      0,      0,      1, 0 },
                                    new float[] { 0,      0,      0,      0, 1 }
                                });
                                attrs.SetColorMatrix(grayMatrix);
                                g.DrawImage(opaqueBmp, fitRect, 0, 0, img.Width, img.Height, GraphicsUnit.Pixel, attrs);
                            }
                        }
                    }
                }
            }
            else
            {
                using (var headerFont = new Font("Arial", 12, FontStyle.Bold))
                using (var bodyFont = new Font("Arial", 9, FontStyle.Regular))
                using (var pen = new Pen(Color.Black, 1.0f))
                {
                    g.DrawRectangle(pen, destSlot.X + 20, destSlot.Y + 20, Math.Max(40, destSlot.Width - 40), Math.Max(40, destSlot.Height - 40));
                    g.DrawString("PrintAway Order: " + orderCode + " (Page " + pageNum + " of " + totalSelected + ")", headerFont, Brushes.Black, destSlot.X + 35, destSlot.Y + 35);
                    g.DrawString("Queue: " + targetQueueName + " | Mode: " + (isColor ? "Color" : "B&W") + " | Duplex: " + duplexMode, bodyFont, Brushes.Black, destSlot.X + 35, destSlot.Y + 65);
                }
            }
        }

        public static bool RenderPdfPagesToPng(string pdfPath, List<int> pages, string outDir)
        {
            try
            {
                if (!Directory.Exists(outDir)) Directory.CreateDirectory(outDir);
                string repoRoot = AppDomain.CurrentDomain.BaseDirectory;
                string scriptPath = Path.Combine(repoRoot, "scripts", "render-pdf.ps1");
                if (!File.Exists(scriptPath))
                {
                    scriptPath = Path.Combine(repoRoot, "..", "scripts", "render-pdf.ps1");
                }
                if (!File.Exists(scriptPath))
                {
                    scriptPath = Path.Combine(Directory.GetCurrentDirectory(), "scripts", "render-pdf.ps1");
                }
                if (!File.Exists(scriptPath))
                {
                    // Auto-heal: Ensure scripts directory exists and write built-in render script
                    string defaultScriptsDir = Path.Combine(repoRoot, "scripts");
                    try
                    {
                        if (!Directory.Exists(defaultScriptsDir)) Directory.CreateDirectory(defaultScriptsDir);
                        string autoScript = Path.Combine(defaultScriptsDir, "render-pdf.ps1");
                        File.WriteAllText(autoScript, GetEmbeddedRenderScriptContent(), Encoding.UTF8);
                        scriptPath = autoScript;
                    }
                    catch { }
                }

                if (!File.Exists(scriptPath))
                {
                    return false;
                }

                string pageArg = (pages == null || pages.Count == 0) ? "all" : string.Join(",", pages);
                var psi = new ProcessStartInfo
                {
                    FileName = "powershell.exe",
                    Arguments = string.Format(CultureInfo.InvariantCulture,
                        "-NoProfile -ExecutionPolicy Bypass -File \"{0}\" -PdfPath \"{1}\" -OutDir \"{2}\" -Pages \"{3}\"",
                        scriptPath, Path.GetFullPath(pdfPath), Path.GetFullPath(outDir), pageArg),
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };

                using (var proc = Process.Start(psi))
                {
                    string stdout = proc.StandardOutput.ReadToEnd();
                    string stderr = proc.StandardError.ReadToEnd();
                    proc.WaitForExit(35000);
                    return proc.ExitCode == 0;
                }
            }
            catch
            {
                return false;
            }
        }

        private static string GetEmbeddedRenderScriptContent()
        {
            return @"[CmdletBinding()]
param(
    [Parameter(Mandatory=$true)]
    [string]$PdfPath,
    [Parameter(Mandatory=$true)]
    [string]$OutDir,
    [string]$Pages = ""all""
)

Set-StrictMode -Version Latest
$ErrorActionPreference = ""Stop""

if (-not (Test-Path $PdfPath)) {
    throw ""PDF file not found: $PdfPath""
}
if (-not (Test-Path $OutDir)) {
    New-Item -ItemType Directory -Path $OutDir -Force | Out-Null
}

$runtimeDll = [System.IO.Directory]::GetFiles(""C:\Windows\Microsoft.NET\Framework64\v4.0.30319"", ""System.Runtime.WindowsRuntime.dll"")[0]
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
if ($Pages -eq ""all"" -or [string]::IsNullOrWhiteSpace($Pages)) {
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
    $renderScale = 4.1666667
    $opts.DestinationWidth = [uint32][Math]::Round($page.Size.Width * $renderScale)
    $opts.DestinationHeight = [uint32][Math]::Round($page.Size.Height * $renderScale)
    Invoke-WinRtAction ($page.RenderToStreamAsync($ras, $opts))
    
    $netStream = [System.IO.WindowsRuntimeStreamExtensions]::AsStreamForRead($ras)
    $netStream.Position = 0
    $outPng = Join-Path $OutDir ""page_$pageNum.png""
    $fs = [System.IO.File]::Create($outPng)
    $netStream.CopyTo($fs)
    $fs.Close()
    $netStream.Close()
    Write-Host ""RENDERED: page_$pageNum.png ($($outPng))""
}
[Environment]::Exit(0)
";
        }

        public static byte[] GenerateSamplePdfBytes(int pageCount, string title)
        {
            using (var ms = new MemoryStream())
            using (var bw = new BinaryWriter(ms, Encoding.ASCII, true))
            {
                var offsets = new List<long>();
                Action<string> writeStr = s =>
                {
                    byte[] bytes = Encoding.ASCII.GetBytes(s);
                    bw.Write(bytes);
                };

                writeStr("%PDF-1.4\n%\xE2\xE3\xCF\xD3\n");

                offsets.Add(ms.Position);
                writeStr("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");

                var kids = new StringBuilder();
                for (int i = 0; i < pageCount; i++)
                {
                    int pageObjId = 4 + (i * 2);
                    kids.AppendFormat(CultureInfo.InvariantCulture, "{0} 0 R ", pageObjId);
                }
                offsets.Add(ms.Position);
                writeStr(string.Format(CultureInfo.InvariantCulture, "2 0 obj\n<< /Type /Pages /Kids [ {0}] /Count {1} >>\nendobj\n", kids.ToString(), pageCount));

                offsets.Add(ms.Position);
                writeStr("3 0 obj\n<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>\nendobj\n");

                string safeTitle = (title ?? "Sample Document").Replace("\\", "\\\\").Replace("(", "\\(").Replace(")", "\\)");
                for (int i = 0; i < pageCount; i++)
                {
                    int pageObjId = 4 + (i * 2);
                    int contentObjId = pageObjId + 1;
                    string pageText = string.Format(
                        CultureInfo.InvariantCulture,
                        "BT /F1 16 Tf 50 770 Td (PrintAway Document: {0}) Tj 0 -28 Td /F1 12 Tf (Page {1} of {2} - ISO A4 B&W) Tj ET\n50 700 495 95 re S\n",
                        safeTitle, i + 1, pageCount);
                    byte[] streamBytes = Encoding.ASCII.GetBytes(pageText);

                    offsets.Add(ms.Position);
                    writeStr(string.Format(
                        CultureInfo.InvariantCulture,
                        "{0} 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 595 842] /Resources << /Font << /F1 3 0 R >> >> /Contents {1} 0 R >>\nendobj\n",
                        pageObjId, contentObjId));

                    offsets.Add(ms.Position);
                    writeStr(string.Format(CultureInfo.InvariantCulture, "{0} 0 obj\n<< /Length {1} >>\nstream\n", contentObjId, streamBytes.Length));
                    bw.Write(streamBytes);
                    writeStr("\nendstream\nendobj\n");
                }

                long xrefPos = ms.Position;
                int totalObjs = offsets.Count + 1;
                writeStr(string.Format(CultureInfo.InvariantCulture, "xref\n0 {0}\n0000000000 65535 f \n", totalObjs));
                foreach (long off in offsets)
                {
                    writeStr(string.Format(CultureInfo.InvariantCulture, "{0:0000000000} 00000 n \n", off));
                }
                writeStr(string.Format(CultureInfo.InvariantCulture, "trailer\n<< /Size {0} /Root 1 0 R >>\nstartxref\n{1}\n%%EOF\n", totalObjs, xrefPos));
                bw.Flush();
                return ms.ToArray();
            }
        }
    }
}
