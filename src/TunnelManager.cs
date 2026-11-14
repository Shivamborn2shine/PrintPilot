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
        private static readonly Regex CloudflareUrlRegex = new Regex(@"https://(?!api\.|pkg\.|update\.|staging\.|www\.)([a-zA-Z0-9-]{5,}\.trycloudflare\.com)", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly Regex SshUrlRegex = new Regex(@"https://[a-zA-Z0-9.-]+\.lhr\.(?:life|rocks|pro)", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly Regex PinggyUrlRegex = new Regex(@"https://[a-zA-Z0-9.-]+\.(?:free\.pinggy\.link|pinggy\.link|pinggy\.io)", RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private readonly string _rootDir;
        private readonly int _localPort;
        private string _shopId;
        private string _shopName;
        private string _registryTopic;
        private readonly object _lock = new object();

        private Process _tunnelProcess;
        private Timer _broadcastTimer;
        private Timer _watchdogTimer;
        private string _liveTunnelUrl;
        private string _activeProviderName;
        private string _healthStatus = "Initializing";
        private DateTime? _lastHealthOkUtc;
        private DateTime _lastNtfyPublishUtc = DateTime.MinValue;
        private string _lastNtfyPublishedUrl;
        private DateTime _cloudflareCooldownUntilUtc = DateTime.MinValue;
        private DateTime _localhostRunCooldownUntilUtc = DateTime.MinValue;
        private DateTime _pinggyCooldownUntilUtc = DateTime.MinValue;
        private int _consecutiveHealthFailures;
        private int _generation;
        private bool _disposed;
        private bool _isSwitchingToFallback;
        private volatile bool _probeInFlight;

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

        public static bool IsValidPublicTunnelUrl(string url)
        {
            if (string.IsNullOrWhiteSpace(url)) return false;
            string clean = url.Trim().TrimEnd('/');
            if (!clean.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) return false;
            if (clean.Length < 12) return false;
            if (clean.IndexOf("api.trycloudflare.com", StringComparison.OrdinalIgnoreCase) >= 0 ||
                clean.IndexOf("pkg.trycloudflare.com", StringComparison.OrdinalIgnoreCase) >= 0 ||
                clean.IndexOf("update.trycloudflare.com", StringComparison.OrdinalIgnoreCase) >= 0 ||
                clean.IndexOf("staging.trycloudflare.com", StringComparison.OrdinalIgnoreCase) >= 0 ||
                clean.IndexOf("www.trycloudflare.com", StringComparison.OrdinalIgnoreCase) >= 0 ||
                clean.IndexOf("localhost.run", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return false;
            }
            Uri parsed;
            if (!Uri.TryCreate(clean, UriKind.Absolute, out parsed)) return false;
            if (string.IsNullOrEmpty(parsed.Host) || parsed.Host.IndexOf('.') < 0) return false;
            return true;
        }

        public TunnelManager(string rootDir, int localPort = 8080, string shopId = "midtownstationary", string shopName = "Midtown Stationary")
        {
            _rootDir = rootDir;
            _localPort = localPort;
            _shopId = !string.IsNullOrWhiteSpace(shopId) ? shopId.Trim() : "midtownstationary";
            _shopName = !string.IsNullOrWhiteSpace(shopName) ? shopName.Trim() : "Midtown Stationary";
            _registryTopic = GetRegistryTopicForShop(_shopId);
            _activeProviderName = "Initializing";
            ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12 | SecurityProtocolType.Tls11 | SecurityProtocolType.Tls;
            ServicePointManager.DefaultConnectionLimit = 64;
        }

        public void UpdateShopIdentity(string newShopId, string newShopName)
        {
            if (!string.IsNullOrWhiteSpace(newShopId)) _shopId = newShopId.Trim();
            if (!string.IsNullOrWhiteSpace(newShopName)) _shopName = newShopName.Trim();
            _registryTopic = GetRegistryTopicForShop(_shopId);
            string url = _liveTunnelUrl;
            if (IsValidPublicTunnelUrl(url))
            {
                _lastNtfyPublishedUrl = null;
                PublishToRegistry(url, true);
            }
        }

        public string LiveTunnelUrl { get { return _liveTunnelUrl; } }
        public string ActiveProviderName { get { return _activeProviderName; } }
        public string CurrentHealthStatus { get { return _healthStatus; } }
        public DateTime? LastHealthOkUtc { get { return _lastHealthOkUtc; } }

        private void ReadConfig(out string token, out string customUrl, out string providerMode)
        {
            token = null;
            customUrl = null;
            providerMode = "auto";

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
                            customUrl = Convert.ToString(dict["customTunnelUrl"]).Trim().TrimEnd('/');
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
        }

        public void Start()
        {
            lock (_lock)
            {
                if (_disposed) return;

                int gen = Interlocked.Increment(ref _generation);
                KillOwnedOrphanTunnelProcesses();
                StopProcessOnly();

                _liveTunnelUrl = null;
                _consecutiveHealthFailures = 0;
                _isSwitchingToFallback = false;
                _healthStatus = "Connecting";

                string token, customUrl, providerMode;
                ReadConfig(out token, out customUrl, out providerMode);

                // Ensure watchdog & heartbeat timers are always running
                StartBroadcastTimer();
                StartWatchdogTimer();

                // Case A: Custom Static Domain / Reverse Proxy (without Cloudflare Named Token)
                if (!string.IsNullOrWhiteSpace(customUrl) && (providerMode == "custom" || string.IsNullOrWhiteSpace(token)))
                {
                    _activeProviderName = "Custom Static Domain";
                    _liveTunnelUrl = customUrl;
                    _healthStatus = "Live";
                    Log("Custom static tunnel URL configured: " + customUrl);
                    PublishToRegistry(customUrl, true);
                    FireCaptured(customUrl);
                    return;
                }

                // Case B: Cloudflare Named Tunnel with Authenticated Token (Permanent Zero-Rate-Limit SLA)
                if (!string.IsNullOrWhiteSpace(token))
                {
                    StartCloudflareNamedTunnel(token, customUrl, gen);
                    return;
                }

                // Case C: Explicit OpenSSH tunnel requested
                if (providerMode == "openssh")
                {
                    StartOpenSshTunnel(gen);
                    return;
                }

                // Case D: Explicit Pinggy SSH tunnel requested
                if (providerMode == "pinggy")
                {
                    StartPinggySshTunnel(gen);
                    return;
                }

                // Case E: Explicit Cloudflare mode requested -> force clear cooldown
                if (providerMode == "cloudflare")
                {
                    _cloudflareCooldownUntilUtc = DateTime.MinValue;
                    StartCloudflareQuickTunnel(gen);
                    return;
                }

                // Case F: Resilient Auto Cascade (Cloudflare Quick -> OpenSSH localhost.run -> Pinggy SSH)
                if (DateTime.UtcNow < _cloudflareCooldownUntilUtc)
                {
                    Log("Cloudflare Quick Tunnel in rate-limit cooldown. Launching resilient OpenSSH tunnel...");
                    if (DateTime.UtcNow < _localhostRunCooldownUntilUtc)
                    {
                        StartPinggySshTunnel(gen);
                    }
                    else
                    {
                        StartOpenSshTunnel(gen);
                    }
                }
                else
                {
                    StartCloudflareQuickTunnel(gen);
                }
            }
        }

        private void StartCloudflareNamedTunnel(string token, string staticUrl, int gen)
        {
            if (_disposed || Volatile.Read(ref _generation) != gen) return;

            string exePath = EnsureCloudflared();
            if (string.IsNullOrEmpty(exePath))
            {
                Log("Cloudflare binary unavailable. Switching to OpenSSH tunnel...");
                StartOpenSshTunnel(gen);
                return;
            }

            try
            {
                Log("Starting authenticated Cloudflare Named Tunnel (Zero Rate-Limit)...");
                _activeProviderName = "Cloudflare Named Tunnel";

                var psi = new ProcessStartInfo
                {
                    FileName = exePath,
                    Arguments = string.Format(CultureInfo.InvariantCulture, "tunnel --no-autoupdate --protocol http2 run --token {0}", token),
                    CreateNoWindow = true,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    WorkingDirectory = Path.GetDirectoryName(exePath)
                };

                _tunnelProcess = new Process { StartInfo = psi };
                _tunnelProcess.OutputDataReceived += (s, e) => HandleNamedTunnelOutput(e.Data, staticUrl, gen);
                _tunnelProcess.ErrorDataReceived += (s, e) => HandleNamedTunnelOutput(e.Data, staticUrl, gen);
                _tunnelProcess.EnableRaisingEvents = true;
                _tunnelProcess.Exited += (s, e) =>
                {
                    if (_disposed || Volatile.Read(ref _generation) != gen) return;
                    _liveTunnelUrl = null;
                    _healthStatus = "Reconnecting";
                    Log("Cloudflare Named Tunnel disconnected. Reconnecting in 5s...");
                    FireCaptured("");
                    ScheduleRestart(5000, gen);
                };

                _tunnelProcess.Start();
                RecordOwnedPid(_tunnelProcess.Id);
                _tunnelProcess.BeginOutputReadLine();
                _tunnelProcess.BeginErrorReadLine();

                if (IsValidPublicTunnelUrl(staticUrl))
                {
                    _liveTunnelUrl = staticUrl;
                    _healthStatus = "Live";
                    _consecutiveHealthFailures = 0;
                    Log("Cloudflare Named Tunnel Active: " + staticUrl);
                    PublishToRegistry(staticUrl, true);
                    FireCaptured(staticUrl);
                }
            }
            catch (Exception ex)
            {
                Log("Error starting Cloudflare Named Tunnel: " + ex.Message + ". Falling back to OpenSSH...");
                StartOpenSshTunnel(gen);
            }
        }

        private void HandleNamedTunnelOutput(string line, string staticUrl, int gen)
        {
            if (string.IsNullOrEmpty(line) || Volatile.Read(ref _generation) != gen) return;
            AppendTunnelLog("[CF-Named] " + line);

            if (line.IndexOf("Registered tunnel connection", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                if (IsValidPublicTunnelUrl(staticUrl))
                {
                    _liveTunnelUrl = staticUrl;
                    _healthStatus = "Live";
                    _consecutiveHealthFailures = 0;
                    _lastHealthOkUtc = DateTime.UtcNow;
                    PublishToRegistry(staticUrl, false);
                    FireCaptured(staticUrl);
                }
            }
            else if (line.IndexOf("Provided Tunnel token is not valid", StringComparison.OrdinalIgnoreCase) >= 0 ||
                     line.IndexOf("Unauthorized", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                Log("Cloudflare Named Tunnel token invalid. Falling back to OpenSSH...");
                ThreadPool.QueueUserWorkItem(_ =>
                {
                    lock (_lock)
                    {
                        if (_disposed || Volatile.Read(ref _generation) != gen) return;
                        StopProcessOnly();
                        StartOpenSshTunnel(gen);
                    }
                });
            }
        }

        private void StartCloudflareQuickTunnel(int gen)
        {
            if (_disposed || Volatile.Read(ref _generation) != gen) return;

            string exePath = EnsureCloudflared();
            if (string.IsNullOrEmpty(exePath))
            {
                Log("Cloudflare binary not found. Activating OpenSSH tunnel...");
                StartOpenSshTunnel(gen);
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
                    Arguments = string.Format(CultureInfo.InvariantCulture, "tunnel --no-autoupdate --protocol http2 --url http://127.0.0.1:{0}", _localPort),
                    CreateNoWindow = true,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    WorkingDirectory = Path.GetDirectoryName(exePath)
                };

                _tunnelProcess = new Process { StartInfo = psi };
                _tunnelProcess.OutputDataReceived += (s, e) => HandleCloudflareOutput(e.Data, gen);
                _tunnelProcess.ErrorDataReceived += (s, e) => HandleCloudflareOutput(e.Data, gen);
                _tunnelProcess.EnableRaisingEvents = true;
                _tunnelProcess.Exited += (s, e) =>
                {
                    if (_disposed || _isSwitchingToFallback || Volatile.Read(ref _generation) != gen) return;

                    bool hadUrl = !string.IsNullOrEmpty(_liveTunnelUrl);
                    _liveTunnelUrl = null;
                    _healthStatus = "Reconnecting";
                    FireCaptured("");

                    if (!hadUrl)
                    {
                        Log("Cloudflare Quick Tunnel exited (rate-limited or offline). Activating OpenSSH fallback...");
                        _cloudflareCooldownUntilUtc = DateTime.UtcNow.AddMinutes(5);
                        SwitchToFallbackLocked(gen, true);
                    }
                    else
                    {
                        Log("Cloudflare Tunnel disconnected under load. Reconnecting automatically...");
                        ScheduleRestart(3000, gen);
                    }
                };

                _tunnelProcess.Start();
                RecordOwnedPid(_tunnelProcess.Id);
                _tunnelProcess.BeginOutputReadLine();
                _tunnelProcess.BeginErrorReadLine();

                // Startup timeout guard: if no URL is captured in 20s, do not hang -> fall back to OpenSSH
                ThreadPool.QueueUserWorkItem(_ =>
                {
                    Thread.Sleep(20000);
                    lock (_lock)
                    {
                        if (_disposed || _isSwitchingToFallback || Volatile.Read(ref _generation) != gen) return;
                        if (string.IsNullOrEmpty(_liveTunnelUrl))
                        {
                            Log("Cloudflare Quick Tunnel timed out after 20s. Switching to OpenSSH fallback...");
                            _cloudflareCooldownUntilUtc = DateTime.UtcNow.AddMinutes(5);
                            SwitchToFallbackLocked(gen, true);
                        }
                    }
                });
            }
            catch (Exception ex)
            {
                Log("Cloudflare failed to start (" + ex.Message + "). Activating OpenSSH tunnel...");
                _cloudflareCooldownUntilUtc = DateTime.UtcNow.AddMinutes(5);
                StartOpenSshTunnel(gen);
            }
        }

        private void HandleCloudflareOutput(string line, int gen)
        {
            if (string.IsNullOrEmpty(line) || Volatile.Read(ref _generation) != gen) return;
            AppendTunnelLog("[CF-Quick] " + line);

            if (line.IndexOf("status 429", StringComparison.OrdinalIgnoreCase) >= 0 ||
                line.IndexOf("quick tunnel provisioning failed", StringComparison.OrdinalIgnoreCase) >= 0 ||
                line.IndexOf("too many requests", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                _cloudflareCooldownUntilUtc = DateTime.UtcNow.AddMinutes(5);
                if (!_isSwitchingToFallback)
                {
                    Log("Cloudflare rate-limit active (HTTP 429). Switching immediately to resilient OpenSSH tunnel...");
                    ThreadPool.QueueUserWorkItem(_ =>
                    {
                        lock (_lock)
                        {
                            SwitchToFallbackLocked(gen, true);
                        }
                    });
                }
                return;
            }

            Match match = CloudflareUrlRegex.Match(line);
            if (match.Success)
            {
                string capturedUrl = match.Value.Trim().TrimEnd('/');
                if (!IsValidPublicTunnelUrl(capturedUrl)) return;

                if (!string.Equals(_liveTunnelUrl, capturedUrl, StringComparison.OrdinalIgnoreCase))
                {
                    _liveTunnelUrl = capturedUrl;
                    _activeProviderName = "Cloudflare Tunnel";
                    _healthStatus = "Live";
                    _consecutiveHealthFailures = 0;
                    _lastHealthOkUtc = DateTime.UtcNow;
                    Log("Cloudflare Tunnel Live: " + capturedUrl);
                    PublishToRegistry(capturedUrl, true);
                    FireCaptured(capturedUrl);
                }
            }
        }

        private void SwitchToFallbackLocked(int expectedGen, bool useOpenSshFirst)
        {
            if (_disposed || Volatile.Read(ref _generation) != expectedGen) return;
            _isSwitchingToFallback = true;
            _liveTunnelUrl = null;
            _healthStatus = "Reconnecting";
            int newGen = Interlocked.Increment(ref _generation);
            StopProcessOnly();
            _isSwitchingToFallback = false;

            if (useOpenSshFirst && DateTime.UtcNow >= _localhostRunCooldownUntilUtc)
            {
                StartOpenSshTunnel(newGen);
            }
            else
            {
                StartPinggySshTunnel(newGen);
            }
        }

        private void StartOpenSshTunnel(int gen)
        {
            lock (_lock)
            {
                if (_disposed || Volatile.Read(ref _generation) != gen) return;

                try
                {
                    StopProcessOnly();
                    _isSwitchingToFallback = false;
                    _liveTunnelUrl = null;
                    string sshExe = FindOpenSshExecutable();
                    Log("Connecting built-in OpenSSH Tunnel (localhost.run)...");
                    _activeProviderName = "OpenSSH (localhost.run)";

                    var psi = new ProcessStartInfo
                    {
                        FileName = sshExe,
                        Arguments = string.Format(
                            CultureInfo.InvariantCulture,
                            "-T -o StrictHostKeyChecking=no -o UserKnownHostsFile=NUL -o ServerAliveInterval=15 -o ServerAliveCountMax=3 -o ExitOnForwardFailure=yes -o ConnectTimeout=10 -o TCPKeepAlive=yes -R 80:127.0.0.1:{0} nokey@localhost.run",
                            _localPort),
                        CreateNoWindow = true,
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true
                    };

                    _tunnelProcess = new Process { StartInfo = psi };
                    _tunnelProcess.OutputDataReceived += (s, e) => HandleOpenSshOutput(e.Data, gen);
                    _tunnelProcess.ErrorDataReceived += (s, e) => HandleOpenSshOutput(e.Data, gen);
                    _tunnelProcess.EnableRaisingEvents = true;
                    _tunnelProcess.Exited += (s, e) =>
                    {
                        if (_disposed || _isSwitchingToFallback || Volatile.Read(ref _generation) != gen) return;

                        bool hadUrl = !string.IsNullOrEmpty(_liveTunnelUrl);
                        _liveTunnelUrl = null;
                        _healthStatus = "Reconnecting";
                        FireCaptured("");

                        if (!hadUrl)
                        {
                            Log("localhost.run SSH did not establish tunnel. Trying Pinggy SSH backup...");
                            _localhostRunCooldownUntilUtc = DateTime.UtcNow.AddMinutes(3);
                            ThreadPool.QueueUserWorkItem(_ =>
                            {
                                lock (_lock)
                                {
                                    if (_disposed || Volatile.Read(ref _generation) != gen) return;
                                    int nextGen = Interlocked.Increment(ref _generation);
                                    StopProcessOnly();
                                    StartPinggySshTunnel(nextGen);
                                }
                            });
                        }
                        else
                        {
                            Log("OpenSSH tunnel disconnected. Reconnecting in 3s...");
                            ScheduleRestart(3000, gen);
                        }
                    };

                    _tunnelProcess.Start();
                    RecordOwnedPid(_tunnelProcess.Id);
                    _tunnelProcess.BeginOutputReadLine();
                    _tunnelProcess.BeginErrorReadLine();

                    // Startup timeout guard (20s)
                    ThreadPool.QueueUserWorkItem(_ =>
                    {
                        Thread.Sleep(20000);
                        lock (_lock)
                        {
                            if (_disposed || _isSwitchingToFallback || Volatile.Read(ref _generation) != gen) return;
                            if (string.IsNullOrEmpty(_liveTunnelUrl))
                            {
                                Log("localhost.run timed out after 20s. Switching to Pinggy SSH backup...");
                                _localhostRunCooldownUntilUtc = DateTime.UtcNow.AddMinutes(3);
                                int nextGen = Interlocked.Increment(ref _generation);
                                StopProcessOnly();
                                StartPinggySshTunnel(nextGen);
                            }
                        }
                    });
                }
                catch (Exception ex)
                {
                    Log("Failed to start OpenSSH tunnel: " + ex.Message + ". Trying Pinggy SSH...");
                    StartPinggySshTunnel(gen);
                }
            }
        }

        private void HandleOpenSshOutput(string line, int gen)
        {
            if (string.IsNullOrEmpty(line) || Volatile.Read(ref _generation) != gen) return;
            AppendTunnelLog("[SSH-LHR] " + line);

            Match match = SshUrlRegex.Match(line);
            if (match.Success)
            {
                string url = match.Value.Trim().TrimEnd('/');
                if (!IsValidPublicTunnelUrl(url)) return;

                if (!string.Equals(_liveTunnelUrl, url, StringComparison.OrdinalIgnoreCase))
                {
                    _liveTunnelUrl = url;
                    _activeProviderName = "OpenSSH (localhost.run)";
                    _healthStatus = "Live";
                    _consecutiveHealthFailures = 0;
                    _lastHealthOkUtc = DateTime.UtcNow;
                    Log("OpenSSH Tunnel Live: " + url);
                    PublishToRegistry(url, true);
                    FireCaptured(url);
                }
            }
        }

        private void StartPinggySshTunnel(int gen)
        {
            lock (_lock)
            {
                if (_disposed || Volatile.Read(ref _generation) != gen) return;

                try
                {
                    StopProcessOnly();
                    _isSwitchingToFallback = false;
                    _liveTunnelUrl = null;
                    string sshExe = FindOpenSshExecutable();
                    Log("Connecting backup Pinggy SSH Tunnel...");
                    _activeProviderName = "Pinggy SSH Tunnel";

                    var psi = new ProcessStartInfo
                    {
                        FileName = sshExe,
                        Arguments = string.Format(
                            CultureInfo.InvariantCulture,
                            "-T -p 443 -o StrictHostKeyChecking=no -o UserKnownHostsFile=NUL -o ServerAliveInterval=15 -o ServerAliveCountMax=3 -o ExitOnForwardFailure=yes -o ConnectTimeout=10 -R0:127.0.0.1:{0} qr@a.pinggy.io",
                            _localPort),
                        CreateNoWindow = true,
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true
                    };

                    _tunnelProcess = new Process { StartInfo = psi };
                    _tunnelProcess.OutputDataReceived += (s, e) => HandlePinggyOutput(e.Data, gen);
                    _tunnelProcess.ErrorDataReceived += (s, e) => HandlePinggyOutput(e.Data, gen);
                    _tunnelProcess.EnableRaisingEvents = true;
                    _tunnelProcess.Exited += (s, e) =>
                    {
                        if (_disposed || _isSwitchingToFallback || Volatile.Read(ref _generation) != gen) return;
                        _liveTunnelUrl = null;
                        _healthStatus = "Reconnecting";
                        _pinggyCooldownUntilUtc = DateTime.UtcNow.AddMinutes(2);
                        FireCaptured("");
                        Log("Pinggy SSH tunnel disconnected. Reconnecting in 5s...");
                        ScheduleRestart(5000, gen);
                    };

                    _tunnelProcess.Start();
                    RecordOwnedPid(_tunnelProcess.Id);
                    _tunnelProcess.BeginOutputReadLine();
                    _tunnelProcess.BeginErrorReadLine();

                    // Startup timeout guard (20s)
                    ThreadPool.QueueUserWorkItem(_ =>
                    {
                        Thread.Sleep(20000);
                        lock (_lock)
                        {
                            if (_disposed || _isSwitchingToFallback || Volatile.Read(ref _generation) != gen) return;
                            if (string.IsNullOrEmpty(_liveTunnelUrl))
                            {
                                Log("All free public tunnels busy right now. Retrying in 8s...");
                                ScheduleRestart(8000, gen);
                            }
                        }
                    });
                }
                catch (Exception ex)
                {
                    Log("Pinggy SSH fallback failed: " + ex.Message + ". Retrying in 10s...");
                    ScheduleRestart(10000, gen);
                }
            }
        }

        private void HandlePinggyOutput(string line, int gen)
        {
            if (string.IsNullOrEmpty(line) || Volatile.Read(ref _generation) != gen) return;
            AppendTunnelLog("[SSH-Pinggy] " + line);

            Match match = PinggyUrlRegex.Match(line);
            if (match.Success)
            {
                string url = match.Value.Trim().TrimEnd('/');
                if (!IsValidPublicTunnelUrl(url)) return;

                if (!string.Equals(_liveTunnelUrl, url, StringComparison.OrdinalIgnoreCase))
                {
                    _liveTunnelUrl = url;
                    _activeProviderName = "Pinggy SSH Tunnel";
                    _healthStatus = "Live";
                    _consecutiveHealthFailures = 0;
                    _lastHealthOkUtc = DateTime.UtcNow;
                    Log("Pinggy Backup Tunnel Live: " + url);
                    PublishToRegistry(url, true);
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
            _cloudflareCooldownUntilUtc = DateTime.MinValue;
            _localhostRunCooldownUntilUtc = DateTime.MinValue;
            _pinggyCooldownUntilUtc = DateTime.MinValue;
            Stop();
            _liveTunnelUrl = null;
            Start();
        }

        private void ScheduleRestart(int delayMs, int expectedGen)
        {
            ThreadPool.QueueUserWorkItem(_ =>
            {
                Thread.Sleep(delayMs);
                if (_disposed || Volatile.Read(ref _generation) != expectedGen) return;
                Start();
            });
        }

        private void StartBroadcastTimer()
        {
            if (_broadcastTimer != null) return;
            // Fast 2-minute Supabase heartbeat keeps station green; ntfy repinged on URL change or every 12 mins
            _broadcastTimer = new Timer(_ =>
            {
                string url = _liveTunnelUrl;
                if (IsValidPublicTunnelUrl(url))
                {
                    bool sendNtfy = !string.Equals(_lastNtfyPublishedUrl, url, StringComparison.OrdinalIgnoreCase) ||
                                    (DateTime.UtcNow - _lastNtfyPublishUtc).TotalMinutes >= 12.0;
                    PublishToRegistry(url, sendNtfy);
                }
            }, null, TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(2));
        }

        private void StartWatchdogTimer()
        {
            if (_watchdogTimer != null) return;
            _watchdogTimer = new Timer(_ => RunWatchdogCheck(), null, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30));
        }

        public bool RunManualProbe(out string detail)
        {
            detail = "";
            if (!ProbeLocalServer())
            {
                detail = "Local PrintAway HTTP server (127.0.0.1:" + _localPort + ") is not responding.";
                return false;
            }
            string url = _liveTunnelUrl;
            if (!IsValidPublicTunnelUrl(url))
            {
                detail = "No active public tunnel URL yet. Provider: " + (_activeProviderName ?? "Unknown");
                return false;
            }
            string err;
            if (ProbePublicTunnel(url, out err))
            {
                _lastHealthOkUtc = DateTime.UtcNow;
                _consecutiveHealthFailures = 0;
                _healthStatus = "Live";
                detail = "Tunnel Healthy (" + (_activeProviderName ?? "Active") + ") -> " + url;
                return true;
            }
            detail = "Public probe failed for " + url + ": " + err;
            return false;
        }

        private void RunWatchdogCheck()
        {
            if (_disposed || _probeInFlight) return;
            _probeInFlight = true;
            try
            {
                // 1. Verify local server is accepting requests before blaming tunnel
                if (!ProbeLocalServer()) return;

                string url = _liveTunnelUrl;
                if (!IsValidPublicTunnelUrl(url)) return;

                string err;
                if (ProbePublicTunnel(url, out err))
                {
                    _consecutiveHealthFailures = 0;
                    _lastHealthOkUtc = DateTime.UtcNow;
                    _healthStatus = "Live";
                    return;
                }

                // First failure -> wait 2.5s and re-confirm so momentary latency doesn't flap a healthy link
                _consecutiveHealthFailures++;
                Thread.Sleep(2500);
                if (ProbePublicTunnel(url, out err))
                {
                    _consecutiveHealthFailures = 0;
                    _lastHealthOkUtc = DateTime.UtcNow;
                    _healthStatus = "Live";
                    return;
                }

                _consecutiveHealthFailures++;
                Log("Watchdog detected dead/stalled tunnel (" + err + "). Auto-recovering...");
                _healthStatus = "Recovering";

                // If Cloudflare Quick Tunnel died under load, temporarily cool down Quick Tunnel so fallback takes over fast
                string tok, cust, mode;
                ReadConfig(out tok, out cust, out mode);
                if (string.IsNullOrWhiteSpace(tok) && mode != "cloudflare" &&
                    (_activeProviderName ?? "").IndexOf("Cloudflare", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    _cloudflareCooldownUntilUtc = DateTime.UtcNow.AddMinutes(5);
                }
                else if ((_activeProviderName ?? "").IndexOf("localhost.run", StringComparison.OrdinalIgnoreCase) >= 0 && mode == "auto")
                {
                    _localhostRunCooldownUntilUtc = DateTime.UtcNow.AddMinutes(3);
                }

                if (mode != "custom")
                {
                    _liveTunnelUrl = null;
                    FireCaptured("");
                    Start();
                }
            }
            catch { }
            finally
            {
                _probeInFlight = false;
            }
        }

        private bool ProbeLocalServer()
        {
            try
            {
                var req = (HttpWebRequest)WebRequest.Create(string.Format(CultureInfo.InvariantCulture, "http://127.0.0.1:{0}/api/health", _localPort));
                req.Method = "GET";
                req.Timeout = 3000;
                using (var resp = (HttpWebResponse)req.GetResponse())
                {
                    return (int)resp.StatusCode == 200;
                }
            }
            catch
            {
                return false;
            }
        }

        private static bool ProbePublicTunnel(string baseUrl, out string error)
        {
            error = null;
            try
            {
                ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12 | SecurityProtocolType.Tls11 | SecurityProtocolType.Tls;
                var req = (HttpWebRequest)WebRequest.Create(baseUrl.TrimEnd('/') + "/api/health");
                req.Method = "GET";
                req.Timeout = 6000;
                req.ReadWriteTimeout = 6000;
                req.UserAgent = "PrintAway-Watchdog/1.0";
                // Pinggy free interstitial bypass header if applicable
                req.Headers["X-Pinggy-No-Screen"] = "true";
                using (var resp = (HttpWebResponse)req.GetResponse())
                using (var sr = new StreamReader(resp.GetResponseStream()))
                {
                    int code = (int)resp.StatusCode;
                    string body = sr.ReadToEnd();
                    if (code >= 200 && code < 300 && body.IndexOf("\"ok\"", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        return true;
                    }
                    error = "HTTP " + code + " unexpected response";
                    return false;
                }
            }
            catch (WebException wex)
            {
                if (wex.Response is HttpWebResponse)
                {
                    error = "HTTP " + (int)((HttpWebResponse)wex.Response).StatusCode;
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
            AppendTunnelLog("[Manager] " + message);
            Action<string> handler = OnStatusMessage;
            if (handler != null)
            {
                try { handler(message); } catch { }
            }
        }

        private void AppendTunnelLog(string line)
        {
            try
            {
                string dataDir = Path.Combine(_rootDir, "data");
                if (!Directory.Exists(dataDir)) Directory.CreateDirectory(dataDir);
                string logFile = Path.Combine(dataDir, "tunnel.log");
                if (File.Exists(logFile) && new FileInfo(logFile).Length > 2 * 1024 * 1024)
                {
                    File.WriteAllText(logFile, "");
                }
                File.AppendAllText(logFile, DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture) + " " + line + Environment.NewLine, Encoding.UTF8);
            }
            catch { }
        }

        private void PublishToRegistry(string url, bool sendNtfy)
        {
            if (!IsValidPublicTunnelUrl(url)) return;
            string cleanUrl = url.Trim().TrimEnd('/');

            // 1. Write to data/tunnel.url for instant zero-latency file sync
            try
            {
                string dataDir = Path.Combine(_rootDir, "data");
                if (!Directory.Exists(dataDir)) Directory.CreateDirectory(dataDir);
                File.WriteAllText(Path.Combine(dataDir, "tunnel.url"), cleanUrl, Encoding.UTF8);
            }
            catch { }

            // 2. Broadcast to ntfy.sh Cloud Registry Topic (deduplicated)
            if (sendNtfy)
            {
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
                        _lastNtfyPublishedUrl = cleanUrl;
                        _lastNtfyPublishUtc = DateTime.UtcNow;
                        AppendTunnelLog(string.Format(CultureInfo.InvariantCulture, "[Registry] ntfy.sh ({0}) -> HTTP {1}", topic, (int)resp.StatusCode));
                    }
                }
                catch (Exception ex)
                {
                    AppendTunnelLog("[Registry] ntfy.sh warning: " + ex.Message);
                }
            }

            // 3. Patch & auto-insert ONLY this shop's row in Supabase Cloud Control Plane (isolated per shop_id)
            try
            {
                string sbUrl = "https://zduoifzakiojfqbtxqqm.supabase.co";
                string sbKey = "sb_publishable_rhmUZZbKhXiJxrChcPTbsg_gFIH9FdO";
                string now = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);

                string patchTarget = string.Format(CultureInfo.InvariantCulture, "{0}/rest/v1/shops?shop_id=eq.{1}", sbUrl, Uri.EscapeDataString(_shopId));
                var req = (HttpWebRequest)WebRequest.Create(patchTarget);
                req.Method = "PATCH";
                req.Headers.Add("apikey", sbKey);
                req.Headers.Add("Authorization", "Bearer " + sbKey);
                req.Headers.Add("Prefer", "return=representation");
                req.ContentType = "application/json";
                req.Timeout = 6000;

                string patchJson = new JavaScriptSerializer().Serialize(new Dictionary<string, object>
                {
                    { "live_tunnel_url", cleanUrl },
                    { "last_heartbeat_utc", now }
                });
                byte[] patchBytes = Encoding.UTF8.GetBytes(patchJson);
                req.ContentLength = patchBytes.Length;
                using (var s = req.GetRequestStream()) { s.Write(patchBytes, 0, patchBytes.Length); }

                string patchRespBody = "";
                using (var resp = (HttpWebResponse)req.GetResponse())
                using (var sr = new StreamReader(resp.GetResponseStream()))
                {
                    patchRespBody = sr.ReadToEnd();
                }

                if (string.IsNullOrWhiteSpace(patchRespBody) || patchRespBody.Trim() == "[]")
                {
                    string postTarget = sbUrl + "/rest/v1/shops";
                    var postReq = (HttpWebRequest)WebRequest.Create(postTarget);
                    postReq.Method = "POST";
                    postReq.Headers.Add("apikey", sbKey);
                    postReq.Headers.Add("Authorization", "Bearer " + sbKey);
                    postReq.Headers.Add("Prefer", "resolution=merge-duplicates");
                    postReq.ContentType = "application/json";
                    postReq.Timeout = 6000;

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
            }
            catch (Exception ex)
            {
                AppendTunnelLog("[Supabase] Heartbeat warning: " + ex.Message);
            }
        }

        private void RecordOwnedPid(int pid)
        {
            try
            {
                string dataDir = Path.Combine(_rootDir, "data");
                if (!Directory.Exists(dataDir)) Directory.CreateDirectory(dataDir);
                string pidFile = Path.Combine(dataDir, "tunnel.pids");
                File.AppendAllText(pidFile, pid.ToString(CultureInfo.InvariantCulture) + Environment.NewLine, Encoding.UTF8);
            }
            catch { }
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

        private void KillOwnedOrphanTunnelProcesses()
        {
            // 1. Always kill leftover cloudflared instances inside our station dir (never kills system SSH)
            try
            {
                string localCloudflared = Path.GetFullPath(Path.Combine(_rootDir, "bin", "cloudflared.exe"));
                foreach (var p in Process.GetProcessesByName("cloudflared"))
                {
                    try
                    {
                        string pPath = null;
                        try { pPath = Path.GetFullPath(p.MainModule.FileName); } catch { }
                        if (pPath == null || string.Equals(pPath, localCloudflared, StringComparison.OrdinalIgnoreCase))
                        {
                            p.Kill();
                        }
                    }
                    catch { }
                }
            }
            catch { }

            // 2. Kill only SSH/tunnel PIDs previously spawned by PrintAway (never unrelated user ssh.exe)
            try
            {
                string pidFile = Path.Combine(_rootDir, "data", "tunnel.pids");
                if (File.Exists(pidFile))
                {
                    string[] lines = File.ReadAllLines(pidFile);
                    File.Delete(pidFile);
                    foreach (string line in lines)
                    {
                        int pid;
                        if (int.TryParse((line ?? "").Trim(), out pid))
                        {
                            try
                            {
                                var proc = Process.GetProcessById(pid);
                                string pname = (proc.ProcessName ?? "").ToLowerInvariant();
                                if (pname == "ssh" || pname == "cloudflared")
                                {
                                    proc.Kill();
                                }
                            }
                            catch { }
                        }
                    }
                }
            }
            catch { }
        }

        public void Stop()
        {
            Interlocked.Increment(ref _generation);
            if (_watchdogTimer != null)
            {
                _watchdogTimer.Dispose();
                _watchdogTimer = null;
            }
            if (_broadcastTimer != null)
            {
                _broadcastTimer.Dispose();
                _broadcastTimer = null;
            }
            StopProcessOnly();
            KillOwnedOrphanTunnelProcesses();
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
