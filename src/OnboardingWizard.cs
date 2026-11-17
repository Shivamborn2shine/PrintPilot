using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Threading;
using System.Windows.Forms;
using PrintAway.Config;
using PrintAway.Engine;
using PrintAway.SaaS;

namespace PrintAway.Gui
{
    /// <summary>Creates .lnk files via the WScript.Shell COM object (no VBScript/cscript dependency).</summary>
    public static class WindowsStartup
    {
        public static string StartupShortcutPath
        {
            get { return System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Startup), "PrintAwayStation.lnk"); }
        }

        public static void CreateShortcut(string shortcutPath, string targetExe)
        {
            Type shellType = Type.GetTypeFromProgID("WScript.Shell", true);
            object shell = Activator.CreateInstance(shellType);
            try
            {
                object link = shellType.InvokeMember("CreateShortcut", System.Reflection.BindingFlags.InvokeMethod, null, shell, new object[] { shortcutPath });
                Type linkType = link.GetType();
                linkType.InvokeMember("TargetPath", System.Reflection.BindingFlags.SetProperty, null, link, new object[] { targetExe });
                linkType.InvokeMember("WorkingDirectory", System.Reflection.BindingFlags.SetProperty, null, link, new object[] { System.IO.Path.GetDirectoryName(targetExe) });
                linkType.InvokeMember("Description", System.Reflection.BindingFlags.SetProperty, null, link, new object[] { "PrintAway Station" });
                linkType.InvokeMember("WindowStyle", System.Reflection.BindingFlags.SetProperty, null, link, new object[] { 1 });
                linkType.InvokeMember("Save", System.Reflection.BindingFlags.InvokeMethod, null, link, null);
                System.Runtime.InteropServices.Marshal.FinalReleaseComObject(link);
            }
            finally
            {
                System.Runtime.InteropServices.Marshal.FinalReleaseComObject(shell);
            }
        }
    }

    public sealed class OnboardingResult
    {
        public string ShopName { get; set; }
        public string ShopId { get; set; }
        public string OwnerName { get; set; }
        public string OwnerPhone { get; set; }
        public string UpiVpa { get; set; }
        public string AdminPassword { get; set; }
        public string PrimaryQueue { get; set; }
        public string BwQueue { get; set; }
        public string ColorQueue { get; set; }
        public bool AutoStart { get; set; }
        /// <summary>The ID already existed in the registry and the user confirmed it is their own shop.</summary>
        public bool ClaimedExistingShopId { get; set; }
        /// <summary>The shop row was created in the cloud registry during onboarding.</summary>
        public bool RegisteredOnline { get; set; }
    }

    /// <summary>
    /// First-run setup shown before the station starts any server, tunnel, or cloud sync.
    /// Nothing is published under a shop identity until this wizard completes.
    /// </summary>
    public sealed class OnboardingWizard : Form
    {
        private static readonly Color Ink = Color.FromArgb(15, 23, 42);
        private static readonly Color Muted = Color.FromArgb(100, 116, 139);
        private static readonly Color Accent = Color.FromArgb(37, 99, 235);
        private static readonly Color Danger = Color.FromArgb(185, 28, 28);
        private static readonly Color Success = Color.FromArgb(21, 128, 61);
        private static readonly Color Surface = Color.FromArgb(248, 250, 252);

        private readonly string _storefrontUrl;
        private readonly string _clientVersion;
        private readonly List<DiscoveredPrinterQueue> _queues;

        private readonly Panel[] _steps = new Panel[3];
        private readonly Label[] _stepDots = new Label[3];
        private int _currentStep;

        private Label _lblStepTitle;
        private Label _lblStepHint;
        private Label _lblError;
        private Button _btnBack;
        private Button _btnNext;
        private Button _btnCancel;

        // Step 1
        private TextBox _txtShopName;
        private TextBox _txtShopId;
        private Label _lblLinkPreview;
        private bool _slugEditedByUser;
        private string _verifiedSlug;
        private bool _claimedExisting;
        private bool _slugAvailable;

        // Step 2
        private TextBox _txtOwnerName;
        private TextBox _txtOwnerPhone;
        private TextBox _txtUpi;

        // Step 3
        private TextBox _txtPass;
        private TextBox _txtConfirm;
        private ComboBox _cmbPrimary;
        private ComboBox _cmbBw;
        private ComboBox _cmbColor;
        private CheckBox _chkAutoStart;

        public OnboardingResult Result { get; private set; }

        public OnboardingWizard(string storefrontUrl, string clientVersion)
        {
            _storefrontUrl = string.IsNullOrWhiteSpace(storefrontUrl) ? StationConfig.DefaultPublicStorefrontUrl : storefrontUrl.TrimEnd('/');
            _clientVersion = clientVersion;
            try { _queues = PdfAndPrinterEngine.DiscoverWindowsQueues(); } catch { _queues = new List<DiscoveredPrinterQueue>(); }
            BuildUi();
            DisableMnemonics(this);
            ShowStep(0);
        }

        /// <summary>Render '&amp;' literally (e.g. "Owner &amp; payments") instead of as a keyboard-accelerator marker.</summary>
        private static void DisableMnemonics(Control root)
        {
            foreach (Control c in root.Controls)
            {
                var label = c as Label;
                if (label != null) label.UseMnemonic = false;
                var check = c as ButtonBase;
                if (check != null && !(c is Button)) check.UseMnemonic = false;
                if (c.HasChildren) DisableMnemonics(c);
            }
        }

        // ------------------------------------------------------------------ layout

        private void BuildUi()
        {
            Text = "PrintAway Station - New Shop Setup";
            AutoScaleMode = AutoScaleMode.Font;
            Font = new Font("Segoe UI", 9.5f);
            ClientSize = new Size(580, 560);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = true;
            StartPosition = FormStartPosition.CenterScreen;
            BackColor = Color.White;
            ShowInTaskbar = true;
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }

            var header = new Panel { Dock = DockStyle.Top, Height = 96, BackColor = Ink };
            header.Controls.Add(new Label
            {
                Text = "Welcome to PrintAway",
                Font = new Font("Segoe UI Semibold", 15f),
                ForeColor = Color.White,
                Location = new Point(24, 14),
                AutoSize = true
            });
            header.Controls.Add(new Label
            {
                Text = "Set up your shop in three short steps. Nothing goes online until you finish.",
                Font = new Font("Segoe UI", 9f),
                ForeColor = Color.FromArgb(203, 213, 225),
                Location = new Point(26, 46),
                AutoSize = true
            });
            string[] names = { "1  Shop", "2  Owner & payments", "3  Security & printer" };
            int x = 26;
            for (int i = 0; i < 3; i++)
            {
                _stepDots[i] = new Label
                {
                    Text = names[i],
                    Font = new Font("Segoe UI", 8.5f, FontStyle.Bold),
                    Location = new Point(x, 70),
                    AutoSize = true
                };
                header.Controls.Add(_stepDots[i]);
                x += i == 0 ? 80 : 170;
            }
            Controls.Add(header);

            _lblStepTitle = new Label { Font = new Font("Segoe UI Semibold", 12f), ForeColor = Ink, Location = new Point(24, 110), AutoSize = true };
            _lblStepHint = new Label { Font = new Font("Segoe UI", 9f), ForeColor = Muted, Location = new Point(25, 136), Size = new Size(530, 36) };
            Controls.Add(_lblStepTitle);
            Controls.Add(_lblStepHint);

            for (int i = 0; i < 3; i++)
            {
                _steps[i] = new Panel { Location = new Point(0, 172), Size = new Size(580, 300), Visible = false };
                Controls.Add(_steps[i]);
            }
            BuildStepShop(_steps[0]);
            BuildStepOwner(_steps[1]);
            BuildStepSecurity(_steps[2]);

            _lblError = new Label
            {
                ForeColor = Danger,
                Font = new Font("Segoe UI", 9f),
                Location = new Point(24, 474),
                Size = new Size(532, 34),
                Text = ""
            };
            Controls.Add(_lblError);

            var footer = new Panel { Dock = DockStyle.Bottom, Height = 52, BackColor = Surface };
            _btnCancel = new Button { Text = "Exit", Size = new Size(90, 32), Location = new Point(24, 10), FlatStyle = FlatStyle.System };
            _btnBack = new Button { Text = "Back", Size = new Size(90, 32), Location = new Point(352, 10), FlatStyle = FlatStyle.System };
            _btnNext = new Button { Text = "Next", Size = new Size(114, 32), Location = new Point(448, 10), FlatStyle = FlatStyle.System };
            _btnCancel.Click += (s, e) => { DialogResult = DialogResult.Cancel; Close(); };
            _btnBack.Click += (s, e) => { if (_currentStep > 0) ShowStep(_currentStep - 1); };
            _btnNext.Click += (s, e) => OnNext();
            footer.Controls.Add(_btnCancel);
            footer.Controls.Add(_btnBack);
            footer.Controls.Add(_btnNext);
            Controls.Add(footer);

            AcceptButton = _btnNext;
            CancelButton = _btnCancel;
        }

        private static Label FieldLabel(string text, int x, int y, int width = 530)
        {
            return new Label { Text = text, Font = new Font("Segoe UI", 9f, FontStyle.Bold), ForeColor = Ink, Location = new Point(x, y), Size = new Size(width, 20) };
        }

        private static Label HelpLabel(string text, int x, int y, int width = 530)
        {
            return new Label { Text = text, Font = new Font("Segoe UI", 8.5f), ForeColor = Muted, Location = new Point(x, y), Size = new Size(width, 18) };
        }

        private static TextBox Input(int x, int y, int width = 530)
        {
            return new TextBox { Location = new Point(x, y), Size = new Size(width, 26), Font = new Font("Segoe UI", 10f) };
        }

        private void BuildStepShop(Panel p)
        {
            p.Controls.Add(FieldLabel("Shop name", 24, 4));
            _txtShopName = Input(24, 26);
            _txtShopName.MaxLength = 80;
            p.Controls.Add(_txtShopName);
            p.Controls.Add(HelpLabel("Shown to customers on the order page, e.g. \"Sharma Xerox & Stationery\".", 24, 56));

            p.Controls.Add(FieldLabel("Shop ID", 24, 90));
            _txtShopId = Input(24, 112);
            _txtShopId.MaxLength = 40;
            _txtShopId.CharacterCasing = CharacterCasing.Lower;
            p.Controls.Add(_txtShopId);
            p.Controls.Add(HelpLabel("Unique ID in your customer QR link. 3-40 characters: a-z, 0-9, '-' or '_'.", 24, 142));

            _lblLinkPreview = new Label
            {
                Font = new Font("Consolas", 9f),
                ForeColor = Accent,
                BackColor = Color.FromArgb(239, 246, 255),
                Location = new Point(24, 176),
                Size = new Size(530, 30),
                TextAlign = ContentAlignment.MiddleLeft,
                Padding = new Padding(8, 0, 0, 0)
            };
            p.Controls.Add(_lblLinkPreview);
            p.Controls.Add(HelpLabel("We'll check that this ID is not already used by another shop when you click Next.", 24, 214));

            _txtShopName.TextChanged += (s, e) =>
            {
                if (!_slugEditedByUser) _txtShopId.Text = StationConfig.Slugify(_txtShopName.Text);
            };
            _txtShopId.KeyPress += (s, e) => { _slugEditedByUser = true; };
            _txtShopId.TextChanged += (s, e) =>
            {
                if (_txtShopId.Text.Length == 0) _slugEditedByUser = false;
                _verifiedSlug = null;
                _claimedExisting = false;
                _slugAvailable = false;
                UpdateLinkPreview();
            };
            UpdateLinkPreview();
        }

        private void BuildStepOwner(Panel p)
        {
            p.Controls.Add(FieldLabel("Owner name", 24, 4));
            _txtOwnerName = Input(24, 26);
            _txtOwnerName.MaxLength = 80;
            p.Controls.Add(_txtOwnerName);

            p.Controls.Add(FieldLabel("Owner mobile number", 24, 66));
            _txtOwnerPhone = Input(24, 88, 260);
            _txtOwnerPhone.MaxLength = 16;
            p.Controls.Add(_txtOwnerPhone);
            p.Controls.Add(HelpLabel("10-digit mobile, used for account and billing contact only.", 24, 118));

            p.Controls.Add(FieldLabel("UPI ID for customer payments", 24, 152));
            _txtUpi = Input(24, 174, 360);
            _txtUpi.MaxLength = 100;
            p.Controls.Add(_txtUpi);
            p.Controls.Add(HelpLabel("Customers pay directly to this UPI ID, e.g. yourshop@okaxis. Double-check it.", 24, 204));
        }

        private void BuildStepSecurity(Panel p)
        {
            p.Controls.Add(FieldLabel("Counter admin password", 24, 4, 260));
            p.Controls.Add(FieldLabel("Confirm password", 294, 4, 260));
            _txtPass = Input(24, 26, 256);
            _txtConfirm = Input(294, 26, 260);
            _txtPass.UseSystemPasswordChar = true;
            _txtConfirm.UseSystemPasswordChar = true;
            p.Controls.Add(_txtPass);
            p.Controls.Add(_txtConfirm);
            var chkShow = new CheckBox { Text = "Show", Location = new Point(24, 56), AutoSize = true, Font = new Font("Segoe UI", 8.5f) };
            chkShow.CheckedChanged += (s, e) =>
            {
                _txtPass.UseSystemPasswordChar = !chkShow.Checked;
                _txtConfirm.UseSystemPasswordChar = !chkShow.Checked;
            };
            p.Controls.Add(chkShow);
            p.Controls.Add(HelpLabel("At least 8 characters with letters and numbers. Used to sign in to /admin.", 90, 58, 460));

            p.Controls.Add(FieldLabel("Printer", 24, 92, 100));
            _cmbPrimary = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Location = new Point(150, 90), Size = new Size(404, 26) };
            p.Controls.Add(_cmbPrimary);
            p.Controls.Add(new Label { Text = "B&W printer", Location = new Point(24, 128), AutoSize = true, ForeColor = Ink });
            _cmbBw = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Location = new Point(150, 124), Size = new Size(404, 26) };
            p.Controls.Add(_cmbBw);
            p.Controls.Add(new Label { Text = "Colour printer", Location = new Point(24, 162), AutoSize = true, ForeColor = Ink });
            _cmbColor = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Location = new Point(150, 158), Size = new Size(404, 26) };
            p.Controls.Add(_cmbColor);

            _cmbBw.Items.Add("(Same as main printer)");
            _cmbColor.Items.Add("(Same as main printer)");
            int firstPhysical = -1;
            for (int i = 0; i < _queues.Count; i++)
            {
                var q = _queues[i];
                string label = q.QueueName + (q.IsVirtualExcluded ? "   (virtual - not a real printer)" : "");
                _cmbPrimary.Items.Add(label);
                _cmbBw.Items.Add(label);
                _cmbColor.Items.Add(label);
                if (firstPhysical < 0 && !q.IsVirtualExcluded) firstPhysical = i;
            }
            if (_cmbPrimary.Items.Count > 0) _cmbPrimary.SelectedIndex = firstPhysical >= 0 ? firstPhysical : 0;
            _cmbBw.SelectedIndex = 0;
            _cmbColor.SelectedIndex = 0;

            if (_queues.Count == 0)
            {
                p.Controls.Add(new Label
                {
                    Text = "No printers found. Install your printer in Windows Settings, then restart PrintAway. You can still finish setup and pick a printer later.",
                    ForeColor = Danger,
                    Location = new Point(24, 192),
                    Size = new Size(530, 36)
                });
            }

            _chkAutoStart = new CheckBox
            {
                Text = "Start PrintAway automatically when Windows starts (recommended)",
                Checked = true,
                Location = new Point(24, 236),
                AutoSize = true
            };
            p.Controls.Add(_chkAutoStart);
        }

        private void UpdateLinkPreview()
        {
            string slug = StationConfig.NormalizeSlug(_txtShopId.Text) ?? "your-shop-id";
            _lblLinkPreview.Text = _storefrontUrl + "/?shop=" + slug;
        }

        public void Prefill(string shopName, string shopId, string ownerName, string ownerPhone, string upiVpa, string printerQueue)
        {
            if (!string.IsNullOrWhiteSpace(shopName)) _txtShopName.Text = shopName;
            if (!string.IsNullOrWhiteSpace(shopId))
            {
                _slugEditedByUser = true;
                _txtShopId.Text = shopId;
            }
            if (!string.IsNullOrWhiteSpace(ownerName)) _txtOwnerName.Text = ownerName;
            if (!string.IsNullOrWhiteSpace(ownerPhone)) _txtOwnerPhone.Text = ownerPhone;
            if (!string.IsNullOrWhiteSpace(upiVpa)) _txtUpi.Text = upiVpa;
            if (!string.IsNullOrWhiteSpace(printerQueue))
            {
                for (int i = 0; i < _queues.Count; i++)
                {
                    if (string.Equals(_queues[i].QueueName, printerQueue, StringComparison.OrdinalIgnoreCase))
                    {
                        _cmbPrimary.SelectedIndex = i;
                        break;
                    }
                }
            }
        }

        // ------------------------------------------------------------------ navigation

        private void ShowStep(int index)
        {
            _currentStep = index;
            for (int i = 0; i < 3; i++)
            {
                _steps[i].Visible = i == index;
                _stepDots[i].ForeColor = i == index ? Color.White : (i < index ? Color.FromArgb(134, 239, 172) : Color.FromArgb(100, 116, 139));
            }
            string[] titles = { "Tell us about your shop", "Owner & payment details", "Secure the counter & pick a printer" };
            string[] hints =
            {
                "Your shop name and a unique Shop ID. The ID becomes part of the QR link customers scan.",
                "Used for your PrintAway account and to route customer UPI payments to you.",
                "Staff sign in to the counter dashboard with this password. Choose the printer that should receive jobs."
            };
            _lblStepTitle.Text = titles[index];
            _lblStepHint.Text = hints[index];
            _lblError.Text = "";
            _btnBack.Enabled = index > 0;
            _btnNext.Text = index == 2 ? "Finish setup" : "Next";

            if (index == 0) _txtShopName.Focus();
            else if (index == 1) _txtOwnerName.Focus();
            else _txtPass.Focus();
        }

        private void SetBusy(bool busy, string message)
        {
            _btnNext.Enabled = !busy;
            _btnBack.Enabled = !busy && _currentStep > 0;
            _btnCancel.Enabled = !busy;
            UseWaitCursor = busy;
            _lblError.ForeColor = busy ? Muted : Danger;
            _lblError.Text = message ?? "";
        }

        private void ShowError(string message, Control focus)
        {
            _lblError.ForeColor = Danger;
            _lblError.Text = message;
            if (focus != null) focus.Focus();
        }

        private void OnNext()
        {
            if (_currentStep == 0) ValidateShopStep();
            else if (_currentStep == 1) { if (ValidateOwnerStep()) ShowStep(2); }
            else FinishSetup();
        }

        private void ValidateShopStep()
        {
            string name = (_txtShopName.Text ?? "").Trim();
            if (name.Length < 3)
            {
                ShowError("Please enter your shop name (at least 3 characters).", _txtShopName);
                return;
            }
            string slug = StationConfig.NormalizeSlug(_txtShopId.Text);
            string slugErr = StationConfig.ValidateSlug(slug);
            if (slugErr != null)
            {
                ShowError(slugErr, _txtShopId);
                return;
            }
            if (slug == _verifiedSlug)
            {
                ShowStep(1);
                return;
            }

            SetBusy(true, "Checking that Shop ID '" + slug + "' is available...");
            ThreadPool.QueueUserWorkItem(_ =>
            {
                var check = ShopRegistryClient.CheckSlug(slug);
                BeginInvoke(new Action(() => OnSlugChecked(slug, check)));
            });
        }

        private void OnSlugChecked(string slug, SlugCheckResult check)
        {
            SetBusy(false, null);
            if (check.Availability == SlugAvailability.Available)
            {
                _verifiedSlug = slug;
                _slugAvailable = true;
                _claimedExisting = false;
                ShowStep(1);
                return;
            }

            if (check.Availability == SlugAvailability.Taken)
            {
                string owner = string.IsNullOrWhiteSpace(check.ExistingShopName) ? "another shop" : "\"" + check.ExistingShopName + "\"";
                var answer = MessageBox.Show(this,
                    "The Shop ID '" + slug + "' is already registered to " + owner + ".\n\n" +
                    "Using another shop's ID would send THEIR customers' orders and payments to this computer.\n\n" +
                    "Is this YOUR existing shop that you are moving to a new computer?\n\n" +
                    "Yes - I own this shop, continue with this ID\n" +
                    "No - let me choose a different Shop ID",
                    "Shop ID already in use", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2);
                if (answer == DialogResult.Yes)
                {
                    _verifiedSlug = slug;
                    _slugAvailable = false;
                    _claimedExisting = true;
                    ShowStep(1);
                }
                else
                {
                    ShowError("Choose a different Shop ID, e.g. '" + slug + "-2' or add your area name.", _txtShopId);
                    _txtShopId.SelectAll();
                }
                return;
            }

            var offline = MessageBox.Show(this,
                "Couldn't reach the PrintAway registry to confirm that '" + slug + "' is free.\n(" + (check.Error ?? "network error") + ")\n\n" +
                "You can continue now; the ID will be registered when this station comes online. Continue?",
                "Can't verify Shop ID", MessageBoxButtons.YesNo, MessageBoxIcon.Information);
            if (offline == DialogResult.Yes)
            {
                _verifiedSlug = slug;
                _slugAvailable = false;
                _claimedExisting = false;
                ShowStep(1);
            }
        }

        private bool ValidateOwnerStep()
        {
            if ((_txtOwnerName.Text ?? "").Trim().Length < 2)
            {
                ShowError("Please enter the owner's name.", _txtOwnerName);
                return false;
            }
            if (StationConfig.NormalizePhone(_txtOwnerPhone.Text) == null)
            {
                ShowError("Please enter a valid mobile number (10 digits, optionally with country code).", _txtOwnerPhone);
                return false;
            }
            if (!StationConfig.IsValidUpiVpa(_txtUpi.Text))
            {
                ShowError("Please enter a valid UPI ID, e.g. yourshop@okaxis.", _txtUpi);
                return false;
            }
            return true;
        }

        private string QueueAt(ComboBox combo, bool hasSameAsOption)
        {
            int idx = combo.SelectedIndex - (hasSameAsOption ? 1 : 0);
            if (idx < 0 || idx >= _queues.Count) return null;
            return _queues[idx].QueueName;
        }

        private void FinishSetup()
        {
            string pass = _txtPass.Text ?? "";
            string passErr = StationConfig.ValidateAdminPassword(pass);
            if (passErr != null)
            {
                ShowError(passErr, _txtPass);
                return;
            }
            if (pass != _txtConfirm.Text)
            {
                ShowError("Passwords do not match.", _txtConfirm);
                return;
            }

            string primary = _cmbPrimary.SelectedIndex >= 0 ? QueueAt(_cmbPrimary, false) : null;
            if (primary != null && _queues[_cmbPrimary.SelectedIndex].IsVirtualExcluded)
            {
                var ok = MessageBox.Show(this,
                    "'" + primary + "' is a virtual printer (it saves files instead of printing on paper).\n\nUse it anyway?",
                    "Virtual printer selected", MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2);
                if (ok != DialogResult.Yes) return;
            }

            var result = new OnboardingResult
            {
                ShopName = _txtShopName.Text.Trim(),
                ShopId = _verifiedSlug,
                OwnerName = _txtOwnerName.Text.Trim(),
                OwnerPhone = StationConfig.NormalizePhone(_txtOwnerPhone.Text),
                UpiVpa = _txtUpi.Text.Trim(),
                AdminPassword = pass,
                PrimaryQueue = primary,
                BwQueue = QueueAt(_cmbBw, true) ?? primary,
                ColorQueue = QueueAt(_cmbColor, true) ?? primary,
                AutoStart = _chkAutoStart.Checked,
                ClaimedExistingShopId = _claimedExisting
            };

            if (!_slugAvailable)
            {
                // Claimed existing ID, or offline: nothing to register right now.
                Complete(result);
                return;
            }

            SetBusy(true, "Registering your shop...");
            ThreadPool.QueueUserWorkItem(_ =>
            {
                bool conflict;
                string err;
                bool ok = ShopRegistryClient.RegisterShop(result.ShopId, result.ShopName, result.OwnerName, result.OwnerPhone,
                    result.UpiVpa, result.PrimaryQueue, _clientVersion, out conflict, out err);
                BeginInvoke(new Action(() =>
                {
                    SetBusy(false, null);
                    if (conflict)
                    {
                        _verifiedSlug = null;
                        ShowStep(0);
                        ShowError("Shop ID '" + result.ShopId + "' was just taken by another shop. Please choose a different ID.", _txtShopId);
                        return;
                    }
                    result.RegisteredOnline = ok;
                    Complete(result);
                }));
            });
        }

        private void Complete(OnboardingResult result)
        {
            Result = result;
            DialogResult = DialogResult.OK;
            Close();
        }
    }
}
