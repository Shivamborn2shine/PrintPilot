using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;

namespace PrintAway.Config
{
    /// <summary>
    /// Per-install station configuration (config/station.json).
    ///
    /// This file is machine-specific and must never be committed: a fresh clone/install
    /// ships WITHOUT it, which is what triggers the first-run onboarding wizard.
    /// Unknown keys are preserved on save so older/newer builds can share the file.
    /// </summary>
    public sealed class StationConfig
    {
        /// <summary>Slug stored in the local DB for a station that has not completed onboarding.</summary>
        public const string UnconfiguredSlug = "unconfigured";

        public const string DefaultPublicStorefrontUrl = "https://printaway-ten.vercel.app";

        /// <summary>Old storefront hosts saved by earlier builds; they are replaced by the default.</summary>
        private static readonly string[] RetiredStorefrontHosts = { "printitaway.netlify.app", "printitaway.vercel.app" };

        private static string _activeStorefrontUrl = DefaultPublicStorefrontUrl;

        /// <summary>Customer-facing storefront base URL (no trailing slash) for QR codes, links and the GUI.</summary>
        public static string ActiveStorefrontUrl
        {
            get { return _activeStorefrontUrl; }
        }

        /// <summary>Builds the customer link for a shop, e.g. https://…/?shop=my-shop.</summary>
        public static string CustomerLink(string shopId)
        {
            return ActiveStorefrontUrl + "/?shop=" + Uri.EscapeDataString(shopId ?? "");
        }

        private static readonly Regex SlugPattern = new Regex(@"^[a-z0-9][a-z0-9_-]{2,39}$", RegexOptions.Compiled);
        private static readonly Regex UpiPattern = new Regex(@"^[a-zA-Z0-9._-]{2,256}@[a-zA-Z][a-zA-Z0-9]{1,63}$", RegexOptions.Compiled);
        private static readonly Regex PhonePattern = new Regex(@"^\+?[0-9]{10,13}$", RegexOptions.Compiled);

        private static readonly HashSet<string> ReservedSlugs = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            UnconfiguredSlug, "pilot", "printaway-pilot", "admin", "superadmin", "api", "www", "root",
            "test", "demo", "default", "null", "undefined", "station", "printaway", "shop", "new"
        };

        private readonly Dictionary<string, object> _data;
        private readonly object _sync = new object();

        public string FilePath { get; private set; }

        /// <summary>True when a config file existed on disk at load time.</summary>
        public bool ExistedOnDisk { get; private set; }

        private StationConfig(string filePath, Dictionary<string, object> data, bool existed)
        {
            FilePath = filePath;
            _data = data ?? new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            ExistedOnDisk = existed;
        }

        /// <summary>
        /// Loads config/station.json. Legacy fallbacks (root station.json, station-*.json) are read
        /// once and then persisted to config/station.json on the next Save().
        /// </summary>
        public static StationConfig Load(string repoRoot)
        {
            string canonical = Path.Combine(repoRoot, "config", "station.json");
            string source = null;

            if (File.Exists(canonical))
            {
                source = canonical;
            }
            else
            {
                string legacyRoot = Path.Combine(repoRoot, "station.json");
                if (File.Exists(legacyRoot)) source = legacyRoot;
            }
            if (source == null)
            {
                string configDir = Path.Combine(repoRoot, "config");
                if (Directory.Exists(configDir))
                {
                    string first = Directory.GetFiles(configDir, "station-*.json").OrderBy(p => p, StringComparer.OrdinalIgnoreCase).FirstOrDefault();
                    if (first != null) source = first;
                }
            }

            var data = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            bool existed = false;
            if (source != null)
            {
                try
                {
                    var parsed = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(File.ReadAllText(source, Encoding.UTF8));
                    if (parsed != null)
                    {
                        foreach (var kv in parsed) data[kv.Key] = kv.Value;
                        existed = true;
                    }
                    Console.WriteLine("[Config] Loaded station configuration: " + source);
                }
                catch (Exception ex)
                {
                    // A corrupt config must not silently become someone else's identity: keep a copy and start clean.
                    Console.WriteLine("[Config] Could not parse " + source + ": " + ex.Message);
                    try { File.Copy(source, source + ".corrupt-" + DateTime.UtcNow.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture), true); } catch { }
                }
            }

