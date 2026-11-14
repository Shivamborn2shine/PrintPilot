using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Cache;
using System.Web.Script.Serialization;

namespace PrintAway.Updater
{
    public static class AutoUpdater
    {
        public const string CurrentVersion = "2.4.1";
        private const string VersionEndpoint = "https://raw.githubusercontent.com/Shivamborn2shine/PrintPilot/main/version.json";
        private const string FallbackVersionEndpoint = "https://printitaway.netlify.app/downloads/version.json";

        public static bool IsVersionNewer(string currentVer, string candidateVer)
        {
            if (string.IsNullOrWhiteSpace(candidateVer)) return false;
            string cur = (currentVer ?? "0.0.0").Trim().TrimStart('v', 'V');
            string cand = candidateVer.Trim().TrimStart('v', 'V');
            Version currentV, latestV;
            if (Version.TryParse(cur, out currentV) && Version.TryParse(cand, out latestV))
            {
                return latestV > currentV;
            }
            return !string.Equals(cand, cur, StringComparison.OrdinalIgnoreCase);
        }

        public static bool CheckForUpdate(out string latestVersion, out string downloadUrl, out string releaseNotes)
        {
            string unusedErr;
            return CheckForUpdate(out latestVersion, out downloadUrl, out releaseNotes, out unusedErr);
        }

        public static bool CheckForUpdate(out string latestVersion, out string downloadUrl, out string releaseNotes, out string errorMsg)
        {
            latestVersion = CurrentVersion;
            downloadUrl = null;
            releaseNotes = null;
            errorMsg = null;

            ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12 | SecurityProtocolType.Tls11 | SecurityProtocolType.Tls;
            string[] endpoints = new[] { VersionEndpoint, FallbackVersionEndpoint };
            var ser = new JavaScriptSerializer();
            string lastErr = null;

            foreach (string baseEp in endpoints)
            {
                try
                {
                    string cacheBusted = baseEp + "?t=" + DateTime.UtcNow.Ticks;
                    var req = (HttpWebRequest)WebRequest.Create(cacheBusted);
                    req.Method = "GET";
                    req.UserAgent = "PrintAway-Station/" + CurrentVersion;
                    req.Timeout = 7000;
                    req.CachePolicy = new RequestCachePolicy(RequestCacheLevel.BypassCache);
                    req.Headers["Cache-Control"] = "no-cache, no-store, max-age=0";
                    req.Headers["Pragma"] = "no-cache";

                    using (var resp = (HttpWebResponse)req.GetResponse())
                    using (var reader = new StreamReader(resp.GetResponseStream()))
                    {
                        string json = reader.ReadToEnd();
                        var dict = ser.Deserialize<Dictionary<string, object>>(json);
                        if (dict != null && dict.ContainsKey("version"))
                        {
                            latestVersion = Convert.ToString(dict["version"]).Trim();
                            if (dict.ContainsKey("downloadUrl")) downloadUrl = Convert.ToString(dict["downloadUrl"]);
                            if (dict.ContainsKey("releaseNotes")) releaseNotes = Convert.ToString(dict["releaseNotes"]);
                            return IsVersionNewer(CurrentVersion, latestVersion);
                        }
                    }
                }
                catch (Exception ex)
                {
                    lastErr = ex.Message;
                }
            }

            errorMsg = lastErr ?? "Unable to reach update server.";
            return false;
        }

