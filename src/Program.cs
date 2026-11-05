using System;
using System.Globalization;
using System.IO;
using System.Threading;
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
                    Console.WriteLine("  Active Verified Queue:       " + PrintAwayRepository.VerifiedXeroxQueueName + " (USB001)");
                    Console.WriteLine("  Worker Dispatch Mode:        " + (forceSimulation ? "SAFE SPOOLER SIMULATION (--simulation)" : "LIVE PHYSICAL PRINT TO XEROX B1025 PCL6 (Default)"));
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