            var cfg = new StationConfig(canonical, data, existed);
            _activeStorefrontUrl = cfg.PublicStorefrontUrl;
            return cfg;
        }

        // ------------------------------------------------------------------ typed accessors

        public string ShopId
        {
            get { return NormalizeSlug(GetString("shopId")); }
            set { Set("shopId", NormalizeSlug(value)); }
        }

        public string ShopName
        {
            get { return GetString("shopName"); }
            set { Set("shopName", value != null ? value.Trim() : null); }
        }

        public string OwnerName { get { return GetString("ownerName"); } set { Set("ownerName", value); } }
        public string OwnerPhone { get { return GetString("ownerPhone"); } set { Set("ownerPhone", value); } }
        public string UpiVpa { get { return GetString("upiVpa"); } set { Set("upiVpa", value); } }
        public string ActivePrinterQueue { get { return GetString("activePrinterQueue"); } set { Set("activePrinterQueue", value); } }
        public string BwPrinterQueue { get { return GetString("bwPrinterQueue"); } set { Set("bwPrinterQueue", value); } }
        public string ColorPrinterQueue { get { return GetString("colorPrinterQueue"); } set { Set("colorPrinterQueue", value); } }

        public string PublicStorefrontUrl
        {
            get
            {
                string v = GetString("storefrontUrl");
                if (string.IsNullOrWhiteSpace(v)) v = GetString("netlifySiteUrl"); // key used by builds before 2.6.0
                if (string.IsNullOrWhiteSpace(v)) return DefaultPublicStorefrontUrl;
                v = v.Trim().TrimEnd('/');
                Uri parsed;
                if (!Uri.TryCreate(v, UriKind.Absolute, out parsed) || parsed.Scheme != Uri.UriSchemeHttps) return DefaultPublicStorefrontUrl;
                foreach (string retired in RetiredStorefrontHosts)
                {
                    if (string.Equals(parsed.Host, retired, StringComparison.OrdinalIgnoreCase)) return DefaultPublicStorefrontUrl;
                }
                return v;
            }
        }

        public int Port
        {
            get
            {
                int p;
                string raw = GetString("port");
                return int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out p) && p > 0 && p < 65536 ? p : 8080;
            }
        }

        public bool AutoFallbackToPhysicalPrinter
        {
            get
            {
                bool b;
                string raw = GetString("autoFallbackToPhysicalPrinter");
                return string.IsNullOrWhiteSpace(raw) || !bool.TryParse(raw, out b) || b;
            }
        }

        /// <summary>A station is onboarded once it holds a valid, non-placeholder shop ID.</summary>
        public bool HasValidShopIdentity
        {
            get { string id = ShopId; return IsUsableSlug(id); }
        }

        public string GetString(string key)
        {
            lock (_sync)
            {
                object v;
                if (!_data.TryGetValue(key, out v) || v == null) return null;
                string s = Convert.ToString(v, CultureInfo.InvariantCulture);
                return s != null ? s.Trim() : null;
            }
        }

        public void Set(string key, object value)
        {
            lock (_sync)
            {
                if (value == null || (value is string && string.IsNullOrWhiteSpace((string)value)))
                {
                    _data.Remove(key);
                }
                else
                {
                    _data[key] = value is string ? ((string)value).Trim() : value;
                }
            }
        }

        /// <summary>Atomically writes config/station.json (write temp file, then replace).</summary>
        public void Save()
        {
            string json;
            lock (_sync)
            {
                json = ToPrettyJson(_data);
            }

            string dir = Path.GetDirectoryName(FilePath);
            if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);

            string tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, json, new UTF8Encoding(false));
            if (File.Exists(FilePath))
            {
                File.Replace(tmp, FilePath, null);
            }
            else
            {
                File.Move(tmp, FilePath);
            }
            ExistedOnDisk = true;
        }

        // ------------------------------------------------------------------ validation helpers

        public static string NormalizeSlug(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return null;
            string s = Regex.Replace(raw.Trim().ToLowerInvariant(), @"[^a-z0-9_-]", "");
            return string.IsNullOrEmpty(s) ? null : s;
        }

        /// <summary>Turns "Sharma Xerox & Stationery" into "sharma-xerox-stationery".</summary>
        public static string Slugify(string shopName)
        {
            if (string.IsNullOrWhiteSpace(shopName)) return "";
            string s = shopName.Trim().ToLowerInvariant();
            s = Regex.Replace(s, @"[^a-z0-9]+", "-");
            s = s.Trim('-');
            if (s.Length > 40) s = s.Substring(0, 40).Trim('-');
            return s;
        }

        public static bool IsUsableSlug(string slug)
        {
            return !string.IsNullOrEmpty(slug) && SlugPattern.IsMatch(slug) && !ReservedSlugs.Contains(slug);
        }

        public static string ValidateSlug(string slug)
        {
            if (string.IsNullOrEmpty(slug)) return "Shop ID is required.";
            if (!SlugPattern.IsMatch(slug)) return "Shop ID must be 3-40 characters: lowercase letters, digits, '-' or '_', starting with a letter or digit.";
            if (ReservedSlugs.Contains(slug)) return "'" + slug + "' is reserved. Please choose a different Shop ID.";
            return null;
        }

        public static bool IsValidUpiVpa(string vpa)
        {
            return !string.IsNullOrWhiteSpace(vpa) && UpiPattern.IsMatch(vpa.Trim());
        }

        public static string NormalizePhone(string phone)
        {
            if (string.IsNullOrWhiteSpace(phone)) return null;
            string p = Regex.Replace(phone, @"[\s()-]", "");
            return PhonePattern.IsMatch(p) ? p : null;
        }

        public static string ValidateAdminPassword(string password)
        {
            if (string.IsNullOrEmpty(password) || password.Length < 8) return "Admin password must be at least 8 characters.";
            if (password.Length > 128) return "Admin password must be at most 128 characters.";
            bool hasLetter = password.Any(char.IsLetter);
            bool hasDigit = password.Any(char.IsDigit);
            if (!hasLetter || !hasDigit) return "Admin password must contain both letters and numbers.";
            string lower = password.ToLowerInvariant();
            if (lower.Contains("password") || lower.Contains("printaway@admin") || lower == "12345678a" || lower == "admin123")
                return "This password is too common. Please choose something harder to guess.";
            return null;
        }

        // ------------------------------------------------------------------ per-install secrets

        /// <summary>
        /// Returns a stable random secret for this install, generated on first use and stored in
        /// data/secrets.json. Used instead of hard-coded defaults that would be identical (and public)
        /// for every station built from this repository.
        /// </summary>
        public static string GetOrCreateInstallSecret(string dataDir, string name)
        {
            string path = Path.Combine(dataDir, "secrets.json");
            var ser = new JavaScriptSerializer();
            Dictionary<string, object> secrets = null;
            try
            {
                if (File.Exists(path)) secrets = ser.Deserialize<Dictionary<string, object>>(File.ReadAllText(path, Encoding.UTF8));
            }
            catch { secrets = null; }
            if (secrets == null) secrets = new Dictionary<string, object>();

            object existing;
            if (secrets.TryGetValue(name, out existing) && existing != null && Convert.ToString(existing).Length >= 32)
            {
                return Convert.ToString(existing);
            }

            byte[] buf = new byte[32];
            using (var rng = RandomNumberGenerator.Create()) rng.GetBytes(buf);
            string secret = BitConverter.ToString(buf).Replace("-", "").ToLowerInvariant();
            secrets[name] = secret;

            if (!Directory.Exists(dataDir)) Directory.CreateDirectory(dataDir);
            File.WriteAllText(path, ToPrettyJson(secrets), new UTF8Encoding(false));
            return secret;
        }

        private static string ToPrettyJson(Dictionary<string, object> data)
        {
            var ser = new JavaScriptSerializer();
            var sb = new StringBuilder();
            sb.Append("{\r\n");
            int i = 0;
            foreach (var kv in data)
            {
                sb.Append("    ").Append(ser.Serialize(kv.Key)).Append(": ").Append(ser.Serialize(kv.Value));
                if (++i < data.Count) sb.Append(',');
                sb.Append("\r\n");
            }
            sb.Append("}\r\n");
            return sb.ToString();
        }
    }
}
