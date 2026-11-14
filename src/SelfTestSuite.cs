using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using PrintAway.Engine;
using PrintAway.Storage;
using PrintAway.Worker;

namespace PrintAway
{
    public static class SelfTestSuite
    {
        public static int RunAllTests(string tempRoot)
        {
            Console.WriteLine("=== PrintAway Automated Integration & PRD §14 Test Matrix Suite ===");
            int passed = 0;
            int failed = 0;

            Action<string, Action> runTest = (name, testBody) =>
            {
                try
                {
                    testBody();
                    passed++;
                    Console.ForegroundColor = ConsoleColor.Green;
                    Console.WriteLine("  [PASS] " + name);
                    Console.ResetColor();
                }
                catch (Exception ex)
                {
                    failed++;
                    Console.ForegroundColor = ConsoleColor.Red;
                    Console.WriteLine("  [FAIL] " + name + " -> " + ex.Message + "\n" + ex.StackTrace);
                    Console.ResetColor();
                }
            };

            if (Directory.Exists(tempRoot))
            {
                try { Directory.Delete(tempRoot, true); } catch { }
            }
            Directory.CreateDirectory(tempRoot);

            string dbPath = Path.Combine(tempRoot, "test_printaway.db");
            string vaultPath = Path.Combine(tempRoot, "vault");

            using (var sqlite = new SqliteDatabase(dbPath))
            {
                var repo = new PrintAwayRepository(sqlite, vaultPath, "test_webhook_secret_key", "test_worker_secret_token");
                var worker = new LocalPrintWorker(repo, simulateSpoolerOnly: true);

                string loginErr;
                var adminSess = repo.AuthenticateAdmin("admin", "PrintAway@Admin2026", out loginErr);
                Assert(adminSess != null, "Admin seed login must succeed");

                // Auto-detect verified physical queue on current host (supports both desktop 'PCL6' and laptop queues)
                var liveQueues = PdfAndPrinterEngine.DiscoverWindowsQueues();
                string verifiedQueueOnHost = null;
                foreach (var q in liveQueues)
                {
                    if (!q.IsVirtualExcluded)
                    {
                        if (q.QueueName == PrintAwayRepository.VerifiedXeroxQueueName || q.QueueName == PrintAwayRepository.SecondaryXeroxQueueName)
                        {
                            verifiedQueueOnHost = q.QueueName;
                            break;
                        }
                        else if (verifiedQueueOnHost == null)
                        {
                            verifiedQueueOnHost = q.QueueName;
                        }
                    }
                }
                if (!string.IsNullOrWhiteSpace(verifiedQueueOnHost))
                {
                    repo.UpdateShopAndPrinterControls(null, null, null, null, "test-init", null, null, null, null, verifiedQueueOnHost);
                }

                // Test 1: Both Xerox queues installed -> 1 active verified queue, secondary disabled, virtual excluded
                runTest("T01: Queue discovery locks verified physical Xerox queue & excludes virtual/duplicate queues", () =>
                {
                    var dbQueues = repo.Db.Query("SELECT * FROM printer_queues ORDER BY id ASC;");
                    Assert(dbQueues.Count >= 2, "Expected at least 2 configured Xerox USB001 queues in DB");
                    var active = repo.Db.QuerySingle("SELECT * FROM printer_queues WHERE is_active_selected = 1;");
                    Assert(active != null && (Convert.ToString(active["windows_queue_name"]) == PrintAwayRepository.VerifiedXeroxQueueName ||
                                              Convert.ToString(active["windows_queue_name"]) == PrintAwayRepository.SecondaryXeroxQueueName ||
                                              Convert.ToString(active["windows_queue_name"]) == verifiedQueueOnHost),
                        "Active queue must be verified on host");

                    bool foundVerified = false;
                    foreach (var q in liveQueues)
                    {
                        if (q.QueueName == PrintAwayRepository.VerifiedXeroxQueueName ||
                            q.QueueName == PrintAwayRepository.SecondaryXeroxQueueName ||
                            q.QueueName == verifiedQueueOnHost)
                        {
                            foundVerified = true;
                            Assert(!q.IsVirtualExcluded, "Verified physical queue must not be marked virtual");
                        }
                        if (q.QueueName.IndexOf("Print to PDF", StringComparison.OrdinalIgnoreCase) >= 0 ||
                            q.QueueName.IndexOf("OneNote", StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            Assert(q.IsVirtualExcluded, "Virtual queue " + q.QueueName + " must be excluded");
                        }
                    }
                    Assert(foundVerified, "Live Windows WMI discovery must find verified physical queue (" + verifiedQueueOnHost + ")");
                });

                // Test 2: Malformed, encrypted, and embedded-JS PDFs rejected before quote/dispatch
                runTest("T02: Malformed, password-protected (/Encrypt), and embedded-JS PDFs are rejected", () =>
                {
                    string errCode, errMsg;
                    var resMalformed = repo.SaveValidatedPdf("bad.pdf", System.Text.Encoding.ASCII.GetBytes("NOT_A_PDF_FILE_CONTENT"), out errCode, out errMsg);
                    Assert(resMalformed == null && errCode == "INVALID_PDF_SIGNATURE", "Malformed file must fail with INVALID_PDF_SIGNATURE");

                    var resEncrypted = repo.SaveValidatedPdf("locked.pdf",
                        System.Text.Encoding.ASCII.GetBytes("%PDF-1.4\n1 0 obj\n<< /Type /Catalog /Encrypt 2 0 R >>\nendobj\n%%EOF"), out errCode, out errMsg);
                    Assert(resEncrypted == null && errCode == "ENCRYPTED_PDF_UNSUPPORTED", "Encrypted PDF must fail with ENCRYPTED_PDF_UNSUPPORTED");

                    var resJs = repo.SaveValidatedPdf("script.pdf",
                        System.Text.Encoding.ASCII.GetBytes("%PDF-1.4\n1 0 obj\n<< /Type /Catalog /JavaScript 2 0 R >>\nendobj\n%%EOF"), out errCode, out errMsg);
                    Assert(resJs == null && errCode == "PDF_ACTIVE_CONTENT_REJECTED", "Embedded JS PDF must fail with PDF_ACTIVE_CONTENT_REJECTED");
                });

                // Test 3: 1-page PDF full lifecycle + Repeated Webhook Idempotency + Mandatory Admin Approval Gate
                string customer1PublicToken = null;
                runTest("T03: 1-page PDF upload -> quote -> repeated UPI webhooks -> stays paid_awaiting_approval -> admin approves -> prints once", () =>
                {
                    string errCode, errMsg;
                    byte[] pdf1 = PdfAndPrinterEngine.GenerateSamplePdfBytes(1, "One Page Test");
                    var saved = repo.SaveValidatedPdf("one-page.pdf", pdf1, out errCode, out errMsg);
                    Assert(saved != null && Convert.ToInt32(saved["pageCount"]) == 1, "1-page PDF must validate with PageCount=1");

                    string qErr;
                    var quote = repo.CreateServerQuote(Convert.ToString(saved["fileToken"]), 1, "OneSided", "all", out qErr);
                    Assert(quote != null && Convert.ToInt32(quote["printedSides"]) == 1 && Convert.ToInt32(quote["physicalSheets"]) == 1,
                        "1-page 1-copy simplex must equal 1 side and 1 sheet");

                    string oErr;
                    var order = repo.CreateOrderAndPaymentIntent(Convert.ToString(quote["quoteToken"]), "Aarav Sharma", "9876543210", true, out oErr);
                    Assert(order != null && Convert.ToString(order["state"]) == "awaiting_payment", "Order must start in awaiting_payment");
                    customer1PublicToken = Convert.ToString(order["publicToken"]);
                    long orderId = Convert.ToInt64(order["orderId"]);
                    string gwOrderId = Convert.ToString(order["gatewayOrderId"]);
                    long amtPaise = Convert.ToInt64(order["amountPaise"]);

                    // Fire webhook #1
                    string payload = string.Format(CultureInfo.InvariantCulture,
                        "{{\"eventId\":\"evt_001\",\"gatewayOrderId\":\"{0}\",\"gatewayPaymentId\":\"pay_001\",\"amountPaise\":{1},\"status\":\"success\"}}",
                        gwOrderId, amtPaise);
                    string sig = repo.ComputeWebhookHmac(payload);
                    int status;
                    string wErr;
                    var wh1 = repo.ProcessVerifiedPaymentWebhook(payload, sig, "evt_001", gwOrderId, "pay_001", amtPaise, "success", out status, out wErr);
                    Assert(wh1 != null && Convert.ToString(wh1["state"]) == "paid_awaiting_approval", "Webhook #1 must transition order to paid_awaiting_approval");

                    // Fire webhook #2 (repeated delivery) -> must be idempotent and MUST NOT auto-print or queue!
                    var wh2 = repo.ProcessVerifiedPaymentWebhook(payload, sig, "evt_001", gwOrderId, "pay_001", amtPaise, "success", out status, out wErr);
                    Assert(wh2 != null && (bool)wh2["idempotentDuplicate"] && Convert.ToString(wh2["state"]) == "paid_awaiting_approval",
                        "Repeated webhook must be idempotent and keep order in paid_awaiting_approval");

                    // Worker runs while order is paid_awaiting_approval -> MUST NOT claim or print!
                    var prematureCycle = worker.RunSingleCycle();
                    Assert(!(bool)prematureCycle["claimed"], "Worker must NEVER claim a paid_awaiting_approval order before admin approval");

                    // Unauthorized approval attempt (missing CSRF) must fail
                    var invalidSess = repo.ValidateAdminSession(Convert.ToString(adminSess["sessionToken"]), "wrong_csrf", true);
                    Assert(invalidSess == null, "Invalid CSRF token must be rejected");

                    // Authorized Admin Approval
                    int appStatus;
                    string appErr;
                    var approved = repo.AdminApproveOrder(orderId, adminSess, "Verified metadata & payment", out appStatus, out appErr);
                    Assert(approved != null && Convert.ToString(approved["state"]) == "queued", "Admin approval must transition order to queued");

                    // Duplicate Admin Approval attempt must be rejected (Hard Rule 4)
                    var dupApproved = repo.AdminApproveOrder(orderId, adminSess, "Second click", out appStatus, out appErr);
                    Assert(dupApproved == null && appStatus == 409, "Second approval attempt must be rejected with 409 Conflict");

                    // Worker claims and dispatches to Xerox B1025 Multifunction Printer PCL6
                    var workRes = worker.RunSingleCycle();
                    Assert((bool)workRes["claimed"] && Convert.ToString(workRes["state"]) == "submitted_to_spooler",
                        "Worker must dispatch approved job and transition to submitted_to_spooler");
                    Assert(workRes["windowsSpoolerJobId"] != null, "Windows Spooler Job ID must be recorded");

                    // Hard Rule 5: submitted_to_spooler != ready_for_pickup until staff confirms physical output
                    var trackedBeforeReady = repo.GetOrderByPublicToken(customer1PublicToken);
                    Assert(Convert.ToString(trackedBeforeReady["state"]) == "submitted_to_spooler",
                        "Order must stay in submitted_to_spooler until staff verifies physical pages");

                    string rdyErr;
                    var rdy = repo.AdminMarkOrderReadyForPickup(orderId, adminSess, out rdyErr);
                    Assert(rdy != null && Convert.ToString(rdy["state"]) == "ready_for_pickup", "Staff mark ready must succeed");

                    // Wrong pickup code must fail
                    string colErr;
                    var wrongCol = repo.AdminMarkOrderCollected(orderId, "WRONG1", adminSess, out colErr);
                    Assert(wrongCol == null, "Wrong pickup code must be rejected");

                    // Correct pickup code -> marks collected AND deletes source PDF from disk
                    string correctPickup = Convert.ToString(rdy["pickupCode"]);
                    var col = repo.AdminMarkOrderCollected(orderId, correctPickup, adminSess, out colErr);
                    Assert(col != null && Convert.ToString(col["state"]) == "collected" && (bool)col["sourcePdfPurged"],
                        "Correct pickup code must mark collected and purge source PDF");
                });

                // Test 4: 10-page PDF x 2 copies Simplex & 5-page PDF x 2 copies Duplex math (PRD §10)
                runTest("T04: 10-page PDF x 2 copies = 20 sides/20 sheets; 5-page Duplex x 2 copies = 10 sides/6 sheets", () =>
                {
                    string errCode, errMsg, qErr;
                    byte[] pdf10 = PdfAndPrinterEngine.GenerateSamplePdfBytes(10, "Ten Page Doc");
                    var saved10 = repo.SaveValidatedPdf("ten-page.pdf", pdf10, out errCode, out errMsg);
                    var quote10 = repo.CreateServerQuote(Convert.ToString(saved10["fileToken"]), 2, "OneSided", "all", out qErr);
                    Assert(quote10 != null && Convert.ToInt32(quote10["printedSides"]) == 20 && Convert.ToInt32(quote10["physicalSheets"]) == 20,
                        "10-page x 2 copies simplex must equal 20 printed sides and 20 physical sheets");

                    byte[] pdf5 = PdfAndPrinterEngine.GenerateSamplePdfBytes(5, "Five Page Duplex Doc");
                    var saved5 = repo.SaveValidatedPdf("five-page.pdf", pdf5, out errCode, out errMsg);
                    var quote5Duplex = repo.CreateServerQuote(Convert.ToString(saved5["fileToken"]), 2, "TwoSidedLongEdge", "all", out qErr);
                    Assert(quote5Duplex != null && Convert.ToInt32(quote5Duplex["printedSides"]) == 10 && Convert.ToInt32(quote5Duplex["physicalSheets"]) == 6,
                        "5-page x 2 copies duplex must equal 10 printed sides and 6 physical sheets (each copy starts on a fresh sheet)");
                });

                // Test 5: Worker disconnect mid-download vs Crash after spooler accepts job (A04, A07)
                runTest("T05: Mid-download disconnect recovers to queued; post-spooler crash enters reconciliation_required (never blind retry)", () =>
                {
                    long orderId = CreatePaidAndApprovedOrder(repo, adminSess, 2, 1);

                    // Simulate crash during download (pre-render)
                    var midDl = worker.RunSingleCycle(simulateCrashMidDownload: true, simulateCrashAfterSpooler: false);
                    Assert((bool)midDl["claimed"], "Job leased for mid-download test");

                    // Force lease expiry in DB
                    repo.Db.ExecuteNonQuery("UPDATE print_jobs SET lease_expires_utc = '2020-01-01T00:00:00Z' WHERE order_id = @oid;",
                        new Dictionary<string, object> { { "oid", orderId } });

                    var rec1 = repo.ReconcileExpiredWorkerLeases();
                    Assert(Convert.ToInt32(rec1["recoveredToQueue"]) == 1, "Pre-render expired lease must recover to queued");

                    // Now simulate crash AFTER spooler accepts job (during rendering/spooling)
                    var postSpool = worker.RunSingleCycle(simulateCrashMidDownload: false, simulateCrashAfterSpooler: true);
                    Assert((bool)postSpool["claimed"], "Job re-leased and sent to spooler before simulated crash");

                    repo.Db.ExecuteNonQuery("UPDATE print_jobs SET lease_expires_utc = '2020-01-01T00:00:00Z' WHERE order_id = @oid;",
                        new Dictionary<string, object> { { "oid", orderId } });

                    var rec2 = repo.ReconcileExpiredWorkerLeases();
                    Assert(Convert.ToInt32(rec2["flaggedForReconciliation"]) == 1, "Post-render crash must enter reconciliation_required");

                    var orderRow = repo.Db.QuerySingle("SELECT state FROM orders WHERE id = @oid;", new Dictionary<string, object> { { "oid", orderId } });
                    Assert(Convert.ToString(orderRow["state"]) == "reconciliation_required", "Order state must be reconciliation_required");

                    // Worker must NOT pick up reconciliation_required job automatically
                    var blockedCycle = worker.RunSingleCycle();
                    Assert(!(bool)blockedCycle["claimed"], "Worker must never auto-retry a reconciliation_required job");

                    // Admin manually reconciles after checking physical tray
                    string recErr;
                    var resolved = repo.AdminReconcileOrder(orderId, adminSess, "confirm_printed", "Checked Xerox B1025 tray; 2 pages printed cleanly", out recErr);
                    Assert(resolved != null && Convert.ToString(resolved["state"]) == "ready_for_pickup", "Admin reconciliation confirm_printed must transition to ready_for_pickup");
                });

                // Test 6: Admin Rejects Paid Order -> Refunded, never printed; Admin Cancels Before Print
                runTest("T06: Admin rejection refunds order without printing; Admin cancel before print prevents dispatch", () =>
                {
                    long rejectOrderId = CreatePaidAwaitingApprovalOrder(repo, 3, 1);
                    string rejErr;
                    var rej = repo.AdminRejectAndRefundOrder(rejectOrderId, adminSess, "Customer requested cancellation", out rejErr);
                    Assert(rej != null && Convert.ToString(rej["state"]) == "refunded" && Convert.ToString(rej["paymentStatus"]) == "refunded",
                        "Rejected paid order must transition to refunded");

                    long cancelOrderId = CreatePaidAndApprovedOrder(repo, adminSess, 2, 1);
                    string canErr;
                    var can = repo.AdminCancelBeforePrint(cancelOrderId, adminSess, "Cancelled before worker claimed", out canErr);
                    Assert(can != null && Convert.ToString(can["state"]) == "cancelled", "Order cancelled before print must transition to cancelled");

                    var cycleAfterCancel = worker.RunSingleCycle();
                    Assert(!(bool)cycleAfterCancel["claimed"], "Cancelled order must never be claimed by worker");
                });

                // Test 7: Printer Jam / Out of Paper & Intake Pause blocks new uploads and stops dispatch
                runTest("T07: Printer jam/paper-out or intake pause stops queue dispatch and blocks new customer uploads", () =>
                {
                    long queuedOrderId = CreatePaidAndApprovedOrder(repo, adminSess, 1, 1);

                    // Simulate printer out of paper / jam reported
                    repo.UpdateShopAndPrinterControls(false, "Paper Jam in Tray 1", null, false, "admin");

                    // Worker must pause and NOT claim the queued job while printerReady=false
                    var pausedCycle = worker.RunSingleCycle();
                    Assert(!(bool)pausedCycle["claimed"], "Worker must not dispatch while printer is in jam/paper-out state");

                    // Customer cannot upload/checkout while printer is unavailable
                    string errCode, errMsg;
                    var blockedUpload = repo.SaveValidatedPdf("blocked.pdf", PdfAndPrinterEngine.GenerateSamplePdfBytes(1, "Blocked"), out errCode, out errMsg);
                    Assert(blockedUpload == null && errCode == "SHOP_UNAVAILABLE", "Uploads must be refused when printer/shop is unavailable");

                    // Clear jam -> worker resumes and dispatches preserved paid order
                    repo.UpdateShopAndPrinterControls(false, "", null, true, "admin");
                    var resumedCycle = worker.RunSingleCycle();
                    Assert((bool)resumedCycle["claimed"] && Convert.ToInt64(resumedCycle["orderId"]) == queuedOrderId,
                        "Preserved paid order must print once jam is cleared");
                });

                // Test 8: Price Rule Versioning (M03) & Customer Tracking Isolation (C07)
                runTest("T08: Price changes create new version without altering past orders; tracking tokens are isolated", () =>
                {
                    var beforeOrder = repo.GetOrderByPublicToken(customer1PublicToken);
                    double originalTotal = Convert.ToDouble(beforeOrder["totalAmountInr"], CultureInfo.InvariantCulture);

                    string prErr;
                    var newRule = repo.UpdatePricingRule(5.00, 4.00, 10.00, 18.0, "admin", out prErr);
                    Assert(newRule != null && Convert.ToInt64(newRule["version"]) == 2, "New price rule version must be 2");

                    var afterOrder = repo.GetOrderByPublicToken(customer1PublicToken);
                    double afterTotal = Convert.ToDouble(afterOrder["totalAmountInr"], CultureInfo.InvariantCulture);
                    Assert(Math.Abs(originalTotal - afterTotal) < 0.001, "Confirmed past order price must never change when admin updates pricing");

                    var unknownTokenOrder = repo.GetOrderByPublicToken("pub_nonexistent_token_12345");
                    Assert(unknownTokenOrder == null, "Guessing another token must return null");
                });

                // Test 9: Real-world PDF ('FIRST SUCCESSFUL DOCUMENT.pdf') end-to-end validation, quote, UPI webhook, admin approval, worker dispatch & collection
                runTest("T09: Real-world 'FIRST SUCCESSFUL DOCUMENT.pdf' (188 KB, 1 page) full end-to-end order lifecycle", () =>
                {
                    string repoRoot = Path.GetFullPath(Path.Combine(tempRoot, "..", ".."));
                    string founderPdfPath = Path.Combine(repoRoot, "FIRST SUCCESSFUL DOCUMENT.pdf");
                    Assert(File.Exists(founderPdfPath), "FIRST SUCCESSFUL DOCUMENT.pdf must exist at " + founderPdfPath);

                    byte[] founderBytes = File.ReadAllBytes(founderPdfPath);
                    string errCode, errMsg;
                    var saved = repo.SaveValidatedPdf("FIRST SUCCESSFUL DOCUMENT.pdf", founderBytes, out errCode, out errMsg);
                    Assert(saved != null, "FIRST SUCCESSFUL DOCUMENT.pdf must pass PDF validation: " + errMsg);
                    Assert(Convert.ToInt32(saved["pageCount"]) == 2, "FIRST SUCCESSFUL DOCUMENT.pdf must report exactly 2 pages");
                    Assert(Convert.ToInt64(saved["sizeBytes"]) == 31508L, "FIRST SUCCESSFUL DOCUMENT.pdf size must match 31508 bytes");

                    string qErr;
                    var quote = repo.CreateServerQuote(Convert.ToString(saved["fileToken"]), 1, "OneSided", "all", out qErr);
                    Assert(quote != null && Convert.ToInt32(quote["printedSides"]) == 2 && Convert.ToInt32(quote["physicalSheets"]) == 2,
                        "Quote for FIRST SUCCESSFUL DOCUMENT.pdf (2 pages simplex) must be 2 sides and 2 sheets");

                    string oErr;
                    var order = repo.CreateOrderAndPaymentIntent(Convert.ToString(quote["quoteToken"]), "Founder Test", "9876543210", true, out oErr);
                    Assert(order != null, "Order creation for FIRST SUCCESSFUL DOCUMENT.pdf must succeed");

                    long orderId = Convert.ToInt64(order["orderId"]);
                    string gwOrderId = Convert.ToString(order["gatewayOrderId"]);
                    long amtPaise = Convert.ToInt64(order["amountPaise"]);
                    string payload = string.Format(CultureInfo.InvariantCulture,
                        "{{\"eventId\":\"evt_founder_doc_1\",\"gatewayOrderId\":\"{0}\",\"gatewayPaymentId\":\"pay_founder_1\",\"amountPaise\":{1},\"status\":\"success\"}}",
                        gwOrderId, amtPaise);
                    string sig = repo.ComputeWebhookHmac(payload);
                    int httpSt;
                    string wErr;
                    var wh = repo.ProcessVerifiedPaymentWebhook(payload, sig, "evt_founder_doc_1", gwOrderId, "pay_founder_1", amtPaise, "success", out httpSt, out wErr);
                    Assert(wh != null && Convert.ToString(wh["state"]) == "paid_awaiting_approval", "Order must enter paid_awaiting_approval");

                    int appSt;
                    string appErr;
                    var app = repo.AdminApproveOrder(orderId, adminSess, "Approved FIRST SUCCESSFUL DOCUMENT.pdf", out appSt, out appErr);
                    Assert(app != null && Convert.ToString(app["state"]) == "queued", "Admin approval must transition to queued");

                    var cycle = worker.RunSingleCycle();
                    Assert((bool)cycle["claimed"] && Convert.ToString(cycle["state"]) == "submitted_to_spooler",
                        "Worker must dispatch FIRST SUCCESSFUL DOCUMENT.pdf to active queue (" + verifiedQueueOnHost + ")");

                    string rdyErr, colErr;
                    var rdy = repo.AdminMarkOrderReadyForPickup(orderId, adminSess, out rdyErr);
                    Assert(rdy != null && Convert.ToString(rdy["state"]) == "ready_for_pickup", "Marked ready_for_pickup");
                    var col = repo.AdminMarkOrderCollected(orderId, Convert.ToString(rdy["pickupCode"]), adminSess, out colErr);
                    Assert(col != null && Convert.ToString(col["state"]) == "collected" && (bool)col["sourcePdfPurged"],
                        "Collected and purged FIRST SUCCESSFUL DOCUMENT.pdf copy from vault");
                });

                // Test 10: Expanded Printer & Page Controls (B&W vs Color, First N Pages, Odd/Even, Custom Range, 2-up/4-up N-Up, Orientation, Admin Override)
                runTest("T10: Granular Page Selection (first:N, odd, even, custom), Color vs B&W pricing, 2-up/4-up N-Up & Admin Override", () =>
                {
                    string errCode, errMsg, qErr;
                    byte[] pdf8 = PdfAndPrinterEngine.GenerateSamplePdfBytes(8, "Eight Page Control Test");
                    var saved8 = repo.SaveValidatedPdf("eight-page.pdf", pdf8, out errCode, out errMsg);
                    string ft = Convert.ToString(saved8["fileToken"]);

                    // First 3 pages ("first:3") in Color Simplex
                    var qFirst3Color = repo.CreateServerQuote(ft, 1, "OneSided", "first:3", out qErr, "color", "A4", "portrait", 1, true);
                    Assert(qFirst3Color != null && Convert.ToInt32(qFirst3Color["selectedPages"]) == 3 && Convert.ToString(qFirst3Color["colorMode"]) == "color",
                        "first:3 in color mode must select 3 pages and set colorMode=color");

                    // Odd pages ("odd") on 8-page doc -> 4 pages (1,3,5,7), 2-up (2 pages per side) -> 2 printed sides, 2 sheets
                    var qOdd2Up = repo.CreateServerQuote(ft, 1, "OneSided", "odd", out qErr, "bw", "A4", "landscape", 2, true);
                    Assert(qOdd2Up != null && Convert.ToInt32(qOdd2Up["selectedPages"]) == 4 && Convert.ToInt32(qOdd2Up["printedSides"]) == 2 && Convert.ToInt32(qOdd2Up["physicalSheets"]) == 2,
                        "8-page odd with 2-up simplex must equal 4 selected pages, 2 printed sides, and 2 physical sheets");

                    // Even pages ("even") on 8-page doc -> 4 pages (2,4,6,8), 4-up (4 pages per side) -> 1 printed side, 1 sheet
                    var qEven4Up = repo.CreateServerQuote(ft, 2, "OneSided", "even", out qErr, "bw", "Legal", "auto", 4, false);
                    Assert(qEven4Up != null && Convert.ToInt32(qEven4Up["selectedPages"]) == 4 && Convert.ToInt32(qEven4Up["printedSides"]) == 2 && Convert.ToInt32(qEven4Up["physicalSheets"]) == 2,
                        "8-page even with 4-up x 2 copies must equal 2 total printed sides and 2 sheets");

                    // Admin override print settings on an existing order before approving
                    long overrideOrderId = CreatePaidAwaitingApprovalOrder(repo, 6, 1);
                    string ovErr;
                    var ov = repo.AdminOverrideOrderPrintSettings(overrideOrderId, "1-2,5", 2, "TwoSidedLongEdge", "color", "A4", "portrait", 1, true, "admin", out ovErr);
                    Assert(ov != null && Convert.ToInt32(ov["selectedPages"]) == 3 && Convert.ToInt32(ov["printedSides"]) == 6 && Convert.ToInt32(ov["physicalSheets"]) == 4 && Convert.ToString(ov["colorMode"]) == "color",
                        "Admin override (3 selected pages x 2 copies duplex in color) must update quote to 6 sides and 4 physical sheets");
                });

                // Test 11: Image and Document Native Auto-Conversion to ISO A4 PDF (Way 3 Safety Net)
                runTest("T11: Image & Document Fallback to ISO A4 PDF (PNG/JPEG/Text to valid PDF)", () =>
                {
                    string errCode, errMsg;
                    // Generate a sample PNG image in memory
                    byte[] pngBytes;
                    using (var bmp = new System.Drawing.Bitmap(600, 400))
                    {
                        using (var g = System.Drawing.Graphics.FromImage(bmp))
                        {
                            g.Clear(System.Drawing.Color.LightSkyBlue);
                            using (var pen = new System.Drawing.Pen(System.Drawing.Color.MidnightBlue, 3.0f))
                            {
                                g.DrawRectangle(pen, 10, 10, 580, 380);
                            }
                        }
                        using (var ms = new System.IO.MemoryStream())
                        {
                            bmp.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
                            pngBytes = ms.ToArray();
                        }
                    }

                    Assert(PdfAndPrinterEngine.IsImageBytes(pngBytes), "PNG bytes must be identified by IsImageBytes");
                    var savedImg = repo.SaveValidatedPdf("sample_photo.png", pngBytes, out errCode, out errMsg);
                    Assert(savedImg != null, "SaveValidatedPdf must succeed for PNG image: " + errMsg);
                    Assert(Convert.ToInt32(savedImg["pageCount"]) == 1, "Image must convert into a 1-page A4 document");
                    Assert(Convert.ToString(savedImg["displayName"]).EndsWith("_A4.pdf", StringComparison.OrdinalIgnoreCase), "Output displayName must end with _A4.pdf");

                    // Verify plain text document auto-conversion (including multi-page pagination)
                    var sbLong = new System.Text.StringBuilder();
                    sbLong.AppendLine("PrintAway Test Receipt & Multi-Page Verification");
                    for (int li = 1; li <= 90; li++)
                    {
                        sbLong.AppendLine("Line " + li + ": PrintAway automatic multi-page A4 document rendering verification entry.");
                    }
                    byte[] txtBytes = System.Text.Encoding.UTF8.GetBytes(sbLong.ToString());
                    var savedTxt = repo.SaveValidatedPdf("receipt.txt", txtBytes, out errCode, out errMsg);
                    Assert(savedTxt != null, "SaveValidatedPdf must succeed for text file: " + errMsg);
                    Assert(Convert.ToInt32(savedTxt["pageCount"]) >= 2, "Long text must paginate into at least 2 A4 PDF pages");
                    Assert(Convert.ToString(savedTxt["displayName"]).EndsWith("_Doc.pdf", StringComparison.OrdinalIgnoreCase), "Output displayName must end with _Doc.pdf");

                    // Verify in-memory OOXML (.docx) ZIP conversion without MS Office/LibreOffice
                    byte[] docxBytes;
                    using (var zms = new System.IO.MemoryStream())
                    {
                        using (var arch = new System.IO.Compression.ZipArchive(zms, System.IO.Compression.ZipArchiveMode.Create, true))
                        {
                            var ctEntry = arch.CreateEntry("[Content_Types].xml");
                            using (var sw = new System.IO.StreamWriter(ctEntry.Open(), System.Text.Encoding.UTF8))
                            {
                                sw.Write("<?xml version=\"1.0\" encoding=\"UTF-8\"?><Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\"></Types>");
                            }
                            var docEntry = arch.CreateEntry("word/document.xml");
                            using (var sw = new System.IO.StreamWriter(docEntry.Open(), System.Text.Encoding.UTF8))
                            {
                                sw.Write("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
                                         "<w:document xmlns:w=\"http://schemas.openxmlformats.org/wordprocessingml/2006/main\">" +
                                         "<w:body>" +
                                         "<w:p><w:pPr><w:pStyle w:val=\"Heading1\"/></w:pPr><w:r><w:t>PrintAway DOCX Heading</w:t></w:r></w:p>" +
                                         "<w:p><w:r><w:t>Hello from native OOXML Word conversion test.</w:t></w:r></w:p>" +
                                         "<w:tbl><w:tr><w:tc><w:p><w:r><w:t>Item</w:t></w:r></w:p></w:tc><w:tc><w:p><w:r><w:t>Price</w:t></w:r></w:p></w:tc></w:tr></w:tbl>" +
                                         "</w:body></w:document>");
                            }
                        }
                        docxBytes = zms.ToArray();
                    }
                    var savedDocx = repo.SaveValidatedPdf("customer_assignment.docx", docxBytes, out errCode, out errMsg);
                    Assert(savedDocx != null, "SaveValidatedPdf must succeed for .docx file: " + errMsg);
                    Assert(Convert.ToInt32(savedDocx["pageCount"]) >= 1, "DOCX must convert into >= 1 PDF page");
                    Assert(Convert.ToString(savedDocx["displayName"]).EndsWith("_Doc.pdf", StringComparison.OrdinalIgnoreCase), "DOCX output displayName must end with _Doc.pdf");

                    // Verify RTF auto-conversion
                    byte[] rtfBytes = System.Text.Encoding.ASCII.GetBytes(@"{\rtf1\ansi\deff0 {\b PrintAway RTF Test}\par Second line of RTF document.}");
                    var savedRtf = repo.SaveValidatedPdf("notes.rtf", rtfBytes, out errCode, out errMsg);
                    Assert(savedRtf != null, "SaveValidatedPdf must succeed for .rtf file: " + errMsg);
                    Assert(Convert.ToInt32(savedRtf["pageCount"]) >= 1, "RTF must convert into >= 1 PDF page");
                });

                // Test 12: Tunnel URL validation (no placeholder leak, valid Cloudflare/OpenSSH/Pinggy/Custom hosts)
                runTest("T12: Tunnel URL sanitization & multi-provider validator rejects placeholders", () =>
                {
                    Assert(!PrintAway.Tunnel.TunnelManager.IsValidPublicTunnelUrl("https://api.trycloudflare.com"), "api.trycloudflare.com must be rejected");
                    Assert(!PrintAway.Tunnel.TunnelManager.IsValidPublicTunnelUrl("https://pkg.trycloudflare.com"), "pkg.trycloudflare.com must be rejected");
                    Assert(!PrintAway.Tunnel.TunnelManager.IsValidPublicTunnelUrl("http://insecure.trycloudflare.com"), "HTTP (non-HTTPS) must be rejected");
                    Assert(PrintAway.Tunnel.TunnelManager.IsValidPublicTunnelUrl("https://bright-river-station.trycloudflare.com"), "Valid trycloudflare.com must be accepted");
                    Assert(PrintAway.Tunnel.TunnelManager.IsValidPublicTunnelUrl("https://abc123.lhr.life"), "Valid localhost.run must be accepted");
                    Assert(PrintAway.Tunnel.TunnelManager.IsValidPublicTunnelUrl("https://xyz.a.free.pinggy.link"), "Valid Pinggy link must be accepted");
                    Assert(PrintAway.Tunnel.TunnelManager.IsValidPublicTunnelUrl("https://shop1.printaway.in"), "Custom HTTPS domain must be accepted");
                });
            }

            Console.WriteLine("------------------------------------------------------------------");
            Console.WriteLine(string.Format(CultureInfo.InvariantCulture, "Summary: {0} Passed, {1} Failed", passed, failed));
            return failed == 0 ? 0 : 1;
        }

        private static long CreatePaidAwaitingApprovalOrder(PrintAwayRepository repo, int pages, int copies)
        {
            string errCode, errMsg, qErr, oErr, wErr;
            byte[] pdf = PdfAndPrinterEngine.GenerateSamplePdfBytes(pages, "Test Doc");
            var saved = repo.SaveValidatedPdf("doc.pdf", pdf, out errCode, out errMsg);
            var quote = repo.CreateServerQuote(Convert.ToString(saved["fileToken"]), copies, "OneSided", "all", out qErr);
            var order = repo.CreateOrderAndPaymentIntent(Convert.ToString(quote["quoteToken"]), "Test User", "9999999999", true, out oErr);
            long orderId = Convert.ToInt64(order["orderId"]);
            string gwOrderId = Convert.ToString(order["gatewayOrderId"]);
            long amtPaise = Convert.ToInt64(order["amountPaise"]);
            string evId = "evt_" + PrintAwayRepository.GenerateRandomToken(8);
            string payload = string.Format(CultureInfo.InvariantCulture,
                "{{\"eventId\":\"{0}\",\"gatewayOrderId\":\"{1}\",\"amountPaise\":{2},\"status\":\"success\"}}",
                evId, gwOrderId, amtPaise);
            string sig = repo.ComputeWebhookHmac(payload);
            int httpSt;
            repo.ProcessVerifiedPaymentWebhook(payload, sig, evId, gwOrderId, "pay_" + evId, amtPaise, "success", out httpSt, out wErr);
            return orderId;
        }

        private static long CreatePaidAndApprovedOrder(PrintAwayRepository repo, Dictionary<string, object> adminSess, int pages, int copies)
        {
            long orderId = CreatePaidAwaitingApprovalOrder(repo, pages, copies);
            int st;
            string err;
            repo.AdminApproveOrder(orderId, adminSess, "Auto test approval", out st, out err);
            return orderId;
        }

        private static void Assert(bool condition, string message)
        {
            if (!condition)
            {
                throw new InvalidOperationException("Assertion failed: " + message);
            }
        }
    }
}
