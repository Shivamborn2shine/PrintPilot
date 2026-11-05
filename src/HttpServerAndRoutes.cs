using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;
using PrintAway.Engine;
using PrintAway.Storage;
using PrintAway.Worker;

namespace PrintAway.Web
{
    public sealed class PrintAwayHttpServer : IDisposable
    {
        private readonly HttpListener _listener;
        private readonly PrintAwayRepository _repo;
        private readonly LocalPrintWorker _worker;
        private readonly string _webRoot;
        private readonly int _port;
        private readonly JavaScriptSerializer _json;
        private readonly ConcurrentDictionary<string, List<long>> _rateLimitBuckets;
        private volatile bool _running;
        private Thread _acceptThread;

        public PrintAwayHttpServer(int port, PrintAwayRepository repo, LocalPrintWorker worker, string webRoot)
        {
            _port = port;
            _repo = repo;
            _worker = worker;
            _webRoot = webRoot;
            _json = new JavaScriptSerializer { MaxJsonLength = 30 * 1024 * 1024 };
            _rateLimitBuckets = new ConcurrentDictionary<string, List<long>>();

            _listener = new HttpListener();
            // Bound strictly to 127.0.0.1 per PRD §8
            _listener.Prefixes.Add(string.Format(CultureInfo.InvariantCulture, "http://127.0.0.1:{0}/", port));
        }

        public int Port { get { return _port; } }

        public void Start()
        {
            if (_running) return;
            _listener.Start();
            _running = true;
            _acceptThread = new Thread(AcceptLoop)
            {
                IsBackground = true,
                Name = "PrintAway-HttpServer"
            };
            _acceptThread.Start();
        }

        private void AcceptLoop()
        {
            while (_running)
            {
                try
                {
                    HttpListenerContext ctx = _listener.GetContext();
                    ThreadPool.QueueUserWorkItem(_ =>
                    {
                        try
                        {
                            HandleContext(ctx);
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine("[HttpServer] Unhandled request exception: " + ex.Message);
                            try { ctx.Response.Abort(); } catch { }
                        }
                    });
                }
                catch (HttpListenerException)
                {
                    if (!_running) break;
                }
                catch
                {
                }
            }
        }

        private bool CheckRateLimit(string bucketKey, int maxRequestsPerMinute)
        {
            long nowTicks = DateTime.UtcNow.Ticks;
            long windowStart = nowTicks - TimeSpan.FromMinutes(1).Ticks;
            var list = _rateLimitBuckets.GetOrAdd(bucketKey, _ => new List<long>());
            lock (list)
            {
                list.RemoveAll(t => t < windowStart);
                if (list.Count >= maxRequestsPerMinute)
                    return false;
                list.Add(nowTicks);
                return true;
            }
        }

