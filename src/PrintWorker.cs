using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using PrintAway.Engine;
using PrintAway.Storage;

namespace PrintAway.Worker
{
    public sealed class LocalPrintWorker
    {
        private readonly PrintAwayRepository _repo;
        private readonly bool _simulateSpoolerOnly;
        private volatile bool _running;
        private Thread _workerThread;

        public LocalPrintWorker(PrintAwayRepository repo, bool simulateSpoolerOnly = false)
        {
            _repo = repo;
            _simulateSpoolerOnly = simulateSpoolerOnly;
        }

        public bool SimulateSpoolerOnly
        {
            get { return _simulateSpoolerOnly; }
        }

        public void StartBackground(int pollIntervalMs = 1500)
        {
            if (_running) return;
            _running = true;
            _workerThread = new Thread(() =>
            {
                while (_running)
                {
                    try
                    {
                        RunSingleCycle();
                    }
                    catch { }
                    Thread.Sleep(pollIntervalMs);
                }
            });
            _workerThread.IsBackground = true;
            _workerThread.Name = "PrintAway-Verified-Worker";
            _workerThread.Start();
        }

        public void Stop()
        {
            _running = false;
        }

        // Executes one deterministic worker heartbeat + lease + render + spool cycle.
        public Dictionary<string, object> RunSingleCycle(bool simulateCrashMidDownload = false, bool simulateCrashAfterSpooler = false)
        {
            _repo.RunRetentionCleanupPass();
            _repo.ReconcileExpiredWorkerLeases();

            var claimed = _repo.ClaimNextQueuedJob(30);
            if (claimed == null)
            {
                return new Dictionary<string, object> { { "claimed", false } };
            }

            long jobId = Convert.ToInt64(claimed["jobId"], CultureInfo.InvariantCulture);
            long orderId = Convert.ToInt64(claimed["orderId"], CultureInfo.InvariantCulture);
            string orderCode = Convert.ToString(claimed["orderCode"]);
            string leaseToken = Convert.ToString(claimed["leaseToken"]);
            string targetQueue = Convert.ToString(claimed["targetQueueName"]);
            int docPages = Convert.ToInt32(claimed["documentPages"], CultureInfo.InvariantCulture);
            string pageRange = Convert.ToString(claimed["pageRange"]);
            int copies = Convert.ToInt32(claimed["copies"], CultureInfo.InvariantCulture);
            string duplexMode = Convert.ToString(claimed["duplexMode"]);

            string evErr;
            _repo.RecordWorkerJobEvent(jobId, leaseToken, "downloading", null, "Downloading validated PDF from host vault", out evErr);

            if (simulateCrashMidDownload)
            {
                // Simulate worker disconnecting during download before rendering starts
                return new Dictionary<string, object>
                {
                    { "claimed", true },
                    { "jobId", jobId },
                    { "orderId", orderId },
                    { "simulatedCrash", "mid_download" }
                };
            }

            string dlErr;
            byte[] pdfBytes = _repo.GetJobPdfBytesForWorker(jobId, leaseToken, out dlErr);
            if (pdfBytes == null)
            {
                _repo.RecordWorkerJobEvent(jobId, leaseToken, "failed", null, "PDF download failed: " + dlErr, out evErr);
                return new Dictionary<string, object>
                {
                    { "claimed", true },
                    { "jobId", jobId },
                    { "orderId", orderId },
                    { "state", "failed" },
                    { "error", dlErr }
                };
            }

            _repo.RecordWorkerJobEvent(jobId, leaseToken, "rendering", null, "Rendering PDF for " + targetQueue + " (A4, B&W)", out evErr);

            string normRange;
            string rangeErr;
            List<int> pagesToPrint = PdfAndPrinterEngine.ParseAndValidatePageRange(pageRange, docPages, out normRange, out rangeErr);

            bool effectiveSimulate = _simulateSpoolerOnly || string.Equals(_repo.GetDispatchMode(), "simulation", StringComparison.OrdinalIgnoreCase);

            PrintDispatchResult dispatch = PdfAndPrinterEngine.DispatchPrintJob(
                targetQueue,
                orderCode,
                pdfBytes,
                pagesToPrint,
                copies,
                duplexMode,
                effectiveSimulate);

            if (!dispatch.Dispatched)
            {
                _repo.RecordWorkerJobEvent(jobId, leaseToken, "reconciliation_required", null, dispatch.ErrorMessage, out evErr);
                return new Dictionary<string, object>
                {
                    { "claimed", true },
                    { "jobId", jobId },
                    { "orderId", orderId },
                    { "state", "reconciliation_required" },
                    { "error", dispatch.ErrorMessage }
                };
            }

            if (simulateCrashAfterSpooler)
            {
                // Simulate worker crashing AFTER sending to Windows Spooler but BEFORE acknowledging to server!
                // When the lease expires, ReconcileExpiredWorkerLeases() will transition this job from 'rendering' -> 'reconciliation_required' (NEVER auto-retry!)
                return new Dictionary<string, object>
                {
                    { "claimed", true },
                    { "jobId", jobId },
                    { "orderId", orderId },
                    { "simulatedCrash", "after_spooler_accept" },
                    { "windowsSpoolerJobId", dispatch.WindowsSpoolerJobId }
                };
            }

            _repo.RecordWorkerJobEvent(
                jobId,
                leaseToken,
                "submitted_to_spooler",
                dispatch.WindowsSpoolerJobId,
                string.Format(CultureInfo.InvariantCulture, "Dispatched to {0} (Sides={1}, Sheets={2}, Simulated={3})",
                    targetQueue, dispatch.PrintedSides, dispatch.PhysicalSheets, dispatch.SimulatedMode),
                out evErr);

            return new Dictionary<string, object>
            {
                { "claimed", true },
                { "jobId", jobId },
                { "orderId", orderId },
                { "orderCode", orderCode },
                { "state", "submitted_to_spooler" },
                { "windowsSpoolerJobId", dispatch.WindowsSpoolerJobId },
                { "targetQueueName", targetQueue },
                { "printedSides", dispatch.PrintedSides },
                { "physicalSheets", dispatch.PhysicalSheets }
            };
        }
    }
}

