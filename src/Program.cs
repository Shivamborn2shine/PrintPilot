using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Web.Script.Serialization;
using PrintAway.Engine;
using PrintAway.Storage;
using PrintAway.Web;
using PrintAway.Worker;

namespace PrintAway
{
    public static class Program
    {
        public static int Main(string[] args)
        {
            string baseDir = AppDomain.CurrentDomain.BaseDirectory;
            string repoRoot = Path.GetFullPath(Path.Combine(baseDir, ".."));
            if (!Directory.Exists(Path.Combine(repoRoot, "web")))
            {
                repoRoot = Directory.GetCurrentDirectory();
            }

            bool runSelfTest = false;
            bool forceSimulation = false;
            int port = 8080;
            string cliPrinterQueue = null;
            string configPrinterQueue = null;
            bool autoFallbackPrinter = true;

            // Load portable station config from config/station.json if present
            string configPath = Path.Combine(repoRoot, "config", "station.json");
            if (File.Exists(configPath))
            {
                try
                {
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
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine("[Config] Warning reading config/station.json: " + ex.Message);
                }
            }

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
                }
            }

            if (runSelfTest)
            {
                string tempDir = Path.Combine(repoRoot, "data", "selftest_tmp");
                return SelfTestSuite.RunAllTests(tempDir);
            }

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
                        repo.UpdateShopAndPrinterControls(null, null, null, null, "system-startup", null, null, null, null, desiredQueue);
                    }
                    else if ((string.Equals(desiredQueue, "auto", StringComparison.OrdinalIgnoreCase) || autoFallbackPrinter) && !string.IsNullOrWhiteSpace(firstPhysicalQueue))
                    {
                        Console.WriteLine("[Printer Auto-Detect] Configured queue '" + desiredQueue + "' not on host; auto-selecting physical printer: " + firstPhysicalQueue);
                        repo.UpdateShopAndPrinterControls(null, null, null, null, "system-startup", null, null, null, null, firstPhysicalQueue);
                    }
                }

                var currentStatus = repo.GetShopAndAvailabilityStatus();
                string activeQueueDisplay = Convert.ToString(currentStatus["activeQueueName"]);

                var worker = new LocalPrintWorker(repo, simulateSpoolerOnly: forceSimulation);
                worker.StartBackground(1500);

                using (var server = new PrintAwayHttpServer(port, repo, worker, webRoot))
                {
                    server.Start();
                    Console.ForegroundColor = ConsoleColor.Cyan;
                    Console.WriteLine("======================================================================");
                    Console.WriteLine("  PrintAway Local Host Server & Print Worker Active");
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
                    Console.CancelKeyPress += (s, e) =>
                    {
                        e.Cancel = true;
                        exitEvent.Set();
                    };
                    exitEvent.WaitOne();
                    worker.Stop();
                }
            }
            return 0;
        }
    }
}
