using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;
using PrintAway.Storage;
using PrintAway.Tunnel;

namespace PrintAway.SaaS
{
    public sealed class QuotaStatus
    {
        public string ShopId { get; set; }
        public string Tier { get; set; } // "free", "pro_monthly", "pro_annual"
        public string Status { get; set; } // "active", "past_due", "suspended"
        public int DailyQuota { get; set; } // 10 for free, 99999 for pro
        public int PagesPrintedToday { get; set; }
        public int RemainingPagesToday { get; set; }
        public bool IsQuotaExceeded { get; set; }
        public bool IsHardBlocked { get; set; } // Exceeded beyond soft grace buffer (3 pages)
        public bool RemotePaused { get; set; }
        public string LastSyncUtc { get; set; }
    }

    public sealed class SupabaseSyncClient : IDisposable
    {
        private readonly string _supabaseUrl;
        private readonly string _apiKey;
        private readonly string _shopId;
        private readonly PrintAwayRepository _repo;
        private readonly TunnelManager _tunnel;
        private readonly Timer _syncTimer;
        private readonly JavaScriptSerializer _serializer;

        private QuotaStatus _currentQuota;
        private bool _disposed;
        private int _inFlightSync;

        public event Action<QuotaStatus> OnQuotaStatusChanged;

        public SupabaseSyncClient(string supabaseUrl, string apiKey, string shopId, PrintAwayRepository repo, TunnelManager tunnel)
        {
            _supabaseUrl = !string.IsNullOrWhiteSpace(supabaseUrl) ? supabaseUrl.Trim().TrimEnd('/') : "";
            _apiKey = !string.IsNullOrWhiteSpace(apiKey) ? apiKey.Trim() : "";
            _shopId = !string.IsNullOrWhiteSpace(shopId) ? shopId.Trim() : "pilot";
            _repo = repo;
            _tunnel = tunnel;
            _serializer = new JavaScriptSerializer();

            _currentQuota = new QuotaStatus
            {
                ShopId = _shopId,
                Tier = "free",
                Status = "active",
                DailyQuota = 10,
                PagesPrintedToday = 0,
                RemainingPagesToday = 10,
                IsQuotaExceeded = false,
                IsHardBlocked = false,
                RemotePaused = false,
                LastSyncUtc = ""
            };

            if (IsConfigured)
            {
                // Run initial sync after 3 seconds, then every 15 minutes
                _syncTimer = new Timer(state => PerformSync(), null, TimeSpan.FromSeconds(3), TimeSpan.FromMinutes(15));
            }
        }

        public bool IsConfigured
        {
            get { return !string.IsNullOrEmpty(_supabaseUrl) && !string.IsNullOrEmpty(_apiKey); }
        }

        public QuotaStatus CurrentQuota
        {
            get { return _currentQuota; }
        }

        public string GetFleetShopsJson()
        {
            if (!IsConfigured) return "[]";
            string url = string.Format(CultureInfo.InvariantCulture, "{0}/rest/v1/shops?select=*&order=created_at.desc", _supabaseUrl);
            return ExecuteHttpRequest(url, "GET", null, null);
        }

        public string GetDailyTelemetryJson(string dateStr)
        {
            if (!IsConfigured) return "[]";
            string url = string.Format(CultureInfo.InvariantCulture, "{0}/rest/v1/daily_telemetry?date=eq.{1}&select=*", _supabaseUrl, Uri.EscapeDataString(dateStr));
            return ExecuteHttpRequest(url, "GET", null, null);
        }

        public string UpdateShop(string shopId, string jsonPayload)
        {
            if (!IsConfigured) throw new InvalidOperationException("Supabase is not configured.");
            string url = string.Format(CultureInfo.InvariantCulture, "{0}/rest/v1/shops?shop_id=eq.{1}", _supabaseUrl, Uri.EscapeDataString(shopId));
            return ExecuteHttpRequest(url, "PATCH", jsonPayload, null);
        }

        public string CreateShop(string jsonPayload)
        {
            if (!IsConfigured) throw new InvalidOperationException("Supabase is not configured.");
            string url = string.Format(CultureInfo.InvariantCulture, "{0}/rest/v1/shops", _supabaseUrl);
            var headers = new Dictionary<string, string> { { "Prefer", "return=representation" } };
            return ExecuteHttpRequest(url, "POST", jsonPayload, headers);
        }

        public void TriggerSyncNow()
        {
            ThreadPool.QueueUserWorkItem(state => PerformSync());
        }

