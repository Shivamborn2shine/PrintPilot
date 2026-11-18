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
        private static readonly Regex PinggyUrlRegex = new Regex(@"https://[a-zA-Z0-9.-]+\.(?:pinggy-free\.link|free\.pinggy\.link|pinggy\.link|pinggy\.online)", RegexOptions.Compiled | RegexOptions.IgnoreCase);

        /// <summary>Set on ssh.exe's environment so PrintAway.exe, re-launched as SSH_ASKPASS, prints an empty password.</summary>
        public const string AskPassEnvVar = "PRINTAWAY_SSH_ASKPASS";

        private readonly string _rootDir;
        private readonly int _localPort;
        private readonly string _storefrontOrigin;
        private string _shopId;
        private string _shopName;
        private string _registryTopic;
        private readonly object _lock = new object();
        private readonly object _publishLock = new object();

        private Process _tunnelProcess;
        private Timer _broadcastTimer;
        private Timer _watchdogTimer;
        private string _candidateTunnelUrl;
        private string _liveTunnelUrl;
        private string _activeProviderName;
        private string _healthStatus = "Initializing";
        private DateTime? _lastHealthOkUtc;
        private DateTime _lastNtfyPublishUtc = DateTime.MinValue;
        private string _lastNtfyPublishedUrl;
        private DateTime _cloudflareCooldownUntilUtc = DateTime.MinValue;
        private int _throttleSwitchGen;
        private static readonly Regex LocalhostRunThrottleRegex = new Regex(@"is being slowed|have been slowed by|will be slowed", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private DateTime _pinggyCooldownUntilUtc = DateTime.MinValue;
        private int _generation;
        private bool _disposed;
        private bool _isSwitchingToFallback;
        private int _probeInFlight;

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

        public TunnelManager(string rootDir, int localPort, string shopId, string shopName)
        {
            _rootDir = rootDir;
            _localPort = localPort;
            Uri storefront;
            if (Uri.TryCreate(PrintAway.Config.StationConfig.ActiveStorefrontUrl, UriKind.Absolute, out storefront))
                _storefrontOrigin = storefront.GetLeftPart(UriPartial.Authority);
            _shopId = !string.IsNullOrWhiteSpace(shopId) ? shopId.Trim() : PrintAway.Config.StationConfig.UnconfiguredSlug;
            _shopName = !string.IsNullOrWhiteSpace(shopName) ? shopName.Trim() : _shopId;
            _registryTopic = GetRegistryTopicForShop(_shopId);
            _activeProviderName = "Initializing";
            ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12 | SecurityProtocolType.Tls11 | SecurityProtocolType.Tls;
            ServicePointManager.DefaultConnectionLimit = 64;
        }

        private string _ownerName;
        private string _ownerPhone;
        private string _upiVpa;

        /// <summary>Real owner details used if the shop row has to be created in the registry.</summary>
        public void SetShopProfile(string ownerName, string ownerPhone, string upiVpa)
        {
            _ownerName = ownerName;
            _ownerPhone = ownerPhone;
            _upiVpa = upiVpa;
        }

        private bool HasPublishableIdentity
        {
            get { return PrintAway.Config.StationConfig.IsUsableSlug(_shopId); }
        }

        public void UpdateShopIdentity(string newShopId, string newShopName)
        {
            lock (_lock)
            {
                if (!string.IsNullOrWhiteSpace(newShopName)) _shopName = newShopName.Trim();
                if (string.IsNullOrWhiteSpace(newShopId) || string.Equals(newShopId.Trim(), _shopId, StringComparison.OrdinalIgnoreCase)) return;
                _shopId = newShopId.Trim();
                _registryTopic = GetRegistryTopicForShop(_shopId);
                string url = _candidateTunnelUrl;
                ClearTunnelUrl("Connecting");
                if (url != null) QueueTunnelProbe(url, _generation);
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

                ClearTunnelUrl("Connecting");
                _isSwitchingToFallback = false;

                string token, customUrl, providerMode;
                ReadConfig(out token, out customUrl, out providerMode);

                // Ensure watchdog & heartbeat timers are always running
                StartBroadcastTimer();
                StartWatchdogTimer();

                // Custom reverse proxy, or a legacy hostname configured without a named tunnel token.
                if (providerMode == "custom" || (!string.IsNullOrWhiteSpace(customUrl) && string.IsNullOrWhiteSpace(token) && providerMode == "auto"))
                {
                    _activeProviderName = "Custom Static Domain";
                    if (IsValidPublicTunnelUrl(customUrl)) QueueTunnelProbe(customUrl, gen);
                    else Log("Enter a valid HTTPS URL for the custom tunnel.");
                    return;
                }

                if (providerMode == "openssh")
                {
                    StartOpenSshTunnel(gen);
                    return;
                }

                if (providerMode == "pinggy")
                {
                    StartPinggySshTunnel(gen);
                    return;
                }

                if (!string.IsNullOrWhiteSpace(token) || (providerMode == "cloudflare" && !string.IsNullOrWhiteSpace(customUrl)))
                {
                    if (string.IsNullOrWhiteSpace(token) || !IsValidPublicTunnelUrl(customUrl))
                    {
                        _activeProviderName = "Cloudflare Named Tunnel";
                        Log("Set both the connector token and the public HTTPS hostname for the Cloudflare Named Tunnel.");
                        return;
                    }
                    StartCloudflareNamedTunnel(token, customUrl, gen);
                    return;
                }

                // Cloudflare Quick -> Pinggy SSH -> OpenSSH localhost.run.
                if (DateTime.UtcNow < _cloudflareCooldownUntilUtc)
                {
                    Log("Cloudflare Quick Tunnel in cooldown. Launching SSH tunnel fallback...");
                    StartBestFallback(gen);
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
                Log("Cloudflare binary unavailable. Retrying the named tunnel in 60s.");
                ScheduleRestart(60000, gen);
                return;
            }

            try
            {
                Log("Starting authenticated Cloudflare Named Tunnel...");
                _activeProviderName = "Cloudflare Named Tunnel";

                var psi = new ProcessStartInfo
                {
                    FileName = exePath,
                    Arguments = "tunnel --no-autoupdate --protocol http2 run",
                    CreateNoWindow = true,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    WorkingDirectory = Path.GetDirectoryName(exePath)
                };
                psi.EnvironmentVariables["TUNNEL_TOKEN"] = token;

                _tunnelProcess = new Process { StartInfo = psi };
                _tunnelProcess.OutputDataReceived += (s, e) => HandleNamedTunnelOutput(e.Data, staticUrl, gen);
                _tunnelProcess.ErrorDataReceived += (s, e) => HandleNamedTunnelOutput(e.Data, staticUrl, gen);
                _tunnelProcess.EnableRaisingEvents = true;
                _tunnelProcess.Exited += (s, e) =>
                {
                    bool hadUrl;
                    if (!HandleTunnelExit(gen, out hadUrl)) return;
                    Log("Cloudflare Named Tunnel disconnected. Reconnecting automatically...");
                    ScheduleRestart(hadUrl ? 5000 : 60000, gen);
                };

                _tunnelProcess.Start();
                RecordOwnedProcess(_tunnelProcess);
                _tunnelProcess.BeginOutputReadLine();
                _tunnelProcess.BeginErrorReadLine();

                ThreadPool.QueueUserWorkItem(_ =>
                {
                    Thread.Sleep(60000);
                    lock (_lock)
                    {
                        if (_disposed || _generation != gen || _candidateTunnelUrl != null || _tunnelProcess == null || _tunnelProcess.HasExited) return;
                        Log("Cloudflare did not register an edge connection within 60s. Retrying the named tunnel.");
                        Start();
                    }
                });
            }
            catch (Exception ex)
            {
                Log("Error starting Cloudflare Named Tunnel: " + ex.Message + ". Retrying in 60s.");
                ScheduleRestart(60000, gen);
            }
        }

        private void HandleNamedTunnelOutput(string line, string staticUrl, int gen)
        {
            if (string.IsNullOrEmpty(line) || Volatile.Read(ref _generation) != gen) return;
            AppendTunnelLog("[CF-Named] " + line);

            if (line.IndexOf("Registered tunnel connection", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                QueueTunnelProbe(staticUrl, gen);
            }
            else if (line.IndexOf("Provided Tunnel token is not valid", StringComparison.OrdinalIgnoreCase) >= 0 ||
                     line.IndexOf("Unauthorized", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                Log("Cloudflare rejected the connector credentials. Check the named tunnel token.");
            }
        }

        private void StartCloudflareQuickTunnel(int gen)
        {
            if (_disposed || Volatile.Read(ref _generation) != gen) return;

            string exePath = EnsureCloudflared();
            if (string.IsNullOrEmpty(exePath))
            {
                Log("Cloudflare binary not found. Trying the fallback providers.");
                _cloudflareCooldownUntilUtc = DateTime.UtcNow.AddMinutes(5);
                StartBestFallback(gen);
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
                    bool hadUrl;
                    if (!HandleTunnelExit(gen, out hadUrl)) return;

                    if (!hadUrl)
                    {
                        lock (_lock)
                        {
                            if (_disposed || _generation != gen) return;
                            Log("Cloudflare Quick Tunnel exited before the public link was ready. Trying the fallback providers.");
                            SwitchFromCloudflare(gen);
                        }
                    }
                    else
                    {
                        Log("Cloudflare Tunnel disconnected under load. Reconnecting automatically...");
                        ScheduleRestart(3000, gen);
                    }
                };

                _tunnelProcess.Start();
                RecordOwnedProcess(_tunnelProcess);
                _tunnelProcess.BeginOutputReadLine();
                _tunnelProcess.BeginErrorReadLine();

                // Wait for a working public route, including DNS propagation at the edge.
                ThreadPool.QueueUserWorkItem(_ =>
                {
                    Thread.Sleep(45000);
                    lock (_lock)
                    {
                        if (_disposed || _isSwitchingToFallback || Volatile.Read(ref _generation) != gen) return;
                        if (string.IsNullOrEmpty(_liveTunnelUrl))
                        {
                            Log("Cloudflare Quick Tunnel is still unreachable after 45s. Trying the fallback providers.");
                            SwitchFromCloudflare(gen);
                        }
                    }
                });
            }
            catch (Exception ex)
            {
                Log("Cloudflare failed to start (" + ex.Message + "). Trying the fallback providers.");
                SwitchFromCloudflare(gen);
            }
        }

        private void HandleCloudflareOutput(string line, int gen)
        {
            if (string.IsNullOrEmpty(line) || Volatile.Read(ref _generation) != gen) return;
            AppendTunnelLog("[CF-Quick] " + line);

            Match match = CloudflareUrlRegex.Match(line);
            if (match.Success) QueueTunnelProbe(match.Value, gen);
        }

        private void SwitchFromCloudflare(int expectedGen)
        {
            if (_disposed || Volatile.Read(ref _generation) != expectedGen) return;
            _isSwitchingToFallback = true;
            _cloudflareCooldownUntilUtc = DateTime.UtcNow.AddMinutes(5);
            ClearTunnelUrl("Reconnecting");
            int newGen = Interlocked.Increment(ref _generation);
            StopProcessOnly();
            _isSwitchingToFallback = false;

            StartBestFallback(newGen);
        }

        // Pinggy first (localhost.run's free tier throttles uploads to a crawl), localhost.run when Pinggy
        // has recently failed. Never leaves the station without a tunnel: a slow tunnel beats none.
        private void StartBestFallback(int gen)
        {
            if (DateTime.UtcNow >= _pinggyCooldownUntilUtc)
            {
                StartPinggySshTunnel(gen);
            }
            else
            {
                StartOpenSshTunnel(gen);
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
                    ClearTunnelUrl("Connecting");
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
                        bool hadUrl;
                        if (!HandleTunnelExit(gen, out hadUrl)) return;

                        if (!hadUrl)
                        {
                            Log("localhost.run SSH did not establish tunnel. Trying Pinggy SSH backup...");
                            ThreadPool.QueueUserWorkItem(_ =>
                            {
                                lock (_lock)
                                {
                                    RecoverFromOpenSshFailure(gen);
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
                    RecordOwnedProcess(_tunnelProcess);
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
                                RecoverFromOpenSshFailure(gen);
                            }
                        }
                    });
                }
                catch (Exception ex)
                {
                    Log("Failed to start OpenSSH tunnel: " + ex.Message);
                    RecoverFromOpenSshFailure(gen);
                }
            }
        }

        private void RecoverFromOpenSshFailure(int gen)
        {
            if (_disposed || _generation != gen) return;
            int nextGen = Interlocked.Increment(ref _generation);
            ClearTunnelUrl("Reconnecting");
            StopProcessOnly();
            if (DateTime.UtcNow >= _pinggyCooldownUntilUtc) StartPinggySshTunnel(nextGen);
            else ScheduleRestart(30000, nextGen);
        }

        private void HandleOpenSshOutput(string line, int gen)
        {
            if (string.IsNullOrEmpty(line) || Volatile.Read(ref _generation) != gen) return;
            AppendTunnelLog("[SSH-LHR] " + line);

            // localhost.run's free tier throttles after a short burst: every request then waits up to a
            // minute, which makes customer uploads hang. Move to Pinggy as soon as throttling is reported.
            if (LocalhostRunThrottleRegex.IsMatch(line) && DateTime.UtcNow >= _pinggyCooldownUntilUtc &&
                Interlocked.Exchange(ref _throttleSwitchGen, gen) != gen)
            {
                ThreadPool.QueueUserWorkItem(_ =>
                {
                    lock (_lock)
                    {
                        if (_disposed || Volatile.Read(ref _generation) != gen) return;
                        Log("localhost.run is throttling this tunnel (free-tier bandwidth limit). Switching to Pinggy SSH tunnel...");
                        ClearTunnelUrl("Reconnecting");
                        int nextGen = Interlocked.Increment(ref _generation);
                        StopProcessOnly();
                        StartPinggySshTunnel(nextGen);
                    }
                });
                return;
            }

            Match match = SshUrlRegex.Match(line);
            if (match.Success) QueueTunnelProbe(match.Value, gen);
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
                    ClearTunnelUrl("Connecting");
                    string sshExe = FindOpenSshExecutable();
                    int debuggerPort = FindFreeLocalPort();
                    Log("Connecting Pinggy SSH Tunnel...");
                    _activeProviderName = "Pinggy SSH Tunnel";

                    // - PubkeyAuthentication=no: a personal ~/.ssh key with loose permissions makes ssh stop and warn.
                    // - Pinggy's free tier asks for a password and accepts an empty one; ssh gets it from SSH_ASKPASS
                    //   (this exe re-launched with AskPassEnvVar) because there is no console to press Enter in.
                    // - -L…:4300 exposes Pinggy's local web debugger, whose /urls endpoint reports the tunnel URL
                    //   reliably even though ssh has no terminal to print it to.
                    var psi = new ProcessStartInfo
                    {
                        FileName = sshExe,
                        Arguments = string.Format(
                            CultureInfo.InvariantCulture,
                            "-T -p 443 -o StrictHostKeyChecking=no -o UserKnownHostsFile=NUL -o PubkeyAuthentication=no " +
                            "-o PreferredAuthentications=none,keyboard-interactive,password -o NumberOfPasswordPrompts=1 " +
                            "-o ServerAliveInterval=15 -o ServerAliveCountMax=3 -o ExitOnForwardFailure=yes -o ConnectTimeout=10 " +
                            "-R0:127.0.0.1:{0} -L{1}:localhost:4300 free.pinggy.io x:passpreflight",
                            _localPort, debuggerPort),
                        CreateNoWindow = true,
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true
                    };
                    string selfExe = System.Reflection.Assembly.GetEntryAssembly() != null
                        ? System.Reflection.Assembly.GetEntryAssembly().Location
                        : Process.GetCurrentProcess().MainModule.FileName;
                    psi.EnvironmentVariables["SSH_ASKPASS"] = selfExe;
                    psi.EnvironmentVariables["SSH_ASKPASS_REQUIRE"] = "force";
                    psi.EnvironmentVariables["DISPLAY"] = "printaway:0";
                    psi.EnvironmentVariables[AskPassEnvVar] = "1";

                    _tunnelProcess = new Process { StartInfo = psi };
                    _tunnelProcess.OutputDataReceived += (s, e) => HandlePinggyOutput(e.Data, gen);
                    _tunnelProcess.ErrorDataReceived += (s, e) => HandlePinggyOutput(e.Data, gen);
                    _tunnelProcess.EnableRaisingEvents = true;
                    _tunnelProcess.Exited += (s, e) =>
                    {
                        bool hadUrl;
                        if (!HandleTunnelExit(gen, out hadUrl)) return;
                        if (hadUrl)
                        {
                            // Free Pinggy tunnels end after a while; reconnect (the registry gets the new URL).
                            Log("Pinggy SSH tunnel ended. Reconnecting in 3s...");
                            ScheduleRestart(3000, gen);
                        }
                        else
                        {
                            ThreadPool.QueueUserWorkItem(_ => FallBackFromPinggy(gen, "Pinggy SSH exited before the tunnel came up"));
                        }
                    };

                    _tunnelProcess.Start();
                    RecordOwnedProcess(_tunnelProcess);
                    _tunnelProcess.BeginOutputReadLine();
                    _tunnelProcess.BeginErrorReadLine();

                    ThreadPool.QueueUserWorkItem(_ => PollPinggyDebuggerForUrl(debuggerPort, gen));

                    // Startup timeout guard (25s)
                    ThreadPool.QueueUserWorkItem(_ =>
                    {
                        Thread.Sleep(25000);
                        if (string.IsNullOrEmpty(_liveTunnelUrl))
                        {
                            FallBackFromPinggy(gen, "Pinggy did not come up within 25s");
                        }
                    });
                }
                catch (Exception ex)
                {
                    Log("Pinggy SSH failed to start: " + ex.Message + ".");
                    _pinggyCooldownUntilUtc = DateTime.UtcNow.AddMinutes(30);
                    StartOpenSshTunnel(Interlocked.Increment(ref _generation));
                }
            }
        }

        // Pinggy failed: rest it for 30 minutes and use localhost.run (slow beats offline).
        private void FallBackFromPinggy(int gen, string reason)
        {
            lock (_lock)
            {
                if (_disposed || _isSwitchingToFallback || Volatile.Read(ref _generation) != gen) return;
                if (!string.IsNullOrEmpty(_liveTunnelUrl)) return;
                Log(reason + ". Using localhost.run instead.");
                _pinggyCooldownUntilUtc = DateTime.UtcNow.AddMinutes(30);
                int nextGen = Interlocked.Increment(ref _generation);
                StopProcessOnly();
                StartOpenSshTunnel(nextGen);
            }
        }

        private static int FindFreeLocalPort()
        {
            var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            try { return ((IPEndPoint)listener.LocalEndpoint).Port; }
            finally { listener.Stop(); }
        }

        private void PollPinggyDebuggerForUrl(int debuggerPort, int gen)
        {
            var serializer = new JavaScriptSerializer();
            for (int attempt = 0; attempt < 24; attempt++)
            {
                Thread.Sleep(1000);
                if (_disposed || Volatile.Read(ref _generation) != gen || !string.IsNullOrEmpty(_liveTunnelUrl)) return;
                try
                {
                    var req = (HttpWebRequest)WebRequest.Create("http://127.0.0.1:" + debuggerPort.ToString(CultureInfo.InvariantCulture) + "/urls");
                    req.Timeout = 2000;
                    req.ReadWriteTimeout = 2000;
                    req.Proxy = null;
                    using (var res = (HttpWebResponse)req.GetResponse())
                    using (var reader = new StreamReader(res.GetResponseStream(), Encoding.UTF8))
                    {
                        var parsed = serializer.Deserialize<Dictionary<string, object>>(reader.ReadToEnd());
                        object urlsObj;
                        if (parsed == null || !parsed.TryGetValue("urls", out urlsObj)) continue;
                        var urls = urlsObj as System.Collections.IEnumerable;
                        if (urls == null) continue;
                        foreach (object u in urls)
                        {
                            string s = Convert.ToString(u, CultureInfo.InvariantCulture);
                            if (s != null && s.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                            {
                                AcceptPinggyUrl(s, gen);
                                return;
                            }
                        }
                    }
                }
                catch
                {
                    // Debugger not listening yet (ssh still connecting); keep polling.
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
                AcceptPinggyUrl(match.Value, gen);
            }
        }

        private void AcceptPinggyUrl(string rawUrl, int gen)
        {
            QueueTunnelProbe(rawUrl, gen);
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
            lock (_lock)
            {
                Log("Restarting tunnel connection...");
                _cloudflareCooldownUntilUtc = DateTime.MinValue;
                _pinggyCooldownUntilUtc = DateTime.MinValue;
                Start();
            }
        }

        private void ScheduleRestart(int delayMs, int expectedGen)
        {
            ThreadPool.QueueUserWorkItem(_ =>
            {
                Thread.Sleep(delayMs);
                lock (_lock)
                {
                    if (_disposed || _generation != expectedGen) return;
                    Start();
                }
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
            _watchdogTimer = new Timer(_ => RunWatchdogCheck(), null, TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(15));
        }

        private void QueueTunnelProbe(string url, int gen)
        {
            if (!IsValidPublicTunnelUrl(url)) return;
            url = url.Trim().TrimEnd('/');
            lock (_lock)
            {
                if (_disposed || _generation != gen || (_tunnelProcess != null && _tunnelProcess.HasExited)) return;
                if (string.Equals(_candidateTunnelUrl, url, StringComparison.OrdinalIgnoreCase)) return;
                ClearTunnelUrl("Verifying public route");
                _candidateTunnelUrl = url;
            }
            ThreadPool.QueueUserWorkItem(_ => RunWatchdogCheck());
        }

        private void ClearTunnelUrl(string status)
        {
            bool wasLive = _liveTunnelUrl != null;
            _candidateTunnelUrl = null;
            _liveTunnelUrl = null;
            _lastHealthOkUtc = null;
            _healthStatus = status;
            if (wasLive) FireCaptured("");
        }

        private bool HandleTunnelExit(int gen, out bool hadUrl)
        {
            lock (_lock)
            {
                hadUrl = _liveTunnelUrl != null;
                if (_disposed || _isSwitchingToFallback || _generation != gen) return false;
                ClearTunnelUrl("Reconnecting");
                return true;
            }
        }

        public bool RunManualProbe(out string detail)
        {
            detail = "";
            if (!ProbeLocalServer())
            {
                detail = "Local PrintAway HTTP server (127.0.0.1:" + _localPort + ") is not responding.";
                return false;
            }
            string url = _candidateTunnelUrl;
            if (!IsValidPublicTunnelUrl(url))
            {
                detail = "No active public tunnel URL yet. Provider: " + (_activeProviderName ?? "Unknown");
                return false;
            }
            string err;
            if (ProbePublicTunnel(url, _shopId, out err))
            {
                detail = "Tunnel Healthy (" + (_activeProviderName ?? "Active") + ") -> " + url;
                return true;
            }
            detail = "Public probe failed for " + url + ": " + err;
            return false;
        }

        private void RunWatchdogCheck()
        {
            if (Interlocked.CompareExchange(ref _probeInFlight, 1, 0) != 0) return;
            try
            {
                int gen;
                string url, shopId;
                bool wasLive;
                lock (_lock)
                {
                    if (_disposed || _candidateTunnelUrl == null) return;
                    gen = _generation;
                    url = _candidateTunnelUrl;
                    shopId = _shopId;
                    wasLive = _liveTunnelUrl != null;
                }
                string err;
                bool healthy = ProbePublicTunnel(url, shopId, out err);
                if (!healthy && wasLive)
                {
                    // Confirm the outage before withdrawing a previously working route.
                    Thread.Sleep(2500);
                    healthy = ProbePublicTunnel(url, shopId, out err);
                }

                lock (_lock)
                {
                    if (_disposed || _generation != gen || url != _candidateTunnelUrl || shopId != _shopId) return;
                    if (healthy)
                    {
                        _lastHealthOkUtc = DateTime.UtcNow;
                        _healthStatus = "Live";
                        if (_liveTunnelUrl == url) return;
                        _liveTunnelUrl = url;
                        if (_activeProviderName == "Pinggy SSH Tunnel") _pinggyCooldownUntilUtc = DateTime.MinValue;
                        Log(_activeProviderName + " live: " + url);
                        FireCaptured(url);
                    }
                    else
                    {
                        if (wasLive) ClearTunnelUrl("Recovering");
                        Log("Public tunnel check failed: " + err);
                        if (!wasLive) return;

                        if (_activeProviderName == "Custom Static Domain" || !ProbeLocalServer()) _candidateTunnelUrl = url;
                        else if (_activeProviderName == "Cloudflare Quick Tunnel") SwitchFromCloudflare(gen);
                        else Start();
                        return;
                    }
                }
                PublishToRegistry(url, true);
            }
            catch { }
            finally
            {
                Interlocked.Exchange(ref _probeInFlight, 0);
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

        private bool ProbePublicTunnel(string baseUrl, string shopId, out string error)
        {
            error = null;
            try
            {
                ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12 | SecurityProtocolType.Tls11 | SecurityProtocolType.Tls;
                string endpoint = baseUrl + "/api/shop/status";
                bool isPinggy = new Uri(baseUrl).Host.IndexOf("pinggy", StringComparison.OrdinalIgnoreCase) >= 0;
                if (isPinggy && !CanBrowserReachPinggy(endpoint))
                {
                    error = "Pinggy did not allow the storefront's browser preflight";
                    return false;
                }

                var req = (HttpWebRequest)WebRequest.Create(endpoint);
                req.Method = "GET";
                req.AllowAutoRedirect = false;
                req.Timeout = 6000;
                req.ReadWriteTimeout = 6000;
                req.UserAgent = "PrintAway-Watchdog/1.0";
                if (isPinggy) req.Headers["X-Pinggy-No-Screen"] = "1";
                using (var resp = (HttpWebResponse)req.GetResponse())
                using (var sr = new StreamReader(resp.GetResponseStream()))
                {
                    if (resp.StatusCode != HttpStatusCode.OK || !resp.ContentType.StartsWith("application/json", StringComparison.OrdinalIgnoreCase))
                    {
                        error = "HTTP " + (int)resp.StatusCode + " did not return station status";
                        return false;
                    }
                    var body = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(sr.ReadToEnd());
                    object actualShopId;
                    if (body != null && body.TryGetValue("shopId", out actualShopId) &&
                        string.Equals(Convert.ToString(actualShopId), shopId, StringComparison.OrdinalIgnoreCase)) return true;
                    error = "public route returned a different shop ID";
                    return false;
                }
            }
            catch (WebException wex)
            {
                using (var response = wex.Response as HttpWebResponse)
                {
                    error = response == null ? wex.Message : "HTTP " + (int)response.StatusCode;
                }
                return false;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

        private bool CanBrowserReachPinggy(string endpoint)
        {
            if (string.IsNullOrEmpty(_storefrontOrigin)) return false;
            var req = (HttpWebRequest)WebRequest.Create(endpoint);
            req.Method = "OPTIONS";
            req.AllowAutoRedirect = false;
            req.Timeout = 5000;
            req.UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 Chrome/131.0.0.0 Safari/537.36";
            req.Headers["Origin"] = _storefrontOrigin;
            req.Headers["Access-Control-Request-Method"] = "GET";
            req.Headers["Access-Control-Request-Headers"] = "x-pinggy-no-screen";
            using (var resp = (HttpWebResponse)req.GetResponse())
            {
                string origin = resp.Headers["Access-Control-Allow-Origin"];
                string headers = resp.Headers["Access-Control-Allow-Headers"] ?? "";
                return (int)resp.StatusCode >= 200 && (int)resp.StatusCode < 300 &&
                    (origin == "*" || origin == _storefrontOrigin) &&
                    (headers.Trim() == "*" || Regex.IsMatch(headers, @"(?:^|,)\s*x-pinggy-no-screen\s*(?:,|$)", RegexOptions.IgnoreCase));
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
            lock (_publishLock)
            {
                string shopId, topic, shopName;
                lock (_lock)
                {
                    if (url != _liveTunnelUrl || !HasPublishableIdentity) return;
                    shopId = _shopId;
                    shopName = _shopName;
                    topic = _registryTopic;
                }
                PublishVerifiedUrl(url, sendNtfy, shopId, shopName, topic);
            }
        }

        private void PublishVerifiedUrl(string cleanUrl, bool sendNtfy, string shopId, string shopName, string topic)
        {
            // Broadcast to ntfy.sh Cloud Registry Topic (deduplicated).
            if (sendNtfy)
            {
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
                string sbUrl = PrintAway.SaaS.ControlPlane.Url;
                string sbKey = PrintAway.SaaS.ControlPlane.Key;
                string now = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);

                string patchTarget = string.Format(CultureInfo.InvariantCulture, "{0}/rest/v1/shops?shop_id=eq.{1}", sbUrl, Uri.EscapeDataString(shopId));
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

                bool canRegister = !string.IsNullOrWhiteSpace(_ownerName) && !string.IsNullOrWhiteSpace(_ownerPhone) &&
                                   PrintAway.Config.StationConfig.IsValidUpiVpa(_upiVpa);
                if ((string.IsNullOrWhiteSpace(patchRespBody) || patchRespBody.Trim() == "[]") && !canRegister)
                {
                    AppendTunnelLog("[Supabase] Shop row missing and owner profile incomplete; open 'Shop & Password' to complete registration.");
                }
                else if (string.IsNullOrWhiteSpace(patchRespBody) || patchRespBody.Trim() == "[]")
                {
                    string postTarget = sbUrl + "/rest/v1/shops";
                    var postReq = (HttpWebRequest)WebRequest.Create(postTarget);
                    postReq.Method = "POST";
                    postReq.Headers.Add("apikey", sbKey);
                    postReq.Headers.Add("Authorization", "Bearer " + sbKey);
                    // Plain insert (no merge): if another station owns this ID the insert fails instead of overwriting it.
                    postReq.Headers.Add("Prefer", "return=minimal");
                    postReq.ContentType = "application/json";
                    postReq.Timeout = 6000;

                    var postDict = new Dictionary<string, object>
                    {
                        { "shop_id", shopId },
                        { "shop_name", shopName ?? shopId },
                        { "owner_name", _ownerName },
                        { "owner_phone", _ownerPhone },
                        { "upi_vpa", _upiVpa },
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

        private void RecordOwnedProcess(Process process)
        {
            try
            {
                string dataDir = Path.Combine(_rootDir, "data");
                if (!Directory.Exists(dataDir)) Directory.CreateDirectory(dataDir);
                string pidFile = Path.Combine(dataDir, "tunnel.pids");
                string entry = string.Format(CultureInfo.InvariantCulture, "{0} {1}{2}", process.Id, process.StartTime.ToUniversalTime().Ticks, Environment.NewLine);
                File.AppendAllText(pidFile, entry, Encoding.UTF8);
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
            // Verify the process start time as Windows can reuse a PID after a station crash.
            try
            {
                string pidFile = Path.Combine(_rootDir, "data", "tunnel.pids");
                if (File.Exists(pidFile))
                {
                    string[] lines = File.ReadAllLines(pidFile);
                    File.Delete(pidFile);
                    foreach (string line in lines)
                    {
                        string[] entry = line.Split(' ');
                        int pid;
                        long ticks;
                        if (entry.Length == 2 && int.TryParse(entry[0], out pid) && long.TryParse(entry[1], out ticks))
                        {
                            try
                            {
                                using (var proc = Process.GetProcessById(pid))
                                {
                                    string pname = proc.ProcessName.ToLowerInvariant();
                                    if ((pname == "ssh" || pname == "cloudflared") && proc.StartTime.ToUniversalTime().Ticks == ticks)
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
            lock (_lock)
            {
                Interlocked.Increment(ref _generation);
                ClearTunnelUrl("Stopped");
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