        private void HandleContext(HttpListenerContext ctx)
        {
            HttpListenerRequest req = ctx.Request;
            HttpListenerResponse res = ctx.Response;

            // Security & CORS Headers
            res.Headers["X-Content-Type-Options"] = "nosniff";
            res.Headers["X-Frame-Options"] = "DENY";
            res.Headers["Referrer-Policy"] = "no-referrer";
            res.Headers["Cache-Control"] = "no-store";
            res.KeepAlive = false;
            res.Headers["Access-Control-Allow-Origin"] = "*";
            res.Headers["Access-Control-Allow-Methods"] = "GET, POST, OPTIONS, PUT, DELETE";
            res.Headers["Access-Control-Allow-Headers"] = "Content-Type, X-Admin-Session, X-CSRF-Token, X-Worker-Token, X-File-Name, X-PrintAway-Signature, Authorization";

            string path = (req.Url.AbsolutePath ?? "/").TrimEnd('/');
            if (path.Length == 0) path = "/";
            string method = req.HttpMethod.ToUpperInvariant();

            // Handle CORS preflight requests
            if (method == "OPTIONS")
            {
                res.StatusCode = 204;
                res.Close();
                return;
            }

            try
            {
                // Static UI routes
                if (method == "GET" && (path == "/" || path == "/index.html" || path.StartsWith("/track/")))
                {
                    ServeStaticHtml(res, "index.html");
                    return;
                }
                if (method == "GET" && (path == "/admin" || path == "/admin.html"))
                {
                    ServeStaticHtml(res, "admin.html");
                    return;
                }

                // Public Shop & Availability Status
                if (method == "GET" && path == "/api/shop/status")
                {
                    WriteJson(res, 200, _repo.GetShopAndAvailabilityStatus());
                    return;
                }

                // Customer PDF Upload (POST /api/uploads)
                if (method == "POST" && path == "/api/uploads")
                {
                    if (!CheckRateLimit("upload", 30))
                    {
                        WriteError(res, 429, "RATE_LIMITED", "Too many upload requests. Please wait a moment.");
                        return;
                    }

                    string fileName = req.Headers["X-File-Name"] ?? req.QueryString["filename"] ?? "document.pdf";
                    byte[] rawBytes;
                    string contentType = req.ContentType ?? "";

                    if (contentType.IndexOf("application/json", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        var body = ReadJsonBody(req);
                        fileName = GetString(body, "fileName", fileName);
                        string base64 = GetString(body, "pdfBase64", "");
                        rawBytes = string.IsNullOrEmpty(base64) ? new byte[0] : Convert.FromBase64String(base64);
                    }
                    else
                    {
                        using (var ms = new MemoryStream())
                        {
                            req.InputStream.CopyTo(ms);
                            rawBytes = ms.ToArray();
                        }
                    }

                    string errCode, errMsg;
                    var saved = _repo.SaveValidatedPdf(fileName, rawBytes, out errCode, out errMsg);
                    if (saved == null)
                    {
                        int status = errCode == "SHOP_UNAVAILABLE" ? 503 : 400;
                        WriteError(res, status, errCode, errMsg);
                        return;
                    }
                    WriteJson(res, 200, saved);
                    return;
                }

                // Customer Sample PDF Generator (convenience helper for local demo)
                if (method == "POST" && path == "/api/uploads/sample")
                {
                    var body = ReadJsonBody(req);
                    string preset = GetString(body, "preset", "");
                    if (string.Equals(preset, "first-successful-document", StringComparison.OrdinalIgnoreCase))
                    {
                        string repoRoot = Path.GetFullPath(Path.Combine(_webRoot, ".."));
                        string founderPdfPath = Path.Combine(repoRoot, "FIRST SUCCESSFUL DOCUMENT.pdf");
                        if (!File.Exists(founderPdfPath))
                        {
                            WriteError(res, 404, "FILE_NOT_FOUND", "FIRST SUCCESSFUL DOCUMENT.pdf not found in repository root.");
                            return;
                        }
                        byte[] founderBytes = File.ReadAllBytes(founderPdfPath);
                        string fErrCode, fErrMsg;
                        var fSaved = _repo.SaveValidatedPdf("FIRST SUCCESSFUL DOCUMENT.pdf", founderBytes, out fErrCode, out fErrMsg);
                        if (fSaved == null)
                        {
                            WriteError(res, 400, fErrCode, fErrMsg);
                            return;
                        }
                        WriteJson(res, 200, fSaved);
                        return;
                    }

                    int pages = GetInt(body, "pages", 5);
                    string title = GetString(body, "title", "PrintAway Demo Assignment");
                    byte[] samplePdf = PdfAndPrinterEngine.GenerateSamplePdfBytes(Math.Max(1, Math.Min(50, pages)), title);
                    string errCode, errMsg;
                    var saved = _repo.SaveValidatedPdf(title.Replace(" ", "_") + "_" + pages + "p.pdf", samplePdf, out errCode, out errMsg);
                    if (saved == null)
                    {
                        WriteError(res, 400, errCode, errMsg);
                        return;
                    }
                    WriteJson(res, 200, saved);
                    return;
                }

                // Server-Authoritative Quote (POST /api/orders/quote)
                if (method == "POST" && path == "/api/orders/quote")
                {
                    var body = ReadJsonBody(req);
                    string fileToken = GetString(body, "fileToken", "");
                    int copies = GetInt(body, "copies", 1);
                    string duplexMode = GetString(body, "duplexMode", "OneSided");
                    string pageRange = GetString(body, "pageRange", "all");
                    string colorMode = GetString(body, "colorMode", "bw");
                    string paperSize = GetString(body, "paperSize", "A4");
                    string orientation = GetString(body, "orientation", "auto");
                    int pagesPerSheet = GetInt(body, "pagesPerSheet", 1);
                    bool collate = GetBool(body, "collate", true);

                    string errMsg;
                    var quote = _repo.CreateServerQuote(fileToken, copies, duplexMode, pageRange, out errMsg, colorMode, paperSize, orientation, pagesPerSheet, collate);
                    if (quote == null)
                    {
                        WriteError(res, 400, "INVALID_QUOTE_REQUEST", errMsg);
                        return;
                    }
                    WriteJson(res, 200, quote);
                    return;
                }

                // Create Order + UPI Payment Intent (POST /api/orders or POST /api/payments/create)
                if (method == "POST" && (path == "/api/orders" || path == "/api/payments/create"))
                {
                    var body = ReadJsonBody(req);
                    string quoteToken = GetString(body, "quoteToken", "");
                    string customerName = GetString(body, "customerName", "Walk-in Customer");
                    string customerPhone = GetString(body, "customerPhone", "");
                    bool consent = GetBool(body, "consentAccepted", true);

                    string errMsg;
                    var orderRes = _repo.CreateOrderAndPaymentIntent(quoteToken, customerName, customerPhone, consent, out errMsg);
                    if (orderRes == null)
                    {
                        WriteError(res, 400, "ORDER_CREATION_FAILED", errMsg);
                        return;
                    }
                    WriteJson(res, 201, orderRes);
                    return;
                }

                // Verified Payment Webhook (POST /api/payments/webhook)
                if (method == "POST" && path == "/api/payments/webhook")
                {
                    string rawBody = ReadRawBody(req);
                    string sig = req.Headers["X-PrintAway-Signature"] ?? "";
                    var body = _json.Deserialize<Dictionary<string, object>>(rawBody);
                    string eventId = GetString(body, "eventId", "");
                    string gatewayOrderId = GetString(body, "gatewayOrderId", "");
                    string gatewayPaymentId = GetString(body, "gatewayPaymentId", "upi_pay_verified");
                    long amountPaise = GetLong(body, "amountPaise", 0);
                    string outcome = GetString(body, "status", "success");

                    int status;
                    string errMsg;
                    try
                    {
                        var whRes = _repo.ProcessVerifiedPaymentWebhook(rawBody, sig, eventId, gatewayOrderId, gatewayPaymentId, amountPaise, outcome, out status, out errMsg);
                        if (whRes == null)
                        {
                            WriteError(res, status, "WEBHOOK_REJECTED", errMsg);
                            return;
                        }
                        WriteJson(res, 200, whRes);
                    }
                    catch (Exception ex)
                    {
                        WriteError(res, 400, "WEBHOOK_ERROR", ex.Message);
                    }
                    return;
                }

                // Simulated Signed UPI Webhook for Local MVP Demo (POST /api/payments/simulate-upi)
                if (method == "POST" && path == "/api/payments/simulate-upi")
                {
                    var body = ReadJsonBody(req);
                    string gatewayOrderId = GetString(body, "gatewayOrderId", "");
                    long amountPaise = GetLong(body, "amountPaise", 0);
                    string eventId = GetString(body, "eventId", "evt_" + PrintAwayRepository.GenerateRandomToken(8));

                    var payloadObj = new Dictionary<string, object>
                    {
                        { "eventId", eventId },
                        { "gatewayOrderId", gatewayOrderId },
                        { "gatewayPaymentId", "upi_sim_" + PrintAwayRepository.GenerateRandomToken(6) },
                        { "amountPaise", amountPaise },
                        { "status", "success" }
                    };
                    string rawJson = _json.Serialize(payloadObj);
                    string validSig = _repo.ComputeWebhookHmac(rawJson);

                    int status;
                    string errMsg;
                    var whRes = _repo.ProcessVerifiedPaymentWebhook(
                        rawJson, validSig, eventId, gatewayOrderId,
                        Convert.ToString(payloadObj["gatewayPaymentId"]), amountPaise, "success",
                        out status, out errMsg);

                    if (whRes == null)
                    {
                        WriteError(res, status, "SIMULATED_WEBHOOK_FAILED", errMsg);
                        return;
                    }
                    WriteJson(res, 200, whRes);
                    return;
                }

                // Customer Order Tracking by Non-Guessable Public Token (GET /api/orders/{public_token})
                if (method == "GET" && path.StartsWith("/api/orders/pub_"))
                {
                    string pubToken = path.Substring("/api/orders/".Length);
                    var tracked = _repo.GetOrderByPublicToken(pubToken);
                    if (tracked == null)
                    {
                        WriteError(res, 404, "ORDER_NOT_FOUND", "Order not found or invalid tracking token.");
                        return;
                    }
                    WriteJson(res, 200, tracked);
                    return;
                }

                // Admin Authentication
                if (method == "POST" && path == "/api/admin/login")
                {
                    if (!CheckRateLimit("admin_login", 15))
                    {
                        WriteError(res, 429, "RATE_LIMITED", "Too many login attempts.");
                        return;
                    }
                    var body = ReadJsonBody(req);
                    string username = GetString(body, "username", "");
                    string password = GetString(body, "password", "");
                    string errMsg;
                    var sess = _repo.AuthenticateAdmin(username, password, out errMsg);
                    if (sess == null)
                    {
                        WriteError(res, 401, "AUTH_FAILED", errMsg);
                        return;
                    }
                    res.Headers["Set-Cookie"] = "pa_admin_session=" + sess["sessionToken"] + "; Path=/; HttpOnly; SameSite=Strict";
                    WriteJson(res, 200, sess);
                    return;
                }

                // Protected Admin Endpoints (/api/admin/*)
                if (path.StartsWith("/api/admin/"))
                {
                    bool isMutating = method != "GET";
                    var adminSess = AuthenticateAdminRequest(req, isMutating);
                    if (adminSess == null)
                    {
                        WriteError(res, 401, "ADMIN_UNAUTHORIZED", "Valid admin authentication and CSRF token required.");
                        return;
                    }

                    if (method == "GET" && (path == "/api/admin/dashboard" || path == "/api/admin/orders"))
                    {
                        string stateFilter = req.QueryString["state"] ?? "all";
                        string search = req.QueryString["q"] ?? "";
                        WriteJson(res, 200, _repo.GetAdminDashboardSummary(stateFilter, search));
                        return;
                    }

                    if (method == "GET" && path == "/api/admin/printers")
                    {
                        var discovered = PdfAndPrinterEngine.DiscoverWindowsQueues();
                        var dbQueues = _repo.Db.Query("SELECT * FROM printer_queues ORDER BY id ASC;");
                        WriteJson(res, 200, new Dictionary<string, object>
                        {
                            { "activeVerifiedQueue", PrintAwayRepository.VerifiedXeroxQueueName },
                            { "secondaryExcludedQueue", PrintAwayRepository.SecondaryXeroxQueueName },
                            { "configuredQueues", dbQueues },
                            { "liveWindowsQueues", discovered }
                        });
                        return;
                    }

                    if (method == "POST" && path == "/api/admin/printers/controls")
                    {
                        var body = ReadJsonBody(req);
                        bool? intakePaused = body.ContainsKey("intakePaused") ? (bool?)GetBool(body, "intakePaused", false) : null;
                        string pauseReason = GetString(body, "pauseReason", "");
                        bool? supportsDuplex = body.ContainsKey("supportsDuplex") ? (bool?)GetBool(body, "supportsDuplex", false) : null;
                        bool? supportsColor = body.ContainsKey("supportsColor") ? (bool?)GetBool(body, "supportsColor", true) : null;
                        bool? supportsPageRange = body.ContainsKey("supportsPageRange") ? (bool?)GetBool(body, "supportsPageRange", true) : null;
                        string allowedPaperSizes = body.ContainsKey("allowedPaperSizes") ? GetString(body, "allowedPaperSizes", null) : null;
                        int? maxCopies = body.ContainsKey("maxCopies") ? (int?)GetInt(body, "maxCopies", 50) : null;
                        string activeQueueName = body.ContainsKey("activeQueueName") ? GetString(body, "activeQueueName", null) : null;
                        bool? printerReady = body.ContainsKey("printerReady") ? (bool?)GetBool(body, "printerReady", true) : null;

                        var updated = _repo.UpdateShopAndPrinterControls(
                            intakePaused, pauseReason, supportsDuplex, printerReady, Convert.ToString(adminSess["username"]),
                            supportsColor, supportsPageRange, allowedPaperSizes, maxCopies, activeQueueName);
                        WriteJson(res, 200, updated);
                        return;
                    }

                    if (method == "POST" && path == "/api/admin/printers/dispatch-mode")
                    {
                        var body = ReadJsonBody(req);
                        string mode = GetString(body, "mode", "physical");
                        _repo.SetDispatchMode(mode, Convert.ToString(adminSess["username"]));
                        WriteJson(res, 200, new Dictionary<string, object>
                        {
                            { "dispatchMode", _repo.GetDispatchMode() }
                        });
                        return;
                    }

                    if (method == "POST" && path == "/api/admin/pricing")
                    {
                        var body = ReadJsonBody(req);
                        double sRate = GetDouble(body, "simplexPerSideInr", 2.0);
                        double dRate = GetDouble(body, "duplexPerSideInr", 1.5);
                        double csRate = GetDouble(body, "colorSimplexPerSideInr", 10.0);
                        double cdRate = GetDouble(body, "colorDuplexPerSideInr", 8.0);
                        double minCharge = GetDouble(body, "minOrderChargeInr", 2.0);
                        double tax = GetDouble(body, "taxPercent", 0.0);

                        string errMsg;
                        var pr = _repo.UpdatePricingRule(sRate, dRate, minCharge, tax, Convert.ToString(adminSess["username"]), out errMsg, csRate, cdRate);
                        if (pr == null)
                        {
                            WriteError(res, 400, "INVALID_PRICING", errMsg);
                            return;
                        }
                        WriteJson(res, 200, pr);
                        return;
                    }

                    if (method == "POST" && path == "/api/admin/worker/step")
                    {
                        var cycleRes = _worker.RunSingleCycle();
                        WriteJson(res, 200, cycleRes);
                        return;
                    }

                    // Order actions: /api/admin/orders/{id}/{action}
                    if (method == "POST" && path.StartsWith("/api/admin/orders/"))
                    {
                        string[] segs = path.Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries);
                        if (segs.Length == 5)
                        {
                            long orderId;
                            if (!long.TryParse(segs[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out orderId))
                            {
                                WriteError(res, 400, "BAD_ORDER_ID", "Invalid order ID.");
                                return;
                            }
                            string action = segs[4].ToLowerInvariant();
                            var body = ReadJsonBody(req);
                            string reason = GetString(body, "reason", "");

                            if (action == "approve")
                            {
                                int st;
                                string err;
                                var appRes = _repo.AdminApproveOrder(orderId, adminSess, reason, out st, out err);
                                if (appRes == null)
                                {
                                    WriteError(res, st, "APPROVAL_REJECTED", err);
                                    return;
                                }
                                // Trigger worker check asynchronously or inline so approved order is picked up immediately (<1s)
                                ThreadPool.QueueUserWorkItem(_ => { try { _worker.RunSingleCycle(); } catch { } });
                                WriteJson(res, 200, appRes);
                                return;
                            }
                            if (action == "hold")
                            {
                                string err;
                                var holdRes = _repo.AdminHoldOrder(orderId, adminSess, reason, out err);
                                if (holdRes == null) { WriteError(res, 400, "HOLD_FAILED", err); return; }
                                WriteJson(res, 200, holdRes);
                                return;
                            }
                            if (action == "reject")
                            {
                                string err;
                                var rejRes = _repo.AdminRejectAndRefundOrder(orderId, adminSess, reason, out err);
                                if (rejRes == null) { WriteError(res, 400, "REJECT_FAILED", err); return; }
                                WriteJson(res, 200, rejRes);
                                return;
                            }
                            if (action == "cancel")
                            {
                                string err;
                                var canRes = _repo.AdminCancelBeforePrint(orderId, adminSess, reason, out err);
                                if (canRes == null) { WriteError(res, 400, "CANCEL_FAILED", err); return; }
                                WriteJson(res, 200, canRes);
                                return;
                            }
                            if (action == "reconcile")
                            {
                                string resAction = GetString(body, "resolutionAction", "confirm_printed");
                                string err;
                                var recRes = _repo.AdminReconcileOrder(orderId, adminSess, resAction, reason, out err);
                                if (recRes == null) { WriteError(res, 400, "RECONCILE_FAILED", err); return; }
                                WriteJson(res, 200, recRes);
                                return;
                            }
                            if (action == "ready")
                            {
                                string err;
                                var rdyRes = _repo.AdminMarkOrderReadyForPickup(orderId, adminSess, out err);
                                if (rdyRes == null) { WriteError(res, 400, "MARK_READY_FAILED", err); return; }
                                WriteJson(res, 200, rdyRes);
                                return;
                            }
                            if (action == "collected")
                            {
                                string pickupCode = GetString(body, "pickupCode", "");
                                string err;
                                var colRes = _repo.AdminMarkOrderCollected(orderId, pickupCode, adminSess, out err);
                                if (colRes == null) { WriteError(res, 400, "COLLECTION_FAILED", err); return; }
                                WriteJson(res, 200, colRes);
                                return;
                            }
                            if (action == "override-settings")
                            {
                                string pageRange = GetString(body, "pageRange", "all");
                                int copies = GetInt(body, "copies", 1);
                                string duplexMode = GetString(body, "duplexMode", "OneSided");
                                string colorMode = GetString(body, "colorMode", "bw");
                                string paperSize = GetString(body, "paperSize", "A4");
                                string orientation = GetString(body, "orientation", "auto");
                                int pagesPerSheet = GetInt(body, "pagesPerSheet", 1);
                                bool collate = GetBool(body, "collate", true);

                                string err;
                                var ovRes = _repo.AdminOverrideOrderPrintSettings(
                                    orderId, pageRange, copies, duplexMode, colorMode, paperSize, orientation, pagesPerSheet, collate,
                                    Convert.ToString(adminSess["username"]), out err);
                                if (ovRes == null) { WriteError(res, 400, "OVERRIDE_FAILED", err); return; }
                                WriteJson(res, 200, ovRes);
                                return;
                            }
                        }
                    }
                }

                // Authenticated Worker API Endpoints (/api/worker/*)
                if (path.StartsWith("/api/worker/"))
                {
                    string workerToken = req.Headers["X-Worker-Token"] ?? "";
                    if (!_repo.ValidateWorkerToken(workerToken))
                    {
                        WriteError(res, 401, "WORKER_UNAUTHORIZED", "Unpaired or unauthorized worker token.");
                        return;
                    }

                    if (method == "POST" && path == "/api/worker/claim")
                    {
                        var claim = _repo.ClaimNextQueuedJob(30);
                        WriteJson(res, 200, claim ?? new Dictionary<string, object> { { "claimed", false } });
                        return;
                    }

                    if (method == "GET" && path.StartsWith("/api/worker/jobs/") && path.EndsWith("/download"))
                    {
                        string[] segs = path.Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries);
                        long jobId = long.Parse(segs[3], CultureInfo.InvariantCulture);
                        string leaseTok = req.QueryString["leaseToken"] ?? "";
                        string dlErr;
                        byte[] pdfBytes = _repo.GetJobPdfBytesForWorker(jobId, leaseTok, out dlErr);
                        if (pdfBytes == null)
                        {
                            WriteError(res, 403, "DOWNLOAD_DENIED", dlErr);
                            return;
                        }
                        WriteBytes(res, 200, "application/pdf", pdfBytes);
                        return;
                    }

                    if (method == "POST" && path.StartsWith("/api/worker/jobs/") && path.EndsWith("/events"))
                    {
                        string[] segs = path.Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries);
                        long jobId = long.Parse(segs[3], CultureInfo.InvariantCulture);
                        var body = ReadJsonBody(req);
                        string leaseTok = GetString(body, "leaseToken", "");
                        string newState = GetString(body, "state", "");
                        int? spoolId = body.ContainsKey("windowsSpoolerJobId") && body["windowsSpoolerJobId"] != null
                            ? (int?)GetInt(body, "windowsSpoolerJobId", 1)
                            : null;
                        string details = GetString(body, "details", "");

                        string evErr;
                        var evRes = _repo.RecordWorkerJobEvent(jobId, leaseTok, newState, spoolId, details, out evErr);
                        if (evRes == null)
                        {
                            WriteError(res, 400, "WORKER_EVENT_REJECTED", evErr);
                            return;
                        }
                        WriteJson(res, 200, evRes);
                        return;
                    }
                }

                WriteError(res, 404, "NOT_FOUND", "Endpoint not found: " + method + " " + path);
            }
            catch (Exception ex)
            {
                WriteError(res, 500, "INTERNAL_SERVER_ERROR", ex.Message);
            }
        }

