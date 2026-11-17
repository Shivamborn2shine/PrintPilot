using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Text;
using System.Web.Script.Serialization;

namespace PrintAway.SaaS
{
    /// <summary>Single place for the SaaS control-plane endpoint (overridable via .env).</summary>
    public static class ControlPlane
    {
        // Public (publishable/anon) key: safe to ship in clients. Row-level security on the
        // Supabase project is what actually protects data; never put a service-role key here.
        private const string DefaultUrl = "https://zduoifzakiojfqbtxqqm.supabase.co";
        private const string DefaultPublishableKey = "sb_publishable_rhmUZZbKhXiJxrChcPTbsg_gFIH9FdO";

        public static string Url
        {
            get
            {
                string v = Environment.GetEnvironmentVariable("SUPABASE_URL");
                return (string.IsNullOrWhiteSpace(v) ? DefaultUrl : v.Trim()).TrimEnd('/');
            }
        }

        public static string Key
        {
            get
            {
                string v = Environment.GetEnvironmentVariable("SUPABASE_SERVICE_ROLE_KEY");
                if (string.IsNullOrWhiteSpace(v)) v = Environment.GetEnvironmentVariable("SUPABASE_KEY");
                return string.IsNullOrWhiteSpace(v) ? DefaultPublishableKey : v.Trim();
            }
        }
    }

    public enum SlugAvailability
    {
        Available,
        Taken,
        Unknown
    }

    public sealed class SlugCheckResult
    {
        public SlugAvailability Availability { get; set; }
        public string ExistingShopName { get; set; }
        public string LastHeartbeatUtc { get; set; }
        public string Error { get; set; }
    }

    /// <summary>
    /// Shop-ID registry operations used by onboarding. Prevents a new station from silently
    /// re-using another shop's ID (which would redirect that shop's customers and payments here).
    /// </summary>
    public static class ShopRegistryClient
    {
        public static SlugCheckResult CheckSlug(string slug, int timeoutMs = 6000)
        {
            var result = new SlugCheckResult { Availability = SlugAvailability.Unknown };
            try
            {
                string url = string.Format(CultureInfo.InvariantCulture,
                    "{0}/rest/v1/shops?shop_id=eq.{1}&select=shop_id,shop_name,last_heartbeat_utc",
                    ControlPlane.Url, Uri.EscapeDataString(slug));
                string body = Send(url, "GET", null, null, timeoutMs);
                var rows = new JavaScriptSerializer().Deserialize<List<Dictionary<string, object>>>(body ?? "[]");
                if (rows == null || rows.Count == 0)
                {
                    result.Availability = SlugAvailability.Available;
                }
                else
                {
                    result.Availability = SlugAvailability.Taken;
                    object n, hb;
                    if (rows[0].TryGetValue("shop_name", out n) && n != null) result.ExistingShopName = Convert.ToString(n);
                    if (rows[0].TryGetValue("last_heartbeat_utc", out hb) && hb != null) result.LastHeartbeatUtc = Convert.ToString(hb);
                }
            }
            catch (Exception ex)
            {
                result.Error = ex.Message;
            }
            return result;
        }

        /// <summary>
        /// Creates the shop row. Returns false with <paramref name="conflict"/> = true when the ID was
        /// claimed by someone else in the meantime.
        /// </summary>
        public static bool RegisterShop(string slug, string shopName, string ownerName, string ownerPhone, string upiVpa,
            string printerModel, string clientVersion, out bool conflict, out string error)
        {
            conflict = false;
            error = null;
            try
            {
                var row = new Dictionary<string, object>
                {
                    { "shop_id", slug },
                    { "shop_name", shopName },
                    { "owner_name", ownerName },
                    { "owner_phone", ownerPhone },
                    { "upi_vpa", upiVpa },
                    { "active_printer_model", printerModel ?? "" },
                    { "client_version", clientVersion ?? "" },
                    { "last_heartbeat_utc", DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture) }
                };
                Send(ControlPlane.Url + "/rest/v1/shops", "POST", new JavaScriptSerializer().Serialize(row), "return=minimal", 8000);
                return true;
            }
            catch (WebException wex)
            {
                var resp = wex.Response as HttpWebResponse;
                if (resp != null && resp.StatusCode == HttpStatusCode.Conflict)
                {
                    conflict = true;
                    error = "This Shop ID was just registered by another shop.";
                }
                else
                {
                    error = wex.Message;
                }
                return false;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

        /// <summary>Updates owner-facing profile fields on an existing row (used after reinstall / edits).</summary>
        public static bool UpdateShopProfile(string slug, string shopName, string ownerName, string ownerPhone, string upiVpa, out string error)
        {
            error = null;
            try
            {
                var patch = new Dictionary<string, object>();
                if (!string.IsNullOrWhiteSpace(shopName)) patch["shop_name"] = shopName;
                if (!string.IsNullOrWhiteSpace(ownerName)) patch["owner_name"] = ownerName;
                if (!string.IsNullOrWhiteSpace(ownerPhone)) patch["owner_phone"] = ownerPhone;
                if (!string.IsNullOrWhiteSpace(upiVpa)) patch["upi_vpa"] = upiVpa;
                if (patch.Count == 0) return true;
                string url = string.Format(CultureInfo.InvariantCulture, "{0}/rest/v1/shops?shop_id=eq.{1}", ControlPlane.Url, Uri.EscapeDataString(slug));
                Send(url, "PATCH", new JavaScriptSerializer().Serialize(patch), "return=minimal", 8000);
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

        private static string Send(string url, string method, string jsonBody, string prefer, int timeoutMs)
        {
            ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;
            var req = (HttpWebRequest)WebRequest.Create(url);
            req.Method = method;
            req.Timeout = timeoutMs;
            req.ReadWriteTimeout = timeoutMs;
            req.Accept = "application/json";
            req.Headers.Add("apikey", ControlPlane.Key);
            req.Headers.Add("Authorization", "Bearer " + ControlPlane.Key);
            if (!string.IsNullOrEmpty(prefer)) req.Headers.Add("Prefer", prefer);

            if (jsonBody != null)
            {
                byte[] bytes = Encoding.UTF8.GetBytes(jsonBody);
                req.ContentType = "application/json";
                req.ContentLength = bytes.Length;
                using (var s = req.GetRequestStream()) s.Write(bytes, 0, bytes.Length);
            }

            using (var resp = (HttpWebResponse)req.GetResponse())
            using (var sr = new StreamReader(resp.GetResponseStream(), Encoding.UTF8))
            {
                return sr.ReadToEnd();
            }
        }
    }
}