        private void PerformSync()
        {
            if (!IsConfigured || _disposed) return;
            if (Interlocked.CompareExchange(ref _inFlightSync, 1, 0) != 0) return;

            try
            {
                ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12 | SecurityProtocolType.Tls11 | SecurityProtocolType.Tls;

                string todayDate = DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

                // 1. Fetch local performance counters from SQLite
                var dash = _repo.GetAdminDashboardSummary("all", "");
                int todayPages = 0;
                int todaySheets = 0;
                int todayOrders = 0;
                double todayRevenue = 0.0;

                if (dash != null && dash.ContainsKey("metrics") && dash["metrics"] is Dictionary<string, object>)
                {
                    var m = (Dictionary<string, object>)dash["metrics"];
                    if (m.ContainsKey("totalPrintedSides")) todayPages = Convert.ToInt32(m["totalPrintedSides"]);
                    if (m.ContainsKey("totalPhysicalSheets")) todaySheets = Convert.ToInt32(m["totalPhysicalSheets"]);
                    if (m.ContainsKey("collectedOrders")) todayOrders = Convert.ToInt32(m["collectedOrders"]);
                    if (m.ContainsKey("verifiedRevenueInr")) todayRevenue = Convert.ToDouble(m["verifiedRevenueInr"]);
                }

                // Get active printer & tunnel info
                var shopStatus = _repo.GetShopAndAvailabilityStatus();
                string activePrinter = Convert.ToString(shopStatus["activeQueueName"]);
                string tunnelUrl = _tunnel != null && !string.IsNullOrWhiteSpace(_tunnel.LiveTunnelUrl) ? _tunnel.LiveTunnelUrl : "";

                // 2. Heartbeat to public.shops
                SendHeartbeat(activePrinter, tunnelUrl);

                // 3. Upsert to public.daily_telemetry
                SendDailyTelemetry(todayDate, todayPages, todaySheets, todayOrders, todayRevenue);

                // 4. Fetch latest SaaS status & remote pause flags from Supabase
                FetchAndApplyShopPolicy(todayPages);
            }
            catch (Exception ex)
            {
                Console.WriteLine("[SaaS Sync] Sync warning (operating in resilient offline mode): " + ex.Message);
            }
            finally
            {
                Interlocked.Exchange(ref _inFlightSync, 0);
            }
        }

        private void SendHeartbeat(string activePrinter, string tunnelUrl)
        {
            try
            {
                string patchUrl = string.Format(CultureInfo.InvariantCulture, "{0}/rest/v1/shops?shop_id=eq.{1}", _supabaseUrl, Uri.EscapeDataString(_shopId));
                var payload = new Dictionary<string, object>
                {
                    { "client_version", "2.2.0" },
                    { "last_heartbeat_utc", DateTime.UtcNow.ToString("o") },
                    { "active_printer_model", activePrinter ?? "" }
                };

                if (!string.IsNullOrWhiteSpace(tunnelUrl))
                {
                    payload["live_tunnel_url"] = tunnelUrl;
                }

                ExecuteHttpRequest(patchUrl, "PATCH", _serializer.Serialize(payload), null);
            }
            catch (Exception ex)
            {
                Console.WriteLine("[SaaS Sync] Heartbeat patch warning: " + ex.Message);
            }
        }

        private void SendDailyTelemetry(string dateStr, int pages, int sheets, int orders, double revenue)
        {
            try
            {
                string postUrl = string.Format(CultureInfo.InvariantCulture, "{0}/rest/v1/daily_telemetry", _supabaseUrl);
                var row = new Dictionary<string, object>
                {
                    { "shop_id", _shopId },
                    { "date", dateStr },
                    { "pages_count", pages },
                    { "sheets_count", sheets },
                    { "orders_count", orders },
                    { "revenue_inr", Math.Round(revenue, 2) },
                    { "last_updated", DateTime.UtcNow.ToString("o") }
                };

                var headers = new Dictionary<string, string>
                {
                    { "Prefer", "resolution=merge-duplicates" }
                };

                ExecuteHttpRequest(postUrl, "POST", _serializer.Serialize(row), headers);
            }
            catch (Exception ex)
            {
                Console.WriteLine("[SaaS Sync] Telemetry upsert warning: " + ex.Message);
            }
        }