        private Dictionary<string, object> AuthenticateAdminRequest(HttpListenerRequest req, bool requireCsrf)
        {
            string token = req.Headers["X-Admin-Session"] ?? "";
            if (string.IsNullOrEmpty(token))
            {
                string cookieHeader = req.Headers["Cookie"] ?? "";
                foreach (string part in cookieHeader.Split(';'))
                {
                    string trimmed = part.Trim();
                    if (trimmed.StartsWith("pa_admin_session=", StringComparison.Ordinal))
                    {
                        token = trimmed.Substring("pa_admin_session=".Length);
                        break;
                    }
                }
            }
            string csrf = req.Headers["X-CSRF-Token"] ?? "";
            return _repo.ValidateAdminSession(token, csrf, requireCsrf);
        }

        private void ServeStaticHtml(HttpListenerResponse res, string fileName)
        {
            string fullPath = Path.Combine(_webRoot, fileName);
            if (!File.Exists(fullPath))
            {
                WriteError(res, 404, "UI_FILE_MISSING", "Missing UI file: " + fileName);
                return;
            }
            byte[] bytes = File.ReadAllBytes(fullPath);
            WriteBytes(res, 200, "text/html; charset=utf-8", bytes);
        }

        private static string ReadRawBody(HttpListenerRequest req)
        {
            using (var sr = new StreamReader(req.InputStream, Encoding.UTF8))
            {
                return sr.ReadToEnd();
            }
        }

