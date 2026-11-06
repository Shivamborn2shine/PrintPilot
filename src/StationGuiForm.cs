using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using PrintAway.Engine;
using PrintAway.SaaS;
using PrintAway.Storage;
using PrintAway.Tunnel;
using PrintAway.Updater;
using PrintAway.Web;
using PrintAway.Worker;

namespace PrintAway.Gui
{
    public sealed class StationGuiForm : Form
    {
        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Auto)]
        private static extern int RegisterWindowMessage(string lpString);

        [DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
        private const int SW_RESTORE = 9;

        public static readonly int WM_SHOWME = RegisterWindowMessage("PRINTAWAY_SHOWME_RESTORE_MSG");

        private readonly PrintAwayRepository _repo;
        private readonly LocalPrintWorker _worker;
        private readonly PrintAwayHttpServer _server;
        private readonly TunnelManager _tunnel;
        private readonly string _repoRoot;
        private readonly int _port;
        private readonly SupabaseSyncClient _supabaseSync;

        // UI Controls
        private Label _lblStatusBadge;
        private Label _lblShopTitle;
        private Label _lblCustomerLink;
        private Label _lblTunnelUrl;
        private Label _lblQuotaBadge;
        private ComboBox _cmbPrinters;
        private RadioButton _rbPhysical;
        private RadioButton _rbSimulation;
        private Button _btnPauseResume;
        private Label _lblMetricOrders;
        private Label _lblMetricSides;
        private Label _lblMetricRevenue;
        private CheckBox _chkAutoStart;
        private CheckBox _chkMinimizeToTray;
        private NotifyIcon _trayIcon;
        private ContextMenuStrip _trayMenu;
        private System.Windows.Forms.Timer _metricsTimer;
        private bool _reallyExit;
        private bool _isInitializing = true;

        public StationGuiForm(PrintAwayRepository repo, LocalPrintWorker worker, PrintAwayHttpServer server, TunnelManager tunnel, string repoRoot, int port)
            : this(repo, worker, server, tunnel, repoRoot, port, null)
        {
        }

        public StationGuiForm(PrintAwayRepository repo, LocalPrintWorker worker, PrintAwayHttpServer server, TunnelManager tunnel, string repoRoot, int port, SupabaseSyncClient supabaseSync)
        {
            _repo = repo;
            _worker = worker;
            _server = server;
            _tunnel = tunnel;
            _repoRoot = repoRoot;
            _port = port;
            _supabaseSync = supabaseSync;

            InitializeGui();
            LoadInitialData();

            if (_tunnel != null)
            {
                _tunnel.OnTunnelUrlCaptured += OnTunnelCaptured;
                _tunnel.OnStatusMessage += OnTunnelStatusMessage;
            }

            if (_supabaseSync != null)
            {
                _supabaseSync.OnQuotaStatusChanged += OnQuotaChanged;
            }
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_SHOWME)
            {
                RestoreWindow();
            }
            base.WndProc(ref m);
        }

        private void InitializeGui()
        {
            this.Text = "PrintAway Station v" + AutoUpdater.CurrentVersion;
            this.ClientSize = new Size(670, 680);
            this.StartPosition = FormStartPosition.CenterScreen;
            this.FormBorderStyle = FormBorderStyle.FixedSingle;
            this.MaximizeBox = false;
            this.Icon = SystemIcons.Application;
            this.BackColor = Color.FromArgb(248, 250, 252);
            this.Font = new Font("Segoe UI", 9f, FontStyle.Regular);

            // 1. Top Header Bar (Fixed Top)
            var pnlHeader = new Panel
            {
                Dock = DockStyle.Top,
                Height = 65,
                BackColor = Color.FromArgb(15, 23, 42)
            };

            var lblHeaderTitle = new Label
            {
                Text = "PrintAway Station",
                ForeColor = Color.White,
                Font = new Font("Segoe UI", 13f, FontStyle.Bold),
                Location = new Point(18, 12),
                AutoSize = true
            };

            var lblHeaderSub = new Label
            {
                Text = "Automated Hardware Printing & Secure Tunnel Hub",
                ForeColor = Color.FromArgb(148, 163, 184),
                Font = new Font("Segoe UI", 8.5f, FontStyle.Regular),
                Location = new Point(20, 38),
                AutoSize = true
            };

            _lblStatusBadge = new Label
            {
                Text = "● ONLINE",
                BackColor = Color.FromArgb(21, 128, 61),
                ForeColor = Color.White,
                Font = new Font("Segoe UI", 8.5f, FontStyle.Bold),
                TextAlign = ContentAlignment.MiddleCenter,
                Size = new Size(95, 26),
                Location = new Point(550, 18)
            };

            pnlHeader.Controls.Add(lblHeaderTitle);
            pnlHeader.Controls.Add(lblHeaderSub);
            pnlHeader.Controls.Add(_lblStatusBadge);

            // 2. Body Scrollable Container (Starts directly below 65px header bar)
            var pnlBody = new Panel
            {
                Location = new Point(0, 65),
                Size = new Size(this.ClientSize.Width, this.ClientSize.Height - 65),
                Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right,
                BackColor = Color.FromArgb(248, 250, 252),
                AutoScroll = true
            };
            this.Controls.Add(pnlBody);
            this.Controls.Add(pnlHeader);

            int y = 14;

            // 3. Card: Shop Identity & Live Customer QR Link
            var grpShop = CreateCardPanel("🏪 Shop Identity & Live Customer QR", 18, y, 620, 150);
            y += 162;

            _lblShopTitle = new Label
            {
                Text = "Shop: Loading...",
                Font = new Font("Segoe UI", 10f, FontStyle.Bold),
                ForeColor = Color.FromArgb(30, 41, 59),
                Location = new Point(14, 28),
                Size = new Size(470, 22)
            };

            var btnSetShop = new Button
            {
                Text = "⚙️ Import Config",
                Size = new Size(115, 26),
                Location = new Point(490, 10),
                BackColor = Color.White,
                FlatStyle = FlatStyle.System,
                Cursor = Cursors.Hand
            };
            btnSetShop.Click += (s, e) => OnSetShopClicked();

            _lblCustomerLink = new Label
            {
                Text = "Customer QR: https://printitaway.netlify.app/?shop=pilot",
                Font = new Font("Segoe UI", 8.5f, FontStyle.Regular),
                ForeColor = Color.FromArgb(29, 78, 216),
                Location = new Point(14, 54),
                Size = new Size(470, 20)
            };

            var btnCopyLink = new Button
            {
                Text = "📋 Copy Link",
                Size = new Size(115, 26),
                Location = new Point(490, 48),
                BackColor = Color.White,
                FlatStyle = FlatStyle.System
            };
            btnCopyLink.Click += (s, e) =>
            {
                try
                {
                    Clipboard.SetText(_lblCustomerLink.Text.Replace("Customer QR: ", "").Trim());
                    MessageBox.Show("Customer QR link copied to clipboard!", "PrintAway", MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
                catch { }
            };

            _lblTunnelUrl = new Label
            {
                Text = "Tunnel: Initializing secure tunnel engine...",
                Font = new Font("Segoe UI", 8f, FontStyle.Regular),
                ForeColor = Color.FromArgb(71, 85, 105),
                Location = new Point(14, 82),
                Size = new Size(410, 26)
            };

            var btnCopyTunnel = new Button
            {
                Text = "📋 Copy",
                Size = new Size(56, 23),
                Location = new Point(430, 79),
                BackColor = Color.FromArgb(241, 245, 249),
                ForeColor = Color.FromArgb(15, 23, 42),
                Font = new Font("Segoe UI", 7.5f, FontStyle.Regular),
                FlatStyle = FlatStyle.System,
                Cursor = Cursors.Hand
            };
            btnCopyTunnel.Click += (s, e) =>
            {
                try
                {
                    if (!string.IsNullOrEmpty(_tunnel.LiveTunnelUrl))
                    {
                        Clipboard.SetText(_tunnel.LiveTunnelUrl);
                        MessageBox.Show("Active tunnel URL copied to clipboard:\n" + _tunnel.LiveTunnelUrl, "PrintAway", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    }
                    else
                    {
                        MessageBox.Show("Tunnel is not connected yet.", "PrintAway", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    }
                }
                catch { }
            };

            var btnRestartTunnel = new Button
            {
                Text = "🔄 Reset",
                Size = new Size(56, 23),
                Location = new Point(490, 79),
                BackColor = Color.FromArgb(241, 245, 249),
                ForeColor = Color.FromArgb(15, 23, 42),
                Font = new Font("Segoe UI", 7.5f, FontStyle.Regular),
                FlatStyle = FlatStyle.System,
                Cursor = Cursors.Hand
            };
            btnRestartTunnel.Click += (s, e) =>
            {
                _lblTunnelUrl.Text = "Tunnel: Restarting connection...";
                _lblTunnelUrl.ForeColor = Color.FromArgb(180, 83, 9);
                ThreadPool.QueueUserWorkItem(state => _tunnel.Restart());
            };

            var btnTunnelConfig = new Button
            {
                Text = "⚙️ Config",
                Size = new Size(56, 23),
                Location = new Point(550, 79),
                BackColor = Color.FromArgb(241, 245, 249),
                ForeColor = Color.FromArgb(15, 23, 42),
                Font = new Font("Segoe UI", 7.5f, FontStyle.Regular),
                FlatStyle = FlatStyle.System,
                Cursor = Cursors.Hand
            };
            btnTunnelConfig.Click += (s, e) => ShowTunnelConfigDialog();

            _lblQuotaBadge = new Label
            {
                Text = "📊 Daily Quota: 0 / 10 Free Pages • Pro: ₹149/mo (Unlimited)",
                Font = new Font("Segoe UI", 8f, FontStyle.Bold),
                ForeColor = Color.FromArgb(79, 70, 229),
                Location = new Point(14, 118),
                Size = new Size(470, 24),
                Cursor = Cursors.Hand
            };
            _lblQuotaBadge.Click += (s, e) => LaunchBrowser("http://127.0.0.1:" + _port + "/admin#pro-upgrade");

            var btnUpgradePro = new Button
            {
                Text = "⚡ Pro Upgrade",
                Size = new Size(115, 26),
                Location = new Point(490, 114),
                BackColor = Color.FromArgb(238, 242, 255),
                ForeColor = Color.FromArgb(67, 56, 202),
                Font = new Font("Segoe UI", 8f, FontStyle.Bold),
                FlatStyle = FlatStyle.System,
                Cursor = Cursors.Hand
            };
            btnUpgradePro.Click += (s, e) => LaunchBrowser("http://127.0.0.1:" + _port + "/admin#pro-upgrade");

            grpShop.Controls.Add(_lblShopTitle);
            grpShop.Controls.Add(btnSetShop);
            grpShop.Controls.Add(_lblCustomerLink);
            grpShop.Controls.Add(btnCopyLink);
            grpShop.Controls.Add(_lblTunnelUrl);
            grpShop.Controls.Add(btnCopyTunnel);
            grpShop.Controls.Add(btnRestartTunnel);
            grpShop.Controls.Add(btnTunnelConfig);
            grpShop.Controls.Add(_lblQuotaBadge);
            grpShop.Controls.Add(btnUpgradePro);
            pnlBody.Controls.Add(grpShop);

            // 4. Card: Printer Fleet & Dispatch Control
            var grpPrinter = CreateCardPanel("🖨️ Hardware Printer & Dispatch Mode", 18, y, 620, 128);
            y += 140;

            var lblSelP = new Label
            {
                Text = "Active Physical Printer:",
                Font = new Font("Segoe UI", 8.5f, FontStyle.Bold),
                Location = new Point(14, 25),
                AutoSize = true
            };

            _cmbPrinters = new ComboBox
            {
                DropDownStyle = ComboBoxStyle.DropDownList,
                Location = new Point(160, 22),
                Size = new Size(330, 26)
            };
            _cmbPrinters.SelectedIndexChanged += OnPrinterSelectionChanged;

            _btnPauseResume = new Button
            {
                Text = "⏸ Pause Intake",
                Size = new Size(110, 28),
                Location = new Point(498, 21),
                BackColor = Color.FromArgb(241, 245, 249),
                FlatStyle = FlatStyle.System
            };
            _btnPauseResume.Click += OnPauseResumeClicked;

            _rbPhysical = new RadioButton
            {
                Text = "● Live Physical Printing",
                Checked = true,
                Font = new Font("Segoe UI", 8.5f, FontStyle.Bold),
                ForeColor = Color.FromArgb(21, 128, 61),
                Location = new Point(16, 60),
                AutoSize = true
            };
            _rbPhysical.CheckedChanged += OnDispatchModeChanged;

            _rbSimulation = new RadioButton
            {
                Text = "○ Safe Spooler Simulation (Dry-Run)",
                Checked = false,
                Font = new Font("Segoe UI", 8.5f, FontStyle.Regular),
                ForeColor = Color.FromArgb(180, 83, 9),
                Location = new Point(220, 60),
                AutoSize = true
            };

            var lblPaperNote = new Label
            {
                Text = "Prints via standard Windows GDI+ / PCL6 rasterizer. Auto-routes B&W vs Color queues.",
                Font = new Font("Segoe UI", 8f, FontStyle.Regular),
                ForeColor = Color.FromArgb(100, 116, 139),
                Location = new Point(16, 92),
                AutoSize = true
            };

            grpPrinter.Controls.Add(lblSelP);
            grpPrinter.Controls.Add(_cmbPrinters);
            grpPrinter.Controls.Add(_btnPauseResume);
            grpPrinter.Controls.Add(_rbPhysical);
            grpPrinter.Controls.Add(_rbSimulation);
            grpPrinter.Controls.Add(lblPaperNote);
            pnlBody.Controls.Add(grpPrinter);

            // 5. Card: Real-Time Counter Metrics
            var grpMetrics = CreateCardPanel("📊 Today's Counter Performance (Real-Time)", 18, y, 620, 90);
            y += 102;

            _lblMetricOrders = CreateMetricBox("ORDERS TODAY", "0", 14, 22, 185, grpMetrics);
            _lblMetricSides = CreateMetricBox("PAGES PRINTED", "0", 214, 22, 185, grpMetrics);
            _lblMetricRevenue = CreateMetricBox("TOTAL REVENUE", "₹0.00", 414, 22, 190, grpMetrics);
            pnlBody.Controls.Add(grpMetrics);

            // 6. Quick Action Launchers
            var btnOpenAdmin = new Button
            {
                Text = "⚙️ Open Counter Admin Dashboard (/admin)",
                BackColor = Color.FromArgb(37, 99, 235),
                ForeColor = Color.White,
                Font = new Font("Segoe UI", 9.5f, FontStyle.Bold),
                FlatStyle = FlatStyle.Flat,
                Size = new Size(305, 42),
                Location = new Point(18, y),
                Cursor = Cursors.Hand
            };
            btnOpenAdmin.FlatAppearance.BorderSize = 0;
            btnOpenAdmin.Click += (s, e) => LaunchBrowser("http://127.0.0.1:" + _port + "/admin");

            var btnOpenQr = new Button
            {
                Text = "🌐 Open Customer Storefront (QR Link)",
                BackColor = Color.FromArgb(15, 23, 42),
                ForeColor = Color.White,
                Font = new Font("Segoe UI", 9.5f, FontStyle.Bold),
                FlatStyle = FlatStyle.Flat,
                Size = new Size(305, 42),
                Location = new Point(333, y),
                Cursor = Cursors.Hand
            };
            btnOpenQr.FlatAppearance.BorderSize = 0;
            btnOpenQr.Click += (s, e) => LaunchBrowser("https://printitaway.netlify.app/?shop=" + GetCurrentShopId());

            pnlBody.Controls.Add(btnOpenAdmin);
            pnlBody.Controls.Add(btnOpenQr);
            y += 54;

            // 7. Preferences & Boot Settings
            _chkAutoStart = new CheckBox
            {
                Text = "Start station automatically when Windows boots",
                Location = new Point(22, y),
                AutoSize = true,
                Font = new Font("Segoe UI", 8.5f, FontStyle.Regular)
            };
            _chkAutoStart.CheckedChanged += OnAutoStartToggled;

            _chkMinimizeToTray = new CheckBox
            {
                Text = "Minimize to System Tray (near clock) when closed",
                Checked = false, // Default false so closing actually exits unless user wants tray mode!
                Location = new Point(335, y),
                AutoSize = true,
                Font = new Font("Segoe UI", 8.5f, FontStyle.Regular)
            };

            y += 28;
            var btnCheckUpdates = new Button
            {
                Text = "🔄 Check for Updates",
                Size = new Size(150, 28),
                Location = new Point(488, y),
                BackColor = Color.White,
                FlatStyle = FlatStyle.System
            };
            btnCheckUpdates.Click += OnCheckUpdatesClicked;

            pnlBody.Controls.Add(_chkAutoStart);
            pnlBody.Controls.Add(_chkMinimizeToTray);
            pnlBody.Controls.Add(btnCheckUpdates);

            // 8. System Tray Icon Setup
            SetupSystemTray();

            // 9. Form Close Handler
            this.FormClosing += (s, e) =>
            {
                if (!_reallyExit && _chkMinimizeToTray.Checked)
                {
                    e.Cancel = true;
                    this.Hide();
                    if (_trayIcon != null)
                    {
                        _trayIcon.ShowBalloonTip(1500, "PrintAway Station",
                            "Station is running quietly in the system tray. Double-click to open.", ToolTipIcon.Info);
                    }
                }
                else
                {
                    ShutdownStation();
                }
            };

            // 10. Metrics Auto-Refresh Timer (every 3 seconds)
            _metricsTimer = new System.Windows.Forms.Timer { Interval = 3000 };
            _metricsTimer.Tick += (s, e) => RefreshDashboardMetrics();
            _metricsTimer.Start();
        }

        private Panel CreateCardPanel(string title, int x, int y, int w, int h)
        {
            var pnl = new Panel
            {
                Location = new Point(x, y),
                Size = new Size(w, h),
                BackColor = Color.White,
                BorderStyle = BorderStyle.FixedSingle
            };
            var lblTitle = new Label
            {
                Text = title,
                Font = new Font("Segoe UI", 8.5f, FontStyle.Bold),
                ForeColor = Color.FromArgb(71, 85, 105),
                Location = new Point(10, 5),
                AutoSize = true
            };
            pnl.Controls.Add(lblTitle);
            return pnl;
        }

        private Label CreateMetricBox(string label, string initValue, int x, int y, int w, Panel parent)
        {
            var pnl = new Panel
            {
                Location = new Point(x, y),
                Size = new Size(w, 55),
                BackColor = Color.FromArgb(248, 250, 252),
                BorderStyle = BorderStyle.FixedSingle
            };

            var lblT = new Label
            {
                Text = label,
                Font = new Font("Segoe UI", 7.5f, FontStyle.Bold),
                ForeColor = Color.FromArgb(100, 116, 139),
                Location = new Point(6, 4),
                AutoSize = true
            };

            var lblV = new Label
            {
                Text = initValue,
                Font = new Font("Segoe UI", 12f, FontStyle.Bold),
                ForeColor = Color.FromArgb(15, 23, 42),
                Location = new Point(6, 22),
                AutoSize = true
            };

            pnl.Controls.Add(lblT);
            pnl.Controls.Add(lblV);
            parent.Controls.Add(pnl);
            return lblV;
        }

        private void SetupSystemTray()
        {
            _trayMenu = new ContextMenuStrip();
            _trayMenu.Items.Add("Open Counter Admin Dashboard", null, (s, e) => LaunchBrowser("http://127.0.0.1:" + _port + "/admin"));
            _trayMenu.Items.Add("Open Customer Storefront", null, (s, e) => LaunchBrowser("https://printitaway.netlify.app/?shop=" + GetCurrentShopId()));
            _trayMenu.Items.Add("Pause / Resume Order Intake", null, OnPauseResumeClicked);
            _trayMenu.Items.Add(new ToolStripSeparator());
            _trayMenu.Items.Add("Show Station Window", null, (s, e) => RestoreWindow());
            _trayMenu.Items.Add("Check for Updates...", null, OnCheckUpdatesClicked);
            _trayMenu.Items.Add(new ToolStripSeparator());
            _trayMenu.Items.Add("Exit PrintAway", null, (s, e) =>
            {
                _reallyExit = true;
                this.Close();
            });

            _trayIcon = new NotifyIcon
            {
                Text = "PrintAway Station - Online",
                Icon = SystemIcons.Application,
                ContextMenuStrip = _trayMenu,
                Visible = true
            };
            _trayIcon.DoubleClick += (s, e) => RestoreWindow();
        }

        public void RestoreWindow()
        {
            if (this.IsDisposed) return;
            if (this.InvokeRequired)
            {
                this.BeginInvoke(new Action(RestoreWindow));
                return;
            }
            if (!this.Visible)
            {
                this.Show();
            }
            if (this.WindowState == FormWindowState.Minimized)
            {
                this.WindowState = FormWindowState.Normal;
            }
            this.ShowInTaskbar = true;
            ShowWindow(this.Handle, SW_RESTORE);
            SetForegroundWindow(this.Handle);
            this.BringToFront();
            this.Activate();
        }

        private void LoadInitialData()
        {
            _isInitializing = true;
            try
            {
                var status = _repo.GetShopAndAvailabilityStatus();
                string shopId = status.ContainsKey("shopId") && status["shopId"] != null ? Convert.ToString(status["shopId"]) : "pilot";
                string shopName = status.ContainsKey("shopName") && status["shopName"] != null ? Convert.ToString(status["shopName"]) : "PrintAway Pilot Shop";
                string activeQueue = Convert.ToString(status["activeQueueName"]);
                bool accepting = (bool)status["acceptingOrders"];

                _lblShopTitle.Text = string.Format(CultureInfo.InvariantCulture, "{0} (ID: {1})", shopName, shopId);
                _lblCustomerLink.Text = "Customer QR: https://printitaway.netlify.app/?shop=" + shopId;

                UpdateIntakeButtonState(accepting);

                // Populate printer queues
                var queues = PdfAndPrinterEngine.DiscoverWindowsQueues();
                _cmbPrinters.Items.Clear();
                int selectIdx = 0;
                for (int i = 0; i < queues.Count; i++)
                {
                    var q = queues[i];
                    string prefix = q.IsVirtualExcluded ? "[VIRTUAL] " : "[PHYSICAL] ";
                    _cmbPrinters.Items.Add(prefix + q.QueueName);
                    if (string.Equals(q.QueueName, activeQueue, StringComparison.OrdinalIgnoreCase))
                    {
                        selectIdx = i;
                    }
                }
                if (_cmbPrinters.Items.Count > 0)
                {
                    _cmbPrinters.SelectedIndex = selectIdx;
                }

                // Auto-start check
                string startupShortcut = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Startup), "PrintAwayStation.lnk");
                _chkAutoStart.Checked = File.Exists(startupShortcut);

                string dispatchMode = _repo.GetDispatchMode();
                bool isPhysical = !string.Equals(dispatchMode, "simulation", StringComparison.OrdinalIgnoreCase);
                _rbPhysical.Checked = isPhysical;
                _rbSimulation.Checked = !isPhysical;

                RefreshDashboardMetrics();
            }
            catch (Exception ex)
            {
                MessageBox.Show("Error loading station state: " + ex.Message, "PrintAway", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
            finally
            {
                _isInitializing = false;
            }
        }

        private void RefreshDashboardMetrics()
        {
            try
            {
                var status = _repo.GetShopAndAvailabilityStatus();
                bool isAccepting = (bool)status["acceptingOrders"];
                UpdateIntakeButtonState(isAccepting);

                var dash = _repo.GetAdminDashboardSummary("all", "");
                if (dash != null && dash.ContainsKey("metrics") && dash["metrics"] is Dictionary<string, object>)
                {
                    var dbMetrics = (Dictionary<string, object>)dash["metrics"];
                    _lblMetricOrders.Text = Convert.ToString(dbMetrics["paidAwaitingApproval"]) + " pending / " + Convert.ToString(dbMetrics["collectedOrders"]) + " done";
                    _lblMetricSides.Text = Convert.ToString(dbMetrics["totalPrintedSides"]) + " sides";
                    _lblMetricRevenue.Text = string.Format(CultureInfo.InvariantCulture, "₹{0:0.00}", Convert.ToDouble(dbMetrics["verifiedRevenueInr"]));
                }

                if (_supabaseSync != null && _supabaseSync.CurrentQuota != null)
                {
                    OnQuotaChanged(_supabaseSync.CurrentQuota);
                }
            }
            catch { }
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            if (_supabaseSync != null && _supabaseSync.CurrentQuota != null)
            {
                OnQuotaChanged(_supabaseSync.CurrentQuota);
            }
        }

        private void OnQuotaChanged(QuotaStatus quota)
        {
            if (this.IsDisposed || !this.IsHandleCreated) return;
            try
            {
                if (this.InvokeRequired)
                {
                    this.BeginInvoke(new Action<QuotaStatus>(OnQuotaChanged), quota);
                    return;
                }

                if (_lblQuotaBadge == null || quota == null) return;

                bool isPro = !string.IsNullOrEmpty(quota.Tier) && quota.Tier.StartsWith("pro", StringComparison.OrdinalIgnoreCase);
                if (isPro)
                {
                    _lblQuotaBadge.Text = string.Format(CultureInfo.InvariantCulture, "✨ Pro Tier Active ({0}) • Unlimited Daily Printing", quota.Tier == "pro_annual" ? "₹149/mo Annual" : "₹249/mo Monthly");
                    _lblQuotaBadge.ForeColor = Color.FromArgb(21, 128, 61);
                }
                else
                {
                    if (quota.IsHardBlocked)
                    {
                        _lblQuotaBadge.Text = string.Format(CultureInfo.InvariantCulture, "🚫 Daily Quota Exceeded ({0}/10)! Printing Blocked • Upgrade to Pro", quota.PagesPrintedToday);
                        _lblQuotaBadge.ForeColor = Color.FromArgb(220, 38, 38);
                    }
                    else if (quota.IsQuotaExceeded)
                    {
                        _lblQuotaBadge.Text = string.Format(CultureInfo.InvariantCulture, "⚠️ Daily Quota Reached ({0}/10)! Grace Buffer Active • Upgrade Pro", quota.PagesPrintedToday);
                        _lblQuotaBadge.ForeColor = Color.FromArgb(180, 83, 9);
                    }
                    else
                    {
                        _lblQuotaBadge.Text = string.Format(CultureInfo.InvariantCulture, "📊 Daily Quota: {0} / 10 Free Pages Used • Pro: ₹149/mo (Unlimited)", quota.PagesPrintedToday);
                        _lblQuotaBadge.ForeColor = Color.FromArgb(79, 70, 229);
                    }
                }
            }
            catch { }
        }

        private void OnTunnelStatusMessage(string msg)
        {
            if (this.IsDisposed || !this.IsHandleCreated) return;
            try
            {
                if (this.InvokeRequired)
                {
                    this.BeginInvoke(new Action<string>(OnTunnelStatusMessage), msg);
                    return;
                }
                if (_lblTunnelUrl != null && string.IsNullOrEmpty(_tunnel.LiveTunnelUrl))
                {
                    _lblTunnelUrl.Text = "Tunnel: " + msg;
                    _lblTunnelUrl.ForeColor = Color.FromArgb(180, 83, 9);
                }
            }
            catch { }
        }

        private void OnTunnelCaptured(string url)
        {
            if (this.IsDisposed || !this.IsHandleCreated) return;
            try
            {
                if (this.InvokeRequired)
                {
                    this.BeginInvoke(new Action<string>(OnTunnelCaptured), url);
                    return;
                }
                if (_lblTunnelUrl != null)
                {
                    string provider = !string.IsNullOrEmpty(_tunnel.ActiveProviderName) ? _tunnel.ActiveProviderName : "Active";
                    _lblTunnelUrl.Text = "🟢 Live (" + provider + "): " + url;
                    _lblTunnelUrl.ForeColor = Color.FromArgb(21, 128, 61);
                }
            }
            catch { }
        }

        private void ShowTunnelConfigDialog()
        {
            using (var dlg = new Form())
            {
                dlg.Text = "PrintAway Multi-Provider Tunnel Engine";
                dlg.ClientSize = new Size(520, 310);
                dlg.StartPosition = FormStartPosition.CenterParent;
                dlg.FormBorderStyle = FormBorderStyle.FixedDialog;
                dlg.MaximizeBox = false;
                dlg.MinimizeBox = false;
                dlg.BackColor = Color.FromArgb(248, 250, 252);
                dlg.Font = new Font("Segoe UI", 9f);

                var lblTitle = new Label
                {
                    Text = "Secure Station Tunnel Configuration",
                    Font = new Font("Segoe UI", 11f, FontStyle.Bold),
                    ForeColor = Color.FromArgb(15, 23, 42),
                    Location = new Point(18, 14),
                    AutoSize = true
                };

                var lblDesc = new Label
                {
                    Text = "Auto mode uses Cloudflare HTTP/2 with instant OpenSSH fallback when rate-limited.\nConfigure a Cloudflare Named Token for permanent subdomains and 100% SLA.",
                    Font = new Font("Segoe UI", 8.25f),
                    ForeColor = Color.FromArgb(100, 116, 139),
                    Location = new Point(20, 42),
                    Size = new Size(480, 35)
                };

                var lblMode = new Label { Text = "Tunnel Provider Mode:", Location = new Point(20, 85), AutoSize = true, Font = new Font("Segoe UI", 8.5f, FontStyle.Bold) };
                var cmbMode = new ComboBox { Location = new Point(20, 105), Width = 475, DropDownStyle = ComboBoxStyle.DropDownList };
                cmbMode.Items.Add("Auto: Cloudflare Quick Tunnel + Built-in OpenSSH Fallback (Recommended)");
                cmbMode.Items.Add("Cloudflare Named Tunnel (Requires Token below - Zero Rate Limit)");
                cmbMode.Items.Add("Built-in Windows OpenSSH Only (localhost.run - Zero Config)");
                cmbMode.Items.Add("Custom Static Domain / Reverse Proxy");

                var lblToken = new Label { Text = "Cloudflare Tunnel Token (Optional, from Cloudflare Zero Trust):", Location = new Point(20, 140), AutoSize = true, Font = new Font("Segoe UI", 8.5f, FontStyle.Bold) };
                var txtToken = new TextBox { Location = new Point(20, 160), Width = 475 };

                var lblCustom = new Label { Text = "Custom / Static Domain (Optional, e.g. https://midtown.printaway.live):", Location = new Point(20, 195), AutoSize = true, Font = new Font("Segoe UI", 8.5f, FontStyle.Bold) };
                var txtCustom = new TextBox { Location = new Point(20, 215), Width = 475 };

                // Pre-populate from station.json
                string curToken = "";
                string curCustom = "";
                string curMode = "auto";
                try
                {
                    string cfgPath = Path.Combine(_repoRoot, "config", "station.json");
                    if (!File.Exists(cfgPath)) cfgPath = Path.Combine(_repoRoot, "station.json");
                    if (File.Exists(cfgPath))
                    {
                        var serializer = new System.Web.Script.Serialization.JavaScriptSerializer();
                        var d = serializer.Deserialize<Dictionary<string, object>>(File.ReadAllText(cfgPath));
                        if (d != null)
                        {
                            if (d.ContainsKey("cloudflareTunnelToken") && d["cloudflareTunnelToken"] != null) curToken = Convert.ToString(d["cloudflareTunnelToken"]);
                            if (d.ContainsKey("customTunnelUrl") && d["customTunnelUrl"] != null) curCustom = Convert.ToString(d["customTunnelUrl"]);
                            if (d.ContainsKey("tunnelProvider") && d["tunnelProvider"] != null) curMode = Convert.ToString(d["tunnelProvider"]).ToLowerInvariant();
                        }
                    }
                }
                catch { }

                txtToken.Text = curToken;
                txtCustom.Text = curCustom;
                if (curMode == "cloudflare") cmbMode.SelectedIndex = 1;
                else if (curMode == "openssh") cmbMode.SelectedIndex = 2;
                else if (curMode == "custom") cmbMode.SelectedIndex = 3;
                else cmbMode.SelectedIndex = 0;

                var btnSave = new Button
                {
                    Text = "💾 Save & Reconnect",
                    Location = new Point(335, 260),
                    Size = new Size(160, 32),
                    BackColor = Color.FromArgb(79, 70, 229),
                    ForeColor = Color.White,
                    FlatStyle = FlatStyle.System,
                    Cursor = Cursors.Hand
                };

                var btnCancel = new Button
                {
                    Text = "Cancel",
                    Location = new Point(235, 260),
                    Size = new Size(90, 32),
                    FlatStyle = FlatStyle.System
                };
                btnCancel.Click += (s, e) => dlg.Close();

                btnSave.Click += (s, e) =>
                {
                    string selectedMode = "auto";
                    if (cmbMode.SelectedIndex == 1) selectedMode = "cloudflare";
                    else if (cmbMode.SelectedIndex == 2) selectedMode = "openssh";
                    else if (cmbMode.SelectedIndex == 3) selectedMode = "custom";

                    UpdateStationJson("tunnelProvider", selectedMode);
                    UpdateStationJson("cloudflareTunnelToken", txtToken.Text.Trim());
                    UpdateStationJson("customTunnelUrl", txtCustom.Text.Trim());

                    dlg.Close();
                    _lblTunnelUrl.Text = "Tunnel: Reconnecting with new configuration...";
                    _lblTunnelUrl.ForeColor = Color.FromArgb(180, 83, 9);
                    ThreadPool.QueueUserWorkItem(state => _tunnel.Restart());
                };

                dlg.Controls.Add(lblTitle);
                dlg.Controls.Add(lblDesc);
                dlg.Controls.Add(lblMode);
                dlg.Controls.Add(cmbMode);
                dlg.Controls.Add(lblToken);
                dlg.Controls.Add(txtToken);
                dlg.Controls.Add(lblCustom);
                dlg.Controls.Add(txtCustom);
                dlg.Controls.Add(btnSave);
                dlg.Controls.Add(btnCancel);

                dlg.ShowDialog(this);
            }
        }

        private void OnPrinterSelectionChanged(object sender, EventArgs e)
        {
            if (_isInitializing || _cmbPrinters.SelectedItem == null) return;
            string selectedText = _cmbPrinters.SelectedItem.ToString();
            string cleanQueue = selectedText.Replace("[PHYSICAL] ", "").Replace("[VIRTUAL] ", "").Trim();

            try
            {
                _repo.UpdateShopAndPrinterControls(null, null, null, null, "gui-selector", null, null, null, null, cleanQueue);
                UpdateStationJson("activePrinterQueue", cleanQueue);
            }
            catch (Exception ex)
            {
                MessageBox.Show("Could not switch printer: " + ex.Message, "PrintAway", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void OnDispatchModeChanged(object sender, EventArgs e)
        {
            if (_isInitializing) return;
            bool isPhysical = _rbPhysical.Checked;
            try
            {
                _repo.SetDispatchMode(isPhysical ? "physical" : "simulation", "gui-toggle");
            }
            catch { }
        }

        private void OnPauseResumeClicked(object sender, EventArgs e)
        {
            try
            {
                var status = _repo.GetShopAndAvailabilityStatus();
                bool currentlyAccepting = (bool)status["acceptingOrders"];
                bool newAccepting = !currentlyAccepting;
                _repo.UpdateShopAndPrinterControls(newAccepting, newAccepting ? "" : "Paused by Counter Staff", null, null, "gui-pause-btn");
                UpdateIntakeButtonState(newAccepting);
            }
            catch (Exception ex)
            {
                MessageBox.Show("Could not toggle intake: " + ex.Message, "PrintAway", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        private void UpdateIntakeButtonState(bool accepting)
        {
            if (accepting)
            {
                _lblStatusBadge.Text = "● ONLINE";
                _lblStatusBadge.BackColor = Color.FromArgb(21, 128, 61);
                _btnPauseResume.Text = "⏸ Pause Intake";
                if (_trayIcon != null) _trayIcon.Text = "PrintAway Station - Online";
            }
            else
            {
                _lblStatusBadge.Text = "⏸ PAUSED";
                _lblStatusBadge.BackColor = Color.FromArgb(180, 83, 9);
                _btnPauseResume.Text = "▶ Resume Intake";
                if (_trayIcon != null) _trayIcon.Text = "PrintAway Station - Paused";
            }
        }

        private void OnAutoStartToggled(object sender, EventArgs e)
        {
            if (_isInitializing) return;
            string startupDir = Environment.GetFolderPath(Environment.SpecialFolder.Startup);
            string shortcutPath = Path.Combine(startupDir, "PrintAwayStation.lnk");
            string currentExe = Process.GetCurrentProcess().MainModule.FileName;

            try
            {
                if (_chkAutoStart.Checked)
                {
                    CreateWindowsShortcut(shortcutPath, currentExe);
                }
                else
                {
                    if (File.Exists(shortcutPath)) File.Delete(shortcutPath);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show("Could not update Windows startup shortcut: " + ex.Message, "PrintAway", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        private void CreateWindowsShortcut(string shortcutPath, string targetExe)
        {
            string vbsScript = "Set oWS = WScript.CreateObject(\"WScript.Shell\")\r\n" +
                               "sLinkFile = \"" + shortcutPath.Replace("\\", "\\\\") + "\"\r\n" +
                               "Set oLink = oWS.CreateShortcut(sLinkFile)\r\n" +
                               "oLink.TargetPath = \"" + targetExe.Replace("\\", "\\\\") + "\"\r\n" +
                               "oLink.WorkingDirectory = \"" + Path.GetDirectoryName(targetExe).Replace("\\", "\\\\") + "\"\r\n" +
                               "oLink.WindowStyle = 1\r\n" +
                               "oLink.Description = \"PrintAway Hardware Station\"\r\n" +
                               "oLink.Save\r\n";
            string tempVbs = Path.Combine(Path.GetTempPath(), "create_shortcut.vbs");
            File.WriteAllText(tempVbs, vbsScript);
            Process.Start("cscript.exe", "//nologo \"" + tempVbs + "\"").WaitForExit();
            File.Delete(tempVbs);
        }

        private void OnCheckUpdatesClicked(object sender, EventArgs e)
        {
            string latestVer, dlUrl, notes;
            bool updateAvailable = AutoUpdater.CheckForUpdate(out latestVer, out dlUrl, out notes);
            if (updateAvailable)
            {
                var dr = MessageBox.Show(
                    string.Format(CultureInfo.InvariantCulture,
                        "A new version of PrintAway Station is available!\n\nCurrent Version: v{0}\nLatest Version: v{1}\n\nNotes: {2}\n\nDo you want to update automatically now?",
                        AutoUpdater.CurrentVersion, latestVer, notes ?? "Performance and stability updates."),
                    "PrintAway Update Available",
                    MessageBoxButtons.YesNo,
                    MessageBoxIcon.Information);

                if (dr == DialogResult.Yes)
                {
                    string err;
                    bool ok = AutoUpdater.ApplyUpdate(dlUrl, out err);
                    if (!ok)
                    {
                        MessageBox.Show("Update failed: " + err, "PrintAway", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    }
                }
            }
            else
            {
                MessageBox.Show("Your PrintAway Station is up to date! (v" + AutoUpdater.CurrentVersion + ")", "PrintAway", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
        }

        private void LaunchBrowser(string url)
        {
            try
            {
                Process.Start(url);
            }
            catch
            {
                Process.Start("cmd.exe", "/c start " + url);
            }
        }

        private string GetCurrentShopId()
        {
            try
            {
                var status = _repo.GetShopAndAvailabilityStatus();
                if (status != null && status.ContainsKey("shopId") && status["shopId"] != null)
                {
                    return Convert.ToString(status["shopId"]);
                }
            }
            catch { }
            return "pilot";
        }

        private void OnSetShopClicked()
        {
            using (var ofd = new OpenFileDialog())
            {
                ofd.Title = "Select Franchise Station Configuration (e.g. station-*.json)";
                ofd.Filter = "Station Config (*.json)|*.json|All Files (*.*)|*.*";
                string cfgDir = Path.Combine(_repoRoot, "config");
                ofd.InitialDirectory = Directory.Exists(cfgDir) ? cfgDir : _repoRoot;

                if (ofd.ShowDialog() == DialogResult.OK)
                {
                    try
                    {
                        string text = File.ReadAllText(ofd.FileName);
                        var json = new JavaScriptSerializer();
                        var cfg = json.Deserialize<Dictionary<string, object>>(text);
                        if (cfg != null && cfg.ContainsKey("shopId"))
                        {
                            string newShopId = Convert.ToString(cfg["shopId"]).Trim();
                            string newShopName = cfg.ContainsKey("shopName") && cfg["shopName"] != null
                                ? Convert.ToString(cfg["shopName"]).Trim()
                                : newShopId;

                            // 1. Save / overwrite config/station.json
                            Directory.CreateDirectory(cfgDir);
                            string destCfg = Path.Combine(cfgDir, "station.json");
                            File.WriteAllText(destCfg, text);

                            // 2. Update local SQLite DB
                            _repo.UpdateShopAndPrinterControls(null, null, null, null, "gui-set-shop", null, null, null, null, null, null, null, newShopId, newShopName);

                            // 3. Update UI Labels immediately
                            _lblShopTitle.Text = string.Format(CultureInfo.InvariantCulture, "{0} (ID: {1})", newShopName, newShopId);
                            _lblCustomerLink.Text = "Customer QR: https://printitaway.netlify.app/?shop=" + newShopId;

                            // 4. Trigger cloud telemetry sync with new shop identity
                            if (_supabaseSync != null)
                            {
                                _supabaseSync.TriggerSyncNow();
                            }

                            MessageBox.Show(
                                string.Format(CultureInfo.InvariantCulture,
                                    "Station successfully updated!\n\nFranchise: {0}\nShop ID: {1}\n\nLive Customer QR Link:\nhttps://printitaway.netlify.app/?shop={1}",
                                    newShopName, newShopId),
                                "PrintAway Station Configured",
                                MessageBoxButtons.OK,
                                MessageBoxIcon.Information);
                            return;
                        }
                        else
                        {
                            MessageBox.Show("Selected JSON file does not contain a valid 'shopId' field.", "Invalid Config", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                        }
                    }
                    catch (Exception ex)
                    {
                        MessageBox.Show("Could not load configuration file: " + ex.Message, "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    }
                }
            }
        }

        private void UpdateStationJson(string key, object val)
        {
            try
            {
                string cfgPath = Path.Combine(_repoRoot, "config", "station.json");
                if (File.Exists(cfgPath))
                {
                    string text = File.ReadAllText(cfgPath);
                    string pattern = "\"" + key + "\"\\s*:\\s*\"[^\"]*\"";
                    string replacement = "\"" + key + "\": \"" + val + "\"";
                    text = Regex.Replace(text, pattern, replacement);
                    File.WriteAllText(cfgPath, text);
                }
            }
            catch { }
        }

        private void ShutdownStation()
        {
            if (_metricsTimer != null) { _metricsTimer.Stop(); _metricsTimer.Dispose(); }
            if (_trayIcon != null) { _trayIcon.Visible = false; _trayIcon.Dispose(); }
            if (_tunnel != null) { _tunnel.Dispose(); }
            if (_worker != null) { _worker.Stop(); }
            if (_server != null) { _server.Dispose(); }
        }
    }
}
