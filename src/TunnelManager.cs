using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace PrintAway.Tunnel
{
    public sealed class TunnelManager : IDisposable
    {
        private static readonly Regex TunnelUrlRegex = new Regex(@"https://[a-zA-Z0-9-]+\.trycloudflare\.com", RegexOptions.Compiled);

        private readonly string _rootDir;
        private readonly int _localPort;
        private readonly string _registryTopic;
        private Process _tunnelProcess;
        private Timer _broadcastTimer;
        private string _liveTunnelUrl;
        private bool _disposed;

        public event Action<string> OnTunnelUrlCaptured;
        public event Action<string> OnStatusMessage;

        public TunnelManager(string rootDir, int localPort = 8080, string registryTopic = "printaway_station_registry_pilot_9f8a7b6c5d4e")
        {
            _rootDir = rootDir;
            _localPort = localPort;
            _registryTopic = registryTopic;
        }

        public string LiveTunnelUrl { get { return _liveTunnelUrl; } }

        public void Start()
        {
            if (_tunnelProcess != null && !_tunnelProcess.HasExited) return;

            string binDir = Path.Combine(_rootDir, "bin");
            if (!Directory.Exists(binDir)) Directory.CreateDirectory(binDir);

            string exePath = Path.Combine(binDir, "cloudflared.exe");
            if (!File.Exists(exePath))
            {
                string altExe = Path.Combine(_rootDir, "cloudflared.exe");
                if (File.Exists(altExe))
                {
                    exePath = altExe;
                }
            }

            if (!File.Exists(exePath))
            {
                Log("Cloudflared binary missing. Downloading official Cloudflare Tunnel engine...");
                DownloadCloudflared(exePath);
            }

            if (!File.Exists(exePath))
            {
                Log("Warning: cloudflared.exe could not be prepared. Running local loopback only.");
                return;
            }

            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = exePath,
                    Arguments = string.Format(CultureInfo.InvariantCulture, "tunnel --protocol http2 --url http://127.0.0.1:{0}", _localPort),
                    CreateNoWindow = true,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    WorkingDirectory = binDir
                };

                _tunnelProcess = new Process { StartInfo = psi };
                _tunnelProcess.OutputDataReceived += HandleProcessOutput;
                _tunnelProcess.ErrorDataReceived += HandleProcessOutput;
                _tunnelProcess.EnableRaisingEvents = true;
                _tunnelProcess.Exited += (s, e) =>
                {
                    if (!_disposed && string.IsNullOrEmpty(_liveTunnelUrl))
                    {
                        Log("Tunnel process exited before capturing URL (rate-limit 429 or network). Retrying in 20 seconds...");
                        ThreadPool.QueueUserWorkItem(state =>
                        {
                            Thread.Sleep(20000);
                            if (!_disposed && string.IsNullOrEmpty(_liveTunnelUrl))
                            {
                                Start();
                            }
                        });
                    }
                };

                _tunnelProcess.Start();
                _tunnelProcess.BeginOutputReadLine();
                _tunnelProcess.BeginErrorReadLine();

                Log(string.Format(CultureInfo.InvariantCulture, "Cloudflare HTTP/2 Tunnel engine started (PID: {0}).", _tunnelProcess.Id));

                // Re-broadcast tunnel URL every 30 minutes
                _broadcastTimer = new Timer(state =>
                {
                    if (!string.IsNullOrWhiteSpace(_liveTunnelUrl))
                    {
                        PublishToRegistry(_liveTunnelUrl);
                    }
                }, null, TimeSpan.FromMinutes(30), TimeSpan.FromMinutes(30));
            }
            catch (Exception ex)
            {
                Log("Failed to start tunnel: " + ex.Message);
            }
        }

        private void HandleProcessOutput(object sender, DataReceivedEventArgs e)
        {
            if (string.IsNullOrEmpty(e.Data)) return;

            Match match = TunnelUrlRegex.Match(e.Data);
            if (match.Success)
            {
                string url = match.Value.Trim();
                if (!string.Equals(_liveTunnelUrl, url, StringComparison.OrdinalIgnoreCase))
                {
                    _liveTunnelUrl = url;
                    Log("Live Cloudflare Tunnel Captured: " + url);
                    PublishToRegistry(url);

                    Action<string> handler = OnTunnelUrlCaptured;
                    if (handler != null)
                    {
                        try { handler(url); } catch { }
                    }
                }
            }
        }

        private void PublishToRegistry(string url)
        {
            if (string.IsNullOrWhiteSpace(_registryTopic)) return;
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
                    Log(string.Format(CultureInfo.InvariantCulture, "Tunnel published to registry ({0}) -> Status: {1}", _registryTopic, (int)resp.StatusCode));
                }
            }
            catch (Exception ex)
            {
                Log("Warning publishing to registry: " + ex.Message);
            }
        }

        private void DownloadCloudflared(string targetPath)
        {
            try
            {
                ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12 | SecurityProtocolType.Tls11 | SecurityProtocolType.Tls;
                string url = "https://github.com/cloudflare/cloudflared/releases/latest/download/cloudflared-windows-amd64.exe";
                using (var client = new WebClient())
                {
                    client.DownloadFile(url, targetPath);
                }
                Log("Downloaded cloudflared.exe successfully.");
            }
            catch (Exception ex)
            {
                Log("Failed to download cloudflared.exe: " + ex.Message);
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

        public void Stop()
        {
            if (_broadcastTimer != null)
            {
                _broadcastTimer.Dispose();
                _broadcastTimer = null;
            }

            if (_tunnelProcess != null)
            {
                try
                {
                    if (!_tunnelProcess.HasExited)
                    {
                        _tunnelProcess.Kill();
                        _tunnelProcess.WaitForExit(3000);
                    }
                }
                catch { }
                finally
                {
                    _tunnelProcess.Dispose();
                    _tunnelProcess = null;
                }
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