        private Dictionary<string, object> ReadJsonBody(HttpListenerRequest req)
        {
            string raw = ReadRawBody(req);
            if (string.IsNullOrWhiteSpace(raw)) return new Dictionary<string, object>();
            return _json.Deserialize<Dictionary<string, object>>(raw) ?? new Dictionary<string, object>();
        }

        private void WriteBytes(HttpListenerResponse res, int statusCode, string contentType, byte[] bytes)
        {
            try
            {
                res.StatusCode = statusCode;
                res.ContentType = contentType;
                res.ContentLength64 = bytes.Length;
                res.OutputStream.Write(bytes, 0, bytes.Length);
                res.OutputStream.Close();
            }
            catch
            {
                try { res.Abort(); } catch { }
            }
        }

        private void WriteJson(HttpListenerResponse res, int statusCode, object payload)
        {
            try
            {
                string json = _json.Serialize(payload);
                byte[] bytes = new UTF8Encoding(false).GetBytes(json);
                WriteBytes(res, statusCode, "application/json; charset=utf-8", bytes);
            }
            catch
            {
                try { res.Abort(); } catch { }
            }
        }

        private void WriteError(HttpListenerResponse res, int statusCode, string errorCode, string message)
        {
            try
            {
                WriteJson(res, statusCode, new Dictionary<string, object>
                {
                    { "error", true },
                    { "errorCode", errorCode },
                    { "message", message }
                });
            }
            catch
            {
                try { res.Abort(); } catch { }
            }
        }

