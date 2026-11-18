using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Runtime.InteropServices;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using PrintAway.Config;
using PrintAway.Engine;
using PrintAway.Gui;
using PrintAway.SaaS;
using PrintAway.Storage;
using PrintAway.Tunnel;
using PrintAway.Web;
using PrintAway.Updater;
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
            ServicePointManager.DefaultConnectionLimit = 64;
            ServicePointManager.Expect100Continue = false;
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
            string cliPrinterQueue = null;

            // Per-install station configuration. A fresh clone/install has NO config/station.json,
            // which is what routes the user into first-run onboarding below.
            StationConfig config = StationConfig.Load(repoRoot);
            int port = config.Port;
            string configPath = config.FilePath;

            bool disableInternalTunnel = false;
            var cliSetup = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

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
                    else if (a.StartsWith("--setup-", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                    {
                        // Headless onboarding: --setup-shop-id, --setup-shop-name, --setup-owner-name,
                        // --setup-owner-phone, --setup-upi, --setup-admin-password, --setup-printer
                        cliSetup[a.Substring(8).ToLowerInvariant()] = args[++i];
                    }
                }
            }

            if (runSelfTest || cliMode)
            {
                AttachConsole(ATTACH_PARENT_PROCESS);
            }
            else
            {
                // All WinForms global settings must be applied before the first control is created
                // (the onboarding wizard may be shown before the main window).
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

                // Per-install random secrets unless explicitly provided via .env. Hard-coded defaults would be
                // identical (and public) on every station and let anyone forge payment webhooks.
                string webhookSecret = Environment.GetEnvironmentVariable("PRINTAWAY_WEBHOOK_SECRET");
                if (string.IsNullOrWhiteSpace(webhookSecret) || webhookSecret == "pa_local_webhook_secret_2026")
                    webhookSecret = StationConfig.GetOrCreateInstallSecret(dataDir, "webhookSecret");
                string workerToken = Environment.GetEnvironmentVariable("PRINTAWAY_WORKER_TOKEN");
                if (string.IsNullOrWhiteSpace(workerToken) || workerToken == "pa_local_worker_token_2026")
                    workerToken = StationConfig.GetOrCreateInstallSecret(dataDir, "workerToken");

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

                    // ---------------------------------------------------------------- shop identity
                    if (config.HasValidShopIdentity)
                    {
                        // config/station.json is the source of truth for this machine's identity.
                        repo.UpdateShopAndPrinterControls(null, null, null, null, "config-load", null, null, null, null, null,
                            config.BwPrinterQueue, config.ColorPrinterQueue, config.ShopId, config.ShopName);
                    }
                    else if (repo.IsShopConfigured)
                    {
                        // Existing install whose config file went missing (e.g. it was removed from git and
                        // deleted on pull): rebuild it from the local database instead of re-onboarding.
                        var st = repo.GetShopAndAvailabilityStatus();
                        config.ShopId = Convert.ToString(st["shopId"]);
                        config.ShopName = Convert.ToString(st["shopName"]);
                        if (string.IsNullOrWhiteSpace(config.UpiVpa)) config.UpiVpa = Convert.ToString(st["upiVpa"]);
                        if (string.IsNullOrWhiteSpace(config.ActivePrinterQueue)) config.ActivePrinterQueue = Convert.ToString(st["activeQueueName"]);
                        try { config.Save(); Console.WriteLine("[Config] Restored config/station.json from local database."); }
                        catch (Exception ex) { Console.WriteLine("[Config] Could not restore station.json: " + ex.Message); }
                    }

                    if (!repo.IsOnboardingComplete)
                    {
                        // Nothing (tunnel, registry broadcast, cloud heartbeat, HTTP server) starts until the
                        // station has its own identity and an owner-chosen admin password.
                        bool onboarded = cliMode
                            ? RunHeadlessOnboarding(cliSetup, repo, config)
                            : RunGuiOnboarding(repo, config);
                        if (!onboarded)
                        {
                            if (cliMode)
                            {
                                Console.Error.WriteLine("This station has not been set up yet. Run PrintAway.exe without --cli to use the setup wizard,");
                                Console.Error.WriteLine("or pass --setup-shop-id, --setup-shop-name, --setup-owner-name, --setup-owner-phone,");
                                Console.Error.WriteLine("--setup-upi and --setup-admin-password (optionally --setup-printer).");
                                return 2;
                            }
                            return 0; // wizard closed: exit without publishing anything
                        }
                    }

                    // Keep config in sync when identity is changed later from the GUI or admin dashboard.
                    repo.ShopIdentityChanged += (newSlug, newName) =>
                    {
                        try
                        {
                            config.ShopId = newSlug;
                            config.ShopName = newName;
                            config.Save();
                        }
                        catch (Exception ex) { Console.WriteLine("[Config] Could not persist identity change: " + ex.Message); }
                    };

                    string configShopId = config.ShopId;
                    string configShopName = config.ShopName;

                    // Resolve printer queue for easy hardware/PC migration
                    string desiredQueue = !string.IsNullOrWhiteSpace(cliPrinterQueue) ? cliPrinterQueue : config.ActivePrinterQueue;
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
                            repo.UpdateShopAndPrinterControls(null, null, null, null, "system-startup", null, null, null, null, desiredQueue);
                        }
                        else if ((string.Equals(desiredQueue, "auto", StringComparison.OrdinalIgnoreCase) || config.AutoFallbackToPhysicalPrinter) && !string.IsNullOrWhiteSpace(firstPhysicalQueue))
                        {
                            Console.WriteLine("[Printer Auto-Detect] Configured queue '" + desiredQueue + "' not on host; auto-selecting physical printer: " + firstPhysicalQueue);
                            repo.UpdateShopAndPrinterControls(null, null, null, null, "system-startup", null, null, null, null, firstPhysicalQueue);
                        }
                    }

                    var currentStatus = repo.GetShopAndAvailabilityStatus();
                    string activeQueueDisplay = Convert.ToString(currentStatus["activeQueueName"]);

                    var worker = new LocalPrintWorker(repo, simulateSpoolerOnly: forceSimulation);
                    worker.StartBackground(1500);

                    TunnelManager tunnel = null;
                    if (!disableInternalTunnel)
                    {
                        tunnel = new TunnelManager(repoRoot, port, configShopId, configShopName);
                        tunnel.SetShopProfile(config.OwnerName, config.OwnerPhone, config.UpiVpa);
                        var tunnelRef = tunnel;
                        repo.ShopIdentityChanged += (newSlug, newName) => tunnelRef.UpdateShopIdentity(newSlug, newName);
                        tunnel.OnTunnelUrlCaptured += url =>
                        {
                            try { repo.SetLiveTunnelUrl(url ?? ""); } catch { }
                        };
                        tunnel.Start();
                    }

                    string sbUrl = ControlPlane.Url;
                    string sbKey = ControlPlane.Key;

                    SupabaseSyncClient supabaseSync = null;
                    if (!string.IsNullOrWhiteSpace(sbUrl) && !string.IsNullOrWhiteSpace(sbKey))
                    {
                        supabaseSync = new SupabaseSyncClient(sbUrl, sbKey, configShopId, repo, tunnel);
                        Console.ForegroundColor = ConsoleColor.Green;
                        Console.WriteLine("[SaaS Control Plane] Supabase Heartbeat Active -> " + sbUrl);
                        Console.ResetColor();
                    }

                    using (var server = new PrintAwayHttpServer(port, repo, worker, webRoot, supabaseSync))
                    {
                        server.Start();

                        if (!cliMode)
                        {
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
                            Console.WriteLine("  Shop:                        " + configShopName + " (" + configShopId + ")");
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

                        if (tunnel != null) tunnel.Stop();
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

        private static bool RunGuiOnboarding(PrintAwayRepository repo, StationConfig config)
        {
            using (var wizard = new OnboardingWizard(config.PublicStorefrontUrl, AutoUpdater.CurrentVersion))
            {
                // Pre-fill anything already known (e.g. from scripts/setup-station.ps1 or a partial setup).
                wizard.Prefill(config.HasValidShopIdentity ? config.ShopName : null,
                    config.HasValidShopIdentity ? config.ShopId : null,
                    config.OwnerName, config.OwnerPhone, config.UpiVpa, config.ActivePrinterQueue);
                if (wizard.ShowDialog() != DialogResult.OK || wizard.Result == null) return false;
                var r = wizard.Result;
                try
                {
                    ApplyOnboardingResult(repo, config, r.ShopId, r.ShopName, r.OwnerName, r.OwnerPhone, r.UpiVpa,
                        r.AdminPassword, r.PrimaryQueue, r.BwQueue, r.ColorQueue);
                }
                catch (Exception ex)
                {
                    MessageBox.Show("Setup could not be saved:\n" + ex.Message, "PrintAway Setup", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    return false;
                }

                if (r.AutoStart)
                {
                    try { WindowsStartup.CreateShortcut(WindowsStartup.StartupShortcutPath, Application.ExecutablePath); }
                    catch (Exception ex) { Console.WriteLine("[Setup] Could not create startup shortcut: " + ex.Message); }
                }

                MessageBox.Show(
                    "Your shop is ready!\n\n" +
                    "Shop: " + r.ShopName + "\n" +
                    "Shop ID: " + r.ShopId + "\n" +
                    "Printer: " + (r.PrimaryQueue ?? "(choose later)") + "\n\n" +
                    "Customer QR link:\n" + config.PublicStorefrontUrl + "/?shop=" + r.ShopId + "\n\n" +
                    "Counter dashboard: sign in with your Shop ID as the username and the password you just set." +
                    (r.RegisteredOnline || r.ClaimedExistingShopId ? "" : "\n\nNote: the shop will be registered online once this PC has internet access."),
                    "PrintAway Station Ready", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return true;
            }
        }

        private static bool RunHeadlessOnboarding(Dictionary<string, string> setup, PrintAwayRepository repo, StationConfig config)
        {
            if (setup.Count == 0) return false;

            Func<string, string> get = k => { string v; return setup.TryGetValue(k, out v) && v != null ? v.Trim() : ""; };
            string slug = StationConfig.NormalizeSlug(get("shop-id"));
            string name = get("shop-name");
            string ownerName = get("owner-name");
            string phone = StationConfig.NormalizePhone(get("owner-phone"));
            string upi = get("upi");
            string password;
            setup.TryGetValue("admin-password", out password);
            string printer = get("printer");

            var errors = new List<string>();
            string slugErr = StationConfig.ValidateSlug(slug);
            if (slugErr != null) errors.Add(slugErr);
            if (name.Length < 3) errors.Add("--setup-shop-name is required.");
            if (ownerName.Length < 2) errors.Add("--setup-owner-name is required.");
            if (phone == null) errors.Add("--setup-owner-phone must be a valid mobile number.");
            if (!StationConfig.IsValidUpiVpa(upi)) errors.Add("--setup-upi must be a valid UPI ID (name@bank).");
            string passErr = StationConfig.ValidateAdminPassword(password);
            if (passErr != null) errors.Add(passErr);
            if (errors.Count > 0)
            {
                foreach (var e in errors) Console.Error.WriteLine("[Setup] " + e);
                return false;
            }

            var check = ShopRegistryClient.CheckSlug(slug);
            if (check.Availability == SlugAvailability.Taken && !setup.ContainsKey("claim-existing"))
            {
                Console.Error.WriteLine("[Setup] Shop ID '" + slug + "' is already registered" +
                    (string.IsNullOrEmpty(check.ExistingShopName) ? "" : " to '" + check.ExistingShopName + "'") +
                    ". Choose another ID, or add '--setup-claim-existing yes' if this is your own shop moving to a new PC.");
                return false;
            }
            if (check.Availability == SlugAvailability.Available)
            {
                bool conflict;
                string err;
                if (!ShopRegistryClient.RegisterShop(slug, name, ownerName, phone, upi, printer, AutoUpdater.CurrentVersion, out conflict, out err) && conflict)
                {
                    Console.Error.WriteLine("[Setup] " + err);
                    return false;
                }
            }
            else if (check.Availability == SlugAvailability.Unknown)
            {
                Console.WriteLine("[Setup] Registry unreachable (" + check.Error + "); continuing offline.");
            }

            ApplyOnboardingResult(repo, config, slug, name, ownerName, phone, upi, password,
                string.IsNullOrEmpty(printer) ? null : printer, null, null);
            Console.WriteLine("[Setup] Station onboarded as '" + slug + "'.");
            return true;
        }

        private static void ApplyOnboardingResult(PrintAwayRepository repo, StationConfig config, string slug, string name,
            string ownerName, string ownerPhone, string upi, string adminPassword, string primary, string bw, string color)
        {
            repo.ApplyOnboarding(slug, name, upi, adminPassword, primary, bw, color);

            config.ShopId = slug;
            config.ShopName = name;
            config.OwnerName = ownerName;
            config.OwnerPhone = ownerPhone;
            config.UpiVpa = upi;
            config.ActivePrinterQueue = string.IsNullOrWhiteSpace(primary) ? "auto" : primary;
            config.BwPrinterQueue = bw ?? primary;
            config.ColorPrinterQueue = color ?? primary;
            config.Set("onboardedAtUtc", DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture));
            config.Save();
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
