using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Runtime.InteropServices;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using PrintAway.Engine;
using PrintAway.Gui;
using PrintAway.SaaS;
using PrintAway.Storage;
using PrintAway.Tunnel;
using PrintAway.Web;
using PrintAway.Worker;

namespace PrintAway
{
    public static class Program
    {
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool AttachConsole(int dwProcessId);

        private const int ATTACH_PARENT_PROCESS = -1;

        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Auto)]
        private static extern int RegisterWindowMessage(string lpString);

        [DllImport("user32.dll")]
        private static extern bool PostMessage(IntPtr hWnd, int Msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        [DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr hWnd);

        private const int SW_RESTORE = 9;
        private static readonly IntPtr HWND_BROADCAST = new IntPtr(0xffff);

        [STAThread]
        public static int Main(string[] args)
        {
            ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12 | SecurityProtocolType.Tls11 | SecurityProtocolType.Tls;
            string baseDir = AppDomain.CurrentDomain.BaseDirectory;
            string repoRoot = baseDir;
            if (Directory.Exists(Path.Combine(baseDir, "web")))
            {
                repoRoot = baseDir;
            }
            else if (Directory.Exists(Path.Combine(baseDir, "..", "web")))
            {
                repoRoot = Path.GetFullPath(Path.Combine(baseDir, ".."));
            }
            else if (Directory.Exists(Path.Combine(Directory.GetCurrentDirectory(), "web")))
            {
                repoRoot = Directory.GetCurrentDirectory();
            }

            // Load local environment variables from .env if present
            LoadDotEnvFile(Path.Combine(repoRoot, ".env"));

            bool runSelfTest = false;
            bool forceSimulation = false;
            bool cliMode = false;
            int port = 8080;
            string cliPrinterQueue = null;
            string configPrinterQueue = null;
            bool autoFallbackPrinter = true;
            string configShopId = null;
            string configShopName = null;
            string configBwQueue = null;
            string configColorQueue = null;

            // Load portable station config from config/station.json or any station-*.json if present
            string configPath = Path.Combine(repoRoot, "config", "station.json");
            if (!File.Exists(configPath))
            {
                configPath = Path.Combine(repoRoot, "station.json");
            }
            if (!File.Exists(configPath))
            {
                string configDir = Path.Combine(repoRoot, "config");
                if (Directory.Exists(configDir))
                {
                    string[] candidates = Directory.GetFiles(configDir, "station-*.json");
                    if (candidates != null && candidates.Length > 0)
                        configPath = candidates[0];
                }
            }
            if (!File.Exists(configPath))
            {
                string[] candidates = Directory.GetFiles(repoRoot, "station-*.json");
                if (candidates != null && candidates.Length > 0)
                    configPath = candidates[0];
            }

            if (File.Exists(configPath))
            {
                try
                {
                    Console.WriteLine("[Config] Found station configuration: " + Path.GetFileName(configPath));
                    var json = new JavaScriptSerializer();
                    var cfg = json.Deserialize<Dictionary<string, object>>(File.ReadAllText(configPath));
                    if (cfg != null)
                    {
                        if (cfg.ContainsKey("port") && cfg["port"] != null)
                            int.TryParse(Convert.ToString(cfg["port"], CultureInfo.InvariantCulture), out port);
                        if (cfg.ContainsKey("activePrinterQueue") && cfg["activePrinterQueue"] != null)
                            configPrinterQueue = Convert.ToString(cfg["activePrinterQueue"]).Trim();
                        if (cfg.ContainsKey("autoFallbackToPhysicalPrinter") && cfg["autoFallbackToPhysicalPrinter"] != null)
                            bool.TryParse(Convert.ToString(cfg["autoFallbackToPhysicalPrinter"]), out autoFallbackPrinter);
                        if (cfg.ContainsKey("shopId") && cfg["shopId"] != null)
                            configShopId = Convert.ToString(cfg["shopId"]).Trim();
                        if (cfg.ContainsKey("shopName") && cfg["shopName"] != null)
                            configShopName = Convert.ToString(cfg["shopName"]).Trim();
                        if (cfg.ContainsKey("bwPrinterQueue") && cfg["bwPrinterQueue"] != null)
                            configBwQueue = Convert.ToString(cfg["bwPrinterQueue"]).Trim();
                        if (cfg.ContainsKey("colorPrinterQueue") && cfg["colorPrinterQueue"] != null)
                            configColorQueue = Convert.ToString(cfg["colorPrinterQueue"]).Trim();
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine("[Config] Warning reading config/station.json: " + ex.Message);
                }
            }

            bool disableInternalTunnel = false;

            if (args != null)
            {
                for (int i = 0; i < args.Length; i++)
                {
                    string a = args[i] ?? "";
                    if (string.Equals(a, "--self-test", StringComparison.OrdinalIgnoreCase))
                        runSelfTest = true;
                    else if (string.Equals(a, "--simulation", StringComparison.OrdinalIgnoreCase) || string.Equals(a, "--dry-run", StringComparison.OrdinalIgnoreCase))
                        forceSimulation = true;
                    else if (string.Equals(a, "--port", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                    {
                        int.TryParse(args[++i], NumberStyles.Integer, CultureInfo.InvariantCulture, out port);
                    }
                    else if (string.Equals(a, "--printer", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                    {
                        cliPrinterQueue = args[++i];
                    }
                    else if (string.Equals(a, "--cli", StringComparison.OrdinalIgnoreCase) || string.Equals(a, "--headless", StringComparison.OrdinalIgnoreCase))
                    {
                        cliMode = true;
                    }
                    else if (string.Equals(a, "--no-tunnel", StringComparison.OrdinalIgnoreCase))
                    {
                        disableInternalTunnel = true;
                    }
                }
            }

            if (runSelfTest || cliMode)
            {
                AttachConsole(ATTACH_PARENT_PROCESS);
            }

            if (runSelfTest)
            {
                string tempDir = Path.Combine(repoRoot, "data", "selftest_tmp");
                return SelfTestSuite.RunAllTests(tempDir);
            }

            bool createdNewMutex = false;
            Mutex singleInstanceMutex = null;
            EventWaitHandle restoreEvent = null;
            if (!cliMode)
            {
                singleInstanceMutex = new Mutex(true, "PrintAway_Station_SingleInstance_Mutex_9f8a", out createdNewMutex);
                if (!createdNewMutex)
                {
                    // PrintAway Station is already running! Signal existing window to restore and bring to front.
                    try
                    {
                        EventWaitHandle existingEvent;
                        if (EventWaitHandle.TryOpenExisting("PrintAway_Station_Restore_Event_9f8a", out existingEvent))
                        {
                            existingEvent.Set();
                            existingEvent.Close();
                        }
                    }
                    catch { }

                    try
                    {
                        var cur = System.Diagnostics.Process.GetCurrentProcess();
                        foreach (var p in System.Diagnostics.Process.GetProcessesByName("PrintAway"))
                        {
                            if (p.Id != cur.Id && p.MainWindowHandle != IntPtr.Zero)
                            {
                                ShowWindow(p.MainWindowHandle, SW_RESTORE);
                                SetForegroundWindow(p.MainWindowHandle);
                            }
                        }
                    }
                    catch { }

                    int wmShowMe = RegisterWindowMessage("PRINTAWAY_SHOWME_RESTORE_MSG");
                    PostMessage(HWND_BROADCAST, wmShowMe, IntPtr.Zero, IntPtr.Zero);
                    return 0;
                }

                try
                {
                    restoreEvent = new EventWaitHandle(false, EventResetMode.AutoReset, "PrintAway_Station_Restore_Event_9f8a");
                }
                catch { }
            }

            try
            {
                string dataDir = Path.Combine(repoRoot, "data");
                string vaultDir = Path.Combine(dataDir, "private_vault_pdfs");
                string dbPath = Path.Combine(dataDir, "printaway.db");
                string webRoot = Path.Combine(repoRoot, "web");

                if (!Directory.Exists(dataDir)) Directory.CreateDirectory(dataDir);
                if (!Directory.Exists(vaultDir)) Directory.CreateDirectory(vaultDir);

                string webhookSecret = Environment.GetEnvironmentVariable("PRINTAWAY_WEBHOOK_SECRET") ?? "pa_local_webhook_secret_2026";
                string workerToken = Environment.GetEnvironmentVariable("PRINTAWAY_WORKER_TOKEN") ?? "pa_local_worker_token_2026";

                using (var sqlite = new SqliteDatabase(dbPath))
                {
                    var repo = new PrintAwayRepository(sqlite, vaultDir, webhookSecret, workerToken);

                    string rzpKey = Environment.GetEnvironmentVariable("RAZORPAY_KEY_ID");
                    string rzpSec = Environment.GetEnvironmentVariable("RAZORPAY_KEY_SECRET");
                    string rzpWhSec = Environment.GetEnvironmentVariable("RAZORPAY_WEBHOOK_SECRET");
                    string rzpLinkedAcc = Environment.GetEnvironmentVariable("RAZORPAY_LINKED_ACCOUNT_ID");
                    if (!repo.HasRazorpayConfigured && !string.IsNullOrWhiteSpace(rzpKey) && !string.IsNullOrWhiteSpace(rzpSec))
                    {
                        repo.ConfigureRazorpay(rzpKey, rzpSec, rzpWhSec, rzpLinkedAcc);
                    }
                    if (repo.HasRazorpayConfigured)
                    {
                        Console.ForegroundColor = ConsoleColor.Green;
                        Console.WriteLine("[Payment Gateway] Razorpay Active (" + repo.RazorpayMode.ToUpperInvariant() + ") -> Key ID: " + repo.RazorpayKeyId);
                        Console.ResetColor();
                    }

                    // Always ensure shop identity from config is synced to local repository
                    if (!string.IsNullOrWhiteSpace(configShopId) || !string.IsNullOrWhiteSpace(configShopName))
                    {
                        repo.UpdateShopAndPrinterControls(null, null, null, null, "config-load", null, null, null, null, null, configBwQueue, configColorQueue, configShopId, configShopName);
                    }

                    // Resolve printer queue for easy hardware/PC migration
                    string desiredQueue = !string.IsNullOrWhiteSpace(cliPrinterQueue) ? cliPrinterQueue : configPrinterQueue;
                    if (!string.IsNullOrWhiteSpace(desiredQueue))
                    {
                        var liveQueues = PdfAndPrinterEngine.DiscoverWindowsQueues();
                        bool foundExact = false;
                        string firstPhysicalQueue = null;

                        if (!string.Equals(desiredQueue, "auto", StringComparison.OrdinalIgnoreCase))
                        {
                            foreach (var q in liveQueues)
                            {
                                if (string.Equals(q.QueueName, desiredQueue, StringComparison.OrdinalIgnoreCase))
                                {
                                    foundExact = true;
                                    desiredQueue = q.QueueName;
                                    break;
                                }
                            }
                        }

                        foreach (var q in liveQueues)
                        {
                            if (!q.IsVirtualExcluded && firstPhysicalQueue == null)
                            {
                                firstPhysicalQueue = q.QueueName;
                            }
                        }

                        if (foundExact)
                        {
                            repo.UpdateShopAndPrinterControls(null, null, null, null, "system-startup", null, null, null, null, desiredQueue, configBwQueue, configColorQueue, configShopId, configShopName);
                        }
                        else if ((string.Equals(desiredQueue, "auto", StringComparison.OrdinalIgnoreCase) || autoFallbackPrinter) && !string.IsNullOrWhiteSpace(firstPhysicalQueue))
                        {
                            Console.WriteLine("[Printer Auto-Detect] Configured queue '" + desiredQueue + "' not on host; auto-selecting physical printer: " + firstPhysicalQueue);
                            repo.UpdateShopAndPrinterControls(null, null, null, null, "system-startup", null, null, null, null, firstPhysicalQueue, configBwQueue, configColorQueue, configShopId, configShopName);
                        }
                    }

                    var currentStatus = repo.GetShopAndAvailabilityStatus();
                    string activeQueueDisplay = Convert.ToString(currentStatus["activeQueueName"]);

                    var worker = new LocalPrintWorker(repo, simulateSpoolerOnly: forceSimulation);
                    worker.StartBackground(1500);

                    TunnelManager tunnel = null;
                    if (!disableInternalTunnel)
                    {
                        string registryTopic = "printaway_station_registry_" + (configShopId ?? "pilot") + "_9f8a7b6c5d4e";
                        tunnel = new TunnelManager(repoRoot, port, registryTopic);
                        tunnel.Start();
                    }

                    string sbUrl = Environment.GetEnvironmentVariable("SUPABASE_URL");
                    string sbKey = Environment.GetEnvironmentVariable("SUPABASE_SERVICE_ROLE_KEY");
                    if (string.IsNullOrWhiteSpace(sbKey)) sbKey = Environment.GetEnvironmentVariable("SUPABASE_KEY");

                    // Fallback to configured Supabase Control Plane
                    if (string.IsNullOrWhiteSpace(sbUrl)) sbUrl = "https://zduoifzakiojfqbtxqqm.supabase.co";
                    if (string.IsNullOrWhiteSpace(sbKey)) sbKey = "sb_publishable_rhmUZZbKhXiJxrChcPTbsg_gFIH9FdO";

                    SupabaseSyncClient supabaseSync = null;
                    if (!string.IsNullOrWhiteSpace(sbUrl) && !string.IsNullOrWhiteSpace(sbKey))
                    {
                        supabaseSync = new SupabaseSyncClient(sbUrl, sbKey, configShopId ?? "pilot", repo, tunnel);
                        Console.ForegroundColor = ConsoleColor.Green;
                        Console.WriteLine("[SaaS Control Plane] Supabase Heartbeat Active -> " + sbUrl);
                        Console.ResetColor();
                    }

                    using (var server = new PrintAwayHttpServer(port, repo, worker, webRoot, supabaseSync))
                    {
                        server.Start();

                        if (!cliMode)
                        {
                            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
                            Application.ThreadException += (s, e) =>
                            {
                                try { File.AppendAllText(Path.Combine(repoRoot, "data", "crash.log"), DateTime.UtcNow.ToString("o") + " [ThreadException] " + e.Exception.ToString() + "\r\n"); } catch { }
                            };
                            AppDomain.CurrentDomain.UnhandledException += (s, e) =>
                            {
                                try { File.AppendAllText(Path.Combine(repoRoot, "data", "crash.log"), DateTime.UtcNow.ToString("o") + " [UnhandledException] " + e.ExceptionObject.ToString() + "\r\n"); } catch { }
                            };
                            Application.EnableVisualStyles();
                            Application.SetCompatibleTextRenderingDefault(false);
                            var mainForm = new StationGuiForm(repo, worker, server, tunnel, repoRoot, port, supabaseSync);

                            if (restoreEvent != null)
                            {
                                var restoreThread = new Thread(() =>
                                {
                                    while (true)
                                    {
                                        try
                                        {
                                            if (restoreEvent.WaitOne())
                                            {
                                                if (mainForm != null && !mainForm.IsDisposed)
                                                {
                                                    mainForm.RestoreWindow();
                                                }
                                            }
                                        }
                                        catch (ThreadAbortException) { break; }
                                        catch { }
                                    }
                                });
                                restoreThread.IsBackground = true;
                                restoreThread.Start();
                            }

                            Application.Run(mainForm);
                        }
                        else
                        {
                            Console.ForegroundColor = ConsoleColor.Cyan;
                            Console.WriteLine("======================================================================");
                            Console.WriteLine("  PrintAway Local Host Server & Print Worker Active (CLI Mode)");
                            Console.WriteLine("======================================================================");
                            Console.ResetColor();
                            Console.WriteLine("  Customer Portal (QR / Web):  http://127.0.0.1:" + port + "/");
                            Console.WriteLine("  Staff Admin Dashboard:       http://127.0.0.1:" + port + "/admin");
                            Console.WriteLine("  Default Admin Login:         admin / PrintAway@Admin2026");
                            Console.WriteLine("  Active Verified Queue:       " + activeQueueDisplay);
                            Console.WriteLine("  Worker Dispatch Mode:        " + (forceSimulation ? "SAFE SPOOLER SIMULATION (--simulation)" : "LIVE PHYSICAL PRINT (" + activeQueueDisplay + ")"));
                            Console.WriteLine("  Station Config File:         " + configPath);
                            Console.WriteLine("  SQLite WAL Database:         " + dbPath);
                            Console.WriteLine("  Private PDF Vault:           " + vaultDir);
                            Console.WriteLine("======================================================================");
                            Console.WriteLine("Press Ctrl+C to stop.");

                            var exitEvent = new ManualResetEvent(false);
                            try
                            {
                                Console.CancelKeyPress += (s, e) =>
                                {
                                    e.Cancel = true;
                                    exitEvent.Set();
                                };
                            }
                            catch { }
                            exitEvent.WaitOne();
                        }

                        tunnel.Stop();
                        worker.Stop();
                        if (supabaseSync != null) { supabaseSync.Dispose(); }
                    }
                }
            }
            catch (Exception ex)
            {
                try
                {
                    string err1 = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "startup_error.log");
                    File.AppendAllText(err1, DateTime.UtcNow.ToString("o") + " [FATAL STARTUP EXCEPTION]\r\n" + ex.ToString() + "\r\n\r\n");
                    string err2 = Path.Combine(repoRoot, "data", "startup_error.log");
                    File.AppendAllText(err2, DateTime.UtcNow.ToString("o") + " [FATAL STARTUP EXCEPTION]\r\n" + ex.ToString() + "\r\n\r\n");
                }
                catch { }
                if (!cliMode)
                {
                    MessageBox.Show("PrintAway failed to start:\n" + ex.Message + "\n\nSee data/startup_error.log for details.", "PrintAway Startup Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
                else
                {
                    Console.Error.WriteLine("[FATAL STARTUP EXCEPTION] " + ex.ToString());
                }
                return 1;
            }
            finally
            {
                if (singleInstanceMutex != null)
                {
                    if (createdNewMutex)
                    {
                        try { singleInstanceMutex.ReleaseMutex(); } catch { }
                    }
                    singleInstanceMutex.Dispose();
                }
            }
            return 0;
        }

        private static void LoadDotEnvFile(string envPath)
        {
            if (!File.Exists(envPath)) return;
            try
            {
                foreach (string rawLine in File.ReadAllLines(envPath))
                {
                    string line = rawLine != null ? rawLine.Trim() : "";
                    if (string.IsNullOrEmpty(line) || line.StartsWith("#")) continue;
                    int eqIdx = line.IndexOf('=');
                    if (eqIdx > 0)
                    {
                        string key = line.Substring(0, eqIdx).Trim();
                        string val = line.Substring(eqIdx + 1).Trim();
                        if ((val.StartsWith("\"") && val.EndsWith("\"")) || (val.StartsWith("'") && val.EndsWith("'")))
                        {
                            val = val.Substring(1, val.Length - 2);
                        }
                        Environment.SetEnvironmentVariable(key, val);
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine("[Config] Warning reading .env: " + ex.Message);
            }
        }
    }
}
