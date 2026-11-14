using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Web.Script.Serialization;

namespace PrintAway.Updater
{
    public static class AutoUpdater
    {
        public const string CurrentVersion = "2.3.1";
        private const string VersionEndpoint = "https://raw.githubusercontent.com/Shivamborn2shine/PrintPilot/main/version.json";

        public static bool CheckForUpdate(out string latestVersion, out string downloadUrl, out string releaseNotes)
        {
            latestVersion = CurrentVersion;
            downloadUrl = null;
            releaseNotes = null;

            try
            {
                ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12 | SecurityProtocolType.Tls11 | SecurityProtocolType.Tls;
                var req = (HttpWebRequest)WebRequest.Create(VersionEndpoint);
                req.Method = "GET";
                req.UserAgent = "PrintAway-Station/" + CurrentVersion;
                req.Timeout = 5000;

                using (var resp = (HttpWebResponse)req.GetResponse())
                using (var reader = new StreamReader(resp.GetResponseStream()))
                {
                    string json = reader.ReadToEnd();
                    var ser = new JavaScriptSerializer();
                    var dict = ser.Deserialize<Dictionary<string, object>>(json);
                    if (dict != null && dict.ContainsKey("version"))
                    {
                        latestVersion = Convert.ToString(dict["version"]).Trim();
                        if (dict.ContainsKey("downloadUrl")) downloadUrl = Convert.ToString(dict["downloadUrl"]);
                        if (dict.ContainsKey("releaseNotes")) releaseNotes = Convert.ToString(dict["releaseNotes"]);

                        Version currentV, latestV;
                        if (Version.TryParse(CurrentVersion, out currentV) && Version.TryParse(latestVersion, out latestV))
                        {
                            return latestV > currentV;
                        }
                        return !string.Equals(latestVersion, CurrentVersion, StringComparison.OrdinalIgnoreCase);
                    }
                }
            }
            catch
            {
                // Network unreachable or repository offline - silently ignore
            }
            return false;
        }

        public static bool ApplyUpdate(string downloadUrl, out string error)
        {
            error = null;
            if (string.IsNullOrWhiteSpace(downloadUrl))
            {
                error = "Download URL is invalid.";
                return false;
            }

            try
            {
                string currentExe = Process.GetCurrentProcess().MainModule.FileName;
                string dir = Path.GetDirectoryName(currentExe);
                string newExe = Path.Combine(dir, "PrintAway.exe.update");

                ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12 | SecurityProtocolType.Tls11 | SecurityProtocolType.Tls;
                using (var client = new WebClient())
                {
                    client.Headers.Add("User-Agent", "PrintAway-Station/" + CurrentVersion);
                    try
                    {
                        client.DownloadFile(downloadUrl, newExe);
                    }
                    catch
                    {
                        // Fallback to GitHub raw or Netlify CDN
                        string fallback = "https://raw.githubusercontent.com/Shivamborn2shine/PrintPilot/main/bin/PrintAway.exe";
                        if (!string.Equals(downloadUrl, fallback, StringComparison.OrdinalIgnoreCase))
                        {
                            client.DownloadFile(fallback, newExe);
                        }
                        else
                        {
                            client.DownloadFile("https://printitaway.netlify.app/downloads/PrintAway.exe", newExe);
                        }
                    }
                }

                if (!File.Exists(newExe) || new FileInfo(newExe).Length < 50000)
                {
                    error = "Downloaded binary appears invalid or incomplete.";
                    return false;
                }

                string scriptPath = Path.Combine(dir, "apply-update.bat");
                string script = "@echo off\r\n" +
                                "timeout /t 2 /nobreak >nul\r\n" +
                                "copy /y \"PrintAway.exe.update\" \"" + Path.GetFileName(currentExe) + "\" >nul\r\n" +
                                "del \"PrintAway.exe.update\" >nul\r\n" +
                                "start \"\" \"" + Path.GetFileName(currentExe) + "\"\r\n" +
                                "del \"%~f0\"\r\n";
                File.WriteAllText(scriptPath, script);

                var psi = new ProcessStartInfo
                {
                    FileName = scriptPath,
                    CreateNoWindow = true,
                    UseShellExecute = false,
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
    }
}

