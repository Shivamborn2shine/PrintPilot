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
using PrintAway.SaaS;
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
        private readonly SupabaseSyncClient _supabaseSync;
        private readonly JavaScriptSerializer _json;
        private readonly ConcurrentDictionary<string, List<long>> _rateLimitBuckets;
        private volatile bool _running;
        private Thread _acceptThread;

        public PrintAwayHttpServer(int port, PrintAwayRepository repo, LocalPrintWorker worker, string webRoot)
            : this(port, repo, worker, webRoot, null)
        {
        }

        public PrintAwayHttpServer(int port, PrintAwayRepository repo, LocalPrintWorker worker, string webRoot, SupabaseSyncClient supabaseSync)
        {
            _port = port;
            _repo = repo;
            _worker = worker;
            _webRoot = webRoot;
            _supabaseSync = supabaseSync;
            _json = new JavaScriptSerializer { MaxJsonLength = 30 * 1024 * 1024 };
            _rateLimitBuckets = new ConcurrentDictionary<string, List<long>>();

            _listener = new HttpListener();
            // Bound strictly to 127.0.0.1 per PRD §8
            _listener.Prefixes.Add(string.Format(CultureInfo.InvariantCulture, "http://127.0.0.1:{0}/", port));
        }

        public int Port { get { return _port; } }
        public event Action<string> OnTunnelUrlRegistered;

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
                if (method == "GET" && (path == "/superadmin" || path == "/superadmin.html"))
                {
                    ServeStaticHtml(res, "superadmin.html");
                    return;
                }
                if (method == "GET" && path.StartsWith("/assets/"))
                {
                    ServeStaticAsset(res, path);
                    return;
                }

                // SaaS Quota Telemetry Status
                if (method == "GET" && path == "/api/saas/quota")
                {
                    string targetShop = req.QueryString["shop"];
                    bool forceRefresh = req.QueryString["refresh"] == "1";

                    if (string.IsNullOrWhiteSpace(targetShop))
                    {
                        try
                        {
                            var shopInfo = _repo.GetShopAndAvailabilityStatus();
                            if (shopInfo.ContainsKey("shopId") && shopInfo["shopId"] != null)
                            {
                                targetShop = Convert.ToString(shopInfo["shopId"]);
                            }
                        }
                        catch { }
                    }
                    if (string.IsNullOrWhiteSpace(targetShop)) targetShop = "midtownstationary";

                    if (_supabaseSync != null)
                    {
                        var q = forceRefresh ? _supabaseSync.RefreshQuotaForShop(targetShop) : _supabaseSync.GetQuotaForShop(targetShop);
                        WriteJson(res, 200, new Dictionary<string, object>
                        {
                            { "shopId", q.ShopId },
                            { "tier", q.Tier },
                            { "status", q.Status },
                            { "dailyQuota", q.DailyQuota },
                            { "pagesPrintedToday", q.PagesPrintedToday },
                            { "remainingPagesToday", q.RemainingPagesToday },
                            { "isQuotaExceeded", q.IsQuotaExceeded },
                            { "isHardBlocked", q.IsHardBlocked },
                            { "remotePaused", q.RemotePaused },
                            { "lastSyncUtc", q.LastSyncUtc }
                        });
                    }
                    else
                    {
                        WriteJson(res, 200, new Dictionary<string, object>
                        {
                            { "shopId", targetShop },
                            { "tier", "free" },
                            { "status", "active" },
                            { "dailyQuota", 10 },
                            { "pagesPrintedToday", 0 },
                            { "remainingPagesToday", 10 },
                            { "isQuotaExceeded", false },
                            { "isHardBlocked", false }
                        });
                    }
                    return;
                }

                // Super-Admin Fleet Proxy (Bypasses browser CORS & secret key restrictions securely on backend)
                if (method == "GET" && path == "/api/saas/superadmin/fleet")
                {
                    if (_supabaseSync == null || !_supabaseSync.IsConfigured)
                    {
                        WriteError(res, 503, "SUPABASE_NOT_CONFIGURED", "Supabase SaaS control plane is not configured in .env.");
                        return;
                    }

                    try
                    {
                        string todayStr = DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                        string shopsJson = _supabaseSync.GetFleetShopsJson();
                        string telemJson = _supabaseSync.GetDailyTelemetryJson(todayStr);

                        var shopsList = _json.Deserialize<List<Dictionary<string, object>>>(shopsJson) ?? new List<Dictionary<string, object>>();
                        var telemList = _json.Deserialize<List<Dictionary<string, object>>>(telemJson) ?? new List<Dictionary<string, object>>();

                        WriteJson(res, 200, new Dictionary<string, object>
                        {
                            { "shops", shopsList },
                            { "telemetry", telemList },
                            { "today", todayStr }
                        });
                    }
                    catch (Exception ex)
                    {
                        WriteError(res, 500, "FLEET_SYNC_ERROR", ex.Message);
                    }
                    return;
                }

                if (method == "PATCH" && path.StartsWith("/api/saas/superadmin/shops"))
                {
                    if (_supabaseSync == null || !_supabaseSync.IsConfigured)
                    {
                        WriteError(res, 503, "SUPABASE_NOT_CONFIGURED", "Supabase SaaS control plane is not configured.");
                        return;
                    }

                    string targetShopId = req.QueryString["shopId"] ?? "";
                    if (string.IsNullOrEmpty(targetShopId))
                    {
                        WriteError(res, 400, "MISSING_SHOP_ID", "shopId query parameter is required.");
                        return;
                    }

                    try
                    {
                        string bodyJson;
                        using (var reader = new StreamReader(req.InputStream, req.ContentEncoding ?? Encoding.UTF8))
                        {
                            bodyJson = reader.ReadToEnd();
                        }
                        string resp = _supabaseSync.UpdateShop(targetShopId, bodyJson);
                        WriteJson(res, 200, new Dictionary<string, object> { { "success", true } });
                    }
                    catch (Exception ex)
                    {
                        WriteError(res, 500, "UPDATE_SHOP_ERROR", ex.Message);
                    }
                    return;
                }

                if (method == "POST" && path == "/api/saas/superadmin/shops")
                {
                    if (_supabaseSync == null || !_supabaseSync.IsConfigured)
                    {
                        WriteError(res, 503, "SUPABASE_NOT_CONFIGURED", "Supabase SaaS control plane is not configured.");
                        return;
                    }

                    try
                    {
                        string bodyJson;
                        using (var reader = new StreamReader(req.InputStream, req.ContentEncoding ?? Encoding.UTF8))
                        {
                            bodyJson = reader.ReadToEnd();
                        }
                        string resp = _supabaseSync.CreateShop(bodyJson);
                        WriteJson(res, 201, new Dictionary<string, object> { { "success", true } });
                    }
                    catch (Exception ex)
                    {
                        WriteError(res, 500, "CREATE_SHOP_ERROR", ex.Message);
                    }
                    return;
                }

                // SaaS Upgrade: Create Razorpay Order (POST /api/saas/upgrade/create-order)
                if (method == "POST" && path == "/api/saas/upgrade/create-order")
                {
                    var body = ReadJsonBody(req);
                    string tier = GetString(body, "tier", "pro_annual");
                    long amountPaise = (tier == "pro_monthly") ? 24900 : 178800; // ₹249/mo or ₹1,788/yr
                    string rzpErr;
                    string receipt = "saas_" + tier + "_" + DateTime.UtcNow.Ticks;
                    string orderId = _repo.CreateRazorpayOrder(receipt, amountPaise, out rzpErr);
                    if (string.IsNullOrEmpty(orderId))
                    {
                        WriteError(res, 500, "RAZORPAY_ORDER_FAILED", rzpErr ?? "Failed to create Razorpay upgrade order.");
                        return;
                    }

                    var orderResp = new Dictionary<string, object>
                    {
                        { "razorpayOrderId", orderId },
                        { "razorpayKeyId", _repo.RazorpayKeyId },
                        { "amountPaise", amountPaise },
                        { "currency", "INR" },
                        { "tier", tier }
                    };
                    WriteJson(res, 200, orderResp);
                    return;
                }

                // SaaS Upgrade: Verify Razorpay Payment Callback (POST /api/saas/upgrade/verify)
                if (method == "POST" && path == "/api/saas/upgrade/verify")
                {
                    var body = ReadJsonBody(req);
                    string tier = GetString(body, "tier", "pro_annual");
                    string razorpayOrderId = GetString(body, "razorpayOrderId", "");
                    string razorpayPaymentId = GetString(body, "razorpayPaymentId", "");
                    string razorpaySignature = GetString(body, "razorpaySignature", "");

                    if (string.IsNullOrWhiteSpace(razorpayOrderId) ||
                        string.IsNullOrWhiteSpace(razorpayPaymentId) ||
                        string.IsNullOrWhiteSpace(razorpaySignature))
                    {
                        WriteError(res, 400, "MISSING_RAZORPAY_FIELDS", "razorpayOrderId, razorpayPaymentId, and razorpaySignature are required.");
                        return;
                    }

                    bool validSig = _repo.VerifyRazorpayClientSignature(razorpayOrderId, razorpayPaymentId, razorpaySignature);
                    if (!validSig)
                    {
                        WriteError(res, 400, "INVALID_RAZORPAY_SIGNATURE", "Razorpay HMAC-SHA256 signature verification failed.");
                        return;
                    }

                    double amountInr = (tier == "pro_monthly") ? 249.0 : 1788.0;
                    string currentShopId = "pilot";
                    try
                    {
                        var shopInfo = _repo.GetShopAndAvailabilityStatus();
                        if (shopInfo.ContainsKey("shopId") && shopInfo["shopId"] != null)
                        {
                            currentShopId = Convert.ToString(shopInfo["shopId"]);
                        }
                    }
                    catch { }

                    if (_supabaseSync != null && _supabaseSync.IsConfigured)
                    {
                        _supabaseSync.RecordSubscription(currentShopId, tier, amountInr, razorpayPaymentId);
                    }

                    WriteJson(res, 200, new Dictionary<string, object>
                    {
                        { "status", "success" },
                        { "tier", tier },
                        { "paymentId", razorpayPaymentId },
                        { "message", "Shop successfully upgraded to PrintAway Pro Unlimited!" }
                    });
                    return;
                }

                // Public Shop & Availability Status
                if (method == "GET" && path == "/api/shop/status")
                {
                    WriteJson(res, 200, _repo.GetShopAndAvailabilityStatus());
                    return;
                }

                // Station Tunnel Status & Registration (GET & POST /api/station/tunnel)
                if (path == "/api/station/tunnel")
                {
                    if (method == "POST")
                    {
                        var data = ReadJsonBody(req);
                        string tUrl = data != null && data.ContainsKey("tunnelUrl") ? Convert.ToString(data["tunnelUrl"]) : null;
                        if (!string.IsNullOrWhiteSpace(tUrl))
                        {
                            string cleanUrl = tUrl.Trim();
                            _repo.SetLiveTunnelUrl(cleanUrl);
                            var handler = OnTunnelUrlRegistered;
                            if (handler != null)
                            {
                                try { handler(cleanUrl); } catch { }
                            }
                        }
                        WriteJson(res, 200, new Dictionary<string, object>
                        {
                            { "ok", true },
                            { "liveTunnelUrl", _repo.GetLiveTunnelUrl() }
                        });
                        return;
                    }
                    else if (method == "GET")
                    {
                        WriteJson(res, 200, new Dictionary<string, object>
                        {
                            { "ok", true },
                            { "liveTunnelUrl", _repo.GetLiveTunnelUrl() }
                        });
                        return;
                    }
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
                    if (_supabaseSync != null && _supabaseSync.CurrentQuota != null && _supabaseSync.CurrentQuota.IsHardBlocked)
                    {
                        WriteError(res, 403, "DAILY_QUOTA_EXCEEDED", "This station has reached its Free tier daily limit of 10 pages. Please upgrade to PrintAway Pro for unlimited printing or check back tomorrow.");
                        return;
                    }

                    var body = ReadJsonBody(req);
                    string quoteToken = GetString(body, "quoteToken", "");
                    string customerName = GetString(body, "customerName", "Walk-in Customer");
                    string customerPhone = GetString(body, "customerPhone", "");
                    bool consent = GetBool(body, "consentAccepted", true);
                    string paymentMethod = GetString(body, "paymentMethod", "online");

                    string errMsg;
                    var orderRes = _repo.CreateOrderAndPaymentIntent(quoteToken, customerName, customerPhone, consent, out errMsg, null, paymentMethod);
                    if (orderRes == null)
                    {
                        WriteError(res, 400, "ORDER_CREATION_FAILED", errMsg);
                        return;
                    }
                    WriteJson(res, 201, orderRes);
                    return;
                }

                // Switch Order Payment Method to Cash at Counter (POST /api/payments/select-cash)
                if (method == "POST" && path == "/api/payments/select-cash")
                {
                    var body = ReadJsonBody(req);
                    long orderId = GetLong(body, "orderId", 0);
                    string publicToken = GetString(body, "publicToken", "");

                    string errMsg;
                    var cashRes = _repo.SelectCashAtCounterPayment(orderId, publicToken, out errMsg);
                    if (cashRes == null)
                    {
                        WriteError(res, 400, "CASH_SELECTION_FAILED", errMsg);
                        return;
                    }
                    WriteJson(res, 200, cashRes);
                    return;
                }

                // Verify Razorpay Client Payment Callback (POST /api/payments/verify-razorpay)
                if (method == "POST" && path == "/api/payments/verify-razorpay")
                {
                    var body = ReadJsonBody(req);
                    long orderId = GetLong(body, "orderId", 0);
                    string razorpayOrderId = GetString(body, "razorpayOrderId", "");
                    string razorpayPaymentId = GetString(body, "razorpayPaymentId", "");
                    string razorpaySignature = GetString(body, "razorpaySignature", "");

                    if (string.IsNullOrWhiteSpace(razorpayOrderId) ||
                        string.IsNullOrWhiteSpace(razorpayPaymentId) ||
                        string.IsNullOrWhiteSpace(razorpaySignature))
                    {
                        WriteError(res, 400, "MISSING_RAZORPAY_FIELDS", "razorpayOrderId, razorpayPaymentId, and razorpaySignature are required.");
                        return;
                    }

                    bool validSig = _repo.VerifyRazorpayClientSignature(razorpayOrderId, razorpayPaymentId, razorpaySignature);
                    if (!validSig)
                    {
                        WriteError(res, 400, "INVALID_RAZORPAY_SIGNATURE", "Razorpay HMAC-SHA256 signature verification failed.");
                        return;
                    }

                    var orderRow = _repo.Db.QuerySingle("SELECT * FROM orders WHERE id = @oid LIMIT 1;",
                        new Dictionary<string, object> { { "oid", orderId } });
                    if (orderRow == null && !string.IsNullOrWhiteSpace(razorpayOrderId))
                    {
                        orderRow = _repo.Db.QuerySingle("SELECT * FROM payments WHERE gateway_order_id = @goid LIMIT 1;",
                            new Dictionary<string, object> { { "goid", razorpayOrderId } });
                    }

                    long amountPaise = 0;
                    if (orderRow != null && orderRow.ContainsKey("quote_id"))
                    {
                        var quoteRow = _repo.Db.QuerySingle("SELECT total_amount_paise FROM quotes WHERE id = @qid;",
                            new Dictionary<string, object> { { "qid", orderRow["quote_id"] } });
                        if (quoteRow != null) amountPaise = Convert.ToInt64(quoteRow["total_amount_paise"]);
                    }
                    else if (orderRow != null && orderRow.ContainsKey("amount_paise"))
                    {
                        amountPaise = Convert.ToInt64(orderRow["amount_paise"]);
                    }

                    int whStatus;
                    string whErr;
                    string synthPayload = "razorpay_client_verified_" + razorpayPaymentId;
                    string synthSig = _repo.ComputeWebhookHmac(synthPayload);
                    var whRes = _repo.ProcessVerifiedPaymentWebhook(
                        synthPayload,
                        synthSig,
                        "rzp_client_" + razorpayPaymentId,
                        razorpayOrderId,
                        razorpayPaymentId,
                        amountPaise,
                        "success",
                        out whStatus,
                        out whErr);

                    if (whRes == null)
                    {
                        WriteError(res, whStatus, "VERIFICATION_FAILED", whErr);
                        return;
                    }

                    WriteJson(res, 200, new Dictionary<string, object>
                    {
                        { "status", "success" },
                        { "state", whRes["state"] },
                        { "orderCode", whRes["orderCode"] },
                        { "message", "Payment verified successfully via Razorpay" }
                    });
                    return;
                }

                // Verified Payment Webhook (POST /api/payments/webhook)
                if (method == "POST" && path == "/api/payments/webhook")
                {
                    string rawBody = ReadRawBody(req);
                    string rzpSig = req.Headers["X-Razorpay-Signature"];
                    string customSig = req.Headers["X-PrintAway-Signature"] ?? "";

                    // Official Razorpay server-to-server webhook
                    if (!string.IsNullOrWhiteSpace(rzpSig))
                    {
                        bool validRzp = _repo.VerifyRazorpayWebhookSignature(rawBody, rzpSig);
                        if (!validRzp)
                        {
                            WriteError(res, 401, "INVALID_RAZORPAY_SIGNATURE", "X-Razorpay-Signature HMAC verification failed.");
                            return;
                        }

                        var rzpBody = _json.Deserialize<Dictionary<string, object>>(rawBody);
                        string rzpEvent = GetString(rzpBody, "event", "");
                        if (rzpEvent == "order.paid" || rzpEvent == "payment.captured")
                        {
                            string goid = "";
                            string gpid = "";
                            long amtPaise = 0;

                            if (rzpBody.ContainsKey("payload") && rzpBody["payload"] is Dictionary<string, object>)
                            {
                                var payloadMap = (Dictionary<string, object>)rzpBody["payload"];
                                if (payloadMap.ContainsKey("payment") && payloadMap["payment"] is Dictionary<string, object>)
                                {
                                    var paymentMap = (Dictionary<string, object>)payloadMap["payment"];
                                    if (paymentMap.ContainsKey("entity") && paymentMap["entity"] is Dictionary<string, object>)
                                    {
                                        var entityMap = (Dictionary<string, object>)paymentMap["entity"];
                                        gpid = GetString(entityMap, "id", "");
                                        goid = GetString(entityMap, "order_id", "");
                                        amtPaise = GetLong(entityMap, "amount", 0);
                                    }
                                }
                                if (string.IsNullOrWhiteSpace(goid) && payloadMap.ContainsKey("order") && payloadMap["order"] is Dictionary<string, object>)
                                {
                                    var orderMap = (Dictionary<string, object>)payloadMap["order"];
                                    if (orderMap.ContainsKey("entity") && orderMap["entity"] is Dictionary<string, object>)
                                    {
                                        var entityMap = (Dictionary<string, object>)orderMap["entity"];
                                        goid = GetString(entityMap, "id", "");
                                        if (amtPaise == 0) amtPaise = GetLong(entityMap, "amount", 0);
                                    }
                                }
                            }

                            string rzpEventId = "rzp_wh_" + gpid;
                            int rzpStatus;
                            string rzpErrMsg;
                            string synthPayload = "rzp_wh_verified_" + gpid;
                            string synthSig = _repo.ComputeWebhookHmac(synthPayload);
                            var whRes = _repo.ProcessVerifiedPaymentWebhook(synthPayload, synthSig, rzpEventId, goid, gpid, amtPaise, "success", out rzpStatus, out rzpErrMsg);
                            if (whRes == null)
                            {
                                WriteError(res, rzpStatus, "WEBHOOK_REJECTED", rzpErrMsg);
                                return;
                            }
                            WriteJson(res, 200, whRes);
                            return;
                        }

                        WriteJson(res, 200, new Dictionary<string, object> { { "status", "ignored" }, { "event", rzpEvent } });
                        return;
                    }

                    // Standard PrintAway HMAC webhook
                    var body = _json.Deserialize<Dictionary<string, object>>(rawBody);
                    string eventId = GetString(body, "eventId", "");
                    string gatewayOrderId = GetString(body, "gatewayOrderId", "");
                    string gatewayPaymentId = GetString(body, "gatewayPaymentId", "upi_pay_verified");
                    long amountPaise = GetLong(body, "amountPaise", 0);
                    string outcome = GetString(body, "status", "success");

                    int statusWh;
                    string errMsgWh;
                    try
                    {
                        var whRes = _repo.ProcessVerifiedPaymentWebhook(rawBody, customSig, eventId, gatewayOrderId, gatewayPaymentId, amountPaise, outcome, out statusWh, out errMsgWh);
                        if (whRes == null)
                        {
                            WriteError(res, statusWh, "WEBHOOK_REJECTED", errMsgWh);
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
                    string shopId = GetString(body, "shopId", "");
                    string errMsg;
                    var sess = _repo.AuthenticateAdmin(username, password, out errMsg, shopId);
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
                        string dateFilter = req.QueryString["date"] ?? "today";
                        string shopFilter = req.QueryString["shop"] ?? null;
                        WriteJson(res, 200, _repo.GetAdminDashboardSummary(stateFilter, search, dateFilter, shopFilter));
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
                        string bwQueueName = body.ContainsKey("bwQueueName") ? GetString(body, "bwQueueName", "") : null;
                        string colorQueueName = body.ContainsKey("colorQueueName") ? GetString(body, "colorQueueName", "") : null;
                        string shopId = body.ContainsKey("shopId") ? GetString(body, "shopId", null) : null;
                        string shopName = body.ContainsKey("shopName") ? GetString(body, "shopName", null) : null;
                        bool? printerReady = body.ContainsKey("printerReady") ? (bool?)GetBool(body, "printerReady", true) : null;

                        var updated = _repo.UpdateShopAndPrinterControls(
                            intakePaused, pauseReason, supportsDuplex, printerReady, Convert.ToString(adminSess["username"]),
                            supportsColor, supportsPageRange, allowedPaperSizes, maxCopies, activeQueueName, bwQueueName, colorQueueName, shopId, shopName);
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

                    if (method == "POST" && path == "/api/admin/printers/test-blank")
                    {
                        var status = _repo.GetShopAndAvailabilityStatus();
                        string queue = (status != null && status.ContainsKey("activeQueueName") && !string.IsNullOrWhiteSpace(Convert.ToString(status["activeQueueName"])))
                            ? Convert.ToString(status["activeQueueName"])
                            : "Xerox B1025 Multifunction Printer";
                        string dispMode = _repo.GetDispatchMode();
                        bool simulate = string.Equals(dispMode, "simulation", StringComparison.OrdinalIgnoreCase);

                        byte[] blankPdf = PdfAndPrinterEngine.GenerateSamplePdfBytes(1, "PrintAway 1 Blank Page Test");
                        var dispatch = PdfAndPrinterEngine.DispatchPrintJob(
                            queue,
                            "TEST-BLANK",
                            blankPdf,
                            new List<int> { 1 },
                            1,
                            "OneSided",
                            simulate,
                            "bw",
                            "A4",
                            "portrait",
                            1,
                            false
                        );

                        _repo.LogAudit(null, "ADMIN", Convert.ToString(adminSess["username"]), "PRINT_TEST_BLANK_PAGE", null, null,
                            string.Format(CultureInfo.InvariantCulture, "Queue: {0}, JobId: {1}, Simulated: {2}, Error: {3}",
                                queue, dispatch.WindowsSpoolerJobId, dispatch.SimulatedMode, dispatch.ErrorMessage));

                        WriteJson(res, 200, new Dictionary<string, object>
                        {
                            { "success", dispatch.Dispatched },
                            { "targetQueue", queue },
                            { "windowsSpoolerJobId", dispatch.WindowsSpoolerJobId },
                            { "simulatedMode", dispatch.SimulatedMode },
                            { "errorMessage", dispatch.ErrorMessage },
                            { "message", dispatch.Dispatched
                                ? string.Format(CultureInfo.InvariantCulture, "1 blank test page successfully sent to {0} (Spooler Job #{1})", queue, dispatch.WindowsSpoolerJobId)
                                : ("Test print failed: " + dispatch.ErrorMessage) }
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

                    if (method == "POST" && path == "/api/admin/payments/config")
                    {
                        var body = ReadJsonBody(req);
                        string rzpKeyId = body.ContainsKey("razorpayKeyId") ? GetString(body, "razorpayKeyId", "") : null;
                        string rzpKeySecret = body.ContainsKey("razorpayKeySecret") ? GetString(body, "razorpayKeySecret", "") : null;
                        string rzpLinkedAcc = body.ContainsKey("razorpayLinkedAccountId") ? GetString(body, "razorpayLinkedAccountId", "") : null;
                        string upiVpa = body.ContainsKey("upiVpa") ? GetString(body, "upiVpa", "printaway@upi") : null;

                        var updatedStatus = _repo.UpdatePaymentSettlementConfig(
                            rzpKeyId, rzpKeySecret, rzpLinkedAcc, upiVpa, Convert.ToString(adminSess["username"]));
                        WriteJson(res, 200, updatedStatus);
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
                                string targetQueue = GetString(body, "targetQueueName", null);
                                var appRes = _repo.AdminApproveOrder(orderId, adminSess, reason, out st, out err, targetQueue);
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
                                    Convert.ToString(adminSess["username"]), out err, GetString(body, "targetQueueName", null));
                                if (ovRes == null) { WriteError(res, 400, "OVERRIDE_FAILED", err); return; }
                                WriteJson(res, 200, ovRes);
                                return;
                            }
                        }
                    }

                    if (method == "POST" && path == "/api/admin/change-password")
                    {
                        var body = ReadJsonBody(req);
                        string curPass = GetString(body, "currentPassword", "");
                        string newPass = GetString(body, "newPassword", "");
                        string uName = Convert.ToString(adminSess["username"]);
                        string err;
                        if (!_repo.ChangeAdminPassword(uName, curPass, newPass, out err))
                        {
                            WriteError(res, 400, "PASSWORD_CHANGE_FAILED", err);
                            return;
                        }
                        WriteJson(res, 200, new Dictionary<string, object>
                        {
                            { "success", true },
                            { "message", "Store password updated successfully." }
                        });
                        return;
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
                fullPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "web", fileName);
            }
            if (!File.Exists(fullPath))
            {
                fullPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, fileName);
            }

            if (File.Exists(fullPath))
            {
                byte[] bytes = File.ReadAllBytes(fullPath);
                WriteBytes(res, 200, "text/html; charset=utf-8", bytes);
                return;
            }

            // Graceful fallback: Redirect browser to cloud-hosted frontend on Netlify
            try
            {
                var status = _repo.GetShopAndAvailabilityStatus();
                string shopId = (status != null && status.ContainsKey("shopId") && status["shopId"] != null)
                    ? Convert.ToString(status["shopId"])
                    : "midtownstationary";
                string liveTunnel = _repo.GetLiveTunnelUrl();
                string targetPath = (fileName == "admin.html") ? "/admin" : "";
                string targetUrl = string.IsNullOrEmpty(liveTunnel)
                    ? string.Format(CultureInfo.InvariantCulture, "https://printitaway.netlify.app{0}?shop={1}", targetPath, shopId)
                    : string.Format(CultureInfo.InvariantCulture, "https://printitaway.netlify.app{0}?shop={1}&station={2}", targetPath, shopId, Uri.EscapeDataString(liveTunnel));

                res.StatusCode = 302;
                res.Headers["Location"] = targetUrl;
                string redirectHtml = string.Format(CultureInfo.InvariantCulture,
                    "<!DOCTYPE html><html><head><meta charset='utf-8'><title>Redirecting to PrintAway...</title>" +
                    "<meta http-equiv='refresh' content='0;url={0}'></head>" +
                    "<body style='font-family:Segoe UI,sans-serif;text-align:center;padding:50px;background:#f8fafc;color:#1e293b;'>" +
                    "<h2>PrintAway Station is Online</h2>" +
                    "<p>Redirecting to <a href='{0}' style='color:#2563eb;font-weight:bold;'>PrintAway Cloud Portal</a>...</p>" +
                    "<script>window.location.href='{0}';</script></body></html>",
                    targetUrl);
                byte[] redirectBytes = Encoding.UTF8.GetBytes(redirectHtml);
                WriteBytes(res, 302, "text/html; charset=utf-8", redirectBytes);
                return;
            }
            catch
            {
                string safeHtml = "<!DOCTYPE html><html><head><meta charset='utf-8'><title>PrintAway Station</title></head>" +
                    "<body style='font-family:Segoe UI,sans-serif;text-align:center;padding:50px;background:#f8fafc;'>" +
                    "<h2>PrintAway Hardware Station</h2>" +
                    "<p>Local API server is active on port 8080.</p>" +
                    "<p><a href='https://printitaway.netlify.app/?shop=midtownstationary' style='color:#2563eb;font-weight:bold;'>Open Customer Storefront</a> &nbsp;|&nbsp; " +
                    "<a href='https://printitaway.netlify.app/admin?shop=midtownstationary' style='color:#2563eb;font-weight:bold;'>Open Counter Admin</a></p>" +
                    "</body></html>";
                byte[] safeBytes = Encoding.UTF8.GetBytes(safeHtml);
                WriteBytes(res, 200, "text/html; charset=utf-8", safeBytes);
            }
        }

        private void ServeStaticAsset(HttpListenerResponse res, string urlPath)
        {
            string cleanPath = urlPath.TrimStart('/').Replace('/', Path.DirectorySeparatorChar);
            string fullPath = Path.Combine(_webRoot, cleanPath);
            if (!File.Exists(fullPath))
            {
                fullPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "web", cleanPath);
            }
            if (!File.Exists(fullPath))
            {
                fullPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, cleanPath);
            }

            if (!File.Exists(fullPath))
            {
                WriteError(res, 404, "ASSET_NOT_FOUND", "Asset not found: " + urlPath);
                return;
            }

            string ext = Path.GetExtension(fullPath).ToLowerInvariant();
            string mime = "application/octet-stream";
            if (ext == ".svg") mime = "image/svg+xml";
            else if (ext == ".png") mime = "image/png";
            else if (ext == ".jpg" || ext == ".jpeg") mime = "image/jpeg";
            else if (ext == ".css") mime = "text/css; charset=utf-8";
            else if (ext == ".js") mime = "application/javascript; charset=utf-8";
            else if (ext == ".ico") mime = "image/x-icon";

            byte[] bytes = File.ReadAllBytes(fullPath);
            WriteBytes(res, 200, mime, bytes);
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
