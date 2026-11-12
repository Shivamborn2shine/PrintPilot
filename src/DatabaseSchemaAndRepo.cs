using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Web.Script.Serialization;
using PrintAway.Engine;

namespace PrintAway.Storage
{
    public sealed class PrintAwayRepository
    {
        public const string VerifiedXeroxQueueName = "Xerox B1025 Multifunction Printer PCL6";
        public const string SecondaryXeroxQueueName = "Xerox B1025 Multifunction Printer";

        private readonly SqliteDatabase _db;
        private readonly string _vaultDirectory;
        private readonly string _webhookSecret;
        private readonly string _workerSharedToken;

        public PrintAwayRepository(SqliteDatabase db, string vaultDirectory, string webhookSecret, string workerSharedToken)
        {
            _db = db;
            _vaultDirectory = vaultDirectory;
            _webhookSecret = webhookSecret;
            _workerSharedToken = workerSharedToken;

            if (!Directory.Exists(_vaultDirectory))
            {
                Directory.CreateDirectory(_vaultDirectory);
            }

            InitializeSchemaAndSeed();
        }

        public SqliteDatabase Db { get { return _db; } }
        public string VaultDirectory { get { return _vaultDirectory; } }
        public string WebhookSecret { get { return _webhookSecret; } }
        public string WorkerSharedToken { get { return _workerSharedToken; } }

        private string _razorpayKeyId;
        private string _razorpayKeySecret;
        private string _razorpayWebhookSecret;
        private string _razorpayLinkedAccountId;

        public void ConfigureRazorpay(string keyId, string keySecret, string webhookSecret, string linkedAccountId = null)
        {
            _razorpayKeyId = !string.IsNullOrWhiteSpace(keyId) ? keyId.Trim() : null;
            _razorpayKeySecret = !string.IsNullOrWhiteSpace(keySecret) ? keySecret.Trim() : null;
            _razorpayWebhookSecret = !string.IsNullOrWhiteSpace(webhookSecret) ? webhookSecret.Trim() : null;
            if (linkedAccountId != null)
            {
                _razorpayLinkedAccountId = !string.IsNullOrWhiteSpace(linkedAccountId) ? linkedAccountId.Trim() : null;
            }
        }

        public string RazorpayKeyId { get { return _razorpayKeyId; } }
        public string RazorpayLinkedAccountId { get { return _razorpayLinkedAccountId; } }
        public bool HasRazorpayConfigured
        {
            get { return !string.IsNullOrWhiteSpace(_razorpayKeyId) && !string.IsNullOrWhiteSpace(_razorpayKeySecret); }
        }
        public string RazorpayMode
        {
            get
            {
                if (!HasRazorpayConfigured) return "disabled";
                if (_razorpayKeyId.StartsWith("rzp_live_", StringComparison.OrdinalIgnoreCase)) return "live";
                return "test";
            }
        }

        public string CreateRazorpayOrder(string receipt, long amountPaise, out string error, string shopId = null, string shopName = null)
        {
            error = null;
            if (!HasRazorpayConfigured)
            {
                error = "Razorpay is not configured.";
                return null;
            }

            try
            {
                ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12 | SecurityProtocolType.Tls11 | SecurityProtocolType.Tls;
                var request = (HttpWebRequest)WebRequest.Create("https://api.razorpay.com/v1/orders");
                request.Method = "POST";
                request.ContentType = "application/json";

                string auth = Convert.ToBase64String(Encoding.ASCII.GetBytes(_razorpayKeyId + ":" + _razorpayKeySecret));
                request.Headers["Authorization"] = "Basic " + auth;

                var serializer = new JavaScriptSerializer();
                var notes = new Dictionary<string, object>
                {
                    { "order_code", receipt },
                    { "shop_id", !string.IsNullOrWhiteSpace(shopId) ? shopId : "pilot" },
                    { "shop_name", !string.IsNullOrWhiteSpace(shopName) ? shopName : "PrintAway Store" }
                };
                var payload = new Dictionary<string, object>
                {
                    { "amount", amountPaise },
                    { "currency", "INR" },
                    { "receipt", receipt },
                    { "notes", notes }
                };

                // If Razorpay Route Linked Account ID (acc_...) is configured for this store, automatically route settlement
                if (!string.IsNullOrWhiteSpace(_razorpayLinkedAccountId) && _razorpayLinkedAccountId.StartsWith("acc_", StringComparison.OrdinalIgnoreCase))
                {
                    payload["transfers"] = new object[]
                    {
                        new Dictionary<string, object>
                        {
                            { "account", _razorpayLinkedAccountId },
                            { "amount", amountPaise },
                            { "currency", "INR" },
                            { "notes", notes },
                            { "on_hold", 0 }
                        }
                    };
                }

                string json = serializer.Serialize(payload);
                byte[] bytes = Encoding.UTF8.GetBytes(json);
                request.ContentLength = bytes.Length;

                using (var stream = request.GetRequestStream())
                {
                    stream.Write(bytes, 0, bytes.Length);
                }

                using (var response = (HttpWebResponse)request.GetResponse())
                using (var reader = new StreamReader(response.GetResponseStream(), Encoding.UTF8))
                {
                    string respJson = reader.ReadToEnd();
                    var respObj = serializer.Deserialize<Dictionary<string, object>>(respJson);
                    if (respObj != null && respObj.ContainsKey("id"))
                    {
                        return Convert.ToString(respObj["id"]);
                    }
                }
            }
            catch (WebException wex)
            {
                if (wex.Response != null)
                {
                    using (var reader = new StreamReader(wex.Response.GetResponseStream(), Encoding.UTF8))
                    {
                        error = reader.ReadToEnd();
                    }
                }
                else
                {
                    error = wex.Message;
                }
            }
            catch (Exception ex)
            {
                error = ex.Message;
            }
            return null;
        }

        public bool VerifyRazorpayClientSignature(string razorpayOrderId, string razorpayPaymentId, string razorpaySignature)
        {
            if (string.IsNullOrWhiteSpace(_razorpayKeySecret) ||
                string.IsNullOrWhiteSpace(razorpayOrderId) ||
                string.IsNullOrWhiteSpace(razorpayPaymentId) ||
                string.IsNullOrWhiteSpace(razorpaySignature))
            {
                return false;
            }

            string data = razorpayOrderId.Trim() + "|" + razorpayPaymentId.Trim();
            using (var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(_razorpayKeySecret)))
            {
                byte[] hash = hmac.ComputeHash(Encoding.UTF8.GetBytes(data));
                var sb = new StringBuilder();
                foreach (byte b in hash)
                {
                    sb.Append(b.ToString("x2"));
                }
                string expectedSig = sb.ToString();
                return CryptographicEquals(expectedSig, razorpaySignature.Trim());
            }
        }