        public static bool ApplyUpdate(string downloadUrl, out string error)
        {
            error = null;
            if (string.IsNullOrWhiteSpace(downloadUrl))
            {
                downloadUrl = "https://raw.githubusercontent.com/Shivamborn2shine/PrintPilot/main/bin/PrintAway.exe";
            }

            try
            {
                string currentExe = Process.GetCurrentProcess().MainModule.FileName;
                string dir = Path.GetDirectoryName(currentExe);
                string exeName = Path.GetFileName(currentExe);
                string updateExePath = Path.Combine(dir, exeName + ".update");

                ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12 | SecurityProtocolType.Tls11 | SecurityProtocolType.Tls;
                string bust = "?t=" + DateTime.UtcNow.Ticks;

                using (var client = new WebClient())
                {
                    client.CachePolicy = new RequestCachePolicy(RequestCacheLevel.BypassCache);
                    client.Headers.Add("User-Agent", "PrintAway-Station/" + CurrentVersion);
                    client.Headers.Add("Cache-Control", "no-cache");
                    try
                    {
                        client.DownloadFile(downloadUrl + bust, updateExePath);
                    }
                    catch
                    {
                        string fallback = "https://raw.githubusercontent.com/Shivamborn2shine/PrintPilot/main/bin/PrintAway.exe";
                        if (!string.Equals(downloadUrl, fallback, StringComparison.OrdinalIgnoreCase))
                        {
                            client.DownloadFile(fallback + bust, updateExePath);
                        }
                        else
                        {
                            client.DownloadFile("https://printitaway.netlify.app/downloads/PrintAway.exe" + bust, updateExePath);
                        }
                    }

                    // Best-effort refresh of local web portal files if running from an extracted bundle
                    TrySyncLocalWebFiles(client, dir, bust);
                }

                if (!File.Exists(updateExePath) || new FileInfo(updateExePath).Length < 50000)
                {
                    error = "Downloaded binary appears invalid or incomplete.";
                    return false;
                }

                string scriptPath = Path.Combine(dir, "apply-update.bat");
                string script = "@echo off\r\n" +
                                "cd /d \"" + dir + "\"\r\n" +
                                "setlocal\r\n" +
                                "set RETRIES=0\r\n" +
                                ":waitloop\r\n" +
                                "ping -n 2 127.0.0.1 >nul\r\n" +
                                "copy /y \"" + exeName + ".update\" \"" + exeName + "\" >nul 2>&1\r\n" +
                                "if errorlevel 1 (\r\n" +
                                "  set /a RETRIES+=1\r\n" +
                                "  if %RETRIES% LSS 15 goto waitloop\r\n" +
                                ")\r\n" +
                                "if exist \"" + exeName + ".update\" del /q \"" + exeName + ".update\" >nul 2>&1\r\n" +
                                "start \"\" \"" + exeName + "\"\r\n" +
                                "del /q \"%~f0\" >nul 2>&1\r\n";
                File.WriteAllText(scriptPath, script);

                var psi = new ProcessStartInfo
                {
                    FileName = "cmd.exe",
                    Arguments = "/c \"" + scriptPath + "\"",
                    WindowStyle = ProcessWindowStyle.Hidden,
                    CreateNoWindow = true,
                    UseShellExecute = true,
                    WorkingDirectory = dir
                };
                Process.Start(psi);
                Environment.Exit(0);
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

        private static void TrySyncLocalWebFiles(WebClient client, string dir, string bust)
        {
            string[] candidateWebDirs = new[]
            {
                Path.Combine(dir, "web"),
                Path.GetFullPath(Path.Combine(dir, "..", "web"))
            };
            string[] filesToSync = new[] { "index.html", "admin.html", "pricing.html" };

            foreach (string wDir in candidateWebDirs)
            {
                if (!Directory.Exists(wDir)) continue;
                foreach (string fname in filesToSync)
                {
                    try
                    {
                        string tmp = Path.Combine(wDir, fname + ".tmp");
                        string url = "https://raw.githubusercontent.com/Shivamborn2shine/PrintPilot/main/web/" + fname + bust;
                        client.DownloadFile(url, tmp);
                        if (new FileInfo(tmp).Length > 1000)
                        {
                            File.Copy(tmp, Path.Combine(wDir, fname), true);
                        }
                        if (File.Exists(tmp)) File.Delete(tmp);
                    }
                    catch { }
                }
            }
        }
    }
}