        private static string GetString(Dictionary<string, object> dict, string key, string defaultVal)
        {
            object val;
            if (dict != null && dict.TryGetValue(key, out val) && val != null)
                return Convert.ToString(val, CultureInfo.InvariantCulture);
            return defaultVal;
        }

        private static int GetInt(Dictionary<string, object> dict, string key, int defaultVal)
        {
            object val;
            if (dict != null && dict.TryGetValue(key, out val) && val != null)
            {
                int parsed;
                if (int.TryParse(Convert.ToString(val, CultureInfo.InvariantCulture), NumberStyles.Integer, CultureInfo.InvariantCulture, out parsed))
                    return parsed;
            }
            return defaultVal;
        }

        private static long GetLong(Dictionary<string, object> dict, string key, long defaultVal)
        {
            object val;
            if (dict != null && dict.TryGetValue(key, out val) && val != null)
            {
                long parsed;
                if (long.TryParse(Convert.ToString(val, CultureInfo.InvariantCulture), NumberStyles.Integer, CultureInfo.InvariantCulture, out parsed))
                    return parsed;
            }
            return defaultVal;
        }

        private static double GetDouble(Dictionary<string, object> dict, string key, double defaultVal)
        {
            object val;
            if (dict != null && dict.TryGetValue(key, out val) && val != null)
            {
                double parsed;
                if (double.TryParse(Convert.ToString(val, CultureInfo.InvariantCulture), NumberStyles.Float, CultureInfo.InvariantCulture, out parsed))
                    return parsed;
            }
            return defaultVal;
        }

        private static bool GetBool(Dictionary<string, object> dict, string key, bool defaultVal)
        {
            object val;
            if (dict != null && dict.TryGetValue(key, out val) && val != null)
            {
                if (val is bool) return (bool)val;
                bool parsed;
                if (bool.TryParse(Convert.ToString(val, CultureInfo.InvariantCulture), out parsed))
                    return parsed;
            }
            return defaultVal;
        }

        public void Dispose()
        {
            _running = false;
            try { _listener.Stop(); } catch { }
            try { _listener.Close(); } catch { }
        }
    }
}
