using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Net;
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
        private TunnelManager _tunnel;
        private readonly string _repoRoot;
        private readonly int _port;
        private readonly SupabaseSyncClient _supabaseSync;

        // UI Controls
        private Label _lblStatusBadge;
        private Label _lblShopTitle;
        private Label _lblCustomerLink;
        private Label _lblTunnelUrl;
        private Label _lblQuotaBadge;
        private Button _btnUpgradePro;
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

            if (_server != null)
            {
                _server.OnTunnelUrlRegistered += url =>
                {
                    if (this.IsDisposed || !this.IsHandleCreated) return;
                    try
                    {
                        this.BeginInvoke((MethodInvoker)(() =>
                        {
                            if (_lblTunnelUrl != null && TunnelManager.IsValidPublicTunnelUrl(url))
                            {
                                string provider = (_tunnel != null && !string.IsNullOrEmpty(_tunnel.ActiveProviderName)) ? _tunnel.ActiveProviderName : "Tunnel";
                                _lblTunnelUrl.Text = "🟢 Live (" + provider + "): " + url;
                                _lblTunnelUrl.ForeColor = Color.FromArgb(21, 128, 61);
                            }
                        }));
                    }
                    catch { }
                };
            }

            if (_tunnel != null)
            {
                _tunnel.OnTunnelUrlCaptured += OnTunnelCaptured;
                _tunnel.OnStatusMessage += OnTunnelStatusMessage;
            }

            if (_supabaseSync != null)
            {
                _supabaseSync.OnQuotaStatusChanged += OnQuotaChanged;
            }

            this.Shown += (s, e) =>
            {
                CheckFirstTimeOnboarding();
                CheckForUpdatesAsync(silentWhenUpToDate: true);
            };
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
            Icon appIcon = null;
            try
            {
                string iconPath = Path.Combine(_repoRoot, "app.ico");
                if (File.Exists(iconPath))
                {
                    appIcon = new Icon(iconPath);
                }
                else
                {
                    appIcon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
                }
            }
            catch { }
            this.Icon = appIcon ?? SystemIcons.Application;
            this.BackColor = Color.FromArgb(248, 250, 252);
            this.Font = new Font("Segoe UI", 9f, FontStyle.Regular);

            // 1. Top Header Bar (Fixed Top)
            var pnlHeader = new Panel
            {
                Dock = DockStyle.Top,
                Height = 65,
                BackColor = Color.FromArgb(15, 23, 42)
            };

            string logoPath = Path.Combine(_repoRoot, "web", "assets", "logo.png");
            if (!File.Exists(logoPath)) logoPath = Path.Combine(_repoRoot, "web", "assets", "logo.jpg");

            int textOffsetX = 18;
            if (File.Exists(logoPath))
            {
                try
                {
                    var picLogo = new PictureBox
                    {
                        Image = Image.FromFile(logoPath),
                        SizeMode = PictureBoxSizeMode.Zoom,
                        Size = new Size(42, 42),
                        Location = new Point(16, 11)
                    };
                    pnlHeader.Controls.Add(picLogo);
                    textOffsetX = 66;
                }
                catch { }
            }

            var lblHeaderTitle = new Label
            {
                Text = "PrintAway Station",
                ForeColor = Color.White,
                Font = new Font("Segoe UI", 13f, FontStyle.Bold),
                Location = new Point(textOffsetX, 11),
                AutoSize = true
            };

            var lblHeaderSub = new Label
            {
                Text = "Automated Hardware Printing & Secure Tunnel Hub",
                ForeColor = Color.FromArgb(148, 163, 184),
                Font = new Font("Segoe UI", 8.5f, FontStyle.Regular),
                Location = new Point(textOffsetX + 2, 36),
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
                Size = new Size(325, 22)
            };

            var btnEditShop = new Button
            {
                Text = "✏️ Shop & Password",
                Size = new Size(138, 26),
                Location = new Point(345, 10),
                BackColor = Color.FromArgb(238, 242, 255),
                ForeColor = Color.FromArgb(67, 56, 202),
                Font = new Font("Segoe UI", 8.2f, FontStyle.Bold),
                FlatStyle = FlatStyle.System,
                Cursor = Cursors.Hand
            };
            btnEditShop.Click += (s, e) => ShowEditShopAndAdminPasswordDialog(false);

            var btnSetShop = new Button
            {
                Text = "⚙️ Import JSON",
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
                    string liveTunnel = (_tunnel != null && !string.IsNullOrEmpty(_tunnel.LiveTunnelUrl))
                        ? _tunnel.LiveTunnelUrl
                        : _repo.GetLiveTunnelUrl();

                    if (!string.IsNullOrEmpty(liveTunnel))
                    {
                        Clipboard.SetText(liveTunnel);
                        MessageBox.Show("Active tunnel URL copied to clipboard:\n" + liveTunnel, "PrintAway", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    }
                    else
                    {
                        MessageBox.Show("Tunnel is managed externally or not connected yet.", "PrintAway", MessageBoxButtons.OK, MessageBoxIcon.Information);
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
                if (_tunnel != null)
                {
                    ThreadPool.QueueUserWorkItem(state => _tunnel.Restart());
                }
                else
                {
                    ThreadPool.QueueUserWorkItem(state =>
                    {
                        try
                        {
                            foreach (var p in Process.GetProcessesByName("cloudflared"))
                            {
                                try { p.Kill(); } catch { }
                            }
                            string sId = GetCurrentShopId();
                            var status = _repo.GetShopAndAvailabilityStatus();
                            string sName = status.ContainsKey("shopName") && status["shopName"] != null ? Convert.ToString(status["shopName"]) : sId;
                            _tunnel = new TunnelManager(_repoRoot, _port, sId, sName);
                            _tunnel.OnTunnelUrlCaptured += OnTunnelCaptured;
                            _tunnel.OnStatusMessage += OnTunnelStatusMessage;
                            _tunnel.Start();
                        }
                        catch { }
                    });
                }
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

            _btnUpgradePro = new Button
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
            _btnUpgradePro.Click += (s, e) => LaunchBrowser("http://127.0.0.1:" + _port + "/admin#pro-upgrade");

            grpShop.Controls.Add(_lblShopTitle);
            grpShop.Controls.Add(btnEditShop);
            grpShop.Controls.Add(btnSetShop);
            grpShop.Controls.Add(_lblCustomerLink);
            grpShop.Controls.Add(btnCopyLink);
            grpShop.Controls.Add(_lblTunnelUrl);
            grpShop.Controls.Add(btnCopyTunnel);
            grpShop.Controls.Add(btnRestartTunnel);
            grpShop.Controls.Add(btnTunnelConfig);
            grpShop.Controls.Add(_lblQuotaBadge);
            grpShop.Controls.Add(_btnUpgradePro);
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
            btnOpenAdmin.Click += (s, e) =>
            {
                string shopId = GetCurrentShopId();
                string adminUrl = "https://printitaway.netlify.app/admin?shop=" + Uri.EscapeDataString(shopId);
                LaunchBrowser(adminUrl);
            };

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
            btnOpenQr.Click += (s, e) =>
            {
                string shopId = GetCurrentShopId();
                string qrUrl = "https://printitaway.netlify.app/?shop=" + Uri.EscapeDataString(shopId);
                LaunchBrowser(qrUrl);
            };

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

            // 10. Metrics Auto-Refresh Timer (every 1 second for instant sync with launcher)
            _metricsTimer = new System.Windows.Forms.Timer { Interval = 1000 };
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
                Icon = this.Icon ?? SystemIcons.Application,
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
                string shopId = status.ContainsKey("shopId") && status["shopId"] != null ? Convert.ToString(status["shopId"]) : "midtownstationary";
                string shopName = status.ContainsKey("shopName") && status["shopName"] != null ? Convert.ToString(status["shopName"]) : "Midtown Stationary";
                string activeQueue = Convert.ToString(status["activeQueueName"]);
                bool accepting = (bool)status["acceptingOrders"];

                _lblShopTitle.Text = string.Format(CultureInfo.InvariantCulture, "{0} (ID: {1})", shopName, shopId);
                _lblCustomerLink.Text = "Customer QR: https://printitaway.netlify.app/?shop=" + shopId;

                string initialTunnel = (_tunnel != null && !string.IsNullOrEmpty(_tunnel.LiveTunnelUrl))
                    ? _tunnel.LiveTunnelUrl
                    : _repo.GetLiveTunnelUrl();

                if (!string.IsNullOrEmpty(initialTunnel) &&
                    initialTunnel.IndexOf("api.trycloudflare.com", StringComparison.OrdinalIgnoreCase) < 0 &&
                    initialTunnel.IndexOf("pkg.trycloudflare.com", StringComparison.OrdinalIgnoreCase) < 0 &&
                    _lblTunnelUrl != null)
                {
                    string provider = (_tunnel != null && !string.IsNullOrEmpty(_tunnel.ActiveProviderName))
                        ? _tunnel.ActiveProviderName
                        : "Cloudflare Tunnel";
                    _lblTunnelUrl.Text = "🟢 Live (" + provider + "): " + initialTunnel;
                    _lblTunnelUrl.ForeColor = Color.FromArgb(21, 128, 61);
                }
                else if (_lblTunnelUrl != null)
                {
                    _lblTunnelUrl.Text = "Tunnel: Initializing secure tunnel engine...";
                    _lblTunnelUrl.ForeColor = Color.FromArgb(100, 116, 139);
                }

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

                string currentShop = GetCurrentShopId();
                var dash = _repo.GetAdminDashboardSummary("all", "", "today", currentShop);
                if (dash != null && dash.ContainsKey("metrics") && dash["metrics"] is Dictionary<string, object>)
                {
                    var dbMetrics = (Dictionary<string, object>)dash["metrics"];
                    long pending = dbMetrics.ContainsKey("todayPending") ? Convert.ToInt64(dbMetrics["todayPending"]) : 0;
                    long done = dbMetrics.ContainsKey("todayCompleted") ? Convert.ToInt64(dbMetrics["todayCompleted"]) : 0;
                    long sides = dbMetrics.ContainsKey("todayPrintedSides") ? Convert.ToInt64(dbMetrics["todayPrintedSides"]) : 0;
                    double rev = dbMetrics.ContainsKey("todayRevenueInr") ? Convert.ToDouble(dbMetrics["todayRevenueInr"]) : 0.0;

                    _lblMetricOrders.Text = pending + " pending / " + done + " done";
                    _lblMetricSides.Text = sides + " sides";
                    _lblMetricRevenue.Text = string.Format(CultureInfo.InvariantCulture, "₹{0:0.00}", rev);
                }

                // Keep tunnel status synced with external tunnel launcher (start-tunnel.ps1 / cloudflared)
                string liveTunnel = (_tunnel != null && !string.IsNullOrEmpty(_tunnel.LiveTunnelUrl))
                    ? _tunnel.LiveTunnelUrl
                    : _repo.GetLiveTunnelUrl();

                if (!string.IsNullOrEmpty(liveTunnel) &&
                    liveTunnel.IndexOf("api.trycloudflare.com", StringComparison.OrdinalIgnoreCase) < 0 &&
                    liveTunnel.IndexOf("pkg.trycloudflare.com", StringComparison.OrdinalIgnoreCase) < 0 &&
                    _lblTunnelUrl != null)
                {
                    string provider = (_tunnel != null && !string.IsNullOrEmpty(_tunnel.ActiveProviderName))
                        ? _tunnel.ActiveProviderName
                        : "Cloudflare Tunnel";
                    _lblTunnelUrl.Text = "🟢 Live (" + provider + "): " + liveTunnel;
                    _lblTunnelUrl.ForeColor = Color.FromArgb(21, 128, 61);
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
                if (_btnUpgradePro != null)
                {
                    if (isPro)
                    {
                        _btnUpgradePro.Text = "✨ Pro Active";
                        _btnUpgradePro.BackColor = Color.FromArgb(220, 252, 231);
                        _btnUpgradePro.ForeColor = Color.FromArgb(21, 128, 61);
                        _btnUpgradePro.Enabled = false;
                    }
                    else
                    {
                        _btnUpgradePro.Text = "⚡ Pro Upgrade";
                        _btnUpgradePro.BackColor = Color.FromArgb(238, 242, 255);
                        _btnUpgradePro.ForeColor = Color.FromArgb(67, 56, 202);
                        _btnUpgradePro.Enabled = true;
                    }
                }

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
                if (_lblTunnelUrl != null && (_tunnel == null || string.IsNullOrEmpty(_tunnel.LiveTunnelUrl)))
                {
                    _lblTunnelUrl.Text = "⏳ Tunnel: " + msg;
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
                if (string.IsNullOrWhiteSpace(url))
                {
                    if (_lblTunnelUrl != null)
                    {
                        _lblTunnelUrl.Text = "⏳ Tunnel reconnecting automatically...";
                        _lblTunnelUrl.ForeColor = Color.FromArgb(180, 83, 9);
                    }
                    return;
                }
                if (!TunnelManager.IsValidPublicTunnelUrl(url))
                {
                    return;
                }
                if (_lblTunnelUrl != null)
                {
                    string provider = (_tunnel != null && !string.IsNullOrEmpty(_tunnel.ActiveProviderName)) ? _tunnel.ActiveProviderName : "Active";
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
                dlg.Text = "PrintAway Resilient Tunnel Engine (Self-Healing + Permanent Mode)";
                dlg.ClientSize = new Size(540, 355);
                dlg.StartPosition = FormStartPosition.CenterParent;
                dlg.FormBorderStyle = FormBorderStyle.FixedDialog;
                dlg.MaximizeBox = false;
                dlg.MinimizeBox = false;
                dlg.BackColor = Color.FromArgb(248, 250, 252);
                dlg.Font = new Font("Segoe UI", 9f);

                var lblTitle = new Label
                {
                    Text = "Permanent & Self-Healing Tunnel Configuration",
                    Font = new Font("Segoe UI", 11f, FontStyle.Bold),
                    ForeColor = Color.FromArgb(15, 23, 42),
                    Location = new Point(18, 14),
                    AutoSize = true
                };

                var lblDesc = new Label
                {
                    Text = "Auto mode probes every 30s & cascades Cloudflare -> localhost.run -> Pinggy SSH automatically.\nFor heavy print load (100% uptime, fixed URL, zero 429 rate-limits), paste a free Cloudflare Named Token:",
                    Font = new Font("Segoe UI", 8.25f),
                    ForeColor = Color.FromArgb(100, 116, 139),
                    Location = new Point(20, 42),
                    Size = new Size(500, 36)
                };

                var lblMode = new Label { Text = "Tunnel Provider Mode:", Location = new Point(20, 85), AutoSize = true, Font = new Font("Segoe UI", 8.5f, FontStyle.Bold) };
                var cmbMode = new ComboBox { Location = new Point(20, 105), Width = 495, DropDownStyle = ComboBoxStyle.DropDownList };
                cmbMode.Items.Add("Auto Cascade: Cloudflare -> OpenSSH (localhost.run) -> Pinggy SSH (Recommended)");
                cmbMode.Items.Add("Cloudflare Named Tunnel (Requires Token + Domain below - Permanent & Unlimited)");
                cmbMode.Items.Add("Built-in Windows OpenSSH Only (localhost.run - Zero Config)");
                cmbMode.Items.Add("Pinggy SSH Tunnel Only (a.pinggy.io:443 - Zero Config)");
                cmbMode.Items.Add("Custom Static Domain / Reverse Proxy");

                var lblToken = new Label { Text = "Cloudflare Named Tunnel Token (Free from Cloudflare Zero Trust -> Tunnels -> http://127.0.0.1:8080):", Location = new Point(20, 140), AutoSize = true, Font = new Font("Segoe UI", 8.5f, FontStyle.Bold) };
                var txtToken = new TextBox { Location = new Point(20, 160), Width = 495 };

                var lblCustom = new Label { Text = "Public Hostname for Named Tunnel / Custom Proxy (e.g. https://station1.yourdomain.com):", Location = new Point(20, 195), AutoSize = true, Font = new Font("Segoe UI", 8.5f, FontStyle.Bold) };
                var txtCustom = new TextBox { Location = new Point(20, 215), Width = 495 };

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
                else if (curMode == "pinggy") cmbMode.SelectedIndex = 3;
                else if (curMode == "custom") cmbMode.SelectedIndex = 4;
                else cmbMode.SelectedIndex = 0;

                var btnTest = new Button
                {
                    Text = "🧪 Test Active Link",
                    Location = new Point(20, 300),
                    Size = new Size(140, 32),
                    FlatStyle = FlatStyle.System,
                    Cursor = Cursors.Hand
                };
                btnTest.Click += (s, e) =>
                {
                    if (_tunnel == null)
                    {
                        MessageBox.Show("Tunnel manager is not initialized.", "Tunnel Probe", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                        return;
                    }
                    string detail;
                    bool ok = _tunnel.RunManualProbe(out detail);
                    MessageBox.Show(detail, ok ? "Tunnel Healthy" : "Tunnel Check Failed", MessageBoxButtons.OK, ok ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
                };

                var btnSave = new Button
                {
                    Text = "💾 Save & Reconnect",
                    Location = new Point(355, 300),
                    Size = new Size(160, 32),
                    BackColor = Color.FromArgb(79, 70, 229),
                    ForeColor = Color.White,
                    FlatStyle = FlatStyle.System,
                    Cursor = Cursors.Hand
                };

                var btnCancel = new Button
                {
                    Text = "Cancel",
                    Location = new Point(255, 300),
                    Size = new Size(90, 32),
                    FlatStyle = FlatStyle.System
                };
                btnCancel.Click += (s, e) => dlg.Close();

                btnSave.Click += (s, e) =>
                {
                    string selectedMode = "auto";
                    if (cmbMode.SelectedIndex == 1) selectedMode = "cloudflare";
                    else if (cmbMode.SelectedIndex == 2) selectedMode = "openssh";
                    else if (cmbMode.SelectedIndex == 3) selectedMode = "pinggy";
                    else if (cmbMode.SelectedIndex == 4) selectedMode = "custom";

                    UpdateStationJson("tunnelProvider", selectedMode);
                    UpdateStationJson("cloudflareTunnelToken", txtToken.Text.Trim());
                    UpdateStationJson("customTunnelUrl", txtCustom.Text.Trim());

                    dlg.Close();
                    _lblTunnelUrl.Text = "⏳ Tunnel: Reconnecting with new configuration...";
                    _lblTunnelUrl.ForeColor = Color.FromArgb(180, 83, 9);
                    if (_tunnel != null)
                    {
                        ThreadPool.QueueUserWorkItem(state => _tunnel.Restart());
                    }
                };

                dlg.Controls.Add(lblTitle);
                dlg.Controls.Add(lblDesc);
                dlg.Controls.Add(lblMode);
                dlg.Controls.Add(cmbMode);
                dlg.Controls.Add(lblToken);
                dlg.Controls.Add(txtToken);
                dlg.Controls.Add(lblCustom);
                dlg.Controls.Add(txtCustom);
                dlg.Controls.Add(btnTest);
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

        private void CheckForUpdatesAsync(bool silentWhenUpToDate)
        {
            System.Threading.ThreadPool.QueueUserWorkItem(_ =>
            {
                try
                {
                    if (silentWhenUpToDate) System.Threading.Thread.Sleep(3500);
                    string latestVer, dlUrl, notes, checkErr;
                    bool updateAvailable = AutoUpdater.CheckForUpdate(out latestVer, out dlUrl, out notes, out checkErr);
                    if (this.IsDisposed || !this.IsHandleCreated) return;
                    this.BeginInvoke((MethodInvoker)(() => PromptUpdateResult(updateAvailable, latestVer, dlUrl, notes, checkErr, silentWhenUpToDate)));
                }
                catch { }
            });
        }

        private void PromptUpdateResult(bool updateAvailable, string latestVer, string dlUrl, string notes, string checkErr, bool silentWhenUpToDate)
        {
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
            else if (!silentWhenUpToDate)
            {
                if (!string.IsNullOrEmpty(checkErr))
                {
                    MessageBox.Show(
                        "Could not reach the PrintAway update server (" + checkErr + ").\nCurrent Version: v" + AutoUpdater.CurrentVersion,
                        "PrintAway Update Check",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Warning);
                }
                else
                {
                    MessageBox.Show("Your PrintAway Station is up to date! (v" + AutoUpdater.CurrentVersion + ")", "PrintAway", MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
            }
        }

        private void OnCheckUpdatesClicked(object sender, EventArgs e)
        {
            string latestVer, dlUrl, notes, checkErr;
            bool updateAvailable = AutoUpdater.CheckForUpdate(out latestVer, out dlUrl, out notes, out checkErr);
            PromptUpdateResult(updateAvailable, latestVer, dlUrl, notes, checkErr, silentWhenUpToDate: false);
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
            return "midtownstationary";
        }

        private void CheckFirstTimeOnboarding()
        {
            try
            {
                string flagPath = Path.Combine(_repoRoot, "data", "onboarded.flag");
                string curShop = GetCurrentShopId();
                if (!File.Exists(flagPath) && (curShop == "pilot" || string.IsNullOrEmpty(curShop)))
                {
                    this.BeginInvoke(new Action(() =>
                    {
                        ShowEditShopAndAdminPasswordDialog(true);
                    }));
                }
            }
            catch { }
        }

        private void ShowEditShopAndAdminPasswordDialog(bool isFirstRun = false)
        {
            var status = _repo.GetShopAndAvailabilityStatus();
            string curShopName = status != null && status.ContainsKey("shopName") && status["shopName"] != null
                ? Convert.ToString(status["shopName"]) : "PrintAway Pilot Shop";
            string curShopId = GetCurrentShopId();
            string curActiveQueue = status != null && status.ContainsKey("activeQueueName") && status["activeQueueName"] != null
                ? Convert.ToString(status["activeQueueName"]) : "";
            string curBwQueue = status != null && status.ContainsKey("bwQueueName") && status["bwQueueName"] != null
                ? Convert.ToString(status["bwQueueName"]) : "";
            string curColorQueue = status != null && status.ContainsKey("colorQueueName") && status["colorQueueName"] != null
                ? Convert.ToString(status["colorQueueName"]) : "";

            using (var dlg = new Form())
            {
                dlg.Text = isFirstRun ? "Welcome to PrintAway! - First-Time Shop Setup Wizard" : "Shop Identity, Passwords & Printer Setup";
                dlg.ClientSize = new Size(540, isFirstRun ? 610 : 590);
                dlg.StartPosition = FormStartPosition.CenterParent;
                dlg.FormBorderStyle = FormBorderStyle.FixedDialog;
                dlg.MaximizeBox = false;
                dlg.MinimizeBox = false;
                dlg.BackColor = Color.FromArgb(248, 250, 252);
                dlg.Font = new Font("Segoe UI", 9f, FontStyle.Regular);

                // Top Header Panel
                var pnlHdr = new Panel
                {
                    Dock = DockStyle.Top,
                    Height = 65,
                    BackColor = Color.FromArgb(30, 41, 59)
                };
                var lblHdr = new Label
                {
                    Text = isFirstRun ? "🎉 Welcome to PrintAway! 60-Second Setup" : "🏪 Stationery Shop & Hardware Routing",
                    Font = new Font("Segoe UI", 11.5f, FontStyle.Bold),
                    ForeColor = Color.White,
                    Location = new Point(16, 10),
                    Size = new Size(500, 24)
                };
                var lblSub = new Label
                {
                    Text = isFirstRun
                        ? "Configure your store name, admin password, and printer queues to launch your counter kiosk."
                        : "Configure shop branding, unique customer QR ID, hardware queues, and admin sign-in password.",
                    Font = new Font("Segoe UI", 8.2f, FontStyle.Regular),
                    ForeColor = Color.FromArgb(203, 213, 225),
                    Location = new Point(16, 36),
                    Size = new Size(500, 20)
                };
                pnlHdr.Controls.Add(lblHdr);
                pnlHdr.Controls.Add(lblSub);
                dlg.Controls.Add(pnlHdr);

                int dy = 78;

                // 1. Shop Display Name
                var lblName = new Label
                {
                    Text = "Stationery Store Display Name (e.g. Victoria Communications):",
                    Font = new Font("Segoe UI", 8.5f, FontStyle.Bold),
                    Location = new Point(20, dy),
                    Size = new Size(495, 18)
                };
                var txtName = new TextBox
                {
                    Text = curShopName,
                    Location = new Point(20, dy + 20),
                    Size = new Size(495, 24),
                    Font = new Font("Segoe UI", 9.5f)
                };
                dlg.Controls.Add(lblName);
                dlg.Controls.Add(txtName);
                dy += 52;

                // 2. Shop Slug
                var lblSlug = new Label
                {
                    Text = "Shop Identifier / Slug (Appears in customer QR link: printitaway.netlify.app/?shop=...):",
                    Font = new Font("Segoe UI", 8.5f, FontStyle.Bold),
                    Location = new Point(20, dy),
                    Size = new Size(495, 18)
                };
                var txtSlug = new TextBox
                {
                    Text = curShopId,
                    Location = new Point(20, dy + 20),
                    Size = new Size(495, 24),
                    Font = new Font("Segoe UI", 9.5f)
                };
                dlg.Controls.Add(lblSlug);
                dlg.Controls.Add(txtSlug);
                dy += 52;

                // Auto-slugify as shopkeeper types store name
                bool userEditedSlug = false;
                txtSlug.TextChanged += (s, e) =>
                {
                    if (txtSlug.Focused) userEditedSlug = true;
                };
                txtName.TextChanged += (s, e) =>
                {
                    if (!userEditedSlug && (isFirstRun || curShopId == "pilot"))
                    {
                        string autoSlug = Regex.Replace(txtName.Text.Trim().ToLowerInvariant().Replace(" ", "_"), @"[^a-z0-9_-]", "");
                        txtSlug.Text = autoSlug;
                    }
                };

                // 3. Admin Passwords
                var lblPass = new Label
                {
                    Text = isFirstRun ? "Set Counter Admin Password * (Required):" : "Set Counter Admin Password (leave blank to keep current):",
                    Font = new Font("Segoe UI", 8.5f, FontStyle.Bold),
                    Location = new Point(20, dy),
                    Size = new Size(240, 18)
                };
                var txtPass = new TextBox
                {
                    Text = "",
                    Location = new Point(20, dy + 20),
                    Size = new Size(240, 24),
                    Font = new Font("Segoe UI", 9.5f),
                    UseSystemPasswordChar = true
                };

                var lblConfirm = new Label
                {
                    Text = "Confirm Admin Password:",
                    Font = new Font("Segoe UI", 8.5f, FontStyle.Bold),
                    Location = new Point(275, dy),
                    Size = new Size(240, 18)
                };
                var txtConfirm = new TextBox
                {
                    Text = "",
                    Location = new Point(275, dy + 20),
                    Size = new Size(240, 24),
                    Font = new Font("Segoe UI", 9.5f),
                    UseSystemPasswordChar = true
                };

                dlg.Controls.Add(lblPass);
                dlg.Controls.Add(txtPass);
                dlg.Controls.Add(lblConfirm);
                dlg.Controls.Add(txtConfirm);
                dy += 48;

                var chkShow = new CheckBox
                {
                    Text = "Show Passwords",
                    Location = new Point(22, dy),
                    Size = new Size(130, 20),
                    Font = new Font("Segoe UI", 8f)
                };
                chkShow.CheckedChanged += (s, e) =>
                {
                    txtPass.UseSystemPasswordChar = !chkShow.Checked;
                    txtConfirm.UseSystemPasswordChar = !chkShow.Checked;
                };
                dlg.Controls.Add(chkShow);
                dy += 26;

                // 4. Discover & Select Windows Printer Queues
                var grpPrinters = new GroupBox
                {
                    Text = "🖨️ Windows Printer Queues Selection",
                    Font = new Font("Segoe UI", 8.5f, FontStyle.Bold),
                    Location = new Point(20, dy),
                    Size = new Size(495, 128),
                    BackColor = Color.White
                };

                var lblPri = new Label { Text = "Primary Printer:", Location = new Point(14, 25), Size = new Size(130, 20), Font = new Font("Segoe UI", 8.5f, FontStyle.Regular) };
                var cmbPri = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Location = new Point(150, 22), Size = new Size(330, 24), Font = new Font("Segoe UI", 8.5f) };

                var lblBw = new Label { Text = "Dedicated B&W Printer:", Location = new Point(14, 58), Size = new Size(130, 20), Font = new Font("Segoe UI", 8.5f, FontStyle.Regular) };
                var cmbBw = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Location = new Point(150, 55), Size = new Size(330, 24), Font = new Font("Segoe UI", 8.5f) };

                var lblColor = new Label { Text = "Dedicated Color Printer:", Location = new Point(14, 91), Size = new Size(130, 20), Font = new Font("Segoe UI", 8.5f, FontStyle.Regular) };
                var cmbColor = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Location = new Point(150, 88), Size = new Size(330, 24), Font = new Font("Segoe UI", 8.5f) };

                // Populate with system queues
                var queues = PdfAndPrinterEngine.DiscoverWindowsQueues();
                cmbBw.Items.Add("(Same as Primary Printer)");
                cmbColor.Items.Add("(Same as Primary Printer)");

                int selPriIdx = 0, selBwIdx = 0, selColorIdx = 0;
                for (int i = 0; i < queues.Count; i++)
                {
                    var q = queues[i];
                    string itemLabel = (q.IsVirtualExcluded ? "[VIRTUAL] " : "[PHYSICAL] ") + q.QueueName;
                    cmbPri.Items.Add(itemLabel);
                    cmbBw.Items.Add(itemLabel);
                    cmbColor.Items.Add(itemLabel);

                    if (string.Equals(q.QueueName, curActiveQueue, StringComparison.OrdinalIgnoreCase))
                    {
                        selPriIdx = i;
                    }
                    if (!string.IsNullOrEmpty(curBwQueue) && string.Equals(q.QueueName, curBwQueue, StringComparison.OrdinalIgnoreCase))
                    {
                        selBwIdx = i + 1;
                    }
                    if (!string.IsNullOrEmpty(curColorQueue) && string.Equals(q.QueueName, curColorQueue, StringComparison.OrdinalIgnoreCase))
                    {
                        selColorIdx = i + 1;
                    }
                }
                if (cmbPri.Items.Count > 0) cmbPri.SelectedIndex = selPriIdx;
                cmbBw.SelectedIndex = selBwIdx;
                cmbColor.SelectedIndex = selColorIdx;

                grpPrinters.Controls.Add(lblPri);
                grpPrinters.Controls.Add(cmbPri);
                grpPrinters.Controls.Add(lblBw);
                grpPrinters.Controls.Add(cmbBw);
                grpPrinters.Controls.Add(lblColor);
                grpPrinters.Controls.Add(cmbColor);
                dlg.Controls.Add(grpPrinters);
                dy += 136;

                // 5. Auto-start on boot checkbox
                var chkAuto = new CheckBox
                {
                    Text = "Start station automatically when Windows boots (Recommended for shop counter PC)",
                    Location = new Point(22, dy),
                    Size = new Size(490, 22),
                    Checked = true,
                    Font = new Font("Segoe UI", 8.5f, FontStyle.Regular)
                };
                dlg.Controls.Add(chkAuto);
                dy += 34;

                // 6. Action buttons
                var btnSave = new Button
                {
                    Text = isFirstRun ? "🚀 Finish Setup & Launch Station" : "💾 Save & Sync Station",
                    Location = new Point(140, dy),
                    Size = new Size(230, 36),
                    BackColor = Color.FromArgb(37, 99, 235),
                    ForeColor = Color.White,
                    Font = new Font("Segoe UI", 9.5f, FontStyle.Bold),
                    FlatStyle = FlatStyle.System,
                    Cursor = Cursors.Hand
                };

                var btnCancel = new Button
                {
                    Text = isFirstRun ? "Set Up Later" : "Cancel",
                    Location = new Point(380, dy),
                    Size = new Size(135, 36),
                    BackColor = Color.White,
                    FlatStyle = FlatStyle.System,
                    Cursor = Cursors.Hand
                };
                btnCancel.Click += (s, e) => dlg.Close();

                btnSave.Click += (s, e) =>
                {
                    string newName = txtName.Text.Trim();
                    string rawSlug = txtSlug.Text.Trim().ToLowerInvariant();
                    string newSlug = Regex.Replace(rawSlug, @"[^a-z0-9_-]", "");
                    string newPassword = txtPass.Text.Trim();
                    string confirmPassword = txtConfirm.Text.Trim();

                    if (string.IsNullOrEmpty(newName))
                    {
                        MessageBox.Show("Please enter a valid store name.", "Store Name Required", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                        txtName.Focus();
                        return;
                    }
                    if (string.IsNullOrEmpty(newSlug))
                    {
                        MessageBox.Show("Please enter a valid shop identifier / slug.", "Shop Slug Required", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                        txtSlug.Focus();
                        return;
                    }

                    if (isFirstRun && string.IsNullOrEmpty(newPassword))
                    {
                        MessageBox.Show("Please set a secure Admin Password for your shop counter panel.", "Password Required", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                        txtPass.Focus();
                        return;
                    }

                    bool passwordChanged = false;
                    if (!string.IsNullOrEmpty(newPassword))
                    {
                        if (newPassword.Length < 6)
                        {
                            MessageBox.Show("Password must be at least 6 characters long.", "Password Too Short", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                            txtPass.Focus();
                            return;
                        }
                        if (newPassword != confirmPassword)
                        {
                            MessageBox.Show("Passwords do not match. Please re-enter to confirm.", "Password Mismatch", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                            txtConfirm.Focus();
                            return;
                        }

                        string pErr;
                        if (!_repo.SetAdminPasswordDirect("admin", newPassword, out pErr))
                        {
                            MessageBox.Show("Failed to save password: " + pErr, "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                            return;
                        }
                        if (newSlug != "admin" && newSlug != "pilot")
                        {
                            _repo.SetAdminPasswordDirect(newSlug, newPassword, out pErr);
                        }
                        passwordChanged = true;
                    }

                    // Extract selected queues
                    string selPrimary = cmbPri.SelectedItem != null
                        ? cmbPri.SelectedItem.ToString().Replace("[PHYSICAL] ", "").Replace("[VIRTUAL] ", "").Trim() : "";
                    string selBw = (cmbBw.SelectedIndex > 0 && cmbBw.SelectedItem != null)
                        ? cmbBw.SelectedItem.ToString().Replace("[PHYSICAL] ", "").Replace("[VIRTUAL] ", "").Trim() : selPrimary;
                    string selColor = (cmbColor.SelectedIndex > 0 && cmbColor.SelectedItem != null)
                        ? cmbColor.SelectedItem.ToString().Replace("[PHYSICAL] ", "").Replace("[VIRTUAL] ", "").Trim() : selPrimary;

                    // 1. Update config/station.json
                    UpdateStationJson("shopName", newName);
                    UpdateStationJson("shopId", newSlug);
                    UpdateStationJson("stationRegistryTopic", "printaway_station_registry_" + newSlug + "_9f8a7b");
                    if (!string.IsNullOrEmpty(selPrimary)) UpdateStationJson("activePrinterQueue", selPrimary);
                    if (!string.IsNullOrEmpty(selBw)) UpdateStationJson("bwPrinterQueue", selBw);
                    if (!string.IsNullOrEmpty(selColor)) UpdateStationJson("colorPrinterQueue", selColor);

                    // 2. Update local SQLite database
                    _repo.UpdateShopAndPrinterControls(
                        intakePaused: null,
                        pauseReason: null,
                        supportsDuplex: null,
                        printerReadyOverride: null,
                        adminUsername: "gui-onboard",
                        supportsColor: null,
                        supportsPageRange: null,
                        allowedPaperSizes: null,
                        maxCopies: null,
                        activeQueueName: !string.IsNullOrEmpty(selPrimary) ? selPrimary : null,
                        bwQueueName: selBw,
                        colorQueueName: selColor,
                        shopSlug: newSlug,
                        shopName: newName
                    );

                    // 3. Update Windows Startup autostart shortcut
                    try
                    {
                        string startupDir = Environment.GetFolderPath(Environment.SpecialFolder.Startup);
                        string shortcutPath = Path.Combine(startupDir, "PrintAwayStation.lnk");
                        string currentExe = Process.GetCurrentProcess().MainModule.FileName;
                        if (chkAuto.Checked)
                        {
                            CreateWindowsShortcut(shortcutPath, currentExe);
                            if (_chkAutoStart != null) _chkAutoStart.Checked = true;
                        }
                        else
                        {
                            if (File.Exists(shortcutPath)) File.Delete(shortcutPath);
                            if (_chkAutoStart != null) _chkAutoStart.Checked = false;
                        }
                    }
                    catch { }

                    // 4. Write data/onboarded.flag
                    try
                    {
                        string dataDir = Path.Combine(_repoRoot, "data");
                        Directory.CreateDirectory(dataDir);
                        File.WriteAllText(Path.Combine(dataDir, "onboarded.flag"), "Onboarded: " + DateTime.UtcNow.ToString("o") + "\nShop: " + newSlug + "\nName: " + newName);
                    }
                    catch { }

                    // 5. Reload GUI fields & metrics
                    LoadInitialData();

                    // 6. Broadcast live tunnel to new shop's ntfy topic & sync Supabase
                    string activeTunnel = _repo.GetLiveTunnelUrl();
                    if (!string.IsNullOrEmpty(activeTunnel))
                    {
                        ThreadPool.QueueUserWorkItem(_ =>
                        {
                            try
                            {
                                using (var wc = new WebClient())
                                {
                                    wc.UploadString("https://ntfy.sh/printaway_station_registry_" + newSlug + "_9f8a7b", activeTunnel);
                                }
                            }
                            catch { }
                        });
                    }

                    if (_supabaseSync != null)
                    {
                        _supabaseSync.ShopId = newSlug;
                        _supabaseSync.TriggerSyncNow();
                    }

                    string celebrationMsg = string.Format(CultureInfo.InvariantCulture,
                        "🎉 Stationery Shop Successfully Configured!\n\n" +
                        "Store Name: {0}\n" +
                        "Shop ID: {1}\n" +
                        "Primary Printer: {2}\n" +
                        "Admin Password: {3}\n\n" +
                        "Customer QR URL:\nhttps://printitaway.netlify.app/?shop={1}\n\n" +
                        "Local Counter Admin Panel:\nhttp://127.0.0.1:{4}/admin\n\n" +
                        "Shopkeepers can keep this Station window minimized while the shop is open.",
                        newName, newSlug, selPrimary, passwordChanged ? "Configured & Active" : "Unchanged", _port);

                    MessageBox.Show(celebrationMsg, "PrintAway Station Ready", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    dlg.Close();
                };

                dlg.Controls.Add(btnSave);
                dlg.Controls.Add(btnCancel);
                dlg.ShowDialog(this);
            }
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
