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
        private static readonly Regex CloudflareUrlRegex = new Regex(@"https://[a-zA-Z0-9-]+\.trycloudflare\.com", RegexOptions.Compiled);
        private static readonly Regex SshUrlRegex = new Regex(@"https://[a-zA-Z0-9-]+\.lhr\.life", RegexOptions.Compiled);

        private readonly string _rootDir;
        private readonly int _localPort;
        private readonly string _registryTopic;
        private readonly object _lock = new object();

        private Process _tunnelProcess;
        private Timer _broadcastTimer;
        private string _liveTunnelUrl;
        private string _activeProviderName;
        private bool _disposed;
        private bool _isSwitchingToFallback;

        public event Action<string> OnTunnelUrlCaptured;
        public event Action<string> OnStatusMessage;

        public TunnelManager(string rootDir, int localPort = 8080, string registryTopic = "printaway_station_registry_pilot_9f8a7b6c5d4e")
        {
            _rootDir = rootDir;
            _localPort = localPort;
            _registryTopic = registryTopic;
            _activeProviderName = "Initializing";
        }

        public string LiveTunnelUrl { get { return _liveTunnelUrl; } }
        public string ActiveProviderName { get { return _activeProviderName; } }

        public void Start()
        {
            lock (_lock)
            {
                if (_disposed) return;
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
            if (string.IsNullOrWhiteSpace(_registryTopic) || string.IsNullOrWhiteSpace(url)) return;
            try
            {
                ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12 | SecurityProtocolType.Tls11 | SecurityProtocolType.Tls;
                string target = "https://ntfy.sh/" + _registryTopic;
                var req = (HttpWebRequest)WebRequest.Create(target);
                req.Method = "POST";
                req.ContentType = "text/plain";
                byte[] bytes = Encoding.UTF8.GetBytes(url);
                req.ContentLength = bytes.Length;
                using (var s = req.GetRequestStream())
                {
                    s.Write(bytes, 0, bytes.Length);
                }
                using (var resp = (HttpWebResponse)req.GetResponse())
                {
                    Log(string.Format(CultureInfo.InvariantCulture, "Broadcasted tunnel to Cloud Registry ({0}) -> Status: {1}", _registryTopic, (int)resp.StatusCode));
                }
            }
            catch (Exception ex)
            {
                Log("Warning broadcasting to registry: " + ex.Message);
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
