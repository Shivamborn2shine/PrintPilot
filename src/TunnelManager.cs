using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Web.Script.Serialization;

namespace PrintAway.Tunnel
{
    public sealed class TunnelManager : IDisposable
    {
        private static readonly Regex CloudflareUrlRegex = new Regex(@"https://(?!api\.|pkg\.|update\.|staging\.)([a-zA-Z0-9-]{5,}\.trycloudflare\.com)", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly Regex SshUrlRegex = new Regex(@"https://[a-zA-Z0-9-]+\.lhr\.(life|rocks|pro)", RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private readonly string _rootDir;
        private readonly int _localPort;
        private string _shopId;
        private string _shopName;
        private string _registryTopic;
        private readonly object _lock = new object();

        private Process _tunnelProcess;
        private Timer _broadcastTimer;
        private string _liveTunnelUrl;
        private string _activeProviderName;
        private bool _disposed;
        private bool _isSwitchingToFallback;

        public event Action<string> OnTunnelUrlCaptured;
        public event Action<string> OnStatusMessage;

        public static string GetRegistryTopicForShop(string shopId)
        {
            string clean = Regex.Replace(shopId ?? "pilot", @"[^a-zA-Z0-9_-]", "").ToLowerInvariant();
            if (string.IsNullOrEmpty(clean) || clean == "pilot")
            {
                return "printaway_station_registry_pilot_9f8a7b6c5d4e";
            }
            return "printaway_station_registry_" + clean + "_9f8a7b";
        }

        public TunnelManager(string rootDir, int localPort = 8080, string shopId = "midtownstationary", string shopName = "Midtown Stationary")
        {
            _rootDir = rootDir;
            _localPort = localPort;
            _shopId = !string.IsNullOrWhiteSpace(shopId) ? shopId.Trim() : "midtownstationary";
            _shopName = !string.IsNullOrWhiteSpace(shopName) ? shopName.Trim() : "Midtown Stationary";
            _registryTopic = GetRegistryTopicForShop(_shopId);
            _activeProviderName = "Initializing";
        }

        public void UpdateShopIdentity(string newShopId, string newShopName)
        {
            if (!string.IsNullOrWhiteSpace(newShopId)) _shopId = newShopId.Trim();
            if (!string.IsNullOrWhiteSpace(newShopName)) _shopName = newShopName.Trim();
            _registryTopic = GetRegistryTopicForShop(_shopId);
            if (!string.IsNullOrEmpty(_liveTunnelUrl))
            {
                PublishToRegistry(_liveTunnelUrl);
            }
        }

        public string LiveTunnelUrl { get { return _liveTunnelUrl; } }
        public string ActiveProviderName { get { return _activeProviderName; } }

        public void Start()
        {
            lock (_lock)
            {
                if (_disposed) return;
                KillOrphanTunnelProcesses();
                if (_tunnelProcess != null && !_tunnelProcess.HasExited) return;

                // 1. Read portable config for tunnel settings
                string token = null;
                string customUrl = null;
                string providerMode = "auto"; // "auto", "cloudflare", "openssh", "custom"

                try
                {
                    string cfgPath = Path.Combine(_rootDir, "config", "station.json");
                    if (!File.Exists(cfgPath)) cfgPath = Path.Combine(_rootDir, "station.json");
                    if (File.Exists(cfgPath))
                    {
                        var serializer = new JavaScriptSerializer();
                        var dict = serializer.Deserialize<Dictionary<string, object>>(File.ReadAllText(cfgPath));
                        if (dict != null)
                        {
                            if (dict.ContainsKey("cloudflareTunnelToken") && dict["cloudflareTunnelToken"] != null)
                                token = Convert.ToString(dict["cloudflareTunnelToken"]).Trim();
                            if (dict.ContainsKey("customTunnelUrl") && dict["customTunnelUrl"] != null)
                                customUrl = Convert.ToString(dict["customTunnelUrl"]).Trim();
                            if (dict.ContainsKey("tunnelProvider") && dict["tunnelProvider"] != null)
                                providerMode = Convert.ToString(dict["tunnelProvider"]).Trim().ToLowerInvariant();
                        }
                    }
                }
                catch { }

                if (string.IsNullOrWhiteSpace(token))
                    token = Environment.GetEnvironmentVariable("CLOUDFLARE_TUNNEL_TOKEN");
                if (string.IsNullOrWhiteSpace(customUrl))
                    customUrl = Environment.GetEnvironmentVariable("CUSTOM_TUNNEL_URL");

                // Case A: Custom Static Domain / Reverse Proxy
                if (!string.IsNullOrWhiteSpace(customUrl) && (providerMode == "custom" || string.IsNullOrWhiteSpace(token)))
                {
                    _activeProviderName = "Custom Domain";
                    _liveTunnelUrl = customUrl;
                    Log("Custom static tunnel URL configured: " + customUrl);
                    PublishToRegistry(customUrl);
                    FireCaptured(customUrl);
                    return;
                }

                // Case B: Cloudflare Named Tunnel with Authenticated Token (Production SLA)
                if (!string.IsNullOrWhiteSpace(token))
                {
                    StartCloudflareNamedTunnel(token, customUrl);
                    return;
                }

                // Case C: Explicit OpenSSH tunnel requested
                if (providerMode == "openssh")
                {
                    StartOpenSshTunnel();
                    return;
                }

                // Case D: Auto Mode (Primary: Cloudflare Quick Tunnel -> Resilient Fallback: Built-in OpenSSH)
                StartCloudflareQuickTunnel();
            }
        }

        private void StartCloudflareNamedTunnel(string token, string staticUrl)
        {
            string exePath = EnsureCloudflared();
            if (string.IsNullOrEmpty(exePath))
            {
                Log("Cloudflare binary unavailable. Switching to OpenSSH tunnel...");
                StartOpenSshTunnel();
                return;
            }

            try
            {
                Log("Starting authenticated Cloudflare Named Tunnel (Zero Rate-Limit)...");
                _activeProviderName = "Cloudflare Named Tunnel";

                var psi = new ProcessStartInfo
                {
                    FileName = exePath,
                    Arguments = string.Format(CultureInfo.InvariantCulture, "tunnel run --token {0}", token),
                    CreateNoWindow = true,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    WorkingDirectory = Path.GetDirectoryName(exePath)
                };

                _tunnelProcess = new Process { StartInfo = psi };
                _tunnelProcess.EnableRaisingEvents = true;
                _tunnelProcess.Exited += (s, e) =>
                {
                    if (!_disposed)
                    {
                        Log("Cloudflare Named Tunnel exited. Reconnecting in 10s...");
                        ScheduleRestart(10000);
                    }
                };

                _tunnelProcess.Start();
                _tunnelProcess.BeginOutputReadLine();
                _tunnelProcess.BeginErrorReadLine();

                if (!string.IsNullOrWhiteSpace(staticUrl))
                {
                    _liveTunnelUrl = staticUrl;
                    Log("Cloudflare Named Tunnel Active: " + staticUrl);
                    PublishToRegistry(staticUrl);
                    FireCaptured(staticUrl);
                }

                StartBroadcastTimer();
            }
            catch (Exception ex)
            {
                Log("Error starting Cloudflare Named Tunnel: " + ex.Message + ". Falling back to OpenSSH...");
                StartOpenSshTunnel();
            }
        }

        private void StartCloudflareQuickTunnel()
        {
            string exePath = EnsureCloudflared();
            if (string.IsNullOrEmpty(exePath))
            {
                Log("Cloudflare binary not found. Activating OpenSSH tunnel...");
                StartOpenSshTunnel();
                return;
            }

            try
            {
                Log("Connecting Cloudflare HTTP/2 Quick Tunnel...");
                _activeProviderName = "Cloudflare Quick Tunnel";
                _isSwitchingToFallback = false;

                var psi = new ProcessStartInfo
                {
                    FileName = exePath,
                    Arguments = string.Format(CultureInfo.InvariantCulture, "tunnel --protocol http2 --url http://127.0.0.1:{0}", _localPort),
                    CreateNoWindow = true,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    WorkingDirectory = Path.GetDirectoryName(exePath)
                };

                _tunnelProcess = new Process { StartInfo = psi };
                _tunnelProcess.OutputDataReceived += HandleCloudflareOutput;
                _tunnelProcess.ErrorDataReceived += HandleCloudflareOutput;
                _tunnelProcess.EnableRaisingEvents = true;
                _tunnelProcess.Exited += (s, e) =>
                {
                    if (_disposed || _isSwitchingToFallback) return;

                    if (string.IsNullOrEmpty(_liveTunnelUrl))
                    {
                        Log("Cloudflare Quick Tunnel exited (rate-limited or offline). Activating OpenSSH fallback...");
                        _isSwitchingToFallback = true;
                        ThreadPool.QueueUserWorkItem(state => StartOpenSshTunnel());
                    }
                    else
                    {
                        Log("Cloudflare Tunnel disconnected. Reconnecting in 10s...");
                        ScheduleRestart(10000);
                    }
                };

                _tunnelProcess.Start();
                _tunnelProcess.BeginOutputReadLine();
                _tunnelProcess.BeginErrorReadLine();

                StartBroadcastTimer();
            }
            catch (Exception ex)
            {
                Log("Failed to start Cloudflare: " + ex.Message + ". Activating OpenSSH fallback...");
                StartOpenSshTunnel();
            }
        }

        private void HandleCloudflareOutput(object sender, DataReceivedEventArgs e)
        {
            if (string.IsNullOrEmpty(e.Data)) return;

            string line = e.Data;

            // Check for Cloudflare HTTP 429 Rate Limit
            if (line.IndexOf("status 429", StringComparison.OrdinalIgnoreCase) >= 0 ||
                line.IndexOf("quick tunnel provisioning failed", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                if (!_isSwitchingToFallback && string.IsNullOrEmpty(_liveTunnelUrl))
                {
                    _isSwitchingToFallback = true;
                    Log("Cloudflare rate-limit active (HTTP 429). Instantly switching to resilient OpenSSH tunnel...");
                    ThreadPool.QueueUserWorkItem(state =>
                    {
                        StopProcessOnly();
                        StartOpenSshTunnel();
                    });
                }
                return;
            }

            Match match = CloudflareUrlRegex.Match(line);
            if (match.Success)
            {
                string url = match.Value.Trim();
                if (url.IndexOf("api.trycloudflare.com", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    url.IndexOf("pkg.trycloudflare.com", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return; // Ignore internal management API endpoint
                }

                if (!string.Equals(_liveTunnelUrl, url, StringComparison.OrdinalIgnoreCase))
                {
                    _liveTunnelUrl = url;
                    _activeProviderName = "Cloudflare Quick Tunnel";
                    Log("Cloudflare Tunnel Live: " + url);
                    PublishToRegistry(url);
                    FireCaptured(url);
                }
            }
        }

        private void StartOpenSshTunnel()
        {
            lock (_lock)
            {
                if (_disposed) return;
                StopProcessOnly();
                _liveTunnelUrl = null;

                string sshExe = FindOpenSshExecutable();
                if (string.IsNullOrEmpty(sshExe))
                {
                    Log("Warning: OpenSSH client (ssh.exe) not found on Windows host. Localhost only.");
                    return;
                }

                try
                {
                    Log("Activating High-Availability OpenSSH Tunnel (localhost.run)...");
                    _activeProviderName = "Built-in OpenSSH Tunnel";

                    var psi = new ProcessStartInfo
                    {
                        FileName = sshExe,
                        Arguments = string.Format(CultureInfo.InvariantCulture, "-o StrictHostKeyChecking=no -o ServerAliveInterval=30 -R 80:127.0.0.1:{0} nokey@localhost.run", _localPort),
                        CreateNoWindow = true,
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true
                    };

                    _tunnelProcess = new Process { StartInfo = psi };
                    _tunnelProcess.OutputDataReceived += HandleOpenSshOutput;
                    _tunnelProcess.ErrorDataReceived += HandleOpenSshOutput;
                    _tunnelProcess.EnableRaisingEvents = true;
                    _tunnelProcess.Exited += (s, e) =>
                    {
                        if (!_disposed)
                        {
                            Log("OpenSSH tunnel disconnected. Reconnecting in 5s...");
                            ScheduleRestart(5000);
                        }
                    };

                    _tunnelProcess.Start();
                    _tunnelProcess.BeginOutputReadLine();
                    _tunnelProcess.BeginErrorReadLine();

                    StartBroadcastTimer();
                }
                catch (Exception ex)
                {
                    Log("Failed to start OpenSSH tunnel: " + ex.Message);
                }
            }
        }

        private void HandleOpenSshOutput(object sender, DataReceivedEventArgs e)
        {
            if (string.IsNullOrEmpty(e.Data)) return;

            Match match = SshUrlRegex.Match(e.Data);
            if (match.Success)
            {
                string url = match.Value.Trim();
                if (!string.Equals(_liveTunnelUrl, url, StringComparison.OrdinalIgnoreCase))
                {
                    _liveTunnelUrl = url;
                    _activeProviderName = "Built-in OpenSSH (localhost.run)";
                    Log("OpenSSH Fallback Tunnel Live: " + url);
                    PublishToRegistry(url);
                    FireCaptured(url);
                }
            }
        }

        private string FindOpenSshExecutable()
        {
            string systemSsh = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "OpenSSH", "ssh.exe");
            if (File.Exists(systemSsh)) return systemSsh;

            string winDir = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            string sysNativeSsh = Path.Combine(winDir, "sysnative", "OpenSSH", "ssh.exe");
            if (File.Exists(sysNativeSsh)) return sysNativeSsh;

            string sys32Ssh = Path.Combine(winDir, "System32", "OpenSSH", "ssh.exe");
            if (File.Exists(sys32Ssh)) return sys32Ssh;

            return "ssh.exe";
        }

        private string EnsureCloudflared()
        {
            string binDir = Path.Combine(_rootDir, "bin");
            if (!Directory.Exists(binDir)) Directory.CreateDirectory(binDir);

            string exePath = Path.Combine(binDir, "cloudflared.exe");
            if (!File.Exists(exePath))
            {
                string altExe = Path.Combine(_rootDir, "cloudflared.exe");
                if (File.Exists(altExe)) return altExe;

                Log("Cloudflared binary missing. Downloading official Cloudflare Tunnel engine...");
                try
                {
                    ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12 | SecurityProtocolType.Tls11 | SecurityProtocolType.Tls;
                    using (var client = new WebClient())
                    {
                        client.DownloadFile("https://github.com/cloudflare/cloudflared/releases/latest/download/cloudflared-windows-amd64.exe", exePath);
                    }
                }
                catch (Exception ex)
                {
                    Log("Warning: Could not download cloudflared.exe: " + ex.Message);
                    return null;
                }
            }
            return File.Exists(exePath) ? exePath : null;
        }

        public void Restart()
        {
            Log("Restarting tunnel connection...");
            Stop();
            _liveTunnelUrl = null;
            Start();
        }

        private void ScheduleRestart(int delayMs)
        {
            ThreadPool.QueueUserWorkItem(state =>
            {
                Thread.Sleep(delayMs);
                if (!_disposed)
                {
                    Start();
                }
            });
        }

        private void StartBroadcastTimer()
        {
            if (_broadcastTimer != null) return;
            _broadcastTimer = new Timer(state =>
            {
                if (!string.IsNullOrWhiteSpace(_liveTunnelUrl))
                {
                    PublishToRegistry(_liveTunnelUrl);
                }
            }, null, TimeSpan.FromMinutes(15), TimeSpan.FromMinutes(15));
        }

        private void FireCaptured(string url)
        {
            Action<string> handler = OnTunnelUrlCaptured;
            if (handler != null)
            {
                try { handler(url); } catch { }
            }
        }

        private void Log(string message)
        {
            Action<string> handler = OnStatusMessage;
            if (handler != null)
            {
                try { handler(message); } catch { }
            }
        }

        private void PublishToRegistry(string url)
        {
            if (string.IsNullOrWhiteSpace(url)) return;
            string cleanUrl = url.Trim();
            if (cleanUrl.IndexOf("api.trycloudflare.com", StringComparison.OrdinalIgnoreCase) >= 0 ||
                cleanUrl.IndexOf("pkg.trycloudflare.com", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return;
            }

            // 1. Write to data/tunnel.url for instant zero-latency file sync
            try
            {
                string dataDir = Path.Combine(_rootDir, "data");
                if (!Directory.Exists(dataDir)) Directory.CreateDirectory(dataDir);
                File.WriteAllText(Path.Combine(dataDir, "tunnel.url"), cleanUrl, Encoding.UTF8);
            }
            catch { }

            // 2. Broadcast to ntfy.sh Cloud Registry Topic
            string topic = !string.IsNullOrWhiteSpace(_registryTopic) ? _registryTopic : GetRegistryTopicForShop(_shopId);
            try
            {
                ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12 | SecurityProtocolType.Tls11 | SecurityProtocolType.Tls;
                string target = "https://ntfy.sh/" + topic;
                var req = (HttpWebRequest)WebRequest.Create(target);
                req.Method = "POST";
                req.ContentType = "text/plain";
                req.Timeout = 8000;
                byte[] bytes = Encoding.UTF8.GetBytes(cleanUrl);
                req.ContentLength = bytes.Length;
                using (var s = req.GetRequestStream())
                {
                    s.Write(bytes, 0, bytes.Length);
                }
                using (var resp = (HttpWebResponse)req.GetResponse())
                {
                    Log(string.Format(CultureInfo.InvariantCulture, "Broadcasted tunnel to Cloud Registry ({0}) -> Status: {1}", topic, (int)resp.StatusCode));
                }
            }
            catch (Exception ex)
            {
                Log("Warning broadcasting to registry: " + ex.Message);
            }

            // 3. Directly patch & auto-insert into Supabase Cloud Control Plane
            try
            {
                string sbUrl = "https://zduoifzakiojfqbtxqqm.supabase.co";
                string sbKey = "sb_publishable_rhmUZZbKhXiJxrChcPTbsg_gFIH9FdO";
                string now = DateTime.UtcNow.ToString("o");

                // Step A: PATCH existing shop
                string patchTarget = string.Format(CultureInfo.InvariantCulture, "{0}/rest/v1/shops?shop_id=eq.{1}", sbUrl, Uri.EscapeDataString(_shopId));
                var patchReq = (HttpWebRequest)WebRequest.Create(patchTarget);
                patchReq.Method = "PATCH";
                patchReq.Headers.Add("apikey", sbKey);
                patchReq.Headers.Add("Authorization", "Bearer " + sbKey);
                patchReq.Headers.Add("Prefer", "return=representation");
                patchReq.ContentType = "application/json";
                patchReq.Timeout = 8000;

                var patchDict = new Dictionary<string, object>
                {
                    { "live_tunnel_url", cleanUrl },
                    { "last_heartbeat_utc", now }
                };
                byte[] patchBytes = Encoding.UTF8.GetBytes(new JavaScriptSerializer().Serialize(patchDict));
                patchReq.ContentLength = patchBytes.Length;
                using (var s = patchReq.GetRequestStream()) { s.Write(patchBytes, 0, patchBytes.Length); }

                string respBody = "";
                using (var resp = (HttpWebResponse)patchReq.GetResponse())
                using (var r = new StreamReader(resp.GetResponseStream()))
                {
                    respBody = r.ReadToEnd();
                }

                // Step B: If shop did not exist in Supabase (empty array "[]"), auto-insert it!
                if (string.IsNullOrWhiteSpace(respBody) || respBody.Trim() == "[]")
                {
                    string postTarget = string.Format(CultureInfo.InvariantCulture, "{0}/rest/v1/shops", sbUrl);
                    var postReq = (HttpWebRequest)WebRequest.Create(postTarget);
                    postReq.Method = "POST";
                    postReq.Headers.Add("apikey", sbKey);
                    postReq.Headers.Add("Authorization", "Bearer " + sbKey);
                    postReq.Headers.Add("Prefer", "resolution=merge-duplicates");
                    postReq.ContentType = "application/json";
                    postReq.Timeout = 8000;

                    var postDict = new Dictionary<string, object>
                    {
                        { "shop_id", _shopId },
                        { "shop_name", _shopName ?? _shopId },
                        { "owner_name", _shopId },
                        { "owner_phone", "9999999999" },
                        { "upi_vpa", _shopId + "@upi" },
                        { "live_tunnel_url", cleanUrl },
                        { "last_heartbeat_utc", now }
                    };
                    byte[] postBytes = Encoding.UTF8.GetBytes(new JavaScriptSerializer().Serialize(postDict));
                    postReq.ContentLength = postBytes.Length;
                    using (var s = postReq.GetRequestStream()) { s.Write(postBytes, 0, postBytes.Length); }
                    using (var postResp = (HttpWebResponse)postReq.GetResponse()) { }
                }

                // Step C: If shop is not pilot, also update pilot row as fallback bridge
                if (_shopId != "pilot")
                {
                    try
                    {
                        string pilotPatchTarget = string.Format(CultureInfo.InvariantCulture, "{0}/rest/v1/shops?shop_id=eq.pilot", sbUrl);
                        var pilotReq = (HttpWebRequest)WebRequest.Create(pilotPatchTarget);
                        pilotReq.Method = "PATCH";
                        pilotReq.Headers.Add("apikey", sbKey);
                        pilotReq.Headers.Add("Authorization", "Bearer " + sbKey);
                        pilotReq.ContentType = "application/json";
                        pilotReq.Timeout = 5000;
                        pilotReq.ContentLength = patchBytes.Length;
                        using (var s = pilotReq.GetRequestStream()) { s.Write(patchBytes, 0, patchBytes.Length); }
                        using (var pResp = (HttpWebResponse)pilotReq.GetResponse()) { }
                    }
                    catch { }
                }

                Log("Supabase SaaS Control Plane updated with live tunnel: " + cleanUrl);
            }
            catch (Exception ex)
            {
                Log("Warning updating Supabase control plane: " + ex.Message);
            }
        }

        private void StopProcessOnly()
        {
            if (_tunnelProcess != null)
            {
                try
                {
                    if (!_tunnelProcess.HasExited)
                    {
                        _tunnelProcess.Kill();
                        _tunnelProcess.WaitForExit(2000);
                    }
                }
                catch { }
                finally
                {
                    try { _tunnelProcess.Dispose(); } catch { }
                    _tunnelProcess = null;
                }
            }
        }

        private void KillOrphanTunnelProcesses()
        {
            try
            {
                foreach (var p in Process.GetProcessesByName("cloudflared"))
                {
                    try { p.Kill(); } catch { }
                }
                foreach (var p in Process.GetProcessesByName("ssh"))
                {
                    try { p.Kill(); } catch { }
                }
            }
            catch { }
        }

        public void Stop()
        {
            if (_broadcastTimer != null)
            {
                _broadcastTimer.Dispose();
                _broadcastTimer = null;
            }
            StopProcessOnly();
        }

        public void Dispose()
        {
            if (!_disposed)
            {
                _disposed = true;
                Stop();
            }
        }
    }
}