        public bool VerifyRazorpayWebhookSignature(string rawBody, string providedSignatureHex)
        {
            string secret = !string.IsNullOrWhiteSpace(_razorpayWebhookSecret) ? _razorpayWebhookSecret : _razorpayKeySecret;
            if (string.IsNullOrWhiteSpace(secret) || string.IsNullOrWhiteSpace(providedSignatureHex))
            {
                return false;
            }

            using (var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret)))
            {
                byte[] hash = hmac.ComputeHash(Encoding.UTF8.GetBytes(rawBody));
                var sb = new StringBuilder();
                foreach (byte b in hash)
                {
                    sb.Append(b.ToString("x2"));
                }
                string expectedSig = sb.ToString();
                return CryptographicEquals(expectedSig, providedSignatureHex.Trim());
            }
        }

        public static string UtcNowIso(double offsetSeconds = 0)
        {
            return DateTime.UtcNow.AddSeconds(offsetSeconds).ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);
        }

        public static string ToIstDisplay(string utcIso)
        {
            if (string.IsNullOrWhiteSpace(utcIso)) return "";
            DateTime dt;
            if (DateTime.TryParse(utcIso, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out dt))
            {
                DateTime ist = dt.AddHours(5).AddMinutes(30);
                return ist.ToString("yyyy-MM-dd hh:mm:ss tt 'IST'", CultureInfo.InvariantCulture);
            }
            return utcIso;
        }

        private void InitializeSchemaAndSeed()
        {
            string ddl = @"
CREATE TABLE IF NOT EXISTS shops (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    slug TEXT NOT NULL UNIQUE,
    name TEXT NOT NULL,
    tagline TEXT NOT NULL,
    intake_paused INTEGER NOT NULL DEFAULT 0,
    pause_reason TEXT,
    operating_hours TEXT NOT NULL DEFAULT '09:00 AM - 09:00 PM IST',
    turnaround_text TEXT NOT NULL DEFAULT '5-10 mins after staff approval',
    retention_hours INTEGER NOT NULL DEFAULT 24,
    min_free_disk_mb INTEGER NOT NULL DEFAULT 1024,
    created_at_utc TEXT NOT NULL
);

CREATE TABLE IF NOT EXISTS users (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    username TEXT NOT NULL UNIQUE,
    display_name TEXT NOT NULL,
    role TEXT NOT NULL,
    password_hash TEXT NOT NULL,
    password_salt TEXT NOT NULL,
    is_active INTEGER NOT NULL DEFAULT 1,
    created_at_utc TEXT NOT NULL
);

CREATE TABLE IF NOT EXISTS admin_sessions (
    token TEXT PRIMARY KEY,
    csrf_token TEXT NOT NULL,
    user_id INTEGER NOT NULL,
    username TEXT NOT NULL,
    role TEXT NOT NULL,
    created_at_utc TEXT NOT NULL,
    expires_at_utc TEXT NOT NULL
);

CREATE TABLE IF NOT EXISTS stations (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    shop_id INTEGER NOT NULL,
    station_code TEXT NOT NULL UNIQUE,
    display_name TEXT NOT NULL,
    os_summary TEXT NOT NULL,
    last_heartbeat_utc TEXT,
    free_ram_mb INTEGER NOT NULL DEFAULT 0,
    free_disk_mb INTEGER NOT NULL DEFAULT 0,
    spooler_running INTEGER NOT NULL DEFAULT 1,
    printer_ready INTEGER NOT NULL DEFAULT 1,
    dispatch_mode TEXT NOT NULL DEFAULT 'physical',
    status_message TEXT
);

CREATE TABLE IF NOT EXISTS printers (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    shop_id INTEGER NOT NULL,
    station_id INTEGER NOT NULL,
    device_code TEXT NOT NULL UNIQUE,
    model_name TEXT NOT NULL,
    port_name TEXT NOT NULL,
    supports_color INTEGER NOT NULL DEFAULT 0,
    supports_a4 INTEGER NOT NULL DEFAULT 1,
    supports_duplex INTEGER NOT NULL DEFAULT 0,
    supports_page_range INTEGER NOT NULL DEFAULT 1,
    is_enabled INTEGER NOT NULL DEFAULT 1,
    created_at_utc TEXT NOT NULL
);

CREATE TABLE IF NOT EXISTS printer_queues (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    printer_id INTEGER NOT NULL,
    windows_queue_name TEXT NOT NULL UNIQUE,
    driver_name TEXT NOT NULL,
    port_name TEXT NOT NULL,
    is_active_selected INTEGER NOT NULL DEFAULT 0,
    is_virtual_excluded INTEGER NOT NULL DEFAULT 0,
    verified_status TEXT NOT NULL,
    notes TEXT,
    updated_at_utc TEXT NOT NULL
);

CREATE TABLE IF NOT EXISTS price_rules (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    shop_id INTEGER NOT NULL,
    version INTEGER NOT NULL,
    simplex_per_side_inr REAL NOT NULL,
    duplex_per_side_inr REAL NOT NULL,
    min_order_charge_inr REAL NOT NULL,
    tax_percent REAL NOT NULL DEFAULT 0,
    is_active INTEGER NOT NULL DEFAULT 1,
    created_by TEXT NOT NULL,
    created_at_utc TEXT NOT NULL
);

CREATE TABLE IF NOT EXISTS files (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    file_token TEXT NOT NULL UNIQUE,
    original_filename_redacted TEXT NOT NULL,
    storage_relative_path TEXT NOT NULL,
    sha256_hex TEXT NOT NULL,
    size_bytes INTEGER NOT NULL,
    page_count INTEGER NOT NULL,
    scan_status TEXT NOT NULL,
    retention_deadline_utc TEXT NOT NULL,
    is_deleted INTEGER NOT NULL DEFAULT 0,
    deleted_at_utc TEXT,
    created_at_utc TEXT NOT NULL
);

CREATE TABLE IF NOT EXISTS quotes (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    quote_token TEXT NOT NULL UNIQUE,
    shop_id INTEGER NOT NULL,
    file_id INTEGER NOT NULL,
    price_rule_id INTEGER NOT NULL,
    price_rule_version INTEGER NOT NULL,
    document_pages INTEGER NOT NULL,
    selected_pages INTEGER NOT NULL,
    page_range_spec TEXT NOT NULL,
    copies INTEGER NOT NULL,
    duplex_mode TEXT NOT NULL,
    color_mode TEXT NOT NULL DEFAULT 'bw',
    paper_size TEXT NOT NULL DEFAULT 'A4',
    orientation TEXT NOT NULL DEFAULT 'auto',
    pages_per_sheet INTEGER NOT NULL DEFAULT 1,
    is_collated INTEGER NOT NULL DEFAULT 1,
    printed_sides INTEGER NOT NULL,
    physical_sheets INTEGER NOT NULL,
    rate_per_side_inr REAL NOT NULL,
    base_charge_inr REAL NOT NULL,
    min_charge_delta_inr REAL NOT NULL,
    tax_amount_inr REAL NOT NULL,
    total_amount_inr REAL NOT NULL,
    total_amount_paise INTEGER NOT NULL,
    expires_at_utc TEXT NOT NULL,
    created_at_utc TEXT NOT NULL
);

CREATE TABLE IF NOT EXISTS orders (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    order_code TEXT NOT NULL UNIQUE,
    public_token TEXT NOT NULL UNIQUE,
    pickup_code TEXT NOT NULL,
    shop_id INTEGER NOT NULL,
    file_id INTEGER NOT NULL,
    quote_id INTEGER NOT NULL,
    customer_name TEXT,
    customer_phone TEXT,
    consent_accepted INTEGER NOT NULL DEFAULT 1,
    state TEXT NOT NULL,
    payment_status TEXT NOT NULL DEFAULT 'unpaid',
    target_printer_id INTEGER NOT NULL,
    target_queue_name TEXT NOT NULL,
    hold_or_reject_reason TEXT,
    created_at_utc TEXT NOT NULL,
    updated_at_utc TEXT NOT NULL
);

CREATE TABLE IF NOT EXISTS payments (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    order_id INTEGER NOT NULL,
    gateway_provider TEXT NOT NULL,
    gateway_order_id TEXT NOT NULL UNIQUE,
    gateway_payment_id TEXT,
    webhook_event_id TEXT UNIQUE,
    amount_paise INTEGER NOT NULL,
    currency TEXT NOT NULL DEFAULT 'INR',
    status TEXT NOT NULL,
    verified_by_webhook INTEGER NOT NULL DEFAULT 0,
    webhook_Calls_count INTEGER NOT NULL DEFAULT 0,
    created_at_utc TEXT NOT NULL,
    verified_at_utc TEXT
);

CREATE TABLE IF NOT EXISTS refunds (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    order_id INTEGER NOT NULL,
    payment_id INTEGER NOT NULL,
    amount_paise INTEGER NOT NULL,
    reason TEXT NOT NULL,
    status TEXT NOT NULL,
    initiated_by TEXT NOT NULL,
    created_at_utc TEXT NOT NULL
);

CREATE TABLE IF NOT EXISTS approvals (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    order_id INTEGER NOT NULL,
    admin_user_id INTEGER NOT NULL,
    admin_username TEXT NOT NULL,
    decision TEXT NOT NULL,
    reason TEXT NOT NULL,
    target_queue_name TEXT NOT NULL,
    amount_paise_snapshot INTEGER NOT NULL,
    printed_sides_snapshot INTEGER NOT NULL,
    created_at_utc TEXT NOT NULL
);

CREATE TABLE IF NOT EXISTS print_jobs (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    order_id INTEGER NOT NULL UNIQUE,
    idempotency_key TEXT NOT NULL UNIQUE,
    station_id INTEGER NOT NULL,
    printer_id INTEGER NOT NULL,
    target_queue_name TEXT NOT NULL,
    state TEXT NOT NULL,
    lease_token TEXT,
    lease_expires_utc TEXT,
    attempts INTEGER NOT NULL DEFAULT 0,
    windows_spooler_job_id INTEGER,
    last_error TEXT,
    created_at_utc TEXT NOT NULL,
    updated_at_utc TEXT NOT NULL
);

CREATE TABLE IF NOT EXISTS audit_events (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    order_id INTEGER,
    actor_type TEXT NOT NULL,
    actor_name TEXT NOT NULL,
    event_type TEXT NOT NULL,
    from_state TEXT,
    to_state TEXT,
    details TEXT NOT NULL,
    created_at_utc TEXT NOT NULL
);
";
            _db.ExecuteNonQuery(ddl);
            try { _db.ExecuteNonQuery("ALTER TABLE stations ADD COLUMN dispatch_mode TEXT NOT NULL DEFAULT 'physical';"); } catch { }
            try { _db.ExecuteNonQuery("ALTER TABLE printers ADD COLUMN allowed_paper_sizes TEXT NOT NULL DEFAULT 'A4,Legal,Letter';"); } catch { }
            try { _db.ExecuteNonQuery("ALTER TABLE printers ADD COLUMN max_copies INTEGER NOT NULL DEFAULT 50;"); } catch { }
            try { _db.ExecuteNonQuery("ALTER TABLE price_rules ADD COLUMN color_simplex_per_side_inr REAL NOT NULL DEFAULT 10.00;"); } catch { }
            try { _db.ExecuteNonQuery("ALTER TABLE price_rules ADD COLUMN color_duplex_per_side_inr REAL NOT NULL DEFAULT 8.00;"); } catch { }
            try { _db.ExecuteNonQuery("ALTER TABLE quotes ADD COLUMN color_mode TEXT NOT NULL DEFAULT 'bw';"); } catch { }
            try { _db.ExecuteNonQuery("ALTER TABLE quotes ADD COLUMN paper_size TEXT NOT NULL DEFAULT 'A4';"); } catch { }
            try { _db.ExecuteNonQuery("ALTER TABLE quotes ADD COLUMN orientation TEXT NOT NULL DEFAULT 'auto';"); } catch { }
            try { _db.ExecuteNonQuery("ALTER TABLE quotes ADD COLUMN pages_per_sheet INTEGER NOT NULL DEFAULT 1;"); } catch { }
            try { _db.ExecuteNonQuery("ALTER TABLE quotes ADD COLUMN is_collated INTEGER NOT NULL DEFAULT 1;"); } catch { }
            try { _db.ExecuteNonQuery("ALTER TABLE printers ADD COLUMN bw_queue_name TEXT;"); } catch { }
            try { _db.ExecuteNonQuery("ALTER TABLE printers ADD COLUMN color_queue_name TEXT;"); } catch { }
            try { _db.ExecuteNonQuery("ALTER TABLE orders ADD COLUMN shop_slug TEXT NOT NULL DEFAULT 'pilot';"); } catch { }
            try { _db.ExecuteNonQuery("CREATE INDEX IF NOT EXISTS idx_orders_shop_slug ON orders(shop_slug);"); } catch { }
            try { _db.ExecuteNonQuery("CREATE INDEX IF NOT EXISTS idx_orders_shop_date_state ON orders(shop_slug, created_at_utc, state);"); } catch { }
            try { _db.ExecuteNonQuery("CREATE INDEX IF NOT EXISTS idx_orders_shop_pickup ON orders(shop_slug, pickup_code);"); } catch { }
            try { _db.ExecuteNonQuery("ALTER TABLE shops ADD COLUMN upi_vpa TEXT;"); } catch { }
            try { _db.ExecuteNonQuery("ALTER TABLE shops ADD COLUMN razorpay_key_id TEXT;"); } catch { }
            try { _db.ExecuteNonQuery("ALTER TABLE shops ADD COLUMN razorpay_key_secret TEXT;"); } catch { }
            try { _db.ExecuteNonQuery("ALTER TABLE shops ADD COLUMN razorpay_linked_account_id TEXT;"); } catch { }

            string now = UtcNowIso();
            long shopCount = _db.ExecuteScalarLong("SELECT COUNT(*) FROM shops;");
            if (shopCount == 0)
            {
                _db.ExecuteInsert(
                    @"INSERT INTO shops (slug, name, tagline, intake_paused, operating_hours, turnaround_text, retention_hours, min_free_disk_mb, created_at_utc)
                      VALUES ('midtownstationary', 'Midtown Stationary', 'Printing ke liye pareshan ho? Just PrintAway.', 0, '09:00 AM - 09:00 PM IST', '3-5 mins after counter admin approval', 24, 1024, @now);",
                    new Dictionary<string, object> { { "now", now } });
            }
            else
            {
                var existingShop = _db.QuerySingle("SELECT * FROM shops WHERE id = 1;");
                if (existingShop != null)
                {
                    string dbRzpKey = existingShop.ContainsKey("razorpay_key_id") && existingShop["razorpay_key_id"] != null ? Convert.ToString(existingShop["razorpay_key_id"]).Trim() : null;
                    string dbRzpSec = existingShop.ContainsKey("razorpay_key_secret") && existingShop["razorpay_key_secret"] != null ? Convert.ToString(existingShop["razorpay_key_secret"]).Trim() : null;
                    string dbRzpAcc = existingShop.ContainsKey("razorpay_linked_account_id") && existingShop["razorpay_linked_account_id"] != null ? Convert.ToString(existingShop["razorpay_linked_account_id"]).Trim() : null;
                    if (!string.IsNullOrWhiteSpace(dbRzpKey) && !string.IsNullOrWhiteSpace(dbRzpSec))
                    {
                        ConfigureRazorpay(dbRzpKey, dbRzpSec, _razorpayWebhookSecret, dbRzpAcc);
                    }
                    else if (!string.IsNullOrWhiteSpace(dbRzpAcc))
                    {
                        _razorpayLinkedAccountId = dbRzpAcc;
                    }
                }
            }

            long userCount = _db.ExecuteScalarLong("SELECT COUNT(*) FROM users;");
            if (userCount == 0)
            {
                string salt = GenerateRandomToken(16);
                string hash = HashPassword("PrintAway@Admin2026", salt);
                _db.ExecuteInsert(
                    @"INSERT INTO users (username, display_name, role, password_hash, password_salt, is_active, created_at_utc)
                      VALUES ('admin', 'Shop Owner / Lead Admin', 'admin', @hash, @salt, 1, @now);",
                    new Dictionary<string, object> { { "hash", hash }, { "salt", salt }, { "now", now } });
            }

            long stationCount = _db.ExecuteScalarLong("SELECT COUNT(*) FROM stations;");
            if (stationCount == 0)
            {
                _db.ExecuteInsert(
                    @"INSERT INTO stations (shop_id, station_code, display_name, os_summary, last_heartbeat_utc, free_ram_mb, free_disk_mb, spooler_running, printer_ready, status_message)
                      VALUES (1, 'STATION-PILOT-01', 'Primary Windows Host Station', 'Windows 11 Pro x64 (Build 22621)', @now, 2000, 149000, 1, 1, 'Online & Ready');",
                    new Dictionary<string, object> { { "now", now } });
            }

            long printerCount = _db.ExecuteScalarLong("SELECT COUNT(*) FROM printers;");
            if (printerCount == 0)
            {
                long pid = _db.ExecuteInsert(
                    @"INSERT INTO printers (shop_id, station_id, device_code, model_name, port_name, supports_color, supports_a4, supports_duplex, supports_page_range, allowed_paper_sizes, max_copies, is_enabled, created_at_utc)
                      VALUES (1, 1, 'XEROX-B1025-USB001', 'Xerox B1025 Multifunction Printer', 'USB001', 1, 1, 1, 1, 'A4,Legal,Letter', 50, 1, @now);",
                    new Dictionary<string, object> { { "now", now } });

                _db.ExecuteInsert(
                    @"INSERT INTO printer_queues (printer_id, windows_queue_name, driver_name, port_name, is_active_selected, is_virtual_excluded, verified_status, notes, updated_at_utc)
                      VALUES (@pid, @q1, 'Xerox Global Print Driver PCL6', 'USB001', 1, 0, 'VERIFIED_ACTIVE', 'Founder-confirmed active queue (PaperSize=A4, Color/B&W, Simplex/Duplex)', @now);",
                    new Dictionary<string, object> { { "pid", pid }, { "q1", VerifiedXeroxQueueName }, { "now", now } });

                _db.ExecuteInsert(
                    @"INSERT INTO printer_queues (printer_id, windows_queue_name, driver_name, port_name, is_active_selected, is_virtual_excluded, verified_status, notes, updated_at_utc)
                      VALUES (@pid, @q2, 'Xerox B1025 Multifunction Printer V4 PCL6', 'USB001', 0, 0, 'DISABLED_DUPLICATE_USB001', 'Excluded to prevent duplicate dispatch to same USB001 physical printer', @now);",
                    new Dictionary<string, object> { { "pid", pid }, { "q2", SecondaryXeroxQueueName }, { "now", now } });
            }
            else
            {
                // Ensure existing DB enables color & page range controls by default so the customer & admin UI have full control out-of-the-box
                try { _db.ExecuteNonQuery("UPDATE printers SET supports_color = 1, supports_page_range = 1 WHERE id = 1;"); } catch { }
            }

            long priceCount = _db.ExecuteScalarLong("SELECT COUNT(*) FROM price_rules;");
            if (priceCount == 0)
            {
                _db.ExecuteInsert(
                    @"INSERT INTO price_rules (shop_id, version, simplex_per_side_inr, duplex_per_side_inr, color_simplex_per_side_inr, color_duplex_per_side_inr, min_order_charge_inr, tax_percent, is_active, created_by, created_at_utc)
                      VALUES (1, 1, 2.00, 1.50, 10.00, 8.00, 2.00, 0.0, 1, 'system-seed', @now);",
                    new Dictionary<string, object> { { "now", now } });
            }
        }

        public static string GenerateRandomToken(int byteCount = 16)
        {
            byte[] buf = new byte[byteCount];
            using (var rng = RandomNumberGenerator.Create())
            {
                rng.GetBytes(buf);
            }
            var sb = new StringBuilder(byteCount * 2);
            foreach (byte b in buf) sb.Append(b.ToString("x2", CultureInfo.InvariantCulture));
            return sb.ToString();
        }

        public static string GeneratePickupCode()
        {
            const string chars = "23456789ABCDEFGHJKLMNPQRSTUVWXYZ";
            byte[] buf = new byte[6];
            using (var rng = RandomNumberGenerator.Create())
            {
                rng.GetBytes(buf);
            }
            var sb = new StringBuilder(6);
            for (int i = 0; i < 6; i++)
            {
                sb.Append(chars[buf[i] % chars.Length]);
            }
            return sb.ToString();
        }

        public static string HashPassword(string password, string salt)
        {
            using (var pbkdf2 = new Rfc2898DeriveBytes(password, Encoding.UTF8.GetBytes(salt), 10000, HashAlgorithmName.SHA256))
            {
                return Convert.ToBase64String(pbkdf2.GetBytes(32));
            }
        }

        public string ComputeWebhookHmac(string rawBody)
        {
            using (var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(_webhookSecret)))
            {
                byte[] sig = hmac.ComputeHash(Encoding.UTF8.GetBytes(rawBody ?? ""));
                var sb = new StringBuilder(sig.Length * 2);
                foreach (byte b in sig) sb.Append(b.ToString("x2", CultureInfo.InvariantCulture));
                return sb.ToString();
            }
        }

        public void LogAudit(long? orderId, string actorType, string actorName, string eventType, string fromState, string toState, string details)
        {
            _db.ExecuteInsert(
                @"INSERT INTO audit_events (order_id, actor_type, actor_name, event_type, from_state, to_state, details, created_at_utc)
                  VALUES (@oid, @atype, @aname, @etype, @fstate, @tstate, @details, @now);",
                new Dictionary<string, object>
                {
                    { "oid", orderId.HasValue ? (object)orderId.Value : null },
                    { "atype", actorType },
                    { "aname", actorName },
                    { "etype", eventType },
                    { "fstate", fromState },
                    { "tstate", toState },
                    { "details", details ?? "" },
                    { "now", UtcNowIso() }
                });
        }

        public long GetAvailableDiskMegabytes()
        {
            try
            {
                string root = Path.GetPathRoot(Path.GetFullPath(_vaultDirectory));
                var drive = new DriveInfo(root);
                return drive.AvailableFreeSpace / (1024L * 1024L);
            }
            catch
            {
                return 102400L;
            }
        }

        public Dictionary<string, object> GetShopAndAvailabilityStatus()
        {
            var shop = _db.QuerySingle("SELECT * FROM shops WHERE id = 1;");
            var station = _db.QuerySingle("SELECT * FROM stations WHERE id = 1;");
            var printer = _db.QuerySingle("SELECT * FROM printers WHERE id = 1;");
            var activeQueue = _db.QuerySingle("SELECT * FROM printer_queues WHERE is_active_selected = 1 LIMIT 1;");
            var priceRule = _db.QuerySingle("SELECT * FROM price_rules WHERE shop_id = 1 AND is_active = 1 ORDER BY version DESC LIMIT 1;");

            long freeDiskMb = GetAvailableDiskMegabytes();
            long minDiskMb = Convert.ToInt64(shop["min_free_disk_mb"], CultureInfo.InvariantCulture);
            bool diskLow = freeDiskMb < minDiskMb;
            bool intakePaused = Convert.ToInt64(shop["intake_paused"], CultureInfo.InvariantCulture) != 0;
            bool printerReady = Convert.ToInt64(station["printer_ready"], CultureInfo.InvariantCulture) != 0 &&
                                Convert.ToInt64(station["spooler_running"], CultureInfo.InvariantCulture) != 0;

            bool acceptingOrders = !intakePaused && !diskLow && printerReady;
            string statusReason = "Open & Accepting Orders";
            if (intakePaused) statusReason = "Order intake temporarily paused by shop admin: " + Convert.ToString(shop["pause_reason"] ?? "Maintenance");
            else if (diskLow) statusReason = "Order intake paused: Host storage low.";
            else if (!printerReady) statusReason = "Printer station requires attention (" + Convert.ToString(station["status_message"] ?? "Offline") + ").";

            string allowedPaperSizes = printer.ContainsKey("allowed_paper_sizes") && printer["allowed_paper_sizes"] != null
                ? Convert.ToString(printer["allowed_paper_sizes"])
                : "A4,Legal,Letter";
            int maxCopies = printer.ContainsKey("max_copies") && printer["max_copies"] != null
                ? Convert.ToInt32(printer["max_copies"], CultureInfo.InvariantCulture)
                : 50;
            double colorSimplexRate = priceRule.ContainsKey("color_simplex_per_side_inr") && priceRule["color_simplex_per_side_inr"] != null
                ? Convert.ToDouble(priceRule["color_simplex_per_side_inr"], CultureInfo.InvariantCulture)
                : 10.0;
            double colorDuplexRate = priceRule.ContainsKey("color_duplex_per_side_inr") && priceRule["color_duplex_per_side_inr"] != null
                ? Convert.ToDouble(priceRule["color_duplex_per_side_inr"], CultureInfo.InvariantCulture)
                : 8.0;

            string shopUpiVpa = shop.ContainsKey("upi_vpa") && shop["upi_vpa"] != null ? Convert.ToString(shop["upi_vpa"]).Trim() : "";
            string rzpMode = "disabled";
            if (HasRazorpayConfigured)
            {
                rzpMode = _razorpayKeyId.StartsWith("rzp_live_", StringComparison.OrdinalIgnoreCase) ? "live" : "test";
            }

            return new Dictionary<string, object>
            {
                { "shopId", shop.ContainsKey("slug") && shop["slug"] != null ? shop["slug"] : "pilot" },
                { "shopName", shop["name"] },
                { "upiVpa", shopUpiVpa },
                { "hasRazorpay", HasRazorpayConfigured },
                { "razorpayKeyId", _razorpayKeyId ?? "" },
                { "razorpayMode", rzpMode },
                { "razorpayLinkedAccountId", _razorpayLinkedAccountId ?? "" },
                { "bwQueueName", printer.ContainsKey("bw_queue_name") && printer["bw_queue_name"] != null ? Convert.ToString(printer["bw_queue_name"]) : "" },
                { "colorQueueName", printer.ContainsKey("color_queue_name") && printer["color_queue_name"] != null ? Convert.ToString(printer["color_queue_name"]) : "" },
                { "tagline", shop["tagline"] },
                { "operatingHours", shop["operating_hours"] },
                { "turnaroundText", shop["turnaround_text"] },
                { "acceptingOrders", acceptingOrders },
                { "intakePaused", intakePaused },
                { "diskLow", diskLow },
                { "printerReady", printerReady },
                { "dispatchMode", GetDispatchMode() },
                { "liveTunnelUrl", GetLiveTunnelUrl() },
                { "statusReason", statusReason },
                { "printerModel", printer["model_name"] },
                { "activeQueueName", activeQueue != null ? activeQueue["windows_queue_name"] : VerifiedXeroxQueueName },
                { "supportsColor", Convert.ToInt64(printer["supports_color"]) != 0 },
                { "supportsA4", Convert.ToInt64(printer["supports_a4"]) != 0 },
                { "supportsDuplex", Convert.ToInt64(printer["supports_duplex"]) != 0 },
                { "supportsPageRange", Convert.ToInt64(printer["supports_page_range"]) != 0 },
                { "allowedPaperSizes", allowedPaperSizes },
                { "maxCopies", maxCopies },
                { "pricing", new Dictionary<string, object>
                    {
                        { "version", priceRule["version"] },
                        { "simplexPerSideInr", priceRule["simplex_per_side_inr"] },
                        { "duplexPerSideInr", priceRule["duplex_per_side_inr"] },
                        { "colorSimplexPerSideInr", colorSimplexRate },
                        { "colorDuplexPerSideInr", colorDuplexRate },
                        { "minOrderChargeInr", priceRule["min_order_charge_inr"] },
                        { "taxPercent", priceRule["tax_percent"] }
                    }
                }
            };
        }

        public Dictionary<string, object> SaveValidatedPdf(string originalFilename, byte[] pdfBytes, out string errorCode, out string errorMsg)
        {
            errorCode = null;
            errorMsg = null;

            var status = GetShopAndAvailabilityStatus();
            if (!(bool)status["acceptingOrders"])
            {
                errorCode = "SHOP_UNAVAILABLE";
                errorMsg = Convert.ToString(status["statusReason"]);
                return null;
            }

            // Way 3: Automatic fallback conversion for raw image & text uploads (JPEG, PNG, WEBP, BMP, GIF, TXT)
            if (PdfAndPrinterEngine.IsImageBytes(pdfBytes) || PdfAndPrinterEngine.IsImageExtension(originalFilename))
            {
                byte[] convertedPdf = PdfAndPrinterEngine.ConvertImageToA4Pdf(pdfBytes);
                if (convertedPdf != null && convertedPdf.Length > 0)
                {
                    pdfBytes = convertedPdf;
                    if (!originalFilename.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase))
                    {
                        originalFilename = Path.GetFileNameWithoutExtension(originalFilename) + "_A4.pdf";
                    }
                }
            }
            else if (PdfAndPrinterEngine.IsTextExtension(originalFilename))
            {
                byte[] convertedPdf = PdfAndPrinterEngine.ConvertTextToA4Pdf(pdfBytes);
                if (convertedPdf != null && convertedPdf.Length > 0)
                {
                    pdfBytes = convertedPdf;
                    originalFilename = Path.GetFileNameWithoutExtension(originalFilename) + "_Doc.pdf";
                }
            }

            PdfValidationResult val = PdfAndPrinterEngine.ValidatePdf(pdfBytes);
            if (!val.IsValid)
            {
                errorCode = val.ErrorCode;
                errorMsg = val.ErrorMessage;
                LogAudit(null, "customer", "anonymous", "UPLOAD_REJECTED", "draft", "rejected", val.ErrorCode + ": " + val.ErrorMessage);
                return null;
            }

            string fileToken = "file_" + GenerateRandomToken(12);
            string subDir = DateTime.UtcNow.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
            string targetDir = Path.Combine(_vaultDirectory, subDir);
            if (!Directory.Exists(targetDir))
            {
                Directory.CreateDirectory(targetDir);
            }

            // Never use user-supplied filename for local path (PRD §11)
            string safeVaultFilename = fileToken + ".pdf";
            string fullPath = Path.Combine(targetDir, safeVaultFilename);
            File.WriteAllBytes(fullPath, pdfBytes);

            string relPath = Path.Combine(subDir, safeVaultFilename);
            string safeDisplayFilename = SanitizeDisplayFilename(originalFilename);
            string now = UtcNowIso();
            string deadline = UtcNowIso(24 * 3600);

            long fileId = _db.ExecuteInsert(
                @"INSERT INTO files (file_token, original_filename_redacted, storage_relative_path, sha256_hex, size_bytes, page_count, scan_status, retention_deadline_utc, is_deleted, created_at_utc)
                  VALUES (@token, @name, @rel, @sha, @size, @pages, 'CLEAN_VERIFIED_PDF', @deadline, 0, @now);",
                new Dictionary<string, object>
                {
                    { "token", fileToken },
                    { "name", safeDisplayFilename },
                    { "rel", relPath },
                    { "sha", val.Sha256Hex },
                    { "size", val.FileSizeBytes },
                    { "pages", val.PageCount },
                    { "deadline", deadline },
                    { "now", now }
                });

            LogAudit(null, "customer", "anonymous", "FILE_UPLOADED_AND_VALIDATED", "draft", "validated",
                string.Format(CultureInfo.InvariantCulture, "FileToken={0}, Pages={1}, Size={2}B, SHA256={3}", fileToken, val.PageCount, val.FileSizeBytes, val.Sha256Hex.Substring(0, 12)));

            return new Dictionary<string, object>
            {
                { "fileId", fileId },
                { "fileToken", fileToken },
                { "displayName", safeDisplayFilename },
                { "pageCount", val.PageCount },
                { "sizeBytes", val.FileSizeBytes },
                { "sha256Prefix", val.Sha256Hex.Substring(0, 16) },
                { "scanStatus", "CLEAN_VERIFIED_PDF" }
            };
        }

        private static string SanitizeDisplayFilename(string rawName)
        {
            if (string.IsNullOrWhiteSpace(rawName)) return "document.pdf";
            string baseName = Path.GetFileName(rawName);
            var sb = new StringBuilder();
            foreach (char c in baseName)
            {
                if (char.IsLetterOrDigit(c) || c == '.' || c == '-' || c == '_' || c == ' ')
                    sb.Append(c);
            }
            string cleaned = sb.ToString().Trim();
            if (string.IsNullOrEmpty(cleaned)) return "document.pdf";
            if (cleaned.Length > 48) cleaned = cleaned.Substring(0, 48);
            return cleaned;
        }

        public Dictionary<string, object> CreateServerQuote(
            string fileToken,
            int copies,
            string duplexMode,
            string pageRangeSpec,
            out string errorMsg,
            string colorMode = "bw",
            string paperSize = "A4",
            string orientation = "auto",
            int pagesPerSheet = 1,
            bool collate = true)
        {
            errorMsg = null;
            var fileRow = _db.QuerySingle("SELECT * FROM files WHERE file_token = @ft AND is_deleted = 0;",
                new Dictionary<string, object> { { "ft", fileToken ?? "" } });
            if (fileRow == null)
            {
                errorMsg = "Uploaded file not found or has expired.";
                return null;
            }

            var printer = _db.QuerySingle("SELECT * FROM printers WHERE id = 1;");
            bool duplexAllowed = Convert.ToInt64(printer["supports_duplex"]) != 0;
            bool colorAllowed = Convert.ToInt64(printer["supports_color"]) != 0;
            bool pageRangeAllowed = Convert.ToInt64(printer["supports_page_range"]) != 0;
            int maxCopies = printer.ContainsKey("max_copies") && printer["max_copies"] != null
                ? Convert.ToInt32(printer["max_copies"], CultureInfo.InvariantCulture)
                : 50;

            bool isRequestingDuplex = string.Equals(duplexMode, "TwoSidedLongEdge", StringComparison.OrdinalIgnoreCase) ||
                                      string.Equals(duplexMode, "TwoSidedShortEdge", StringComparison.OrdinalIgnoreCase);
            if (isRequestingDuplex && !duplexAllowed)
            {
                errorMsg = "Duplex (two-sided) printing is currently disabled by shop admin on this printer.";
                return null;
            }

            bool isRequestingColor = string.Equals(colorMode, "color", StringComparison.OrdinalIgnoreCase) ||
                                     string.Equals(colorMode, "colour", StringComparison.OrdinalIgnoreCase);
            if (isRequestingColor && !colorAllowed)
            {
                errorMsg = "Color printing is currently disabled by shop admin on this printer. Please select Black & White.";
                return null;
            }

            if (!pageRangeAllowed && !string.IsNullOrWhiteSpace(pageRangeSpec) && !string.Equals(pageRangeSpec.Trim(), "all", StringComparison.OrdinalIgnoreCase))
            {
                errorMsg = "Custom page selection is currently disabled by shop admin.";
                return null;
            }

            var priceRule = _db.QuerySingle("SELECT * FROM price_rules WHERE shop_id = 1 AND is_active = 1 ORDER BY version DESC LIMIT 1;");
            int docPages = Convert.ToInt32(fileRow["page_count"], CultureInfo.InvariantCulture);

            double colorSimplexRate = priceRule.ContainsKey("color_simplex_per_side_inr") && priceRule["color_simplex_per_side_inr"] != null
                ? Convert.ToDouble(priceRule["color_simplex_per_side_inr"], CultureInfo.InvariantCulture)
                : 10.0;
            double colorDuplexRate = priceRule.ContainsKey("color_duplex_per_side_inr") && priceRule["color_duplex_per_side_inr"] != null
                ? Convert.ToDouble(priceRule["color_duplex_per_side_inr"], CultureInfo.InvariantCulture)
                : 8.0;

            string calcErr;
            QuoteCalculationResult calc = PdfAndPrinterEngine.CalculateQuote(
                docPages,
                pageRangeSpec,
                copies,
                duplexMode,
                Convert.ToDouble(priceRule["simplex_per_side_inr"], CultureInfo.InvariantCulture),
                Convert.ToDouble(priceRule["duplex_per_side_inr"], CultureInfo.InvariantCulture),
                Convert.ToDouble(priceRule["min_order_charge_inr"], CultureInfo.InvariantCulture),
                Convert.ToDouble(priceRule["tax_percent"], CultureInfo.InvariantCulture),
                out calcErr,
                colorMode,
                colorSimplexRate,
                colorDuplexRate,
                paperSize,
                orientation,
                pagesPerSheet,
                collate,
                maxCopies);

            if (calc == null)
            {
                errorMsg = calcErr;
                return null;
            }

            string quoteToken = "qt_" + GenerateRandomToken(12);
            string now = UtcNowIso();
            string expires = UtcNowIso(1800);

            long quoteId = _db.ExecuteInsert(
                @"INSERT INTO quotes (
                    quote_token, shop_id, file_id, price_rule_id, price_rule_version,
                    document_pages, selected_pages, page_range_spec, copies, duplex_mode,
                    color_mode, paper_size, orientation, pages_per_sheet, is_collated,
                    printed_sides, physical_sheets, rate_per_side_inr, base_charge_inr,
                    min_charge_delta_inr, tax_amount_inr, total_amount_inr, total_amount_paise,
                    expires_at_utc, created_at_utc
                  ) VALUES (
                    @qtoken, 1, @fid, @prid, @prver,
                    @dpages, @spages, @range, @copies, @duplex,
                    @cmode, @psize, @orient, @pps, @collate,
                    @sides, @sheets, @rate, @base,
                    @mindelta, @tax, @totalinr, @totalpaise,
                    @exp, @now
                  );",
                new Dictionary<string, object>
                {
                    { "qtoken", quoteToken },
                    { "fid", fileRow["id"] },
                    { "prid", priceRule["id"] },
                    { "prver", priceRule["version"] },
                    { "dpages", calc.DocumentPages },
                    { "spages", calc.SelectedPageCount },
                    { "range", calc.NormalizedPageRange },
                    { "copies", calc.Copies },
                    { "duplex", calc.DuplexMode },
                    { "cmode", calc.ColorMode },
                    { "psize", calc.PaperSize },
                    { "orient", calc.Orientation },
                    { "pps", calc.PagesPerSheet },
                    { "collate", calc.Collate ? 1 : 0 },
                    { "sides", calc.TotalPrintedSides },
                    { "sheets", calc.TotalPhysicalSheets },
                    { "rate", calc.RatePerSideInr },
                    { "base", calc.BaseChargeInr },
                    { "mindelta", calc.MinimumChargeAppliedInr },
                    { "tax", calc.TaxAmountInr },
                    { "totalinr", calc.TotalAmountInr },
                    { "totalpaise", calc.TotalAmountPaise },
                    { "exp", expires },
                    { "now", now }
                });

            return new Dictionary<string, object>
            {
                { "quoteId", quoteId },
                { "quoteToken", quoteToken },
                { "fileToken", fileToken },
                { "priceRuleVersion", priceRule["version"] },
                { "documentPages", calc.DocumentPages },
                { "selectedPages", calc.SelectedPageCount },
                { "resolvedPages", calc.ResolvedPages },
                { "pageRange", calc.NormalizedPageRange },
                { "copies", calc.Copies },
                { "duplexMode", calc.DuplexMode },
                { "colorMode", calc.ColorMode },
                { "paperSize", calc.PaperSize },
                { "orientation", calc.Orientation },
                { "pagesPerSheet", calc.PagesPerSheet },
                { "collate", calc.Collate },
                { "printedSidesPerCopy", calc.PrintedSidesPerCopy },
                { "printedSides", calc.TotalPrintedSides },
                { "sheetsPerCopy", calc.SheetsPerCopy },
                { "physicalSheets", calc.TotalPhysicalSheets },
                { "ratePerSideInr", calc.RatePerSideInr },
                { "baseChargeInr", calc.BaseChargeInr },
                { "minChargeDeltaInr", calc.MinimumChargeAppliedInr },
                { "taxAmountInr", calc.TaxAmountInr },
                { "totalAmountInr", calc.TotalAmountInr },
                { "totalAmountPaise", calc.TotalAmountPaise },
                { "expiresAtUtc", expires }
            };
        }

        public Dictionary<string, object> CreateOrderAndPaymentIntent(
            string quoteToken,
            string customerName,
            string customerPhone,
            bool consentAccepted,
            out string errorMsg,
            string targetQueueName = null,
            string paymentMethod = "online")
        {
            errorMsg = null;
            if (!consentAccepted)
            {
                errorMsg = "You must accept the document retention and counter pickup policy to place an order.";
                return null;
            }

            var status = GetShopAndAvailabilityStatus();
            if (!(bool)status["acceptingOrders"])
            {
                errorMsg = Convert.ToString(status["statusReason"]);
                return null;
            }

            var quote = _db.QuerySingle("SELECT * FROM quotes WHERE quote_token = @qt;",
                new Dictionary<string, object> { { "qt", quoteToken ?? "" } });
            if (quote == null)
            {
                errorMsg = "Quote not found or invalid.";
                return null;
            }

            bool isCashAtCounter = string.Equals(paymentMethod, "cash", StringComparison.OrdinalIgnoreCase);
            string activeShopSlug = status.ContainsKey("shopId") && status["shopId"] != null ? Convert.ToString(status["shopId"]).Trim() : "pilot";
            string activeShopName = status.ContainsKey("shopName") && status["shopName"] != null ? Convert.ToString(status["shopName"]).Trim() : "PrintAway Shop";
            string shopUpiVpa = status.ContainsKey("upiVpa") && status["upiVpa"] != null ? Convert.ToString(status["upiVpa"]).Trim() : "printaway@upi";
            if (string.IsNullOrWhiteSpace(shopUpiVpa)) shopUpiVpa = "printaway@upi";

            string orderCode = "PA-" + GenerateRandomToken(3).ToUpperInvariant();
            string publicToken = "pub_" + GenerateRandomToken(16);
            string pickupCode = GeneratePickupCode();
            string gatewayOrderId = isCashAtCounter ? ("cash_ord_" + GenerateRandomToken(10)) : ("upi_ord_" + GenerateRandomToken(10));
            string rzpOrderId = null;
            if (!isCashAtCounter && HasRazorpayConfigured)
            {
                string rzpErr;
                rzpOrderId = CreateRazorpayOrder(orderCode, Convert.ToInt64(quote["total_amount_paise"]), out rzpErr, activeShopSlug, activeShopName);
                if (!string.IsNullOrWhiteSpace(rzpOrderId))
                {
                    gatewayOrderId = rzpOrderId;
                }
            }
            string targetQueue = Convert.ToString(status["activeQueueName"]);
            string now = UtcNowIso();
            string initialState = isCashAtCounter ? "paid_awaiting_approval" : "awaiting_payment";
            string initialPayStatus = isCashAtCounter ? "cash_at_counter" : "pending";
            string gwProvider = isCashAtCounter ? "CASH_COUNTER" : "UPI_GATEWAY";
            string gwPayId = isCashAtCounter ? "CASH_AT_COUNTER" : null;
            string payRecordStatus = isCashAtCounter ? "cash_at_counter" : "created";

            long orderId = 0;
            _db.RunInTransaction(() =>
            {
                orderId = _db.ExecuteInsert(
                    @"INSERT INTO orders (
                        order_code, public_token, pickup_code, shop_id, file_id, quote_id,
                        customer_name, customer_phone, consent_accepted, state, payment_status,
                        target_printer_id, target_queue_name, created_at_utc, updated_at_utc, shop_slug
                      ) VALUES (
                        @ocode, @ptoken, @pcode, 1, @fid, @qid,
                        @cname, @cphone, 1, @st, @pstat,
                        1, @tqueue, @now, @now, @shop_slug
                      );",
                    new Dictionary<string, object>
                    {
                        { "ocode", orderCode },
                        { "ptoken", publicToken },
                        { "pcode", pickupCode },
                        { "fid", quote["file_id"] },
                        { "qid", quote["id"] },
                        { "cname", SanitizeText(customerName, 40) },
                        { "cphone", SanitizeText(customerPhone, 20) },
                        { "st", initialState },
                        { "pstat", initialPayStatus },
                        { "tqueue", targetQueue },
                        { "now", now },
                        { "shop_slug", activeShopSlug }
                    });

                _db.ExecuteInsert(
                    @"INSERT INTO payments (
                        order_id, gateway_provider, gateway_order_id, gateway_payment_id, amount_paise, currency,
                        status, verified_by_webhook, webhook_calls_count, created_at_utc
                      ) VALUES (
                        @oid, @gwprov, @goid, @gpid, @amt, 'INR',
                        @pstat, 0, 0, @now
                      );",
                    new Dictionary<string, object>
                    {
                        { "oid", orderId },
                        { "gwprov", gwProvider },
                        { "goid", gatewayOrderId },
                        { "gpid", gwPayId },
                        { "amt", quote["total_amount_paise"] },
                        { "pstat", payRecordStatus },
                        { "now", now }
                    });

                LogAudit(orderId, "customer", "customer", isCashAtCounter ? "ORDER_CREATED_CASH_AT_COUNTER" : "ORDER_CREATED_AWAITING_PAYMENT", "quoted", initialState,
                    string.Format(CultureInfo.InvariantCulture, "Order={0}, Method={1}, GatewayOrder={2}, AmountPaise={3}", orderCode, isCashAtCounter ? "CASH_AT_COUNTER" : "ONLINE", gatewayOrderId, quote["total_amount_paise"]));
            });

            return new Dictionary<string, object>
            {
                { "orderId", orderId },
                { "orderCode", orderCode },
                { "publicToken", publicToken },
                { "pickupCode", pickupCode },
                { "state", initialState },
                { "paymentStatus", initialPayStatus },
                { "paymentMethod", isCashAtCounter ? "cash" : "online" },
                { "gatewayOrderId", gatewayOrderId },
                { "razorpayOrderId", rzpOrderId },
                { "razorpayKeyId", _razorpayKeyId },
                { "amountInr", quote["total_amount_inr"] },
                { "amountPaise", quote["total_amount_paise"] },
                { "upiIntentUri", string.Format(CultureInfo.InvariantCulture, "upi://pay?pa={0}&pn={1}&tr={2}&am={3:0.00}&cu=INR", Uri.EscapeDataString(shopUpiVpa), Uri.EscapeDataString(activeShopName), gatewayOrderId, quote["total_amount_inr"]) }
            };
        }

        // Allows a customer on the payment screen to switch an awaiting_payment order to Cash at Counter.
        // Because physical printing NEVER begins until the store admin clicks Approve in the Admin Dashboard,
        // this is 100% immune to false-printing misuse.
        public Dictionary<string, object> SelectCashAtCounterPayment(long orderId, string publicToken, out string errorMsg)
        {
            errorMsg = null;
            try
            {
                return _db.RunInTransaction(() =>
                {
                    Dictionary<string, object> order = null;
                    if (orderId > 0)
                    {
                        order = _db.QuerySingle("SELECT * FROM orders WHERE id = @id;", new Dictionary<string, object> { { "id", orderId } });
                    }
                    else if (!string.IsNullOrWhiteSpace(publicToken))
                    {
                        order = _db.QuerySingle("SELECT * FROM orders WHERE public_token = @pt;", new Dictionary<string, object> { { "pt", publicToken.Trim() } });
                    }

                    if (order == null)
                    {
                        throw new InvalidOperationException("Order not found.");
                    }

                    long oid = Convert.ToInt64(order["id"], CultureInfo.InvariantCulture);
                    string currentState = Convert.ToString(order["state"]);
                    if (currentState != "awaiting_payment" && currentState != "paid_awaiting_approval")
                    {
                        throw new InvalidOperationException("Order cannot be switched to Cash at Counter in state '" + currentState + "'.");
                    }

                    string now = UtcNowIso();
                    _db.ExecuteNonQuery(
                        @"UPDATE orders SET state = 'paid_awaiting_approval', payment_status = 'cash_at_counter', updated_at_utc = @now WHERE id = @oid;
                          UPDATE payments SET gateway_provider = 'CASH_COUNTER', gateway_payment_id = 'CASH_AT_COUNTER', status = 'cash_at_counter' WHERE order_id = @oid;",
                        new Dictionary<string, object> { { "now", now }, { "oid", oid } });

                    LogAudit(oid, "customer", "customer", "CUSTOMER_SELECTED_CASH_AT_COUNTER", currentState, "paid_awaiting_approval",
                        "Customer selected Pay Cash at Counter; awaiting store admin cash verification and approval.");

                    return new Dictionary<string, object>
                    {
                        { "orderId", oid },
                        { "orderCode", order["order_code"] },
                        { "publicToken", order["public_token"] },
                        { "state", "paid_awaiting_approval" },
                        { "paymentStatus", "cash_at_counter" },
                        { "paymentMethod", "cash" }
                    };
                });
            }
            catch (Exception ex)
            {
                errorMsg = ex.Message;
                return null;
            }
        }

        private static string SanitizeText(string input, int maxLen)
        {
            if (string.IsNullOrWhiteSpace(input)) return "";
            string s = input.Trim();
            return s.Length <= maxLen ? s : s.Substring(0, maxLen);
        }

        // Processes a verified payment webhook.
        // HARD RULE (PRD §1A, §7): Payment verification ONLY transitions to 'paid_awaiting_approval'.
        // It NEVER transitions to 'queued' or dispatches a print job!
        public Dictionary<string, object> ProcessVerifiedPaymentWebhook(
            string rawPayload,
            string providedSignatureHex,
            string eventId,
            string gatewayOrderId,
            string gatewayPaymentId,
            long amountPaise,
            string paymentOutcome,
            out int httpStatus,
            out string errorMsg,
            string targetQueueName = null)
        {
            errorMsg = null;
            httpStatus = 200;

            string expectedSig = ComputeWebhookHmac(rawPayload);
            if (string.IsNullOrWhiteSpace(providedSignatureHex) ||
                !CryptographicEquals(expectedSig, providedSignatureHex.Trim()))
            {
                httpStatus = 401;
                errorMsg = "Invalid webhook HMAC signature.";
                LogAudit(null, "webhook", "gateway", "WEBHOOK_SIGNATURE_REJECTED", null, null, "GatewayOrder=" + gatewayOrderId);
                return null;
            }

            if (string.IsNullOrWhiteSpace(eventId) || string.IsNullOrWhiteSpace(gatewayOrderId))
            {
                httpStatus = 400;
                errorMsg = "Missing eventId or gatewayOrderId.";
                return null;
            }

            return _db.RunInTransaction(() =>
            {
                var payment = _db.QuerySingle("SELECT * FROM payments WHERE gateway_order_id = @goid;",
                    new Dictionary<string, object> { { "goid", gatewayOrderId } });
                if (payment == null)
                {
                    throw new InvalidOperationException("Unknown gatewayOrderId: " + gatewayOrderId);
                }

                long orderId = Convert.ToInt64(payment["order_id"], CultureInfo.InvariantCulture);
                var order = _db.QuerySingle("SELECT * FROM orders WHERE id = @oid;",
                    new Dictionary<string, object> { { "oid", orderId } });

                // Idempotency check: if already verified or same eventId received again, increment counter and return current state without side effects!
                string existingEventId = Convert.ToString(payment["webhook_event_id"] ?? "");
                string currentPayStatus = Convert.ToString(payment["status"] ?? "");
                string currentOrderState = Convert.ToString(order["state"] ?? "");

                if (string.Equals(currentPayStatus, "verified", StringComparison.OrdinalIgnoreCase) ||
                    (!string.IsNullOrEmpty(existingEventId) && string.Equals(existingEventId, eventId, StringComparison.Ordinal)))
                {
                    _db.ExecuteNonQuery(
                        "UPDATE payments SET webhook_calls_count = webhook_calls_count + 1 WHERE id = @pid;",
                        new Dictionary<string, object> { { "pid", payment["id"] } });

                    LogAudit(orderId, "webhook", "gateway", "PAYMENT_WEBHOOK_IDEMPOTENT_DUPLICATE", currentOrderState, currentOrderState,
                        "Duplicate webhook ignored safely. EventId=" + eventId);

                    return new Dictionary<string, object>
                    {
                        { "idempotentDuplicate", true },
                        { "orderCode", order["order_code"] },
                        { "state", currentOrderState },
                        { "paymentStatus", currentPayStatus },
                        { "autoPrinted", false }
                    };
                }

                long expectedPaise = Convert.ToInt64(payment["amount_paise"], CultureInfo.InvariantCulture);
                if (amountPaise != expectedPaise)
                {
                    LogAudit(orderId, "webhook", "gateway", "PAYMENT_AMOUNT_MISMATCH", currentOrderState, currentOrderState,
                        string.Format(CultureInfo.InvariantCulture, "Expected={0}, Received={1}", expectedPaise, amountPaise));
                    throw new InvalidOperationException("Payment amount mismatch.");
                }

                string now = UtcNowIso();
                if (string.Equals(paymentOutcome, "failed", StringComparison.OrdinalIgnoreCase))
                {
                    _db.ExecuteNonQuery(
                        @"UPDATE payments SET status = 'failed', webhook_event_id = @ev, gateway_payment_id = @gpid, webhook_calls_count = webhook_calls_count + 1 WHERE id = @pid;
                          UPDATE orders SET state = 'payment_failed', payment_status = 'failed', updated_at_utc = @now WHERE id = @oid;",
                        new Dictionary<string, object>
                        {
                            { "ev", eventId },
                            { "gpid", gatewayPaymentId ?? "pay_failed" },
                            { "pid", payment["id"] },
                            { "oid", orderId },
                            { "now", now }
                        });
                    LogAudit(orderId, "webhook", "gateway", "PAYMENT_FAILED", currentOrderState, "payment_failed", "EventId=" + eventId);
                    return new Dictionary<string, object>
                    {
                        { "idempotentDuplicate", false },
                        { "orderCode", order["order_code"] },
                        { "state", "payment_failed" },
                        { "paymentStatus", "failed" },
                        { "autoPrinted", false }
                    };
                }

                // Transition strictly to paid_awaiting_approval
                _db.ExecuteNonQuery(
                    @"UPDATE payments
                      SET status = 'verified', verified_by_webhook = 1, webhook_event_id = @ev, gateway_payment_id = @gpid,
                          webhook_calls_count = webhook_calls_count + 1, verified_at_utc = @now
                      WHERE id = @pid;
                      UPDATE orders
                      SET state = 'paid_awaiting_approval', payment_status = 'verified', updated_at_utc = @now
                      WHERE id = @oid;",
                    new Dictionary<string, object>
                    {
                        { "ev", eventId },
                        { "gpid", gatewayPaymentId ?? ("upi_pay_" + GenerateRandomToken(6)) },
                        { "pid", payment["id"] },
                        { "oid", orderId },
                        { "now", now }
                    });

                LogAudit(orderId, "webhook", "gateway", "PAYMENT_VERIFIED_AWAITING_ADMIN_APPROVAL", currentOrderState, "paid_awaiting_approval",
                    string.Format(CultureInfo.InvariantCulture, "EventId={0}, GatewayPaymentId={1}, AmountPaise={2}. Waiting for explicit admin approval.", eventId, gatewayPaymentId, amountPaise));

                return new Dictionary<string, object>
                {
                    { "idempotentDuplicate", false },
                    { "orderCode", order["order_code"] },
                    { "state", "paid_awaiting_approval" },
                    { "paymentStatus", "verified" },
                    { "autoPrinted", false }
                };
            });
        }

        private static bool CryptographicEquals(string a, string b)
        {
            if (a == null || b == null || a.Length != b.Length) return false;
            int diff = 0;
            for (int i = 0; i < a.Length; i++)
            {
                diff |= char.ToLowerInvariant(a[i]) ^ char.ToLowerInvariant(b[i]);
            }
            return diff == 0;
        }

        public Dictionary<string, object> GetOrderByPublicToken(string publicToken)
        {
            var row = _db.QuerySingle(
                @"SELECT o.*, q.document_pages, q.selected_pages, q.page_range_spec, q.copies, q.duplex_mode,
                         q.color_mode, q.paper_size, q.orientation, q.pages_per_sheet, q.is_collated,
                         q.printed_sides, q.physical_sheets, q.total_amount_inr, q.rate_per_side_inr,
                         f.original_filename_redacted, f.is_deleted,
                         j.windows_spooler_job_id, j.state AS job_state
                  FROM orders o
                  JOIN quotes q ON o.quote_id = q.id
                  JOIN files f ON o.file_id = f.id
                  LEFT JOIN print_jobs j ON j.order_id = o.id
                  WHERE o.public_token = @pt;",
                new Dictionary<string, object> { { "pt", publicToken ?? "" } });

            if (row == null) return null;

            string state = Convert.ToString(row["state"]);
            string payStatus = Convert.ToString(row["payment_status"]);
            bool showPickupCode = state == "approved" || state == "queued" || state == "leased" ||
                                  state == "downloading" || state == "rendering" ||
                                  state == "submitted_to_spooler" || state == "ready_for_pickup" ||
                                  state == "collected";

            return new Dictionary<string, object>
            {
                { "orderCode", row["order_code"] },
                { "publicToken", row["public_token"] },
                { "state", state },
                { "stateDescription", DescribeStateForCustomer(state, payStatus) },
                { "paymentStatus", payStatus },
                { "pickupCode", showPickupCode ? row["pickup_code"] : "Issued upon approval" },
                { "fileName", row["original_filename_redacted"] },
                { "fileDeletedAfterCollection", Convert.ToInt64(row["is_deleted"]) != 0 },
                { "documentPages", row["document_pages"] },
                { "selectedPages", row["selected_pages"] },
                { "pageRange", row["page_range_spec"] },
                { "copies", row["copies"] },
                { "duplexMode", row["duplex_mode"] },
                { "colorMode", row.ContainsKey("color_mode") && row["color_mode"] != null ? row["color_mode"] : "bw" },
                { "paperSize", row.ContainsKey("paper_size") && row["paper_size"] != null ? row["paper_size"] : "A4" },
                { "orientation", row.ContainsKey("orientation") && row["orientation"] != null ? row["orientation"] : "auto" },
                { "pagesPerSheet", row.ContainsKey("pages_per_sheet") && row["pages_per_sheet"] != null ? row["pages_per_sheet"] : 1 },
                { "collate", !row.ContainsKey("is_collated") || row["is_collated"] == null || Convert.ToInt64(row["is_collated"]) != 0 },
                { "printedSides", row["printed_sides"] },
                { "physicalSheets", row["physical_sheets"] },
                { "totalAmountInr", row["total_amount_inr"] },
                { "holdOrRejectReason", row["hold_or_reject_reason"] },
                { "createdAtUtc", row["created_at_utc"] },
                { "createdAtIst", ToIstDisplay(Convert.ToString(row["created_at_utc"])) },
                { "updatedAtUtc", row["updated_at_utc"] },
                { "updatedAtIst", ToIstDisplay(Convert.ToString(row["updated_at_utc"])) }
            };
        }

        private static string DescribeStateForCustomer(string state, string paymentStatus = null)
        {
            if (state == "paid_awaiting_approval" && string.Equals(paymentStatus, "cash_at_counter", StringComparison.OrdinalIgnoreCase))
            {
                return "Cash at Counter selected! Please pay cash at the counter so the shopkeeper can approve & print your document.";
            }
            switch (state)
            {
                case "awaiting_payment": return "Waiting for online UPI payment or Cash at Counter selection.";
                case "paid_awaiting_approval": return "Online payment verified! Waiting for shopkeeper to approve & print.";
                case "on_hold": return "Order placed on hold by shop admin. Please check with the counter.";
                case "approved":
                case "queued": return "Approved by admin! Queued for Xerox B1025 printer.";
                case "leased":
                case "downloading":
                case "rendering": return "Printer worker is preparing and rendering your PDF.";
                case "submitted_to_spooler": return "Sent to Windows Print Spooler (Xerox B1025). Waiting for staff to verify physical pages.";
                case "ready_for_pickup": return "Printed & verified! Show your Pickup Code at the counter to collect your pages.";
                case "collected": return "Collected at counter. Your source PDF has been purged from host storage.";
                case "rejected_refund_pending":
                case "refunded": return "Order rejected by admin; refund initiated.";
                case "reconciliation_required": return "Printer check in progress by counter staff (preventing duplicate print).";
                case "cancelled": return "Order cancelled.";
                default: return state;
            }
        }

        // Admin Authentication (Supports Shop-Specific Credentials & Master Password Override)
        public Dictionary<string, object> AuthenticateAdmin(string username, string password, out string errorMsg, string shopId = null)
        {
            errorMsg = null;
            string masterPassword = Environment.GetEnvironmentVariable("PRINTAWAY_MASTER_PASSWORD");
            if (string.IsNullOrWhiteSpace(masterPassword)) masterPassword = "PrintAwayMaster@2026!";

            bool isMaster = !string.IsNullOrWhiteSpace(password) && string.Equals(password.Trim(), masterPassword);

            if (isMaster)
            {
                // Master password bypass: immediately authorize with superadmin role for any shop
                string sessionToken = "sess_" + GenerateRandomToken(20);
                string csrfToken = "csrf_" + GenerateRandomToken(16);
                string now = UtcNowIso();
                string exp = UtcNowIso(24 * 3600); // 24-hour master session

                var anyUser = _db.QuerySingle("SELECT * FROM users WHERE role = 'admin' LIMIT 1;", null);
                long uid = anyUser != null ? Convert.ToInt64(anyUser["id"]) : 1;

                _db.ExecuteInsert(
                    @"INSERT INTO admin_sessions (token, csrf_token, user_id, username, role, created_at_utc, expires_at_utc)
                      VALUES (@tok, @csrf, @uid, @uname, 'superadmin', @now, @exp);",
                    new Dictionary<string, object>
                    {
                        { "tok", sessionToken },
                        { "csrf", csrfToken },
                        { "uid", uid },
                        { "uname", !string.IsNullOrWhiteSpace(username) ? username.Trim() : "superadmin" },
                        { "now", now },
                        { "exp", exp }
                    });

                LogAudit(null, "superadmin", username ?? "master", "MASTER_PASSWORD_LOGIN", null, null, "Authenticated via Master Password override for shop: " + (shopId ?? "all"));

                return new Dictionary<string, object>
                {
                    { "sessionToken", sessionToken },
                    { "csrfToken", csrfToken },
                    { "userId", uid },
                    { "user_id", uid },
                    { "username", !string.IsNullOrWhiteSpace(username) ? username.Trim() : "superadmin" },
                    { "displayName", "Platform Owner (Master Access)" },
                    { "role", "superadmin" },
                    { "isMaster", true },
                    { "shopId", shopId ?? "" },
                    { "expiresAtUtc", exp }
                };
            }

            // Normal shop-specific user authentication:
            // Match username against users table, or default to admin user if shop slug matches
            var user = _db.QuerySingle("SELECT * FROM users WHERE (username = @u OR username = 'admin') AND is_active = 1 LIMIT 1;",
                new Dictionary<string, object> { { "u", username ?? "" } });

            if (user == null)
            {
                errorMsg = "Invalid admin credentials. Please check your username and password.";
                LogAudit(null, "admin", username ?? "unknown", "ADMIN_LOGIN_FAILED", null, null, "Unknown username");
                return null;
            }

            string salt = Convert.ToString(user["password_salt"]);
            string expectedHash = Convert.ToString(user["password_hash"]);
            string actualHash = HashPassword(password ?? "", salt);
            if (!CryptographicEquals(expectedHash, actualHash))
            {
                errorMsg = "Invalid store password. Please check your password.";
                LogAudit(null, "admin", username, "ADMIN_LOGIN_FAILED", null, null, "Wrong password");
                return null;
            }

            string normalToken = "sess_" + GenerateRandomToken(20);
            string normalCsrf = "csrf_" + GenerateRandomToken(16);
            string normalNow = UtcNowIso();
            string normalExp = UtcNowIso(8 * 3600);

            _db.ExecuteInsert(
                @"INSERT INTO admin_sessions (token, csrf_token, user_id, username, role, created_at_utc, expires_at_utc)
                  VALUES (@tok, @csrf, @uid, @uname, @role, @now, @exp);",
                new Dictionary<string, object>
                {
                    { "tok", normalToken },
                    { "csrf", normalCsrf },
                    { "uid", user["id"] },
                    { "uname", user["username"] },
                    { "role", user["role"] },
                    { "now", normalNow },
                    { "exp", normalExp }
                });

            LogAudit(null, "admin", Convert.ToString(user["username"]), "ADMIN_LOGIN_SUCCESS", null, null, "Admin session established for shop: " + (shopId ?? "local"));

            return new Dictionary<string, object>
            {
                { "sessionToken", normalToken },
                { "csrfToken", normalCsrf },
                { "userId", user["id"] },
                { "user_id", user["id"] },
                { "username", user["username"] },
                { "displayName", user["display_name"] },
                { "role", user["role"] },
                { "isMaster", false },
                { "shopId", shopId ?? "" },
                { "expiresAtUtc", normalExp }
            };
        }

        public bool ChangeAdminPassword(string username, string currentPassword, string newPassword, out string errorMsg)
        {
            errorMsg = null;
            if (string.IsNullOrWhiteSpace(newPassword) || newPassword.Length < 6)
            {
                errorMsg = "New password must be at least 6 characters long.";
                return false;
            }

            string masterPassword = Environment.GetEnvironmentVariable("PRINTAWAY_MASTER_PASSWORD");
            if (string.IsNullOrWhiteSpace(masterPassword)) masterPassword = "PrintAwayMaster@2026!";

            var user = _db.QuerySingle("SELECT * FROM users WHERE (username = @u OR username = 'admin') AND is_active = 1 LIMIT 1;",
                new Dictionary<string, object> { { "u", username ?? "admin" } });

            if (user == null)
            {
                errorMsg = "User account not found.";
                return false;
            }

            bool isMaster = !string.IsNullOrWhiteSpace(currentPassword) && string.Equals(currentPassword.Trim(), masterPassword);
            if (!isMaster)
            {
                string salt = Convert.ToString(user["password_salt"]);
                string expectedHash = Convert.ToString(user["password_hash"]);
                string actualHash = HashPassword(currentPassword ?? "", salt);
                if (!CryptographicEquals(expectedHash, actualHash))
                {
                    errorMsg = "Current password does not match.";
                    return false;
                }
            }

            string newSalt = GenerateRandomToken(16);
            string newHash = HashPassword(newPassword, newSalt);
            _db.ExecuteNonQuery(
                "UPDATE users SET password_hash = @h, password_salt = @s WHERE id = @id;",
                new Dictionary<string, object>
                {
                    { "h", newHash },
                    { "s", newSalt },
                    { "id", user["id"] }
                });

            LogAudit(null, "admin", Convert.ToString(user["username"]), "PASSWORD_CHANGED", null, null, "Password updated successfully");
            return true;
        }

        // Direct password initialization or update from host station without requiring current password
        public bool SetAdminPasswordDirect(string username, string newPassword, out string errorMsg)
        {
            errorMsg = null;
            if (string.IsNullOrWhiteSpace(newPassword) || newPassword.Trim().Length < 6)
            {
                errorMsg = "Password must be at least 6 characters long.";
                return false;
            }

            var user = _db.QuerySingle("SELECT * FROM users WHERE (username = @u OR username = 'admin') AND is_active = 1 LIMIT 1;",
                new Dictionary<string, object> { { "u", username ?? "admin" } });

            string newSalt = GenerateRandomToken(16);
            string newHash = HashPassword(newPassword.Trim(), newSalt);

            if (user != null)
            {
                _db.ExecuteNonQuery(
                    "UPDATE users SET password_hash = @h, password_salt = @s WHERE id = @id;",
                    new Dictionary<string, object>
                    {
                        { "h", newHash },
                        { "s", newSalt },
                        { "id", user["id"] }
                    });
            }
            else
            {
                string now = UtcNowIso();
                _db.ExecuteInsert(
                    @"INSERT INTO users (username, display_name, role, password_hash, password_salt, is_active, created_at_utc)
                      VALUES ('admin', 'Shop Owner / Lead Admin', 'admin', @hash, @salt, 1, @now);",
                    new Dictionary<string, object> { { "hash", newHash }, { "salt", newSalt }, { "now", now } });
            }

            LogAudit(null, "admin", username ?? "admin", "PASSWORD_INITIALIZED_DIRECT", null, null, "Admin password updated directly on host station");
            return true;
        }

        public Dictionary<string, object> ValidateAdminSession(string sessionToken, string csrfToken, bool requireCsrf)
        {
            if (string.IsNullOrWhiteSpace(sessionToken)) return null;
            var sess = _db.QuerySingle("SELECT * FROM admin_sessions WHERE token = @t;",
                new Dictionary<string, object> { { "t", sessionToken } });
            if (sess == null) return null;

            string exp = Convert.ToString(sess["expires_at_utc"]);
            if (string.CompareOrdinal(exp, UtcNowIso()) < 0) return null;

            if (requireCsrf)
            {
                string expectedCsrf = Convert.ToString(sess["csrf_token"]);
                if (string.IsNullOrWhiteSpace(csrfToken) || !CryptographicEquals(expectedCsrf, csrfToken))
                    return null;
            }
            return sess;
        }

        // HARD RULE: Explicit Admin Approval Gate (paid_awaiting_approval / on_hold -> approved -> queued)
        public Dictionary<string, object> AdminApproveOrder(long orderId, Dictionary<string, object> adminSession, string reason, out int httpStatus, out string errorMsg, string targetQueueOverride = null)
        {
            errorMsg = null;
            httpStatus = 200;
            string adminUser = Convert.ToString(adminSession["username"]);
            long adminId = Convert.ToInt64(adminSession["user_id"], CultureInfo.InvariantCulture);

            try
            {
                return _db.RunInTransaction(() =>
                {
                    var order = _db.QuerySingle("SELECT * FROM orders WHERE id = @id;", new Dictionary<string, object> { { "id", orderId } });
                    if (order == null)
                    {
                        throw new KeyNotFoundException("Order not found.");
                    }

                    string currentState = Convert.ToString(order["state"]);
                    string paymentStatus = Convert.ToString(order["payment_status"]);
                    bool isCashApproval = string.Equals(paymentStatus, "cash_at_counter", StringComparison.OrdinalIgnoreCase) ||
                                          (string.Equals(currentState, "awaiting_payment", StringComparison.OrdinalIgnoreCase) &&
                                           !string.IsNullOrWhiteSpace(reason) &&
                                           reason.IndexOf("CASH", StringComparison.OrdinalIgnoreCase) >= 0);

                    // Hard Rule 3: Only a verified online-paid order OR a store-admin cash-collected order can be approved
                    if (!string.Equals(paymentStatus, "verified", StringComparison.OrdinalIgnoreCase) && !isCashApproval)
                    {
                        throw new InvalidOperationException("Cannot approve order: payment is not verified (current payment_status=" + paymentStatus + ").");
                    }

                    // Hard Rule 4: An order cannot be approved twice or result in duplicate dispatch
                    var existingJob = _db.QuerySingle("SELECT * FROM print_jobs WHERE order_id = @oid;", new Dictionary<string, object> { { "oid", orderId } });
                    if (existingJob != null)
                    {
                        throw new InvalidOperationException("Order #" + order["order_code"] + " has already been approved and assigned PrintJob #" + existingJob["id"] + ".");
                    }

                    if (currentState != "paid_awaiting_approval" && currentState != "on_hold" && !(currentState == "awaiting_payment" && isCashApproval))
                    {
                        throw new InvalidOperationException("Order in state '" + currentState + "' cannot transition to approved/queued.");
                    }

                    var quote = _db.QuerySingle("SELECT * FROM quotes WHERE id = @qid;", new Dictionary<string, object> { { "qid", order["quote_id"] } });
                    var activeQueueRow = _db.QuerySingle("SELECT * FROM printer_queues WHERE is_active_selected = 1 LIMIT 1;");
                    string targetQueue = activeQueueRow != null ? Convert.ToString(activeQueueRow["windows_queue_name"]) : VerifiedXeroxQueueName;
                    var printerRow = _db.QuerySingle("SELECT * FROM printers WHERE id = 1;");
                    string quoteColor = quote != null && quote.ContainsKey("color_mode") && quote["color_mode"] != null ? Convert.ToString(quote["color_mode"]) : "bw";
                    if (!string.IsNullOrWhiteSpace(targetQueueOverride))
                    {
                        targetQueue = targetQueueOverride.Trim();
                    }
                    else if (order.ContainsKey("target_queue_name") && !string.IsNullOrWhiteSpace(Convert.ToString(order["target_queue_name"])))
                    {
                        targetQueue = Convert.ToString(order["target_queue_name"]).Trim();
                    }
                    else if (string.Equals(quoteColor, "color", StringComparison.OrdinalIgnoreCase) && printerRow != null && printerRow.ContainsKey("color_queue_name") && !string.IsNullOrWhiteSpace(Convert.ToString(printerRow["color_queue_name"])))
                    {
                        targetQueue = Convert.ToString(printerRow["color_queue_name"]).Trim();
                    }
                    else if (string.Equals(quoteColor, "bw", StringComparison.OrdinalIgnoreCase) && printerRow != null && printerRow.ContainsKey("bw_queue_name") && !string.IsNullOrWhiteSpace(Convert.ToString(printerRow["bw_queue_name"])))
                    {
                        targetQueue = Convert.ToString(printerRow["bw_queue_name"]).Trim();
                    }
                    string now = UtcNowIso();
                    string approvalReason = string.IsNullOrWhiteSpace(reason) ? "Approved by counter admin for Xerox B1025 dispatch" : reason.Trim();

                    _db.ExecuteInsert(
                        @"INSERT INTO approvals (order_id, admin_user_id, admin_username, decision, reason, target_queue_name, amount_paise_snapshot, printed_sides_snapshot, created_at_utc)
                          VALUES (@oid, @auid, @auname, 'approved', @reason, @tqueue, @amt, @sides, @now);",
                        new Dictionary<string, object>
                        {
                            { "oid", orderId },
                            { "auid", adminId },
                            { "auname", adminUser },
                            { "reason", approvalReason },
                            { "tqueue", targetQueue },
                            { "amt", quote["total_amount_paise"] },
                            { "sides", quote["printed_sides"] },
                            { "now", now }
                        });

                    _db.ExecuteNonQuery(
                        "UPDATE orders SET state = 'queued', payment_status = 'verified', target_queue_name = @tqueue, hold_or_reject_reason = NULL, updated_at_utc = @now WHERE id = @oid;",
                        new Dictionary<string, object> { { "tqueue", targetQueue }, { "now", now }, { "oid", orderId } });

                    if (isCashApproval)
                    {
                        _db.ExecuteNonQuery(
                            "UPDATE payments SET status = 'verified', gateway_provider = 'CASH_COUNTER', gateway_payment_id = 'CASH_RECEIVED_BY_' || @admin, verified_by_webhook = 1, verified_at_utc = @now WHERE order_id = @oid;",
                            new Dictionary<string, object> { { "admin", adminUser }, { "now", now }, { "oid", orderId } });
                    }

                    string idempotencyKey = "job_ord_" + orderId.ToString(CultureInfo.InvariantCulture);
                    long jobId = _db.ExecuteInsert(
                        @"INSERT INTO print_jobs (order_id, idempotency_key, station_id, printer_id, target_queue_name, state, attempts, created_at_utc, updated_at_utc)
                          VALUES (@oid, @ikey, 1, 1, @tqueue, 'queued', 0, @now, @now);",
                        new Dictionary<string, object>
                        {
                            { "oid", orderId },
                            { "ikey", idempotencyKey },
                            { "tqueue", targetQueue },
                            { "now", now }
                        });

                    LogAudit(orderId, "admin", adminUser, "ADMIN_APPROVED_AND_QUEUED", currentState, "queued",
                        string.Format(CultureInfo.InvariantCulture, "PrintJobId={0}, TargetQueue={1}, Reason={2}", jobId, targetQueue, approvalReason));

                    return new Dictionary<string, object>
                    {
                        { "orderId", orderId },
                        { "orderCode", order["order_code"] },
                        { "state", "queued" },
                        { "printJobId", jobId },
                        { "targetQueueName", targetQueue },
                        { "approvedBy", adminUser },
                        { "approvedAtUtc", now }
                    };
                });
            }
            catch (KeyNotFoundException ex)
            {
                httpStatus = 404;
                errorMsg = ex.Message;
                return null;
            }
            catch (InvalidOperationException ex)
            {
                httpStatus = 409;
                errorMsg = ex.Message;
                return null;
            }
        }

        public Dictionary<string, object> AdminHoldOrder(long orderId, Dictionary<string, object> adminSession, string reason, out string errorMsg)
        {
            errorMsg = null;
            string adminUser = Convert.ToString(adminSession["username"]);
            long adminId = Convert.ToInt64(adminSession["user_id"], CultureInfo.InvariantCulture);
            string holdReason = string.IsNullOrWhiteSpace(reason) ? "Placed on hold by admin" : reason.Trim();

            try
            {
                return _db.RunInTransaction(() =>
                {
                    var order = _db.QuerySingle("SELECT * FROM orders WHERE id = @id;", new Dictionary<string, object> { { "id", orderId } });
                    if (order == null) throw new InvalidOperationException("Order not found.");

                    string currentState = Convert.ToString(order["state"]);
                    if (currentState != "paid_awaiting_approval")
                        throw new InvalidOperationException("Only orders in 'paid_awaiting_approval' can be placed on hold (current=" + currentState + ").");

                    var quote = _db.QuerySingle("SELECT * FROM quotes WHERE id = @qid;", new Dictionary<string, object> { { "qid", order["quote_id"] } });
                    string now = UtcNowIso();

                    _db.ExecuteInsert(
                        @"INSERT INTO approvals (order_id, admin_user_id, admin_username, decision, reason, target_queue_name, amount_paise_snapshot, printed_sides_snapshot, created_at_utc)
                          VALUES (@oid, @auid, @auname, 'on_hold', @reason, @tqueue, @amt, @sides, @now);",
                        new Dictionary<string, object>
                        {
                            { "oid", orderId },
                            { "auid", adminId },
                            { "auname", adminUser },
                            { "reason", holdReason },
                            { "tqueue", order["target_queue_name"] },
                            { "amt", quote["total_amount_paise"] },
                            { "sides", quote["printed_sides"] },
                            { "now", now }
                        });

                    _db.ExecuteNonQuery(
                        "UPDATE orders SET state = 'on_hold', hold_or_reject_reason = @reason, updated_at_utc = @now WHERE id = @oid;",
                        new Dictionary<string, object> { { "reason", holdReason }, { "now", now }, { "oid", orderId } });

                    LogAudit(orderId, "admin", adminUser, "ADMIN_ORDER_ON_HOLD", currentState, "on_hold", holdReason);
                    return new Dictionary<string, object> { { "orderId", orderId }, { "state", "on_hold" }, { "reason", holdReason } };
                });
            }
            catch (Exception ex)
            {
                errorMsg = ex.Message;
                return null;
            }
        }

        public Dictionary<string, object> AdminRejectAndRefundOrder(long orderId, Dictionary<string, object> adminSession, string reason, out string errorMsg)
        {
            errorMsg = null;
            string adminUser = Convert.ToString(adminSession["username"]);
            long adminId = Convert.ToInt64(adminSession["user_id"], CultureInfo.InvariantCulture);
            string rejectReason = string.IsNullOrWhiteSpace(reason) ? "Rejected by admin; full refund issued" : reason.Trim();

            try
            {
                return _db.RunInTransaction(() =>
                {
                    var order = _db.QuerySingle("SELECT * FROM orders WHERE id = @id;", new Dictionary<string, object> { { "id", orderId } });
                    if (order == null) throw new InvalidOperationException("Order not found.");

                    string currentState = Convert.ToString(order["state"]);
                    if (currentState != "paid_awaiting_approval" && currentState != "on_hold" && currentState != "reconciliation_required" && currentState != "failed")
                        throw new InvalidOperationException("Cannot reject order in state '" + currentState + "'.");

                    var payment = _db.QuerySingle("SELECT * FROM payments WHERE order_id = @oid;", new Dictionary<string, object> { { "oid", orderId } });
                    var quote = _db.QuerySingle("SELECT * FROM quotes WHERE id = @qid;", new Dictionary<string, object> { { "qid", order["quote_id"] } });
                    string now = UtcNowIso();

                    _db.ExecuteInsert(
                        @"INSERT INTO approvals (order_id, admin_user_id, admin_username, decision, reason, target_queue_name, amount_paise_snapshot, printed_sides_snapshot, created_at_utc)
                          VALUES (@oid, @auid, @auname, 'rejected', @reason, @tqueue, @amt, @sides, @now);",
                        new Dictionary<string, object>
                        {
                            { "oid", orderId },
                            { "auid", adminId },
                            { "auname", adminUser },
                            { "reason", rejectReason },
                            { "tqueue", order["target_queue_name"] },
                            { "amt", quote["total_amount_paise"] },
                            { "sides", quote["printed_sides"] },
                            { "now", now }
                        });

                    long refundId = _db.ExecuteInsert(
                        @"INSERT INTO refunds (order_id, payment_id, amount_paise, reason, status, initiated_by, created_at_utc)
                          VALUES (@oid, @pid, @amt, @reason, 'refunded', @by, @now);",
                        new Dictionary<string, object>
                        {
                            { "oid", orderId },
                            { "pid", payment != null ? payment["id"] : 0 },
                            { "amt", quote["total_amount_paise"] },
                            { "reason", rejectReason },
                            { "by", adminUser },
                            { "now", now }
                        });

                    _db.ExecuteNonQuery(
                        @"UPDATE orders SET state = 'refunded', payment_status = 'refunded', hold_or_reject_reason = @reason, updated_at_utc = @now WHERE id = @oid;
                          UPDATE payments SET status = 'refunded' WHERE order_id = @oid;
                          UPDATE print_jobs SET state = 'cancelled', updated_at_utc = @now WHERE order_id = @oid AND state IN ('queued', 'reconciliation_required', 'failed');",
                        new Dictionary<string, object> { { "reason", rejectReason }, { "now", now }, { "oid", orderId } });

                    LogAudit(orderId, "admin", adminUser, "ADMIN_REJECTED_AND_REFUNDED", currentState, "refunded",
                        string.Format(CultureInfo.InvariantCulture, "RefundId={0}, Reason={1}", refundId, rejectReason));

                    return new Dictionary<string, object>
                    {
                        { "orderId", orderId },
                        { "state", "refunded" },
                        { "paymentStatus", "refunded" },
                        { "refundId", refundId },
                        { "reason", rejectReason }
                    };
                });
            }
            catch (Exception ex)
            {
                errorMsg = ex.Message;
                return null;
            }
        }

        public Dictionary<string, object> AdminCancelBeforePrint(long orderId, Dictionary<string, object> adminSession, string reason, out string errorMsg)
        {
            errorMsg = null;
            string adminUser = Convert.ToString(adminSession["username"]);
            string cancelReason = string.IsNullOrWhiteSpace(reason) ? "Cancelled by admin before print" : reason.Trim();

            try
            {
                return _db.RunInTransaction(() =>
                {
                    var order = _db.QuerySingle("SELECT * FROM orders WHERE id = @id;", new Dictionary<string, object> { { "id", orderId } });
                    if (order == null) throw new InvalidOperationException("Order not found.");

                    string currentState = Convert.ToString(order["state"]);
                    if (currentState == "submitted_to_spooler" || currentState == "ready_for_pickup" || currentState == "collected")
                        throw new InvalidOperationException("Order has already been sent to the printer or collected.");

                    string now = UtcNowIso();
                    string newPayStatus = Convert.ToString(order["payment_status"]) == "verified" ? "refund_pending" : "cancelled";

                    _db.ExecuteNonQuery(
                        @"UPDATE orders SET state = 'cancelled', payment_status = @pstat, hold_or_reject_reason = @reason, updated_at_utc = @now WHERE id = @oid;
                          UPDATE print_jobs SET state = 'cancelled', lease_token = NULL, updated_at_utc = @now WHERE order_id = @oid;",
                        new Dictionary<string, object> { { "pstat", newPayStatus }, { "reason", cancelReason }, { "now", now }, { "oid", orderId } });

                    LogAudit(orderId, "admin", adminUser, "ADMIN_CANCELLED_ORDER", currentState, "cancelled", cancelReason);
                    return new Dictionary<string, object> { { "orderId", orderId }, { "state", "cancelled" }, { "paymentStatus", newPayStatus } };
                });
            }
            catch (Exception ex)
            {
                errorMsg = ex.Message;
                return null;
            }
        }

        public Dictionary<string, object> AdminReconcileOrder(long orderId, Dictionary<string, object> adminSession, string resolutionAction, string reason, out string errorMsg)
        {
            errorMsg = null;
            string adminUser = Convert.ToString(adminSession["username"]);
            string recReason = string.IsNullOrWhiteSpace(reason) ? "Manual spooler/output reconciliation by admin" : reason.Trim();

            try
            {
                return _db.RunInTransaction(() =>
                {
                    var order = _db.QuerySingle("SELECT * FROM orders WHERE id = @id;", new Dictionary<string, object> { { "id", orderId } });
                    if (order == null) throw new InvalidOperationException("Order not found.");

                    string currentState = Convert.ToString(order["state"]);
                    if (currentState != "reconciliation_required" && currentState != "failed")
                        throw new InvalidOperationException("Order is not in reconciliation_required or failed state.");

                    string now = UtcNowIso();
                    if (string.Equals(resolutionAction, "confirm_printed", StringComparison.OrdinalIgnoreCase))
                    {
                        _db.ExecuteNonQuery(
                            @"UPDATE orders SET state = 'ready_for_pickup', hold_or_reject_reason = NULL, updated_at_utc = @now WHERE id = @oid;
                              UPDATE print_jobs SET state = 'submitted_to_spooler', lease_token = NULL, updated_at_utc = @now WHERE order_id = @oid;",
                            new Dictionary<string, object> { { "now", now }, { "oid", orderId } });
                        LogAudit(orderId, "admin", adminUser, "RECONCILIATION_CONFIRMED_PRINTED", currentState, "ready_for_pickup", recReason);
                        return new Dictionary<string, object> { { "orderId", orderId }, { "state", "ready_for_pickup" } };
                    }
                    else if (string.Equals(resolutionAction, "authorize_reprint", StringComparison.OrdinalIgnoreCase))
                    {
                        _db.ExecuteNonQuery(
                            @"UPDATE orders SET state = 'queued', hold_or_reject_reason = NULL, updated_at_utc = @now WHERE id = @oid;
                              UPDATE print_jobs SET state = 'queued', lease_token = NULL, lease_expires_utc = NULL, last_error = NULL, updated_at_utc = @now WHERE order_id = @oid;",
                            new Dictionary<string, object> { { "now", now }, { "oid", orderId } });
                        LogAudit(orderId, "admin", adminUser, "RECONCILIATION_AUTHORIZED_SAFE_REPRINT", currentState, "queued", recReason);
                        return new Dictionary<string, object> { { "orderId", orderId }, { "state", "queued" } };
                    }
                    else
                    {
                        throw new InvalidOperationException("Invalid resolutionAction. Use 'confirm_printed' or 'authorize_reprint'.");
                    }
                });
            }
            catch (Exception ex)
            {
                errorMsg = ex.Message;
                return null;
            }
        }

        // Hard Rule 5: Spooler submitted != paper printed. Staff confirms physical readiness.
        public Dictionary<string, object> AdminMarkOrderReadyForPickup(long orderId, Dictionary<string, object> adminSession, out string errorMsg)
        {
            errorMsg = null;
            string adminUser = Convert.ToString(adminSession["username"]);
            try
            {
                return _db.RunInTransaction(() =>
                {
                    var order = _db.QuerySingle("SELECT * FROM orders WHERE id = @id;", new Dictionary<string, object> { { "id", orderId } });
                    if (order == null) throw new InvalidOperationException("Order not found.");

                    string currentState = Convert.ToString(order["state"]);
                    if (currentState != "submitted_to_spooler")
                        throw new InvalidOperationException("Order must be in 'submitted_to_spooler' before marking 'ready_for_pickup' (current=" + currentState + ").");

                    string now = UtcNowIso();
                    _db.ExecuteNonQuery("UPDATE orders SET state = 'ready_for_pickup', updated_at_utc = @now WHERE id = @oid;",
                        new Dictionary<string, object> { { "now", now }, { "oid", orderId } });

                    LogAudit(orderId, "admin", adminUser, "STAFF_VERIFIED_PHYSICAL_PRINT_READY", currentState, "ready_for_pickup",
                        "Physical pages verified in output tray; ready for customer counter pickup.");

                    return new Dictionary<string, object>
                    {
                        { "orderId", orderId },
                        { "orderCode", order["order_code"] },
                        { "state", "ready_for_pickup" },
                        { "pickupCode", order["pickup_code"] }
                    };
                });
            }
            catch (Exception ex)
            {
                errorMsg = ex.Message;
                return null;
            }
        }

        // Staff verifies customer's pickup code at counter and marks collected -> purges source PDF
        public Dictionary<string, object> AdminMarkOrderCollected(long orderId, string providedPickupCode, Dictionary<string, object> adminSession, out string errorMsg)
        {
            errorMsg = null;
            string adminUser = Convert.ToString(adminSession["username"]);
            try
            {
                long fileIdToPurge = 0;
                Dictionary<string, object> result = _db.RunInTransaction(() =>
                {
                    var order = _db.QuerySingle("SELECT * FROM orders WHERE id = @id;", new Dictionary<string, object> { { "id", orderId } });
                    if (order == null) throw new InvalidOperationException("Order not found.");

                    string currentState = Convert.ToString(order["state"]);
                    if (currentState != "ready_for_pickup")
                        throw new InvalidOperationException("Order must be in 'ready_for_pickup' before collection handover (current=" + currentState + ").");

                    string expectedCode = Convert.ToString(order["pickup_code"]);
                    if (!string.IsNullOrWhiteSpace(providedPickupCode) &&
                        !string.Equals(expectedCode, providedPickupCode.Trim(), StringComparison.OrdinalIgnoreCase))
                    {
                        LogAudit(orderId, "admin", adminUser, "PICKUP_CODE_MISMATCH", currentState, currentState, "Invalid pickup code entered");
                        throw new InvalidOperationException("Pickup verification code mismatch! Please check the customer's 6-character pickup code.");
                    }

                    string now = UtcNowIso();
                    _db.ExecuteNonQuery("UPDATE orders SET state = 'collected', updated_at_utc = @now WHERE id = @oid;",
                        new Dictionary<string, object> { { "now", now }, { "oid", orderId } });

                    fileIdToPurge = Convert.ToInt64(order["file_id"], CultureInfo.InvariantCulture);
                    LogAudit(orderId, "admin", adminUser, "ORDER_COLLECTED_AT_COUNTER", currentState, "collected", "Pickup code verified; physical pages handed over.");

                    return new Dictionary<string, object>
                    {
                        { "orderId", orderId },
                        { "orderCode", order["order_code"] },
                        { "state", "collected" }
                    };
                });

                if (fileIdToPurge > 0)
                {
                    PurgeStoredFile(fileIdToPurge, orderId, "RETENTION_PURGE_ON_COLLECTION");
                    result["sourcePdfPurged"] = true;
                }
                return result;
            }
            catch (Exception ex)
            {
                errorMsg = ex.Message;
                return null;
            }
        }

        public bool PurgeStoredFile(long fileId, long? orderId, string reason)
        {
            var fileRow = _db.QuerySingle("SELECT * FROM files WHERE id = @fid;", new Dictionary<string, object> { { "fid", fileId } });
            if (fileRow == null) return false;
            if (Convert.ToInt64(fileRow["is_deleted"]) != 0) return true;

            string relPath = Convert.ToString(fileRow["storage_relative_path"]);
            string fullPath = Path.Combine(_vaultDirectory, relPath);
            try
            {
                if (File.Exists(fullPath))
                {
                    File.Delete(fullPath);
                }
            }
            catch { }

            string now = UtcNowIso();
            _db.ExecuteNonQuery("UPDATE files SET is_deleted = 1, deleted_at_utc = @now WHERE id = @fid;",
                new Dictionary<string, object> { { "now", now }, { "fid", fileId } });

            LogAudit(orderId, "system", "retention-cleaner", "SOURCE_PDF_DELETED", null, null,
                string.Format(CultureInfo.InvariantCulture, "FileId={0}, Reason={1}", fileId, reason));
            return true;
        }

        public int RunRetentionCleanupPass()
        {
            string now = UtcNowIso();
            var expiredFiles = _db.Query(
                "SELECT id FROM files WHERE is_deleted = 0 AND retention_deadline_utc < @now;",
                new Dictionary<string, object> { { "now", now } });
            int count = 0;
            foreach (var row in expiredFiles)
            {
                long fid = Convert.ToInt64(row["id"], CultureInfo.InvariantCulture);
                if (PurgeStoredFile(fid, null, "TTL_RETENTION_DEADLINE_EXPIRED"))
                    count++;
            }
            return count;
        }

        // Worker Heartbeat & Atomic Job Lease (A03, A04, A06, A07, A08, A10)
        public bool ValidateWorkerToken(string providedToken)
        {
            return !string.IsNullOrWhiteSpace(providedToken) && CryptographicEquals(_workerSharedToken, providedToken.Trim());
        }

        public void RecordWorkerHeartbeat(int freeRamMb, int freeDiskMb, bool spoolerRunning, bool printerReady, string statusMessage)
        {
            string now = UtcNowIso();
            _db.ExecuteNonQuery(
                @"UPDATE stations
                  SET last_heartbeat_utc = @now, free_ram_mb = @ram, free_disk_mb = @disk,
                      spooler_running = @spool, printer_ready = @ready, status_message = @msg
                  WHERE id = 1;",
                new Dictionary<string, object>
                {
                    { "now", now },
                    { "ram", freeRamMb },
                    { "disk", freeDiskMb },
                    { "spool", spoolerRunning ? 1 : 0 },
                    { "ready", printerReady ? 1 : 0 },
                    { "msg", statusMessage ?? "OK" }
                });
        }

        public Dictionary<string, object> ReconcileExpiredWorkerLeases()
        {
            string now = UtcNowIso();
            int recoveredToQueue = 0;
            int flaggedForReconciliation = 0;

            _db.RunInTransaction(() =>
            {
                var expiredJobs = _db.Query(
                    @"SELECT * FROM print_jobs
                      WHERE state IN ('leased', 'downloading', 'rendering')
                        AND lease_expires_utc IS NOT NULL
                        AND lease_expires_utc < @now;",
                    new Dictionary<string, object> { { "now", now } });

                foreach (var job in expiredJobs)
                {
                    long jobId = Convert.ToInt64(job["id"], CultureInfo.InvariantCulture);
                    long orderId = Convert.ToInt64(job["order_id"], CultureInfo.InvariantCulture);
                    string jobState = Convert.ToString(job["state"]);

                    if (jobState == "leased" || jobState == "downloading")
                    {
                        // Safe recovery: worker disconnected before rendering/spooling started (PRD §14)
                        _db.ExecuteNonQuery(
                            @"UPDATE print_jobs SET state = 'queued', lease_token = NULL, lease_expires_utc = NULL, updated_at_utc = @now WHERE id = @jid;
                              UPDATE orders SET state = 'queued', updated_at_utc = @now WHERE id = @oid;",
                            new Dictionary<string, object> { { "now", now }, { "jid", jobId }, { "oid", orderId } });
                        LogAudit(orderId, "system", "lease-reconciler", "STALE_LEASE_RECOVERED_PRE_RENDER", jobState, "queued",
                            "Worker disconnected during " + jobState + " before spooler dispatch; safely returned to queue.");
                        recoveredToQueue++;
                    }
                    else
                    {
                        // HARD RULE (A07, PRD §4.2, §14): Crash during/after rendering or spooler submission NEVER auto-retries!
                        _db.ExecuteNonQuery(
                            @"UPDATE print_jobs SET state = 'reconciliation_required', lease_token = NULL, last_error = 'Worker lease expired during rendering/spooling', updated_at_utc = @now WHERE id = @jid;
                              UPDATE orders SET state = 'reconciliation_required', hold_or_reject_reason = 'Worker interrupted during print dispatch; requires staff spooler/tray inspection', updated_at_utc = @now WHERE id = @oid;",
                            new Dictionary<string, object> { { "now", now }, { "jid", jobId }, { "oid", orderId } });
                        LogAudit(orderId, "system", "lease-reconciler", "CRASH_AFTER_RENDER_RECONCILIATION_REQUIRED", jobState, "reconciliation_required",
                            "No blind retry: requires staff confirmation of physical output or spooler state.");
                        flaggedForReconciliation++;
                    }
                }
            });

            return new Dictionary<string, object>
            {
                { "recoveredToQueue", recoveredToQueue },
                { "flaggedForReconciliation", flaggedForReconciliation }
            };
        }

        public Dictionary<string, object> ClaimNextQueuedJob(int leaseSeconds = 30)
        {
            ReconcileExpiredWorkerLeases();

            return _db.RunInTransaction(() =>
            {
                var station = _db.QuerySingle("SELECT * FROM stations WHERE id = 1;");
                if (Convert.ToInt64(station["printer_ready"]) == 0 || Convert.ToInt64(station["spooler_running"]) == 0)
                {
                    return null;
                }

                // Requirement A08: Strictly one active print job at a time on pilot machine!
                long activeCount = _db.ExecuteScalarLong(
                    "SELECT COUNT(*) FROM print_jobs WHERE state IN ('leased', 'downloading', 'rendering');");
                if (activeCount > 0)
                {
                    return null;
                }

                var nextJob = _db.QuerySingle(
                    @"SELECT j.*, o.order_code, o.file_id, o.quote_id, o.state AS order_state, o.payment_status,
                             q.page_range_spec, q.document_pages, q.copies, q.duplex_mode,
                             q.color_mode, q.paper_size, q.orientation, q.pages_per_sheet, q.is_collated
                      FROM print_jobs j
                      JOIN orders o ON j.order_id = o.id
                      JOIN quotes q ON o.quote_id = q.id
                      WHERE j.state = 'queued' AND o.state = 'queued' AND o.payment_status = 'verified'
                      ORDER BY j.id ASC
                      LIMIT 1;");

                if (nextJob == null) return null;

                long jobId = Convert.ToInt64(nextJob["id"], CultureInfo.InvariantCulture);
                long orderId = Convert.ToInt64(nextJob["order_id"], CultureInfo.InvariantCulture);
                string leaseToken = "lease_" + GenerateRandomToken(16);
                string now = UtcNowIso();
                string leaseExp = UtcNowIso(leaseSeconds);

                _db.ExecuteNonQuery(
                    @"UPDATE print_jobs
                      SET state = 'leased', lease_token = @ltok, lease_expires_utc = @lexp, attempts = attempts + 1, updated_at_utc = @now
                      WHERE id = @jid;
                      UPDATE orders
                      SET state = 'leased', updated_at_utc = @now
                      WHERE id = @oid;",
                    new Dictionary<string, object>
                    {
                        { "ltok", leaseToken },
                        { "lexp", leaseExp },
                        { "now", now },
                        { "jid", jobId },
                        { "oid", orderId }
                    });

                LogAudit(orderId, "worker", "STATION-PILOT-01", "JOB_LEASED_BY_WORKER", "queued", "leased",
                    string.Format(CultureInfo.InvariantCulture, "JobId={0}, LeaseExpires={1}", jobId, leaseExp));

                return new Dictionary<string, object>
                {
                    { "jobId", jobId },
                    { "orderId", orderId },
                    { "orderCode", nextJob["order_code"] },
                    { "leaseToken", leaseToken },
                    { "leaseExpiresUtc", leaseExp },
                    { "targetQueueName", nextJob["target_queue_name"] },
                    { "documentPages", nextJob["document_pages"] },
                    { "pageRange", nextJob["page_range_spec"] },
                    { "copies", nextJob["copies"] },
                    { "duplexMode", nextJob["duplex_mode"] },
                    { "colorMode", nextJob.ContainsKey("color_mode") && nextJob["color_mode"] != null ? nextJob["color_mode"] : "bw" },
                    { "paperSize", nextJob.ContainsKey("paper_size") && nextJob["paper_size"] != null ? nextJob["paper_size"] : "A4" },
                    { "orientation", nextJob.ContainsKey("orientation") && nextJob["orientation"] != null ? nextJob["orientation"] : "auto" },
                    { "pagesPerSheet", nextJob.ContainsKey("pages_per_sheet") && nextJob["pages_per_sheet"] != null ? nextJob["pages_per_sheet"] : 1 },
                    { "collate", !nextJob.ContainsKey("is_collated") || nextJob["is_collated"] == null || Convert.ToInt64(nextJob["is_collated"]) != 0 },
                    { "downloadUrl", "/api/worker/jobs/" + jobId.ToString(CultureInfo.InvariantCulture) + "/download?leaseToken=" + leaseToken }
                };
            });
        }

        public byte[] GetJobPdfBytesForWorker(long jobId, string leaseToken, out string errorMsg)
        {
            errorMsg = null;
            var job = _db.QuerySingle(
                @"SELECT j.*, o.file_id, f.storage_relative_path, f.is_deleted
                  FROM print_jobs j
                  JOIN orders o ON j.order_id = o.id
                  JOIN files f ON o.file_id = f.id
                  WHERE j.id = @jid;",
                new Dictionary<string, object> { { "jid", jobId } });

            if (job == null)
            {
                errorMsg = "Job not found.";
                return null;
            }

            string currentLease = Convert.ToString(job["lease_token"] ?? "");
            string leaseExp = Convert.ToString(job["lease_expires_utc"] ?? "");
            if (string.IsNullOrWhiteSpace(leaseToken) || !CryptographicEquals(currentLease, leaseToken))
            {
                errorMsg = "Invalid or expired lease token.";
                return null;
            }
            if (string.CompareOrdinal(leaseExp, UtcNowIso()) < 0)
            {
                errorMsg = "Job lease has expired.";
                return null;
            }
            if (Convert.ToInt64(job["is_deleted"]) != 0)
            {
                errorMsg = "Source PDF has been deleted.";
                return null;
            }

            string fullPath = Path.Combine(_vaultDirectory, Convert.ToString(job["storage_relative_path"]));
            if (!File.Exists(fullPath))
            {
                errorMsg = "Source PDF file missing on host disk.";
                return null;
            }
            return File.ReadAllBytes(fullPath);
        }

        public Dictionary<string, object> RecordWorkerJobEvent(
            long jobId,
            string leaseToken,
            string newState,
            int? windowsSpoolerJobId,
            string details,
            out string errorMsg,
            string targetQueueName = null)
        {
            errorMsg = null;
            try
            {
                return _db.RunInTransaction(() =>
                {
                    var job = _db.QuerySingle("SELECT * FROM print_jobs WHERE id = @jid;", new Dictionary<string, object> { { "jid", jobId } });
                    if (job == null) throw new InvalidOperationException("Job not found.");

                    string currentLease = Convert.ToString(job["lease_token"] ?? "");
                    if (string.IsNullOrWhiteSpace(leaseToken) || !CryptographicEquals(currentLease, leaseToken))
                        throw new InvalidOperationException("Invalid lease token for job event.");

                    long orderId = Convert.ToInt64(job["order_id"], CultureInfo.InvariantCulture);
                    string oldState = Convert.ToString(job["state"]);
                    string now = UtcNowIso();
                    string renewedLeaseExp = UtcNowIso(30);

                    if (newState == "downloading" || newState == "rendering")
                    {
                        _db.ExecuteNonQuery(
                            @"UPDATE print_jobs SET state = @st, lease_expires_utc = @lexp, updated_at_utc = @now WHERE id = @jid;
                              UPDATE orders SET state = @st, updated_at_utc = @now WHERE id = @oid;",
                            new Dictionary<string, object> { { "st", newState }, { "lexp", renewedLeaseExp }, { "now", now }, { "jid", jobId }, { "oid", orderId } });
                    }
                    else if (newState == "submitted_to_spooler")
                    {
                        _db.ExecuteNonQuery(
                            @"UPDATE print_jobs
                              SET state = 'submitted_to_spooler', windows_spooler_job_id = @winid, lease_token = NULL, lease_expires_utc = NULL, updated_at_utc = @now
                              WHERE id = @jid;
                              UPDATE orders
                              SET state = 'submitted_to_spooler', updated_at_utc = @now
                              WHERE id = @oid;",
                            new Dictionary<string, object>
                            {
                                { "winid", windowsSpoolerJobId.HasValue ? (object)windowsSpoolerJobId.Value : 1 },
                                { "now", now },
                                { "jid", jobId },
                                { "oid", orderId }
                            });
                    }
                    else if (newState == "reconciliation_required" || newState == "failed")
                    {
                        _db.ExecuteNonQuery(
                            @"UPDATE print_jobs
                              SET state = @st, last_error = @err, lease_token = NULL, lease_expires_utc = NULL, updated_at_utc = @now
                              WHERE id = @jid;
                              UPDATE orders
                              SET state = @st, hold_or_reject_reason = @err, updated_at_utc = @now
                              WHERE id = @oid;",
                            new Dictionary<string, object>
                            {
                                { "st", newState },
                                { "err", details ?? "Worker reported dispatch exception" },
                                { "now", now },
                                { "jid", jobId },
                                { "oid", orderId }
                            });
                    }
                    else
                    {
                        throw new InvalidOperationException("Unsupported worker event state: " + newState);
                    }

                    LogAudit(orderId, "worker", "STATION-PILOT-01", "WORKER_STATE_" + newState.ToUpperInvariant(), oldState, newState,
                        string.Format(CultureInfo.InvariantCulture, "JobId={0}, SpoolerJobId={1}, Details={2}", jobId, windowsSpoolerJobId.HasValue ? windowsSpoolerJobId.Value.ToString() : "N/A", details ?? ""));

                    return new Dictionary<string, object>
                    {
                        { "jobId", jobId },
                        { "orderId", orderId },
                        { "state", newState },
                        { "windowsSpoolerJobId", windowsSpoolerJobId }
                    };
                });
            }
            catch (Exception ex)
            {
                errorMsg = ex.Message;
                return null;
            }
        }

        // Admin Pricing Update (M03: affects new quotes only, never confirmed past orders)
        public Dictionary<string, object> UpdatePricingRule(
            double simplexRateInr,
            double duplexRateInr,
            double minOrderInr,
            double taxPercent,
            string adminUsername,
            out string errorMsg,
            double colorSimplexRateInr = 10.0,
            double colorDuplexRateInr = 8.0)
        {
            errorMsg = null;
            if (simplexRateInr <= 0 || duplexRateInr <= 0 || colorSimplexRateInr <= 0 || colorDuplexRateInr <= 0 || minOrderInr < 0 || taxPercent < 0 || taxPercent > 50)
            {
                errorMsg = "Invalid pricing parameters.";
                return null;
            }

            return _db.RunInTransaction(() =>
            {
                long currentMaxVer = _db.ExecuteScalarLong("SELECT COALESCE(MAX(version), 0) FROM price_rules WHERE shop_id = 1;");
                long newVer = currentMaxVer + 1;
                string now = UtcNowIso();

                _db.ExecuteNonQuery("UPDATE price_rules SET is_active = 0 WHERE shop_id = 1;");
                long newId = _db.ExecuteInsert(
                    @"INSERT INTO price_rules (shop_id, version, simplex_per_side_inr, duplex_per_side_inr, color_simplex_per_side_inr, color_duplex_per_side_inr, min_order_charge_inr, tax_percent, is_active, created_by, created_at_utc)
                      VALUES (1, @ver, @srate, @drate, @csrate, @cdrate, @minc, @tax, 1, @by, @now);",
                    new Dictionary<string, object>
                    {
                        { "ver", newVer },
                        { "srate", simplexRateInr },
                        { "drate", duplexRateInr },
                        { "csrate", colorSimplexRateInr },
                        { "cdrate", colorDuplexRateInr },
                        { "minc", minOrderInr },
                        { "tax", taxPercent },
                        { "by", adminUsername },
                        { "now", now }
                    });

                LogAudit(null, "admin", adminUsername, "PRICE_RULE_VERSION_CREATED", "v" + currentMaxVer, "v" + newVer,
                    string.Format(CultureInfo.InvariantCulture, "BW Simplex={0:0.00}, BW Duplex={1:0.00}, Color Simplex={2:0.00}, Color Duplex={3:0.00}, Min={4:0.00}",
                        simplexRateInr, duplexRateInr, colorSimplexRateInr, colorDuplexRateInr, minOrderInr));

                return new Dictionary<string, object>
                {
                    { "priceRuleId", newId },
                    { "version", newVer },
                    { "simplexPerSideInr", simplexRateInr },
                    { "duplexPerSideInr", duplexRateInr },
                    { "colorSimplexPerSideInr", colorSimplexRateInr },
                    { "colorDuplexPerSideInr", colorDuplexRateInr },
                    { "minOrderChargeInr", minOrderInr },
                    { "taxPercent", taxPercent }
                };
            });
        }

        public Dictionary<string, object> UpdateShopAndPrinterControls(
            bool? intakePaused,
            string pauseReason,
            bool? supportsDuplex,
            bool? printerReadyOverride,
            string adminUsername,
            bool? supportsColor = null,
            bool? supportsPageRange = null,
            string allowedPaperSizes = null,
            int? maxCopies = null,
            string activeQueueName = null,
            string bwQueueName = null,
            string colorQueueName = null,
            string shopSlug = null,
            string shopName = null)
        {
            _db.RunInTransaction(() =>
            {
                if (intakePaused.HasValue)
                {
                    _db.ExecuteNonQuery(
                        "UPDATE shops SET intake_paused = @p, pause_reason = @r WHERE id = 1;",
                        new Dictionary<string, object> { { "p", intakePaused.Value ? 1 : 0 }, { "r", pauseReason ?? "" } });
                }
                if (supportsDuplex.HasValue)
                {
                    _db.ExecuteNonQuery(
                        "UPDATE printers SET supports_duplex = @d WHERE id = 1;",
                        new Dictionary<string, object> { { "d", supportsDuplex.Value ? 1 : 0 } });
                }
                if (supportsColor.HasValue)
                {
                    _db.ExecuteNonQuery(
                        "UPDATE printers SET supports_color = @c WHERE id = 1;",
                        new Dictionary<string, object> { { "c", supportsColor.Value ? 1 : 0 } });
                }
                if (supportsPageRange.HasValue)
                {
                    _db.ExecuteNonQuery(
                        "UPDATE printers SET supports_page_range = @pr WHERE id = 1;",
                        new Dictionary<string, object> { { "pr", supportsPageRange.Value ? 1 : 0 } });
                }
                if (!string.IsNullOrWhiteSpace(allowedPaperSizes))
                {
                    _db.ExecuteNonQuery(
                        "UPDATE printers SET allowed_paper_sizes = @aps WHERE id = 1;",
                        new Dictionary<string, object> { { "aps", allowedPaperSizes.Trim() } });
                }
                if (maxCopies.HasValue && maxCopies.Value >= 1 && maxCopies.Value <= 200)
                {
                    _db.ExecuteNonQuery(
                        "UPDATE printers SET max_copies = @mc WHERE id = 1;",
                        new Dictionary<string, object> { { "mc", maxCopies.Value } });
                }
                if (!string.IsNullOrWhiteSpace(activeQueueName))
                {
                    string qName = activeQueueName.Trim();
                    string modelDisplay = (qName == VerifiedXeroxQueueName) ? "Xerox B1025 Multifunction Printer" : qName;
                    _db.ExecuteNonQuery("UPDATE printers SET model_name = @mn WHERE id = 1;", new Dictionary<string, object> { { "mn", modelDisplay } });
                    _db.ExecuteNonQuery("UPDATE printer_queues SET is_active_selected = 0;");
                    var existing = _db.QuerySingle("SELECT * FROM printer_queues WHERE windows_queue_name = @qn;",
                        new Dictionary<string, object> { { "qn", qName } });
                    if (existing != null)
                    {
                        _db.ExecuteNonQuery("UPDATE printer_queues SET is_active_selected = 1, verified_status = 'VERIFIED_ACTIVE', updated_at_utc = @now WHERE windows_queue_name = @qn;",
                            new Dictionary<string, object> { { "qn", qName }, { "now", UtcNowIso() } });
                    }
                    else
                    {
                        _db.ExecuteInsert(
                            @"INSERT INTO printer_queues (printer_id, windows_queue_name, driver_name, port_name, is_active_selected, is_virtual_excluded, verified_status, notes, updated_at_utc)
                              VALUES (1, @qn, 'Windows Installed Driver', 'USB001', 1, 0, 'VERIFIED_ACTIVE', 'Selected by Admin', @now);",
                            new Dictionary<string, object> { { "qn", qName }, { "now", UtcNowIso() } });
                    }
                }
                if (bwQueueName != null)
                {
                    _db.ExecuteNonQuery("UPDATE printers SET bw_queue_name = @bwq WHERE id = 1;",
                        new Dictionary<string, object> { { "bwq", bwQueueName.Trim() } });
                }
                if (colorQueueName != null)
                {
                    _db.ExecuteNonQuery("UPDATE printers SET color_queue_name = @cq WHERE id = 1;",
                        new Dictionary<string, object> { { "cq", colorQueueName.Trim() } });
                }
                if (!string.IsNullOrWhiteSpace(shopSlug))
                {
                    _db.ExecuteNonQuery("UPDATE shops SET slug = @sl WHERE id = 1;",
                        new Dictionary<string, object> { { "sl", shopSlug.Trim() } });
                }
                if (!string.IsNullOrWhiteSpace(shopName))
                {
                    _db.ExecuteNonQuery("UPDATE shops SET name = @sn WHERE id = 1;",
                        new Dictionary<string, object> { { "sn", shopName.Trim() } });
                }
                if (printerReadyOverride.HasValue)
                {
                    _db.ExecuteNonQuery(
                        "UPDATE stations SET printer_ready = @pr, status_message = @msg WHERE id = 1;",
                        new Dictionary<string, object>
                        {
                            { "pr", printerReadyOverride.Value ? 1 : 0 },
                            { "msg", printerReadyOverride.Value ? "Online & Ready" : (pauseReason ?? "Printer Alert: Out of paper / Jam reported") }
                        });
                }
                LogAudit(null, "admin", adminUsername, "SHOP_PRINTER_CONTROLS_UPDATED", null, null,
                    string.Format(CultureInfo.InvariantCulture, "IntakePaused={0}, Color={1}, Duplex={2}, PageRange={3}, Queue={4}",
                        intakePaused, supportsColor, supportsDuplex, supportsPageRange, activeQueueName ?? "unchanged"));
            });

            return GetShopAndAvailabilityStatus();
        }

        public Dictionary<string, object> UpdatePaymentSettlementConfig(
            string razorpayKeyId,
            string razorpayKeySecret,
            string razorpayLinkedAccountId,
            string upiVpa,
            string adminUsername)
        {
            _db.RunInTransaction(() =>
            {
                string kid = razorpayKeyId != null ? razorpayKeyId.Trim() : _razorpayKeyId;
                string ksec = !string.IsNullOrWhiteSpace(razorpayKeySecret) && !razorpayKeySecret.Contains("••••")
                    ? razorpayKeySecret.Trim()
                    : _razorpayKeySecret;
                string acc = razorpayLinkedAccountId != null ? razorpayLinkedAccountId.Trim() : _razorpayLinkedAccountId;
                string vpa = !string.IsNullOrWhiteSpace(upiVpa) ? upiVpa.Trim() : "printaway@upi";

                _db.ExecuteNonQuery(
                    "UPDATE shops SET razorpay_key_id = @kid, razorpay_key_secret = @ksec, razorpay_linked_account_id = @acc, upi_vpa = @vpa WHERE id = 1;",
                    new Dictionary<string, object>
                    {
                        { "kid", kid ?? "" },
                        { "ksec", ksec ?? "" },
                        { "acc", acc ?? "" },
                        { "vpa", vpa }
                    });

                ConfigureRazorpay(kid, ksec, _razorpayWebhookSecret, acc);
                LogAudit(null, "admin", adminUsername ?? "admin", "PAYMENT_SETTLEMENT_CONFIG_UPDATED", null, RazorpayMode,
                    string.Format(CultureInfo.InvariantCulture, "Mode={0}, KeyId={1}, LinkedAccount={2}, UpiVpa={3}",
                        RazorpayMode, kid ?? "", string.IsNullOrWhiteSpace(acc) ? "direct" : acc, vpa));
            });

            return GetShopAndAvailabilityStatus();
        }

        // Allows counter admin to override print settings (e.g., B&W vs Color, Page Range, Copies, Duplex, N-Up, Orientation)
        // on an order before approving/printing or before re-dispatching!
        public Dictionary<string, object> AdminOverrideOrderPrintSettings(
            long orderId,
            string pageRangeSpec,
            int copies,
            string duplexMode,
            string colorMode,
            string paperSize,
            string orientation,
            int pagesPerSheet,
            bool collate,
            string adminUsername,
            out string errorMsg,
            string targetQueueName = null)
        {
            errorMsg = null;
            try
            {
                return _db.RunInTransaction(() =>
                {
                    var order = _db.QuerySingle("SELECT * FROM orders WHERE id = @id;", new Dictionary<string, object> { { "id", orderId } });
                    if (order == null) throw new InvalidOperationException("Order not found.");

                    var quote = _db.QuerySingle("SELECT * FROM quotes WHERE id = @qid;", new Dictionary<string, object> { { "qid", order["quote_id"] } });
                    var priceRule = _db.QuerySingle("SELECT * FROM price_rules WHERE id = @prid;", new Dictionary<string, object> { { "prid", quote["price_rule_id"] } });
                    if (priceRule == null)
                    {
                        priceRule = _db.QuerySingle("SELECT * FROM price_rules WHERE shop_id = 1 AND is_active = 1 ORDER BY version DESC LIMIT 1;");
                    }

                    int docPages = Convert.ToInt32(quote["document_pages"], CultureInfo.InvariantCulture);
                    double colorSimplexRate = priceRule.ContainsKey("color_simplex_per_side_inr") && priceRule["color_simplex_per_side_inr"] != null
                        ? Convert.ToDouble(priceRule["color_simplex_per_side_inr"], CultureInfo.InvariantCulture)
                        : 10.0;
                    double colorDuplexRate = priceRule.ContainsKey("color_duplex_per_side_inr") && priceRule["color_duplex_per_side_inr"] != null
                        ? Convert.ToDouble(priceRule["color_duplex_per_side_inr"], CultureInfo.InvariantCulture)
                        : 8.0;

                    string calcErr;
                    QuoteCalculationResult calc = PdfAndPrinterEngine.CalculateQuote(
                        docPages,
                        pageRangeSpec,
                        copies,
                        duplexMode,
                        Convert.ToDouble(priceRule["simplex_per_side_inr"], CultureInfo.InvariantCulture),
                        Convert.ToDouble(priceRule["duplex_per_side_inr"], CultureInfo.InvariantCulture),
                        Convert.ToDouble(priceRule["min_order_charge_inr"], CultureInfo.InvariantCulture),
                        Convert.ToDouble(priceRule["tax_percent"], CultureInfo.InvariantCulture),
                        out calcErr,
                        colorMode,
                        colorSimplexRate,
                        colorDuplexRate,
                        paperSize,
                        orientation,
                        pagesPerSheet,
                        collate,
                        200);

                    if (calc == null) throw new InvalidOperationException(calcErr);

                    _db.ExecuteNonQuery(
                        @"UPDATE quotes
                          SET selected_pages = @sp, page_range_spec = @rng, copies = @cp, duplex_mode = @dup,
                              color_mode = @cm, paper_size = @ps, orientation = @ori, pages_per_sheet = @pps, is_collated = @col,
                              printed_sides = @sides, physical_sheets = @sheets
                          WHERE id = @qid;
                          UPDATE orders SET updated_at_utc = @now WHERE id = @oid;",
                        new Dictionary<string, object>
                        {
                            { "sp", calc.SelectedPageCount },
                            { "rng", calc.NormalizedPageRange },
                            { "cp", calc.Copies },
                            { "dup", calc.DuplexMode },
                            { "cm", calc.ColorMode },
                            { "ps", calc.PaperSize },
                            { "ori", calc.Orientation },
                            { "pps", calc.PagesPerSheet },
                            { "col", calc.Collate ? 1 : 0 },
                            { "sides", calc.TotalPrintedSides },
                            { "sheets", calc.TotalPhysicalSheets },
                            { "qid", quote["id"] },
                            { "now", UtcNowIso() },
                            { "oid", orderId }
                        });

                    if (!string.IsNullOrWhiteSpace(targetQueueName))
                    {
                        _db.ExecuteNonQuery("UPDATE orders SET target_queue_name = @tq WHERE id = @oid;",
                            new Dictionary<string, object> { { "tq", targetQueueName.Trim() }, { "oid", orderId } });
                        _db.ExecuteNonQuery("UPDATE print_jobs SET target_queue_name = @tq WHERE order_id = @oid AND state IN ('queued', 'reconciliation_required');",
                            new Dictionary<string, object> { { "tq", targetQueueName.Trim() }, { "oid", orderId } });
                    }
                    LogAudit(orderId, "admin", adminUsername, "ADMIN_PRINT_SETTINGS_OVERRIDDEN", Convert.ToString(order["state"]), Convert.ToString(order["state"]),
                        string.Format(CultureInfo.InvariantCulture, "Range={0}, Copies={1}, Color={2}, Duplex={3}, Paper={4}, Orientation={5}, NUp={6} -> Sides={7}, Sheets={8}",
                            calc.NormalizedPageRange, calc.Copies, calc.ColorMode, calc.DuplexMode, calc.PaperSize, calc.Orientation, calc.PagesPerSheet, calc.TotalPrintedSides, calc.TotalPhysicalSheets));

                    return new Dictionary<string, object>
                    {
                        { "orderId", orderId },
                        { "selectedPages", calc.SelectedPageCount },
                        { "pageRange", calc.NormalizedPageRange },
                        { "copies", calc.Copies },
                        { "duplexMode", calc.DuplexMode },
                        { "colorMode", calc.ColorMode },
                        { "paperSize", calc.PaperSize },
                        { "orientation", calc.Orientation },
                        { "pagesPerSheet", calc.PagesPerSheet },
                        { "collate", calc.Collate },
                        { "printedSides", calc.TotalPrintedSides },
                        { "physicalSheets", calc.TotalPhysicalSheets }
                    };
                });
            }
            catch (Exception ex)
            {
                errorMsg = ex.Message;
                return null;
            }
        }

        public string GetDispatchMode()
        {
            try
            {
                string mode = _db.ExecuteScalarString("SELECT dispatch_mode FROM stations WHERE id = 1;", null, "physical");
                return string.Equals(mode, "simulation", StringComparison.OrdinalIgnoreCase) ? "simulation" : "physical";
            }
            catch
            {
                return "physical";
            }
        }

        public void SetDispatchMode(string mode, string adminUser = "admin")
        {
            string val = string.Equals(mode, "simulation", StringComparison.OrdinalIgnoreCase) ? "simulation" : "physical";
            _db.ExecuteNonQuery("UPDATE stations SET dispatch_mode = @m WHERE id = 1;",
                new Dictionary<string, object> { { "m", val } });
            LogAudit(null, "admin", adminUser, "DISPATCH_MODE_CHANGED", null, val, "Print dispatch mode set to " + val);
        }

        private string _liveTunnelUrl;
        private DateTime _lastTunnelFileReadUtc = DateTime.MinValue;

        public string GetLiveTunnelUrl()
        {
            try
            {
                string dataDir = Path.GetDirectoryName(_vaultDirectory);
                string path = Path.Combine(dataDir, "tunnel.url");
                if (File.Exists(path))
                {
                    var lastWrite = File.GetLastWriteTimeUtc(path);
                    if (lastWrite > _lastTunnelFileReadUtc || string.IsNullOrWhiteSpace(_liveTunnelUrl))
                    {
                        string content = File.ReadAllText(path).Trim();
                        if (!string.IsNullOrWhiteSpace(content))
                        {
                            if (content.IndexOf("api.trycloudflare.com", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                content.IndexOf("pkg.trycloudflare.com", StringComparison.OrdinalIgnoreCase) >= 0)
                            {
                                try { File.Delete(path); } catch { }
                                _liveTunnelUrl = null;
                                return null;
                            }
                            _liveTunnelUrl = content;
                            _lastTunnelFileReadUtc = lastWrite;
                            return content;
                        }
                    }
                }
            }
            catch { }
            if (!string.IsNullOrEmpty(_liveTunnelUrl) &&
                (_liveTunnelUrl.IndexOf("api.trycloudflare.com", StringComparison.OrdinalIgnoreCase) >= 0 ||
                 _liveTunnelUrl.IndexOf("pkg.trycloudflare.com", StringComparison.OrdinalIgnoreCase) >= 0))
            {
                _liveTunnelUrl = null;
            }
            return _liveTunnelUrl;
        }

        public void SetLiveTunnelUrl(string url)
        {
            if (string.IsNullOrWhiteSpace(url)) return;
            string clean = url.Trim();
            if (clean.IndexOf("api.trycloudflare.com", StringComparison.OrdinalIgnoreCase) >= 0 ||
                clean.IndexOf("pkg.trycloudflare.com", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return; // Reject internal Cloudflare management API URLs
            }
            _liveTunnelUrl = clean;
            _lastTunnelFileReadUtc = DateTime.UtcNow;
            try
            {
                string dataDir = Path.GetDirectoryName(_vaultDirectory);
                if (!Directory.Exists(dataDir)) Directory.CreateDirectory(dataDir);
                string path = Path.Combine(dataDir, "tunnel.url");
                File.WriteAllText(path, clean, Encoding.UTF8);
            }
            catch { }
        }

        public Dictionary<string, object> GetAdminDashboardSummary(string stateFilter = "all", string searchQuery = "", string dateFilter = "today", string shopFilter = null)
        {
            var status = GetShopAndAvailabilityStatus();
            string activeShopSlug = status.ContainsKey("shopId") && status["shopId"] != null ? Convert.ToString(status["shopId"]).Trim() : "pilot";
            string targetShop = !string.IsNullOrWhiteSpace(shopFilter) ? shopFilter.Trim() : activeShopSlug;
            bool isAllShops = string.Equals(targetShop, "all", StringComparison.OrdinalIgnoreCase);

            // IST Date computation (UTC+5:30)
            DateTime istNow = DateTime.UtcNow.AddHours(5).AddMinutes(30);
            string todayIst = istNow.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            string yesterdayIst = istNow.AddDays(-1).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            string sevenDaysAgoIst = istNow.AddDays(-7).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

            string shopClause = isAllShops ? "" : " AND (o.shop_slug = @targetShop OR (@targetShop = 'pilot' AND (o.shop_slug IS NULL OR o.shop_slug = ''))) ";

            // 1. Calculate Real-Time TODAY metrics for this shop (in IST)
            var todayPrms = new Dictionary<string, object> { { "todayIst", todayIst } };
            if (!isAllShops) todayPrms["targetShop"] = targetShop;

            long todayOrders = _db.ExecuteScalarLong(
                "SELECT COUNT(*) FROM orders o WHERE date(datetime(o.created_at_utc, '+330 minutes')) = @todayIst " + shopClause + ";", todayPrms);
            long todayPending = _db.ExecuteScalarLong(
                "SELECT COUNT(*) FROM orders o WHERE date(datetime(o.created_at_utc, '+330 minutes')) = @todayIst AND o.state = 'paid_awaiting_approval' " + shopClause + ";", todayPrms);
            long todayActiveQueue = _db.ExecuteScalarLong(
                "SELECT COUNT(*) FROM orders o WHERE date(datetime(o.created_at_utc, '+330 minutes')) = @todayIst AND o.state IN ('queued', 'leased', 'downloading', 'rendering', 'submitted_to_spooler') " + shopClause + ";", todayPrms);
            long todayReadyForPickup = _db.ExecuteScalarLong(
                "SELECT COUNT(*) FROM orders o WHERE date(datetime(o.created_at_utc, '+330 minutes')) = @todayIst AND o.state = 'ready_for_pickup' " + shopClause + ";", todayPrms);
            long todayCompleted = _db.ExecuteScalarLong(
                "SELECT COUNT(*) FROM orders o WHERE date(datetime(o.created_at_utc, '+330 minutes')) = @todayIst AND o.state = 'collected' " + shopClause + ";", todayPrms);
            long todayRefunded = _db.ExecuteScalarLong(
                "SELECT COUNT(*) FROM orders o WHERE date(datetime(o.created_at_utc, '+330 minutes')) = @todayIst AND o.payment_status = 'refunded' " + shopClause + ";", todayPrms);

            double todayRevenueInr = _db.ExecuteScalarDouble(
                @"SELECT COALESCE(SUM(q.total_amount_inr), 0)
                  FROM orders o JOIN quotes q ON o.quote_id = q.id
                  WHERE date(datetime(o.created_at_utc, '+330 minutes')) = @todayIst AND o.payment_status = 'verified' " + shopClause + ";", todayPrms);

            long todayPrintedSides = _db.ExecuteScalarLong(
                @"SELECT COALESCE(SUM(q.printed_sides), 0)
                  FROM orders o JOIN quotes q ON o.quote_id = q.id
                  WHERE date(datetime(o.created_at_utc, '+330 minutes')) = @todayIst AND o.state IN ('submitted_to_spooler', 'ready_for_pickup', 'collected') " + shopClause + ";", todayPrms);

            long todayPhysicalSheets = _db.ExecuteScalarLong(
                @"SELECT COALESCE(SUM(q.physical_sheets), 0)
                  FROM orders o JOIN quotes q ON o.quote_id = q.id
                  WHERE date(datetime(o.created_at_utc, '+330 minutes')) = @todayIst AND o.state IN ('submitted_to_spooler', 'ready_for_pickup', 'collected') " + shopClause + ";", todayPrms);

            // 2. Resolve Selected Period Date Filter
            string activeDateFilter = !string.IsNullOrWhiteSpace(dateFilter) ? dateFilter.Trim() : "today";
            string periodDateClause = "";
            var periodPrms = new Dictionary<string, object>();
            if (!isAllShops) periodPrms["targetShop"] = targetShop;

            if (string.Equals(activeDateFilter, "today", StringComparison.OrdinalIgnoreCase))
            {
                periodDateClause = " AND date(datetime(o.created_at_utc, '+330 minutes')) = @periodDate ";
                periodPrms["periodDate"] = todayIst;
            }
            else if (string.Equals(activeDateFilter, "yesterday", StringComparison.OrdinalIgnoreCase))
            {
                periodDateClause = " AND date(datetime(o.created_at_utc, '+330 minutes')) = @periodDate ";
                periodPrms["periodDate"] = yesterdayIst;
            }
            else if (string.Equals(activeDateFilter, "last7days", StringComparison.OrdinalIgnoreCase))
            {
                periodDateClause = " AND date(datetime(o.created_at_utc, '+330 minutes')) >= @periodStartDate ";
                periodPrms["periodStartDate"] = sevenDaysAgoIst;
            }
            else if (!string.Equals(activeDateFilter, "all", StringComparison.OrdinalIgnoreCase))
            {
                // Custom YYYY-MM-DD
                periodDateClause = " AND date(datetime(o.created_at_utc, '+330 minutes')) = @periodDate ";
                periodPrms["periodDate"] = activeDateFilter;
            }

            // 3. Calculate Selected Period Metrics
            long periodTotalOrders = _db.ExecuteScalarLong("SELECT COUNT(*) FROM orders o WHERE 1=1 " + periodDateClause + shopClause + ";", periodPrms);
            long periodAwaitingApproval = _db.ExecuteScalarLong("SELECT COUNT(*) FROM orders o WHERE o.state = 'paid_awaiting_approval' " + periodDateClause + shopClause + ";", periodPrms);
            long periodActiveQueue = _db.ExecuteScalarLong("SELECT COUNT(*) FROM orders o WHERE o.state IN ('queued', 'leased', 'downloading', 'rendering', 'submitted_to_spooler') " + periodDateClause + shopClause + ";", periodPrms);
            long periodReadyForPickup = _db.ExecuteScalarLong("SELECT COUNT(*) FROM orders o WHERE o.state = 'ready_for_pickup' " + periodDateClause + shopClause + ";", periodPrms);
            long periodCollected = _db.ExecuteScalarLong("SELECT COUNT(*) FROM orders o WHERE o.state = 'collected' " + periodDateClause + shopClause + ";", periodPrms);
            long periodReconciliation = _db.ExecuteScalarLong("SELECT COUNT(*) FROM orders o WHERE o.state IN ('reconciliation_required', 'failed', 'on_hold') " + periodDateClause + shopClause + ";", periodPrms);
            long periodRefunded = _db.ExecuteScalarLong("SELECT COUNT(*) FROM orders o WHERE o.payment_status = 'refunded' " + periodDateClause + shopClause + ";", periodPrms);

            double periodRevenueInr = _db.ExecuteScalarDouble(
                @"SELECT COALESCE(SUM(q.total_amount_inr), 0)
                  FROM orders o JOIN quotes q ON o.quote_id = q.id
                  WHERE o.payment_status = 'verified' " + periodDateClause + shopClause + ";", periodPrms);

            long periodPrintedSides = _db.ExecuteScalarLong(
                @"SELECT COALESCE(SUM(q.printed_sides), 0)
                  FROM orders o JOIN quotes q ON o.quote_id = q.id
                  WHERE o.state IN ('submitted_to_spooler', 'ready_for_pickup', 'collected') " + periodDateClause + shopClause + ";", periodPrms);

            long periodPhysicalSheets = _db.ExecuteScalarLong(
                @"SELECT COALESCE(SUM(q.physical_sheets), 0)
                  FROM orders o JOIN quotes q ON o.quote_id = q.id
                  WHERE o.state IN ('submitted_to_spooler', 'ready_for_pickup', 'collected') " + periodDateClause + shopClause + ";", periodPrms);

            // 4. Day-Wise Breakdown History (Past 30 Days)
            var dailyPrms = new Dictionary<string, object>();
            if (!isAllShops) dailyPrms["targetShop"] = targetShop;

            string dailySql = @"
                SELECT 
                    date(datetime(o.created_at_utc, '+330 minutes')) AS day_ist,
                    COUNT(*) AS total_orders,
                    SUM(CASE WHEN o.state = 'collected' THEN 1 ELSE 0 END) AS completed_orders,
                    SUM(CASE WHEN o.state = 'paid_awaiting_approval' THEN 1 ELSE 0 END) AS pending_approval,
                    COALESCE(SUM(CASE WHEN o.payment_status = 'verified' THEN q.total_amount_inr ELSE 0 END), 0) AS revenue_inr,
                    COALESCE(SUM(CASE WHEN o.state IN ('submitted_to_spooler', 'ready_for_pickup', 'collected') THEN q.printed_sides ELSE 0 END), 0) AS printed_sides,
                    COALESCE(SUM(CASE WHEN o.state IN ('submitted_to_spooler', 'ready_for_pickup', 'collected') THEN q.physical_sheets ELSE 0 END), 0) AS physical_sheets,
                    COALESCE(SUM(CASE WHEN o.state IN ('submitted_to_spooler', 'ready_for_pickup', 'collected') AND q.color_mode = 'color' THEN q.printed_sides ELSE 0 END), 0) AS color_sides,
                    COALESCE(SUM(CASE WHEN o.state IN ('submitted_to_spooler', 'ready_for_pickup', 'collected') AND (q.color_mode = 'bw' OR q.color_mode IS NULL) THEN q.printed_sides ELSE 0 END), 0) AS bw_sides
                FROM orders o
                LEFT JOIN quotes q ON o.quote_id = q.id
                WHERE 1=1 " + shopClause + @"
                GROUP BY day_ist
                ORDER BY day_ist DESC
                LIMIT 30;";

            var dailyRows = _db.Query(dailySql, dailyPrms);
            var dailyList = new List<Dictionary<string, object>>();
            bool hasTodayInList = false;
            foreach (var row in dailyRows)
            {
                string d = Convert.ToString(row["day_ist"]);
                bool isToday = (d == todayIst);
                if (isToday) hasTodayInList = true;
                dailyList.Add(new Dictionary<string, object>
                {
                    { "date", d },
                    { "isToday", isToday },
                    { "totalOrders", Convert.ToInt64(row["total_orders"]) },
                    { "completedOrders", Convert.ToInt64(row["completed_orders"]) },
                    { "pendingApproval", Convert.ToInt64(row["pending_approval"]) },
                    { "revenueInr", Math.Round(Convert.ToDouble(row["revenue_inr"]), 2) },
                    { "printedSides", Convert.ToInt64(row["printed_sides"]) },
                    { "physicalSheets", Convert.ToInt64(row["physical_sheets"]) },
                    { "colorSides", Convert.ToInt64(row["color_sides"]) },
                    { "bwSides", Convert.ToInt64(row["bw_sides"]) }
                });
            }
            if (!hasTodayInList)
            {
                dailyList.Insert(0, new Dictionary<string, object>
                {
                    { "date", todayIst },
                    { "isToday", true },
                    { "totalOrders", 0 },
                    { "completedOrders", 0 },
                    { "pendingApproval", 0 },
                    { "revenueInr", 0.0 },
                    { "printedSides", 0 },
                    { "physicalSheets", 0 },
                    { "colorSides", 0 },
                    { "bwSides", 0 }
                });
            }

            // 5. Query Individual Orders (Scoped by Shop, Period & State)
            var sql = new StringBuilder(@"
                SELECT o.*, q.document_pages, q.selected_pages, q.page_range_spec, q.copies, q.duplex_mode,
                       q.color_mode, q.paper_size, q.orientation, q.pages_per_sheet, q.is_collated,
                       q.printed_sides, q.physical_sheets, q.total_amount_inr, q.price_rule_version,
                       f.original_filename_redacted, f.sha256_hex, f.size_bytes, f.is_deleted,
                       p.gateway_provider, p.gateway_order_id, p.gateway_payment_id, p.webhook_calls_count,
                       j.id AS job_id, j.state AS job_state, j.windows_spooler_job_id, j.attempts
                FROM orders o
                JOIN quotes q ON o.quote_id = q.id
                JOIN files f ON o.file_id = f.id
                LEFT JOIN payments p ON p.order_id = o.id
                LEFT JOIN print_jobs j ON j.order_id = o.id
                WHERE 1=1 ");

            var prms = new Dictionary<string, object>();
            if (!isAllShops)
            {
                sql.Append(shopClause);
                prms["targetShop"] = targetShop;
            }
            if (!string.IsNullOrEmpty(periodDateClause))
            {
                sql.Append(periodDateClause);
                foreach (var kvp in periodPrms)
                {
                    if (kvp.Key != "targetShop") prms[kvp.Key] = kvp.Value;
                }
            }
            if (!string.IsNullOrWhiteSpace(stateFilter) && !string.Equals(stateFilter, "all", StringComparison.OrdinalIgnoreCase))
            {
                sql.Append(" AND o.state = @sf ");
                prms["sf"] = stateFilter.Trim();
            }
            if (!string.IsNullOrWhiteSpace(searchQuery))
            {
                sql.Append(" AND (o.order_code LIKE @sq OR o.pickup_code LIKE @sq OR o.customer_name LIKE @sq OR o.customer_phone LIKE @sq) ");
                prms["sq"] = "%" + searchQuery.Trim() + "%";
            }
            sql.Append(" ORDER BY o.id DESC LIMIT 150;");

            var orderRows = _db.Query(sql.ToString(), prms);
            var formattedOrders = new List<Dictionary<string, object>>();
            foreach (var r in orderRows)
            {
                formattedOrders.Add(new Dictionary<string, object>
                {
                    { "id", r["id"] },
                    { "shopSlug", r.ContainsKey("shop_slug") && r["shop_slug"] != null ? r["shop_slug"] : "pilot" },
                    { "orderCode", r["order_code"] },
                    { "publicToken", r["public_token"] },
                    { "pickupCode", r["pickup_code"] },
                    { "customerName", r["customer_name"] },
                    { "customerPhone", r["customer_phone"] },
                    { "state", r["state"] },
                    { "paymentStatus", r["payment_status"] },
                    { "fileName", r["original_filename_redacted"] },
                    { "sha256Prefix", Convert.ToString(r["sha256_hex"]).Substring(0, 12) },
                    { "fileSizeBytes", r["size_bytes"] },
                    { "fileDeleted", Convert.ToInt64(r["is_deleted"]) != 0 },
                    { "documentPages", r["document_pages"] },
                    { "selectedPages", r["selected_pages"] },
                    { "pageRange", r["page_range_spec"] },
                    { "copies", r["copies"] },
                    { "duplexMode", r["duplex_mode"] },
                    { "colorMode", r.ContainsKey("color_mode") && r["color_mode"] != null ? r["color_mode"] : "bw" },
                    { "paperSize", r.ContainsKey("paper_size") && r["paper_size"] != null ? r["paper_size"] : "A4" },
                    { "orientation", r.ContainsKey("orientation") && r["orientation"] != null ? r["orientation"] : "auto" },
                    { "pagesPerSheet", r.ContainsKey("pages_per_sheet") && r["pages_per_sheet"] != null ? r["pages_per_sheet"] : 1 },
                    { "collate", !r.ContainsKey("is_collated") || r["is_collated"] == null || Convert.ToInt64(r["is_collated"]) != 0 },
                    { "printedSides", r["printed_sides"] },
                    { "physicalSheets", r["physical_sheets"] },
                    { "totalAmountInr", r["total_amount_inr"] },
                    { "priceRuleVersion", r["price_rule_version"] },
                    { "targetQueueName", r["target_queue_name"] },
                    { "holdOrRejectReason", r["hold_or_reject_reason"] },
                    { "gatewayProvider", r.ContainsKey("gateway_provider") && r["gateway_provider"] != null ? r["gateway_provider"] : "UPI_GATEWAY" },
                    { "gatewayOrderId", r["gateway_order_id"] },
                    { "gatewayPaymentId", r["gateway_payment_id"] },
                    { "webhookCallsCount", r["webhook_calls_count"] },
                    { "jobId", r["job_id"] },
                    { "jobState", r["job_state"] },
                    { "windowsSpoolerJobId", r["windows_spooler_job_id"] },
                    { "createdAtUtc", r["created_at_utc"] },
                    { "createdAtIst", ToIstDisplay(Convert.ToString(r["created_at_utc"])) },
                    { "updatedAtUtc", r["updated_at_utc"] },
                    { "updatedAtIst", ToIstDisplay(Convert.ToString(r["updated_at_utc"])) }
                });
            }

            var recentAudits = _db.Query("SELECT * FROM audit_events ORDER BY id DESC LIMIT 40;");

            return new Dictionary<string, object>
            {
                { "shopStatus", status },
                { "metrics", new Dictionary<string, object>
                    {
                        // Selected period metrics (used for dashboard cards based on date filter)
                        { "totalOrders", periodTotalOrders },
                        { "paidAwaitingApproval", periodAwaitingApproval },
                        { "activePrintQueue", periodActiveQueue },
                        { "readyForPickup", periodReadyForPickup },
                        { "collectedOrders", periodCollected },
                        { "reconciliationOrHold", periodReconciliation },
                        { "refundedOrders", periodRefunded },
                        { "verifiedRevenueInr", Math.Round(periodRevenueInr, 2) },
                        { "totalPrintedSides", periodPrintedSides },
                        { "totalPhysicalSheets", periodPhysicalSheets },
                        { "freeDiskMb", GetAvailableDiskMegabytes() },
                        // Explicit Real-time TODAY in IST metrics
                        { "todayOrders", todayOrders },
                        { "todayPending", todayPending },
                        { "todayActiveQueue", todayActiveQueue },
                        { "todayReadyForPickup", todayReadyForPickup },
                        { "todayCompleted", todayCompleted },
                        { "todayRefunded", todayRefunded },
                        { "todayRevenueInr", Math.Round(todayRevenueInr, 2) },
                        { "todayPrintedSides", todayPrintedSides },
                        { "todayPhysicalSheets", todayPhysicalSheets },
                        { "activeDateFilter", activeDateFilter },
                        { "activeShopFilter", targetShop },
                        { "todayDateIst", todayIst }
                    }
                },
                { "dailyBreakdown", dailyList },
                { "orders", formattedOrders },
                { "recentAuditEvents", recentAudits }
            };
        }
    }
}