        private void FetchAndApplyShopPolicy(int todayPages)
        {
            try
            {
                string getUrl = string.Format(CultureInfo.InvariantCulture, "{0}/rest/v1/shops?shop_id=eq.{1}&select=*", _supabaseUrl, Uri.EscapeDataString(_shopId));
                string jsonResp = ExecuteHttpRequest(getUrl, "GET", null, null);

                if (!string.IsNullOrWhiteSpace(jsonResp))
                {
                    var items = _serializer.Deserialize<List<Dictionary<string, object>>>(jsonResp);
                    if (items != null && items.Count > 0)
                    {
                        var row = items[0];
                        string tier = row.ContainsKey("subscription_tier") && row["subscription_tier"] != null ? Convert.ToString(row["subscription_tier"]).Trim() : "free";
                        string status = row.ContainsKey("subscription_status") && row["subscription_status"] != null ? Convert.ToString(row["subscription_status"]).Trim() : "active";
                        int quota = row.ContainsKey("daily_page_quota") && row["daily_page_quota"] != null ? Convert.ToInt32(row["daily_page_quota"]) : 10;
                        bool remotePause = row.ContainsKey("emergency_remote_pause") && row["emergency_remote_pause"] != null && Convert.ToBoolean(row["emergency_remote_pause"]);

                        bool isPro = tier.StartsWith("pro", StringComparison.OrdinalIgnoreCase);
                        int effectiveQuota = isPro ? 999999 : quota;

                        bool isExceeded = !isPro && (todayPages >= effectiveQuota);
                        const int softGracePages = 3;
                        bool isHardBlocked = !isPro && (todayPages >= (effectiveQuota + softGracePages));

                        var newQuota = new QuotaStatus
                        {
                            ShopId = _shopId,
                            Tier = tier,
                            Status = status,
                            DailyQuota = effectiveQuota,
                            PagesPrintedToday = todayPages,
                            RemainingPagesToday = Math.Max(0, effectiveQuota - todayPages),
                            IsQuotaExceeded = isExceeded,
                            IsHardBlocked = isHardBlocked,
                            RemotePaused = remotePause,
                            LastSyncUtc = DateTime.UtcNow.ToString("o")
                        };

                        _currentQuota = newQuota;

                        // Remote pause enforcement
                        var currentStatus = _repo.GetShopAndAvailabilityStatus();
                        bool currentlyAccepting = (bool)currentStatus["acceptingOrders"];

                        if (remotePause && currentlyAccepting)
                        {
                            Console.WriteLine("[SaaS Super-Admin] Emergency remote pause received from cloud! Halting intake.");
                            _repo.UpdateShopAndPrinterControls(false, "Emergency Remote Pause (Super-Admin)", null, null, "saas-remote-pause");
                        }

                        Action<QuotaStatus> handler = OnQuotaStatusChanged;
                        if (handler != null)
                        {
                            try { handler(newQuota); } catch { }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine("[SaaS Sync] Policy check warning: " + ex.Message);
            }
        }

        private string ExecuteHttpRequest(string url, string method, string jsonBody, Dictionary<string, string> extraHeaders)
        {
            ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12 | SecurityProtocolType.Tls11 | SecurityProtocolType.Tls;
            var req = (HttpWebRequest)WebRequest.Create(url);
            req.Method = method;
            req.ContentType = "application/json; charset=utf-8";
            req.Accept = "application/json";
            req.Headers["apikey"] = _apiKey;
            req.UserAgent = "PrintAwayStation/2.2.0";
            req.Timeout = 10000;

            if (extraHeaders != null)
            {
                foreach (var kv in extraHeaders)
                {
                    req.Headers[kv.Key] = kv.Value;
                }
            }

            if (!string.IsNullOrEmpty(jsonBody) && (method == "POST" || method == "PATCH" || method == "PUT"))
            {
                byte[] bytes = Encoding.UTF8.GetBytes(jsonBody);
                req.ContentLength = bytes.Length;
                using (var stream = req.GetRequestStream())
                {
                    stream.Write(bytes, 0, bytes.Length);
                }
            }

            try
            {
                using (var resp = (HttpWebResponse)req.GetResponse())
                using (var reader = new StreamReader(resp.GetResponseStream(), Encoding.UTF8))
                {
                    return reader.ReadToEnd();
                }
            }
            catch (WebException wex)
            {
                if (wex.Response != null)
                {
                    using (var reader = new StreamReader(wex.Response.GetResponseStream(), Encoding.UTF8))
                    {
                        string errBody = reader.ReadToEnd();
                        throw new InvalidOperationException(string.Format(CultureInfo.InvariantCulture, "HTTP {0}: {1}", (int)((HttpWebResponse)wex.Response).StatusCode, errBody));
                    }
                }
                throw;
            }
        }

        public void Dispose()
        {
            _disposed = true;
            if (_syncTimer != null)
            {
                _syncTimer.Dispose();
            }
        }
    }
}

