using GenericWeb.DeploymentManager.Models;
using GenericWeb.DeploymentManager.Services;
using System.Text;
using System.Net;
using System.Net.Sockets;
using System.Text.RegularExpressions;
using System.Security.Cryptography;
using System.Text.Json;
using System.IO.Compression;

namespace GenericWeb.DeploymentManager;

public sealed class WizardForm : Form
{
    private readonly DeploymentProfile _profile = new();
    private readonly AppDataService _appData = new();
    private readonly LogService _logService;
    private DeploymentState _persistentState = new();
    private bool _operationRunning;
    private readonly CommandRunner _runner = new();
    private readonly EnvironmentService _environment;
    private readonly PrerequisiteService _prereq;
    private readonly SqlServerService _sql;
    private readonly DeploymentService _deployment;
    private readonly DeploymentHistoryService _history;
    private readonly RemoteDeploymentService _remote;
    private readonly RuntimeMetricsService _runtimeMetrics = new();
    private readonly BackgroundHealthMonitor _healthMonitor;
    private readonly NotifyIcon _trayIcon = new();
    private readonly System.Windows.Forms.Timer _trayBlinkTimer = new() { Interval = 550 };
    private readonly Icon _traySuccessIcon = CreateTrayStatusIcon(Color.ForestGreen, true);
    private readonly Icon _trayFailureIcon = CreateTrayStatusIcon(Color.Firebrick, false);
    private bool _trayBlinkVisible = true;
    private bool _trayAlertActive;
    private readonly Label _iisHealth = new() { AutoSize = true, ForeColor = Color.DimGray, Padding = new Padding(8, 8, 2, 0), Text = "○ IIS" };
    private readonly Label _sqlHealth = new() { AutoSize = true, ForeColor = Color.DimGray, Padding = new Padding(4, 8, 2, 0), Text = "○ SQL" };
    private readonly Label _httpHealth = new() { AutoSize = true, ForeColor = Color.DimGray, Padding = new Padding(4, 8, 2, 0), Text = "○ HTTP" };
    private readonly Label _healthChecked = new() { AutoSize = true, ForeColor = Color.Gray, Padding = new Padding(6, 8, 8, 0), Text = "Checked: —" };
    private readonly Label _deploymentIndicator = new() { Dock = DockStyle.Fill, AutoSize = false, AutoEllipsis = true, TextAlign = ContentAlignment.MiddleLeft, Padding = new Padding(8, 2, 6, 2), ForeColor = Color.DimGray, Text = "Recorded deployment result: Not started" };
    private readonly ToolTip _healthTip = new() { AutoPopDelay = 20000, InitialDelay = 350, ReshowDelay = 100 };
    private readonly System.Windows.Forms.Timer _runtimeMetricsTimer = new() { Interval = 5000 };
    private readonly Label _resourceUsage = new() { AutoSize = true, ForeColor = Color.DimGray, Padding = new Padding(8, 8, 8, 0), Text = "CPU/RAM: sampling..." };
    // Persistent settings are coalesced on the UI thread, not written for each
    // TextChanged character. Flush before a page's controls are disposed.
    private readonly System.Windows.Forms.Timer _settingsSaveTimer = new() { Interval = 650 };
    private readonly Dictionary<string, Action> _deferredSettingsSaves = new(StringComparer.Ordinal);

    private readonly string[] _steps =
    {
        "Project Source & Dependencies", "Target & Identity", "Environment Scan", "IIS", "Installers & Runtimes",
        "SQL Server & Database", "Deployment Location", "Review & Deploy"
    };

    private readonly List<Label> _stepLabels = new();
    private readonly FlowLayoutPanel _stepPanel = new();
    private readonly Panel _content = new();
    private readonly Button _back = new() { Text = "< Back", Width = 135, Height = 40 };
    private readonly Button _next = new() { Text = "Next >", Width = 135, Height = 40 };
    private readonly Button _autoDeploy = new() { Text = "Auto Deploy", Width = 145, Height = 40 };
    private bool _autoDeployRunning;
    private bool _offlineDependenciesReady;
    private bool _dependencySyncRunning;
    private string _offlineDependenciesDetail = "Not checked";
    private readonly Label _pageTitle = new() { AutoSize = true, Font = new Font("Segoe UI Semibold", 18F) };
    private readonly Label _pageDescription = new() { AutoSize = true, MaximumSize = new Size(820, 0), ForeColor = Color.DimGray };
    private readonly Label _stepIndicator = new() { AutoSize = true, ForeColor = Color.DimGray, Font = new Font("Segoe UI Semibold", 9.5F) };
    private readonly TextBox _log = new() { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Dock = DockStyle.Fill, Font = new Font("Consolas", 9F) };
    private readonly ProgressBar _progress = new() { Dock = DockStyle.Bottom, Height = 16 };
    private readonly Label _activity = new() { AutoSize = false, Dock = DockStyle.Fill, Height = 36, Padding = new Padding(8, 0, 8, 0), TextAlign = ContentAlignment.MiddleLeft, ForeColor = Color.DimGray, Text = "Ready" };

    private Button? _saveProfileAction;
    private Button? _loadProfileAction;
    private CheckBox? _lockSourceAction;
    private Button? _exportSourceAction;
    private int _pageIndex;
    private int _lastLoggedPageIndex = -1;
    private bool _scanCompleted;
    private PrerequisiteState _iisState = new(false, "Not scanned");
    private PrerequisiteState _hostingState = new(false, "Not scanned");
    private PrerequisiteState _sdkState = new(false, "Not scanned");
    private PrerequisiteState _nodeState = new(false, "Not scanned");
    private List<SqlInstanceInfo> _sqlInstances = new();
    private bool _sqlAdminConnectionVerified => _sql.Connections.State(_profile).Verified;
    private bool _databaseConnectionVerified => _sqlAdminConnectionVerified && !string.IsNullOrWhiteSpace(_profile.DatabaseName);
    private bool _iisAutoVerified;
    private bool _runtimesAutoVerified;
    private bool _sqlDatabaseAutoVerified => _databaseConnectionVerified;
    private bool _deploymentLocationAutoVerified;
    private CancellationTokenSource? _backgroundVerifyCts;
    private int _preparedValidationGeneration;
    private string? _lastErrorReportPath;
    private const string InterruptedDeploymentStatus = "PREVIOUS DEPLOYMENT INTERRUPTED — RECOVERY UNVERIFIED";
    private string _deploymentStatus = "Not started";
    private string? _lastSuccessfulPublishPath;
    private string _deploymentElapsedText = "Elapsed: 00:00:00";
    private Label? _deploymentElapsedLabel;
    private LogViewMode _logViewMode = LogViewMode.Normal;
    private readonly List<(DateTime At, string Line)> _uiLogLines = new();
    private const int MaxPendingUiLogLines = 8_192;
    private const int MaxUiLogLinesPerFlush = 512;
    private readonly object _pendingUiLogSync = new();
    private readonly Queue<(DateTime At, string Line)> _pendingUiLogLines = new();
    private readonly System.Windows.Forms.Timer _uiLogFlushTimer = new() { Interval = 100 };
    private int _droppedUiLogLines;
    private volatile bool _acceptUiLogLines = true;
    private ComboBox? _logModeCombo;
    private Label? _reviewActivity;
    private CancellationTokenSource? _deploymentCts;
    private CancellationTokenSource? _autoDeployCts;
    private string ProfilesFolder => Path.Combine(_appData.Root, "Profiles");

    public WizardForm()
    {
        _logService = new LogService(_appData);
        _persistentState = _appData.LoadState();
        _deploymentStatus = _persistentState.LastResult switch
        {
            "RecoveryFailed" => "PREVIOUS DEPLOYMENT — RECOVERY FAILED",
            // A persisted InProgress result survives a crash and is not proof of a clean start.
            "InProgress" => InterruptedDeploymentStatus,
            "Failed" => "PREVIOUS DEPLOYMENT UNSUCCESSFUL",
            "Successful" => "PREVIOUS DEPLOYMENT SUCCESSFUL",
            _ => "Not started"
        };
        _environment = new EnvironmentService(_runner);
        _prereq = new PrerequisiteService(_runner);
        _sql = new SqlServerService(_runner, _prereq);
        _deployment = new DeploymentService(_runner, _sql, _appData);
        _history = new DeploymentHistoryService();
        _remote = new RemoteDeploymentService(_runner, _prereq);
        _healthMonitor = new BackgroundHealthMonitor(_environment, _sql, _profile);
        _profile.InstallerCacheFolder = _appData.GetInstallerCacheFolder();
        _profile.DeploymentRepositoryFolder = _appData.GetDeploymentRepositoryFolder();
        _profile.WorkspaceRoot = _appData.GetWorkspaceRoot();
        _profile.InstallerCacheFolder = Path.Combine(_profile.DeploymentRepositoryFolder, "Installers");
        _appData.SaveDeploymentRepositoryFolder(_profile.DeploymentRepositoryFolder);
        // V40: the selected dependency mode is authoritative and persists across page rebuilds/restarts.
        _profile.DependencyResolutionMode = NormalizeDependencyMode(_appData.GetDependencyResolutionMode());
        _profile.OfflineBuild = string.Equals(_profile.DependencyResolutionMode, "Offline Only", StringComparison.OrdinalIgnoreCase);
        var identity = _appData.GetDeploymentIdentitySettings();
        _profile.ApplicationName = identity.ApplicationName;
        _profile.SiteName = identity.SiteName;
        _profile.AppPoolName = identity.SiteName;
        _profile.DatabaseName = identity.DatabaseName;
        var savedRemote = _appData.GetRemoteCredential();
        if (savedRemote.Remember)
        {
            _profile.TargetHost = savedRemote.Host;
            _profile.RemoteUserName = savedRemote.UserName;
            _profile.RemotePassword = savedRemote.Password;
        }
        var hostingSettings = _appData.GetHostingSettings();
        _profile.HostingRoot = NormalizeAutoHostingRoot(hostingSettings.HostingRoot, identity.ApplicationName);
        _profile.DeploymentLayout = hostingSettings.DeploymentLayout;
        _profile.CleanupPreviousDeployments = hostingSettings.CleanupPreviousDeployments;
        _profile.UseDefaultIisWebsite = hostingSettings.UseDefaultIisWebsite;
        if (_profile.UseDefaultIisWebsite) { _profile.HostingRoot = @"C:\inetpub\wwwroot"; _profile.DeploymentLayout = "Single Folder"; _profile.HttpPort = 80; _profile.HostHeader = string.Empty; }
        _runner.Output += AppendLog;

        Text = $"Generic Web Deployment Manager {ProductInfo.DisplayVersion} — Unified Diagnostics & Smart Settings";
        Width = 1280;
        Height = 860;
        MinimumSize = new Size(1080, 740);
        StartPosition = FormStartPosition.CenterScreen;
        WindowState = FormWindowState.Maximized;
        Font = new Font("Segoe UI", 9.5F);
        // Scale controls consistently when Windows display DPI is changed.
        AutoScaleMode = AutoScaleMode.Dpi;

        BuildShell();
        _settingsSaveTimer.Tick += (_, _) => FlushDeferredSettingsSaves();
        _uiLogFlushTimer.Tick += (_, _) => FlushPendingUiLogLines();
        _uiLogFlushTimer.Start();
        _trayIcon.Icon = Icon ?? SystemIcons.Application;
        _trayIcon.Text = "GWD — Ready";
        _trayIcon.Visible = true;
        _trayIcon.MouseClick += (_, e) =>
        {
            if (e.Button != MouseButtons.Left) return;
            AcknowledgeTrayAlert();
            if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
            Show();
            Activate();
        };
        _trayBlinkTimer.Tick += (_, _) =>
        {
            if (!_trayAlertActive) return;
            _trayBlinkVisible = !_trayBlinkVisible;
            _trayIcon.Icon = _trayBlinkVisible ? (_persistentState.LastResult == "Successful" ? _traySuccessIcon : _trayFailureIcon) : (Icon ?? SystemIcons.Application);
        };
        _runtimeMetricsTimer.Tick += (_, _) => UpdateRuntimeMetrics();
        _runtimeMetricsTimer.Start();
        UpdateRuntimeMetrics();
        FormClosing += WizardForm_FormClosing;
        _healthMonitor.Updated += OnHealthUpdated;
        _healthMonitor.Start();
        ShowPage(0);
        AppendLog($"Deployment Manager {ProductInfo.DisplayVersion} started. GUI and headless CLI share the deployment services. Persistent state: {_appData.StateFile}");
        AppendLog("Deployment history: " + _history.Root);
        AppendLog("Deployment Repository: " + _profile.DeploymentRepositoryFolder);
        AppendLog("Stable Build Workspace Root: " + _profile.WorkspaceRoot);
        if (_persistentState.LastResult == "InProgress")
            AppendLog("[RECOVERY] Previous deployment has no recorded final result. Verify IIS states, recovery markers, owned backups and HTTP health before retrying. No markers or backups were cleared.");
    }

    private void OnHealthUpdated(HealthSnapshot snapshot)
    {
        if (IsDisposed || Disposing || !IsHandleCreated) return;
        try
        {
            BeginInvoke((Action)(() =>
            {
                // A scheduled health callback can arrive after the form closes.
                if (IsDisposed || Disposing) return;
                _iisHealth.Text = snapshot.IisInstalled ? "✓ IIS installed" : "○ IIS unverified";
                _iisHealth.ForeColor = snapshot.IisInstalled ? Color.ForestGreen : Color.DarkOrange;
                var iisDetail = snapshot.IisInstalled
                    ? $"IIS installed; {snapshot.SiteCount} site(s), {snapshot.AppPoolCount} app pool(s). Target site and release identity NOT verified."
                    : "IIS not verified/available. A SQL-only update is not an IIS health check.";
                _healthTip.SetToolTip(_iisHealth, iisDetail);

                var sqlState = _sql.Connections.State(_profile);
                var sqlConnected = snapshot.SqlOk == true && sqlState.Verified;
                _sqlHealth.Text = sqlConnected ? "✓ SQL connected" : snapshot.SqlOk == false ? "✕ SQL unavailable" : "○ SQL unchecked";
                _sqlHealth.ForeColor = sqlConnected ? Color.ForestGreen : snapshot.SqlOk == false ? Color.Firebrick : Color.DimGray;
                _healthTip.SetToolTip(_sqlHealth, SecretRedactor.Redact(sqlState.Detail) + " — SQL connection is not proof of database/schema health.");

                var httpResponded = snapshot.HttpStatus is >= 200 and < 400;
                _httpHealth.Text = snapshot.HttpStatus is null ? "○ HTTP unchecked" : $"HTTP {snapshot.HttpStatus} (probe)";
                _httpHealth.ForeColor = snapshot.HttpStatus is null ? Color.Gray : httpResponded ? Color.DarkGreen : Color.Firebrick;
                _healthTip.SetToolTip(_httpHealth, SecretRedactor.Redact(snapshot.HttpDetail) +
                    " — A localhost HTTP response does NOT verify IIS site ownership, API/frontend version, or release identity.");
                _healthChecked.Text = $"Checked: {snapshot.CheckedAt:HH:mm:ss}";
                _healthTip.SetToolTip(_healthChecked, "Timestamp of last health snapshot; results may be stale and are not proof of a successful deployment.");
                UpdateStepIndicator();
            }));
        }
        catch (InvalidOperationException) when (IsDisposed || Disposing || !IsHandleCreated) { }
        catch (ObjectDisposedException) when (IsDisposed || Disposing) { }
    }

    private void UpdateDeploymentIndicator()
    {
        if (_deploymentIndicator.IsDisposed) return;
        var needsReview = _deploymentStatus.Contains("INTERRUPTED", StringComparison.OrdinalIgnoreCase)
            || _deploymentStatus.Contains("RECOVERY FAILED", StringComparison.OrdinalIgnoreCase)
            || _deploymentStatus.Contains("UNSUCCESSFUL", StringComparison.OrdinalIgnoreCase)
            || _deploymentStatus.Contains("FAILED", StringComparison.OrdinalIgnoreCase)
            || _deploymentStatus.Contains("CANCELLED", StringComparison.OrdinalIgnoreCase);
        _deploymentIndicator.Text = needsReview
            ? "⚠ RECOVERY REVIEW REQUIRED — " + _deploymentStatus + " (inspect IIS, markers and backups)"
            : "Recorded result: " + _deploymentStatus + " — live release identity not verified here";
        _deploymentIndicator.ForeColor = needsReview ? Color.Firebrick
            : _deploymentStatus.Contains("SUCCESSFUL", StringComparison.OrdinalIgnoreCase) ? Color.DarkGreen
            : _deploymentStatus.Contains("IN PROGRESS", StringComparison.OrdinalIgnoreCase) ? Color.RoyalBlue : Color.DimGray;
        _healthTip.SetToolTip(_deploymentIndicator, needsReview
            ? "Do not infer successful recovery from IIS status or HTTP response. Review the attempt Journal, recovery markers, backups and actual site/API/frontend state before retrying."
            : "Recorded process outcome only; check actual IIS target, HTTP endpoint and running release identity independently.");
    }

    private void BuildShell()
    {
        // Independent health/status row and full-width action footer avoid the
        // old competition for horizontal space at 125%/150% DPI.
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 3 };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 250));
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 68));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 62));

        _stepPanel.Dock = DockStyle.Fill;
        _stepPanel.FlowDirection = FlowDirection.TopDown;
        _stepPanel.WrapContents = false;
        _stepPanel.AutoScroll = true;
        _stepPanel.BackColor = Color.FromArgb(245, 247, 250);
        _stepPanel.Padding = new Padding(14, 20, 10, 10);
        foreach (var step in _steps)
        {
            var label = new Label
            {
                Text = step,
                AutoSize = false,
                Width = 215,
                Height = 48,
                Padding = new Padding(12, 12, 4, 0),
                Font = new Font("Segoe UI Semibold", 10.5F),
                ForeColor = Color.Gray,
                BackColor = Color.Transparent,
                Cursor = Cursors.Hand
            };
            var stepIndex = _stepLabels.Count;
            label.Click += (_, _) => { if (!_operationRunning && !_autoDeployRunning && !_dependencySyncRunning) ShowPage(stepIndex); };
            _stepLabels.Add(label);
            _stepPanel.Controls.Add(label);
        }
        root.Controls.Add(_stepPanel, 0, 0);
        root.SetRowSpan(_stepPanel, 2);

        var right = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 3, Padding = new Padding(26, 18, 26, 0) };
        right.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        right.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        right.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        var header = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 2 };
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        header.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        header.Controls.Add(_pageTitle, 0, 0);
        _stepIndicator.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        header.Controls.Add(_stepIndicator, 1, 0);

        right.Controls.Add(header, 0, 0);
        right.Controls.Add(_pageDescription, 0, 1);
        _content.Dock = DockStyle.Fill;
        // Auto-sized wizard rows can exceed the available height, especially at 125%/150% DPI.
        // Keep the footer visible and scroll the page instead of clipping/overlapping fields.
        _content.AutoScroll = true;
        _content.Padding = new Padding(0, 18, 0, 0);
        right.Controls.Add(_content, 0, 2);
        root.Controls.Add(right, 1, 0);

        // These health labels were updated by OnHealthUpdated but were never
        // parented to a visible control. Keep the recovery warning in the top
        // line, with horizontally scrollable status details below it.
        var healthBar = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 2, Padding = new Padding(12, 0, 12, 0), Margin = new Padding(0) };
        healthBar.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));
        healthBar.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        healthBar.Controls.Add(_deploymentIndicator, 0, 0);
        var healthDetails = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill, FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false, AutoScroll = true, Margin = new Padding(0), Padding = new Padding(3, 0, 3, 0)
        };
        healthDetails.Controls.Add(_iisHealth);
        healthDetails.Controls.Add(_sqlHealth);
        healthDetails.Controls.Add(_httpHealth);
        healthDetails.Controls.Add(_healthChecked);
        healthBar.Controls.Add(healthDetails, 0, 1);
        root.Controls.Add(healthBar, 1, 1);
        UpdateDeploymentIndicator();

        var footer = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, Padding = new Padding(10, 8, 14, 8) };
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        var statusPanel = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoScroll = true, WrapContents = false };
        _activity.AutoEllipsis = true;
        statusPanel.Controls.Add(_activity);
        statusPanel.Controls.Add(_resourceUsage);
        footer.Controls.Add(statusPanel, 0, 0);

        var footerButtons = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft, WrapContents = false, Margin = new Padding(8, 0, 0, 0) };
        footerButtons.Controls.Add(_next);
        footerButtons.Controls.Add(_back);
        var resetWizard = new Button { Text = "Start Over", Width = 125, Height = 36 };
        var startOverMenu = new ContextMenuStrip();
        resetWizard.ContextMenuStrip = startOverMenu;
        startOverMenu.Items.Add("Start Over — Clear Log", null, (_, _) => ResetWizard(clearLog: true));
        startOverMenu.Items.Add("Start Over — Keep Log", null, (_, _) => ResetWizard(clearLog: false));
        resetWizard.Click += (_, _) => startOverMenu.Show(resetWizard, new Point(0, resetWizard.Height));
        footerButtons.Controls.Add(resetWizard);
        footerButtons.Controls.Add(_autoDeploy);
        var viewLogs = new Button { Text = "Diagnostics Center", Width = 145, Height = 36 };
        viewLogs.Click += (_, _) =>
        {
            using var dialog = new DiagnosticsCenterForm(_appData, _history, _profile, _deployment, _logService.LogFile, _runtimeMetrics);
            dialog.ShowDialog(this);
        };
        // Diagnostics Center is accessed from the Actions menu, not the wizard footer.
        footer.Controls.Add(footerButtons, 1, 0);
        root.Controls.Add(footer, 0, 2);
        root.SetColumnSpan(footer, 2);

        Controls.Add(root);
        Controls.Add(_progress);
        var menu = new MenuStrip { Dock = DockStyle.Top };
        var fileMenu = new ToolStripMenuItem("File");
        var saveMenu = new ToolStripMenuItem("Save Profile");
        saveMenu.Click += (_, _) => { if (_operationRunning || _autoDeployRunning) return; ShowPage(0); _saveProfileAction?.PerformClick(); };
        var loadMenu = new ToolStripMenuItem("Load Profile");
        loadMenu.Click += (_, _) => { if (_operationRunning || _autoDeployRunning) return; ShowPage(0); _loadProfileAction?.PerformClick(); };
        fileMenu.DropDownItems.Add(saveMenu);
        fileMenu.DropDownItems.Add(loadMenu);
        fileMenu.DropDownItems.Add(new ToolStripSeparator());
        var settingsButton = new Button { Text = "Settings", Width = 110, Height = 36 };
        settingsButton.Click += (_, _) =>
        {
            using var dialog = new SettingsForm(_appData);
            if (dialog.ShowDialog(this) == DialogResult.OK)
            {
                _profile.DeploymentRepositoryFolder = _appData.GetDeploymentRepositoryFolder();
                _profile.WorkspaceRoot = _appData.GetWorkspaceRoot();
                AppendLog("Settings updated. Release output: " + _appData.GetManagerReleaseRoot());
            }
        };


        var settingsMenu = new ToolStripMenuItem("Settings");
        settingsMenu.Click += (_, _) => settingsButton.PerformClick();
        fileMenu.DropDownItems.Add(settingsMenu);
        var databaseMenu = new ToolStripMenuItem("Database Management");
        databaseMenu.Click += (_, _) =>
        {
            if (_operationRunning || _autoDeployRunning || _dependencySyncRunning)
            {
                MessageBox.Show(this, "Stop the current operation before opening Database Management.");
                return;
            }
            using var dialog = new DatabaseRelocationForm(_profile, _sql);
            dialog.ShowDialog(this);
        };
        fileMenu.DropDownItems.Add(databaseMenu);
        menu.Items.Add(fileMenu);
        var actionsMenu = new ToolStripMenuItem("Actions");
        var cleanupMenu = new ToolStripMenuItem("Clean Previous Versions");
        cleanupMenu.Click += async (_, _) => { if (!_operationRunning && !_autoDeployRunning) await CleanupPreviousVersionsAsync(); };
        actionsMenu.DropDownItems.Add(cleanupMenu);
        var deploymentCleanupMenu = new ToolStripMenuItem("Deployment Cleanup...");
        deploymentCleanupMenu.Click += async (_, _) => { if (!_operationRunning && !_autoDeployRunning && !_dependencySyncRunning) await ShowDeploymentCleanupAsync(); };
        actionsMenu.DropDownItems.Add(deploymentCleanupMenu);
        menu.Items.Add(actionsMenu);
        actionsMenu.DropDownItems.Add(new ToolStripSeparator());
        var lockSourceMenu = new ToolStripMenuItem("Lock Source") { CheckOnClick = false };
        lockSourceMenu.Click += (_, _) => { if (_pageIndex != 0) ShowPage(0); if (_lockSourceAction is { IsDisposed: false } control) { control.Checked = !control.Checked; lockSourceMenu.Checked = control.Checked; } };
        actionsMenu.DropDownItems.Add(lockSourceMenu);
        var exportSourceMenu = new ToolStripMenuItem("Export Clean Source ZIP");
        exportSourceMenu.Click += (_, _) => { if (_pageIndex != 0) ShowPage(0); _exportSourceAction?.PerformClick(); };
        actionsMenu.DropDownItems.Add(exportSourceMenu);
        var diagnosticsMenu = new ToolStripMenuItem("Diagnostics Center");
        diagnosticsMenu.Click += (_, _) => viewLogs.PerformClick();
        actionsMenu.DropDownItems.Add(diagnosticsMenu);
        MainMenuStrip = menu;
        Controls.Add(menu);


        _back.Click += (_, _) => { if (_pageIndex > 0) ShowPage(_pageIndex - 1); };
        _next.Click += async (_, _) => await NextAsync();
        _autoDeploy.Click += async (_, _) =>
        {
            if (_autoDeployRunning)
            {
                _autoDeployCts?.Cancel();
                _autoDeploy.Text = "Cancelling Deploy...";
                _autoDeploy.Enabled = false;
                _activity.Text = "Cancelling automatic deployment...";
                AppendLog("[AUTO] Cancellation requested by user.");
                return;
            }
            await AutoDeployAsync();
        };
    }

    private void UpdateRuntimeMetrics()
    {
        if (IsDisposed || Disposing) return;
        var sample = _runtimeMetrics.Sample();
        _resourceUsage.Text = sample.ToDisplayText();
        _resourceUsage.ForeColor = sample.WorkingSetBytes > 768L * 1024 * 1024 ? Color.Firebrick : (sample.WorkingSetBytes > 384L * 1024 * 1024 ? Color.DarkOrange : Color.DimGray);
    }

    private async Task CleanupPreviousVersionsAsync()
    {
        if (_operationRunning || _autoDeployRunning)
        {
            MessageBox.Show(this, "ابتدا عملیات استقرار جاری را متوقف کنید.", "پاکسازی", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        // Only direct children of the configured IIS hosting root are eligible.
        var root = string.IsNullOrWhiteSpace(_profile.HostingRoot) ? @"C:\inetpub" : _profile.HostingRoot;
        try
        {
            root = Path.GetFullPath(Environment.ExpandEnvironmentVariables(root)).TrimEnd(Path.DirectorySeparatorChar);
            if (!Directory.Exists(root)) throw new DirectoryNotFoundException(root);
            if ((File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("مسیر اصلی یک لینک یا Reparse Point است.");
            var candidates = Directory.EnumerateDirectories(root, "wwwroot.gwd-previous-*", SearchOption.TopDirectoryOnly)
                .Where(p => Path.GetFileName(p).StartsWith("wwwroot.gwd-previous-", StringComparison.OrdinalIgnoreCase))
                .Where(p => (File.GetAttributes(p) & FileAttributes.ReparsePoint) == 0)
                .ToArray();
            if (candidates.Length == 0)
            {
                MessageBox.Show(this, "هیچ نسخه قدیمی قابل پاکسازی پیدا نشد.", "پاکسازی");
                return;
            }

            // Refuse to delete a folder currently configured as this IIS site's physical path.
            var active = await _deployment.GetSitePhysicalPathAsync(_profile, CancellationToken.None);
            if (!string.IsNullOrWhiteSpace(active))
            {
                var activePath = Path.GetFullPath(Environment.ExpandEnvironmentVariables(active)).TrimEnd(Path.DirectorySeparatorChar);
                candidates = candidates.Where(p => !string.Equals(Path.GetFullPath(p).TrimEnd(Path.DirectorySeparatorChar), activePath, StringComparison.OrdinalIgnoreCase)).ToArray();
            }
            if (candidates.Length == 0) throw new IOException("تمام مسیرهای پیدا شده در حال استفاده هستند.");

            var answer = MessageBox.Show(this,
                $"تعداد {candidates.Length} پوشه نسخه قدیمی در مسیر {root} حذف خواهند شد.\nاین عملیات قابل بازگشت نیست. ادامه می‌دهید؟",
                "تأیید پاکسازی", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2);
            if (answer != DialogResult.Yes) return;

            int deleted = 0;
            var failures = new List<string>();
            foreach (var folder in candidates)
            {
                try
                {
                    // Recheck immediately before deletion; never traverse a linked directory.
                    if ((File.GetAttributes(folder) & FileAttributes.ReparsePoint) != 0) throw new IOException("Reparse Point");
                    if (Directory.EnumerateFileSystemEntries(folder, "*", SearchOption.AllDirectories)
                        .Any(p => (File.GetAttributes(p) & FileAttributes.ReparsePoint) != 0))
                        throw new IOException("پوشه حاوی لینک است و حذف نشد.");
                    Directory.Delete(folder, recursive: true);
                    deleted++;
                    AppendLog("[CLEANUP] Deleted: " + folder);
                }
                catch (Exception ex)
                {
                    failures.Add(Path.GetFileName(folder) + ": " + ex.Message);
                    AppendLog("[CLEANUP] Failed: " + folder + " — " + ex.Message);
                }
            }
            MessageBox.Show(this, $"پاکسازی پایان یافت. حذف‌شده: {deleted}، ناموفق: {failures.Count}" +
                (failures.Count > 0 ? "\\n" + string.Join("\\n", failures.Take(5)) : ""),
                "نتیجه پاکسازی", MessageBoxButtons.OK, failures.Count > 0 ? MessageBoxIcon.Warning : MessageBoxIcon.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "پاکسازی انجام نشد: " + ex.Message, "خطا", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void ResetWizard(bool clearLog)
    {
        if (_operationRunning)
        {
            MessageBox.Show(this, "Cancel the running deployment before starting over.", "Start over", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        var modeText = clearLog ? "clear the visible log and begin a new log session" : "keep the current log and continue the same log session";
        var answer = MessageBox.Show(this,
            $"Start over from the beginning and {modeText}? Current wizard selections and scan/test results will be cleared. Saved application defaults will be kept.",
            "Start over", MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2);
        if (answer != DialogResult.Yes) return;

        AppendLog(clearLog ? "[START-OVER] Start Over selected — Clear Log." : "[START-OVER] Start Over selected — Keep Log.");

        _deploymentCts?.Cancel();
        _deploymentCts?.Dispose();
        _deploymentCts = null;
        _autoDeployCts?.Cancel();
        _autoDeployCts?.Dispose();
        _autoDeployCts = null;
        _deployment.ResetPerformanceSnapshot();
        DisposeContentControls();

        // Keep the selected project so Start Over returns to Project Source ready for confirmation/change.
        _profile.ReadyPublishFolder = string.Empty;
        _profile.BuildMode = "Fast Local Test";
        _profile.DependencyResolutionMode = NormalizeDependencyMode(_appData.GetDependencyResolutionMode());
        _profile.OfflineBuild = string.Equals(_profile.DependencyResolutionMode, "Offline Only", StringComparison.OrdinalIgnoreCase);
        _appData.SaveDependencyResolutionMode(_profile.DependencyResolutionMode);
        _profile.SqlInstance = string.Empty;
        _profile.HostingBundleInstallerPath = string.Empty;
        _profile.DotNetSdkInstallerPath = string.Empty;
        _profile.NodeInstallerPath = string.Empty;
        _profile.SqlInstallerPath = string.Empty;

        _offlineDependenciesReady = false;
        _offlineDependenciesDetail = "Not checked";
        _scanCompleted = false;
        
        
        _iisState = new(false, "Not scanned");
        _hostingState = new(false, "Not scanned");
        _sdkState = new(false, "Not scanned");
        _nodeState = new(false, "Not scanned");
        _sqlInstances = new();
        _lastErrorReportPath = null;
        _lastSuccessfulPublishPath = null;
        // Start Over must not erase an unresolved prior deployment warning.
        _deploymentStatus = _persistentState.LastResult == "InProgress" ? InterruptedDeploymentStatus : "Not started";
        _deploymentElapsedText = "Elapsed: 00:00:00";
        _activity.Text = "Ready";
        _progress.Value = 0;

        _uiLogLines.Clear();
        // Start Over clears only the on-screen backlog. Every line was already
        // committed to its session log before being queued for display.
        lock (_pendingUiLogSync)
        {
            _pendingUiLogLines.Clear();
            _droppedUiLogLines = 0;
        }
        _log.Clear();
        if (clearLog)
        {
            _logService.StartNewSession();
            AppendLog("New deployment log session started after Start Over — Clear Log. Previous session log was retained on disk.");
        }
        else
        {
            AppendLog("Wizard state and in-memory log buffer reset. Existing deployment log retained on disk and the current disk session continued.");
        }

        _lastLoggedPageIndex = -1;
        _healthMonitor.Start();
        ShowPage(0);
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Optimized, blocking: false, compacting: false);
    }

    private void ShowPage(int index)
    {
        var previousPage = _pageIndex;
        _pageIndex = Math.Clamp(index, 0, _steps.Length - 1);
        if (_lastLoggedPageIndex != _pageIndex)
        {
            if (_lastLoggedPageIndex >= 0)
                AppendLog($"[STAGE-END] {_steps[_lastLoggedPageIndex]}");
            AppendLog("[STAGE-SEPARATOR] ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━");
            AppendLog($"[STAGE-START] Step {_pageIndex + 1}/{_steps.Length} — {_steps[_pageIndex]}");
            _lastLoggedPageIndex = _pageIndex;
        }
        _activity.Visible = _pageIndex != _steps.Length - 1;
        UpdateDeploymentIndicator();
        UpdateStepIndicator();
        DisposeContentControls();
        _stepIndicator.Text = $"Step {_pageIndex + 1} of {_steps.Length}";
        _back.Enabled = _pageIndex > 0;
        _next.Text = _pageIndex == _steps.Length - 1 ? ((_persistentState.LastResult == "Failed" || _persistentState.LastResult == "RecoveryFailed") ? "Retry Deploy" : (_persistentState.LastResult == "Successful" ? "Deploy Again" : "Deploy")) : "Next >";
        _progress.Value = (int)Math.Round((_pageIndex / (double)(_steps.Length - 1)) * 100);

        switch (_pageIndex)
        {
            case 0: BuildProjectSourcePage(); break;
            case 1: BuildTargetPage(); break;
            case 2: BuildScanPage(); break;
            case 3: BuildIisPage(); break;
            case 4: BuildInstallersPage(); break;
            case 5: BuildSqlPage(); break;
            case 6: BuildDeploymentLocationPage(); break;
            case 7: BuildReviewPage(); break;
        }
        ApplyInteractiveCursors(this);
        UpdateNavigationState();
    }

    private void QueueDeferredSettingsSave(string key, Action save)
    {
        _deferredSettingsSaves[key] = save;
        _settingsSaveTimer.Stop();
        _settingsSaveTimer.Start();
    }

    private void CancelDeferredSettingsSave(string key)
    {
        _deferredSettingsSaves.Remove(key);
        if (_deferredSettingsSaves.Count == 0) _settingsSaveTimer.Stop();
    }

    private void FlushDeferredSettingsSaves()
    {
        _settingsSaveTimer.Stop();
        if (_deferredSettingsSaves.Count == 0) return;
        var saves = _deferredSettingsSaves.ToArray();
        _deferredSettingsSaves.Clear();
        foreach (var (key, save) in saves)
        {
            try { save(); }
            catch (Exception ex)
            {
                // Settings failures must not block closing the window or
                // unexpectedly trigger deployment; preserve diagnostic detail.
                InternalDiagnostics.Record(ex, "Persist deferred setting: " + key);
            }
        }
    }

    private void DisposeContentControls()
    {
        // Source/target controls may own the latest unsaved edit. Their queued
        // actions must execute while controls are still alive.
        FlushDeferredSettingsSaves();
        if (_content.Controls.Count == 0) return;

        // The live log textbox is reused across wizard pages and is hosted inside
        // the review page's split container. Detach it before disposing the page
        // tree; otherwise disposing the split panel also disposes _log and the
        // next deployment attempts to access a disposed TextBox.
        if (!_log.IsDisposed && _log.Parent is not null)
            _log.Parent.Controls.Remove(_log);

        var controls = _content.Controls.Cast<Control>().ToArray();
        _content.Controls.Clear();
        foreach (var control in controls) control.Dispose();
    }

    private void UpdateStepIndicator()
    {
        for (var i = 0; i < _stepLabels.Count; i++)
        {
            var label = _stepLabels[i];
            if (i < _pageIndex)
            {
                label.Text = $"✓  {_steps[i]}";
                label.ForeColor = Color.ForestGreen;
                label.BackColor = Color.Transparent;
            }
            else if (i == _pageIndex)
            {
                label.Text = $"▶  {_steps[i]}";
                label.ForeColor = Color.FromArgb(25, 75, 145);
                label.BackColor = Color.FromArgb(225, 235, 248);
            }
            else
            {
                var autoVerified = (i == 2 && _scanCompleted) || (i == 3 && _iisAutoVerified) || (i == 4 && _runtimesAutoVerified) || (i == 5 && _sqlDatabaseAutoVerified) || (i == 6 && _deploymentLocationAutoVerified);
                label.Text = autoVerified ? $"✓  {_steps[i]} — Auto verified" : $"○  {_steps[i]}";
                label.ForeColor = autoVerified ? Color.ForestGreen : Color.Gray;
                label.BackColor = Color.Transparent;
            }
        }
    }

    private void UpdateNavigationState()
    {
        _back.Enabled = _pageIndex > 0;
        var localOnly = string.Equals(_profile.BuildMode, "Fast Local Test", StringComparison.OrdinalIgnoreCase);
        bool canNext = _pageIndex switch
        {
            0 => string.Equals(_profile.BuildMode, "Prepared IIS Publish", StringComparison.OrdinalIgnoreCase)
                ? DeploymentService.ValidatePreparedPublish(_profile.ReadyPublishFolder).Ok && !string.IsNullOrWhiteSpace(_profile.ApplicationName)
                : Directory.Exists(_profile.SourceFolder) && !string.IsNullOrWhiteSpace(_profile.ApplicationName) && !string.IsNullOrWhiteSpace(_environment.DetectApiProject(_profile.SourceFolder)),
            1 => CanLeaveTargetPage(),
            2 => _scanCompleted,
            3 => localOnly || _iisState.Installed,
            4 => InstallersPageReady(),
            5 => localOnly || (!string.IsNullOrWhiteSpace(_profile.SqlInstance) && _sqlAdminConnectionVerified && !string.IsNullOrWhiteSpace(_profile.DatabaseName) && _databaseConnectionVerified),
            6 => localOnly || !string.IsNullOrWhiteSpace(_profile.HostingRoot),
            7 => true,
            _ => false
        };
        if (_operationRunning && _pageIndex == _steps.Length - 1)
        {
            _next.Enabled = true;
            _next.Text = _deploymentCts?.IsCancellationRequested == true ? "Cancelling..." : "Cancel Deploy";
        }
        else
        {
            _next.Enabled = canNext && !_autoDeployRunning && !_dependencySyncRunning;
        }
        _back.Enabled = _pageIndex > 0 && !_operationRunning && !_autoDeployRunning && !_dependencySyncRunning;
        if (_autoDeployRunning)
            _autoDeploy.Enabled = _autoDeployCts?.IsCancellationRequested != true;
        else
            _autoDeploy.Enabled = !_operationRunning && !_dependencySyncRunning && CanAutoDeployStart();
    }

    private bool CanLeaveTargetPage()
    {
        if (string.Equals(_profile.TargetMode, "Remote IIS Server", StringComparison.OrdinalIgnoreCase))
        {
            if (!IPAddress.TryParse(_profile.TargetHost?.Trim(), out var remoteIp) || remoteIp.AddressFamily != AddressFamily.InterNetwork) return false;
            if (string.IsNullOrWhiteSpace(_profile.RemoteUserName) || string.IsNullOrWhiteSpace(_profile.RemotePassword)) return false;
            if (_profile.RemoteWinRmPort is < 1 or > 65535) return false;
        }
        else if (!_environment.IsLocalTarget(_profile.TargetHost))
        {
            return false;
        }

        if (_profile.BuildMode == "Prepared IIS Publish")
            return Directory.Exists(_profile.ReadyPublishFolder) && Directory.EnumerateFiles(_profile.ReadyPublishFolder, "*", SearchOption.TopDirectoryOnly).Any();
        if (string.Equals(_profile.BuildMode, "Fast Local Test", StringComparison.OrdinalIgnoreCase) && string.IsNullOrWhiteSpace(_profile.WorkspaceRoot)) return false;
        if (!Directory.Exists(_profile.SourceFolder)) return false;
        var api = _environment.DetectApiProject(_profile.SourceFolder);
        return !string.IsNullOrWhiteSpace(api);
    }

    private bool CanAutoDeployStart()
    {
        if (_profile.BuildMode == "Prepared IIS Publish")
            return Directory.Exists(_profile.ReadyPublishFolder) && Directory.EnumerateFiles(_profile.ReadyPublishFolder, "*", SearchOption.TopDirectoryOnly).Any();
        if (!Directory.Exists(_profile.SourceFolder)) return false;
        return !string.IsNullOrWhiteSpace(_environment.DetectApiProject(_profile.SourceFolder));
    }

    private static string NormalizeDependencyMode(string? mode)
    {
        if (string.Equals(mode, "Offline Only", StringComparison.OrdinalIgnoreCase)) return "Offline Only";
        if (string.Equals(mode, "Hybrid", StringComparison.OrdinalIgnoreCase) || string.Equals(mode, "Local First", StringComparison.OrdinalIgnoreCase)) return "Hybrid";
        if (string.Equals(mode, "Online", StringComparison.OrdinalIgnoreCase) || string.Equals(mode, "Online / Normal", StringComparison.OrdinalIgnoreCase)) return "Online";
        return "Offline Only";
    }

    private static bool IsCredentialFailure(string? detail)
    {
        var value = detail ?? string.Empty;
        return value.Contains("Access is denied", StringComparison.OrdinalIgnoreCase)
            || value.Contains("user name or password is incorrect", StringComparison.OrdinalIgnoreCase)
            || value.Contains("Logon failure", StringComparison.OrdinalIgnoreCase)
            || value.Contains("0x8007052e", StringComparison.OrdinalIgnoreCase);
    }

    private static string InferApplicationName(string selectedFolder, string apiProject)
    {
        var canonical = DeploymentService.ResolveCanonicalSourceRoot(selectedFolder, apiProject);
        var solution = Directory.EnumerateFiles(canonical, "*.sln", SearchOption.TopDirectoryOnly).FirstOrDefault();
        var raw = solution is not null ? Path.GetFileNameWithoutExtension(solution) : Path.GetFileNameWithoutExtension(apiProject);
        raw = Regex.Replace(raw, @"(?i)(\.Api|[-_. ]?v?\d+(?:\.\d+)+|[-_. ]?source)$", string.Empty);
        raw = Regex.Replace(raw, "(?<=[a-z])(?=[A-Z])|(?<=[A-Z])(?=[A-Z][a-z])", " ");
        raw = Regex.Replace(raw.Replace('_', ' ').Replace('-', ' '), @"\s+", " ").Trim();
        return string.IsNullOrWhiteSpace(raw) ? new DirectoryInfo(canonical).Name : raw;
    }

    private void ApplyInteractiveCursors(Control root)
    {
        foreach (Control control in root.Controls)
        {
            if (control is Button or LinkLabel) control.Cursor = control.Enabled ? Cursors.Hand : Cursors.Default;
            if (control.HasChildren) ApplyInteractiveCursors(control);
        }
    }

    private bool InstallersPageReady()
    {
        var needsSdk = _profile.BuildMode != "Prepared IIS Publish";
        var needsNode = needsSdk && _profile.RequiresNode;
        var localOnly = string.Equals(_profile.BuildMode, "Fast Local Test", StringComparison.OrdinalIgnoreCase);
        var runtimeOk = (localOnly || _hostingState.Installed)
            && (!needsSdk || _sdkState.Installed)
            && (!needsNode || _nodeState.Installed);
        var sqlReady = localOnly || _sqlInstances.Count > 0 || File.Exists(_profile.SqlInstallerPath);
        return runtimeOk && sqlReady;
    }

    private async Task AutoDeployAsync()
    {
        if (_operationRunning || _autoDeployRunning) return;

        _autoDeployCts?.Dispose();
        _autoDeployCts = new CancellationTokenSource();
        var ct = _autoDeployCts.Token;
        _autoDeployRunning = true;
        _next.Enabled = false;
        _back.Enabled = false;
        var originalText = _autoDeploy.Text;
        _autoDeploy.Text = "Cancel Deploy";
        _autoDeploy.Enabled = true;
        _activity.Text = "Automatic deployment running...";
        AppendLog("[AUTO] Automatic deployment started. Each wizard stage will be validated in order; automation stops on the first stage that needs attention.");

        try
        {
            for (var step = Math.Max(0, _pageIndex); step < _steps.Length; step++)
            {
                ct.ThrowIfCancellationRequested();
                ShowPage(step);
                _autoDeploy.Text = $"Cancel Deploy ({step + 1}/{_steps.Length})";
                AppendLog($"[AUTO] Checking step {step + 1}/{_steps.Length}: {_steps[step]}");
                await Task.Delay(120, ct);

                switch (step)
                {
                    case 0:
                        break;

                    case 1:
                        ValidateTargetPage();
                        break;

                    case 2:
                        await ScanEnvironmentAsync();
                        ct.ThrowIfCancellationRequested();
                        if (!_scanCompleted) throw new InvalidOperationException("Environment scan did not complete.");
                        break;

                    case 3:
                        if (string.Equals(_profile.BuildMode, "Fast Local Test", StringComparison.OrdinalIgnoreCase))
                        {
                            AppendLog("[AUTO][LOCAL] IIS check skipped; Fast Local Test does not touch IIS.");
                            break;
                        }
                        _iisState = await _environment.CheckIisAsync();
                        if (!_iisState.Installed)
                            throw new InvalidOperationException("IIS is not installed. Automatic deployment stopped on the IIS step. Install/enable IIS here, then run Auto Deploy again.");
                        if (_profile.TargetMode != "Remote IIS Server") await _deployment.ValidateIisPreflightAsync(_profile, ct);
                        break;

                    case 4:
                        await RefreshRuntimeStatesAsync();
                        var needsSdk = _profile.BuildMode != "Prepared IIS Publish";
                        var needsNode = needsSdk && _profile.RequiresNode;
                        var localOnly = string.Equals(_profile.BuildMode, "Fast Local Test", StringComparison.OrdinalIgnoreCase);
                        if ((!localOnly && !_hostingState.Installed) || (needsSdk && !_sdkState.Installed) || (needsNode && !_nodeState.Installed))
                            throw new InvalidOperationException("One or more required runtimes/build prerequisites are missing. Automatic deployment stopped on Installers & Runtimes. Prepare/install the missing prerequisite, then run Auto Deploy again.");
                        if (!localOnly && _sqlInstances.Count == 0 && !File.Exists(_profile.SqlInstallerPath))
                            throw new InvalidOperationException("SQL Server is not installed and no SQL Server installer is prepared. Automatic deployment stopped on Installers & Runtimes.");
                        break;

                    case 5:
                        if (string.Equals(_profile.BuildMode, "Fast Local Test", StringComparison.OrdinalIgnoreCase)) { AppendLog("[AUTO][LOCAL] SQL & Database step skipped."); break; }
                        if (string.IsNullOrWhiteSpace(_profile.SqlInstance) && _sqlInstances.Count > 0) _profile.SqlInstance = _sqlInstances[0].ServerName;
                        if (string.IsNullOrWhiteSpace(_profile.SqlInstance)) throw new InvalidOperationException("No SQL Server instance is selected.");
                        if (string.IsNullOrWhiteSpace(_profile.DatabaseName)) throw new InvalidOperationException("Database name is required.");
                        await _sql.TestConnectionWithSafeFallbackAsync(_profile);
                        
                        break;

                    case 6:
                        if (string.Equals(_profile.BuildMode, "Fast Local Test", StringComparison.OrdinalIgnoreCase)) { AppendLog("[AUTO][LOCAL] Deployment location step skipped."); break; }
                        ValidateDeploymentLocationPage();
                        _deploymentLocationAutoVerified = true;
                        break;

                    case 7:
                        if (!string.Equals(_profile.BuildMode, "Fast Local Test", StringComparison.OrdinalIgnoreCase)) ValidateIisSitePage();
                        AppendLog("[AUTO] All pre-deployment stages passed. Starting deployment automatically.");
                        await DeployAsync();
                        return;
                }

                AppendLog($"[AUTO] PASS — {_steps[step]}");
            }
        }
        catch (OperationCanceledException)
        {
            AppendLog($"[AUTO] CANCELLED by user at step {_pageIndex + 1}/{_steps.Length}: {_steps[Math.Clamp(_pageIndex, 0, _steps.Length - 1)]}");
            _activity.Text = "Automatic deployment cancelled. You can go Back/Next or start Auto Deploy again.";
        }
        catch (Exception ex)
        {
            var offlineMissing = string.Equals(_profile.DependencyResolutionMode, "Offline Only", StringComparison.OrdinalIgnoreCase)
                && IsLikelyOfflineDependencyFailure(ex);
            if (offlineMissing)
            {
                _offlineDependenciesReady = false;
                _offlineDependenciesDetail = GenericWeb.DeploymentManager.Services.SecretRedactor.Redact(ex.Message);
                AppendLog("[AUTO][OFFLINE] Missing/incomplete local dependency repository detected by the real build. Returning to Deployment Settings.");
                ShowPage(1);
                MessageBox.Show(
                    this,
                    "A dependency required by the real build is missing from the Offline Repository.\r\n\r\n" +
                    "Go back to the Source & Dependencies page and use Sync Missing Online Once while Internet is available, then run Auto Deploy again.\r\n\r\n" +
                    GenericWeb.DeploymentManager.Services.SecretRedactor.Redact(ex.Message),
                    "Offline Repository needs attention",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
            }
            else
            {
                AppendLog($"[AUTO] STOPPED at step {_pageIndex + 1}/{_steps.Length} — {_steps[Math.Clamp(_pageIndex, 0, _steps.Length - 1)]}: {GenericWeb.DeploymentManager.Services.SecretRedactor.Redact(ex.Message)}");
                MessageBox.Show(
                    this,
                    "Automatic deployment stopped at this step because it needs attention.\r\n\r\n" + GenericWeb.DeploymentManager.Services.SecretRedactor.Redact(ex.Message) + "\r\n\r\nYou can go Back, fix the current step, or press Auto Deploy again.",
                    "Auto Deploy stopped",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
            }
        }
        finally
        {
            _autoDeployRunning = false;
            _autoDeploy.Text = originalText;
            _autoDeployCts?.Dispose();
            _autoDeployCts = null;
            UpdateNavigationState();
        }
    }

    private static bool IsLikelyOfflineDependencyFailure(Exception ex)
    {
        var text = ex.ToString();
        string[] markers =
        [
            "ENOTCACHED",
            "only-if-cached",
            "not cached",
            "not found in source",
            "Unable to find package",
            "NU1101",
            "NU1102",
            "offline cache",
            "Missing dependency"
        ];
        return markers.Any(marker => text.Contains(marker, StringComparison.OrdinalIgnoreCase));
    }

    private async Task<DeploymentService.OfflinePackageValidation> PrepareOfflineRepositoryWithProgressAsync(CancellationToken ct)
    {
        var repository = _profile.DeploymentRepositoryFolder;
        var nodeCache = Path.Combine(repository, "Node", "npm-cache");
        var nugetCache = Path.Combine(repository, "NuGet");
        var before = SafeDirectorySize(nodeCache) + SafeDirectorySize(nugetCache);
        var preparation = _deployment.PrepareOfflinePackageAsync(_profile, ct);

        while (!preparation.IsCompleted)
        {
            ct.ThrowIfCancellationRequested();
            var current = SafeDirectorySize(nodeCache) + SafeDirectorySize(nugetCache);
            var added = Math.Max(0, current - before);
            var latest = TryGetLatestRepositoryFile(repository);
            var latestText = string.IsNullOrWhiteSpace(latest) ? "scanning/downloading dependencies" : $"current: {latest}";
            _activity.Text = $"Offline repository: {FormatBytes(current)} total | +{FormatBytes(added)} this run | {latestText}";
            await Task.Delay(500, ct);
        }

        var result = await preparation;
        var after = SafeDirectorySize(nodeCache) + SafeDirectorySize(nugetCache);
        AppendLog($"[OFFLINE-PREP] Repository size: {FormatBytes(after)}; added this run: {FormatBytes(Math.Max(0, after - before))}.");
        _activity.Text = result.Ok ? $"OFFLINE BUILD READY — repository {FormatBytes(after)}" : "Offline repository preparation failed.";
        return result;
    }

    private static long SafeDirectorySize(string folder)
    {
        try
        {
            if (!Directory.Exists(folder)) return 0;
            long total = 0;
            foreach (var file in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories))
            {
                try { total += new FileInfo(file).Length; } catch (Exception ex) { InternalDiagnostics.Record(ex, "Measure file size: " + file); }
            }
            return total;
        }
        catch (Exception ex) { InternalDiagnostics.Record(ex, "Calculate directory size"); return 0; }
    }

    private static string TryGetLatestRepositoryFile(string repository)
    {
        try
        {
            if (!Directory.Exists(repository)) return string.Empty;
            var file = Directory.EnumerateFiles(repository, "*", SearchOption.AllDirectories)
                .Select(path => new FileInfo(path))
                .OrderByDescending(info => info.LastWriteTimeUtc)
                .FirstOrDefault();
            return file is null ? string.Empty : Path.GetRelativePath(repository, file.FullName);
        }
        catch (Exception ex) { InternalDiagnostics.Record(ex, "Read version metadata"); return string.Empty; }
    }

    private static string FormatBytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double value = Math.Max(0, bytes);
        var index = 0;
        while (value >= 1024 && index < units.Length - 1)
        {
            value /= 1024;
            index++;
        }
        return $"{value:0.##} {units[index]}";
    }

    private async Task NextAsync()
    {
        if (_operationRunning)
        {
            RequestDeploymentCancellation();
            return;
        }
        try
        {
            switch (_pageIndex)
            {
                case 0:
                    if (string.Equals(_profile.BuildMode, "Prepared IIS Publish", StringComparison.OrdinalIgnoreCase))
                    {
                        var prepared = DeploymentService.ValidatePreparedPublish(_profile.ReadyPublishFolder);
                        if (!prepared.Ok) throw new InvalidOperationException(prepared.Detail);
                    }
                    else
                    {
                        if (!Directory.Exists(_profile.SourceFolder)) throw new InvalidOperationException("Select a valid project source folder.");
                        _profile.ApiProjectPath = _environment.DetectApiProject(_profile.SourceFolder) ?? throw new InvalidOperationException("No ASP.NET Core Web project was found in this source.");
                    }
                    if (string.IsNullOrWhiteSpace(_profile.ApplicationName)) throw new InvalidOperationException("Application identity could not be determined.");
                    break;
                case 1:
                    ValidateTargetPage();
                    break;
                case 2:
                    if (!_scanCompleted) throw new InvalidOperationException("Run the environment scan before continuing.");
                    break;
                case 3:
                    _iisState = await _environment.CheckIisAsync();
                    if (!_iisState.Installed) throw new InvalidOperationException("IIS must be installed before continuing.");
                    break;
                case 4:
                    await RefreshRuntimeStatesAsync();
                    var needsSdk = _profile.BuildMode != "Prepared IIS Publish";
                    var needsNode = needsSdk && _profile.RequiresNode;
                    if (!_hostingState.Installed || (needsSdk && !_sdkState.Installed) || (needsNode && !_nodeState.Installed))
                        throw new InvalidOperationException("Install all prerequisites required by the selected build mode before continuing.");
                    if (_sqlInstances.Count == 0 && !File.Exists(_profile.SqlInstallerPath))
                        throw new InvalidOperationException("No SQL Server engine is installed. Download or choose a SQL Server Express installer before continuing.");
                    break;
                case 5:
                    if (string.IsNullOrWhiteSpace(_profile.SqlInstance)) throw new InvalidOperationException("Select or install a SQL Server instance.");
                    if (string.IsNullOrWhiteSpace(_profile.DatabaseName)) throw new InvalidOperationException("Database name is required.");
                    if (!_sqlAdminConnectionVerified || !_databaseConnectionVerified) throw new InvalidOperationException("Verify SQL Server and database connectivity before continuing.");
                    await _sql.TestConnectionWithSafeFallbackAsync(_profile);
                    break;
                case 6:
                    ValidateDeploymentLocationPage();
                    break;
                case 7:
                    if (!string.Equals(_profile.BuildMode, "Fast Local Test", StringComparison.OrdinalIgnoreCase)) ValidateIisSitePage();
                    await DeployAsync();
                    return;
            }
            var nextPage = _pageIndex + 1;
            if (_pageIndex == 2)
            {
                if (_iisAutoVerified) nextPage = Math.Max(nextPage, 4);
                if (_runtimesAutoVerified && nextPage == 4) nextPage = 5;
                if (_sqlDatabaseAutoVerified && nextPage == 5) nextPage = 6;
                if (_deploymentLocationAutoVerified && nextPage == 6) nextPage = 7;
                if (nextPage > _pageIndex + 1)
                    AppendLog($"[AUTO-VERIFY] Environment Scan skipped verified stages; continuing at {_steps[nextPage]}.");
            }
            ShowPage(nextPage);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, SecretRedactor.Redact(ex.Message), "Cannot continue", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            AppendLog("ERROR: " + GenericWeb.DeploymentManager.Services.SecretRedactor.Redact(ex.Message));
            UpdateNavigationState();
        }
    }

    private void BuildWelcomePage()
    {
        _pageTitle.Text = "Welcome";
        _pageDescription.Text = "Prepare a Windows machine, validate prerequisites, build an ASP.NET Core web source, and deploy it to IIS in a controlled step-by-step workflow.";
        var body = VerticalPanel();
        body.Controls.Add(InfoBox("What this wizard does", "• Detects the selected ASP.NET Core web project and its target .NET version\r\n• Checks and enables IIS\r\n• Reuses cached/local installers or downloads missing prerequisites\r\n• Detects or installs SQL Server Express\r\n• Creates/prepares the database\r\n• Supports automatic build, Visual Studio-prepared source, or an already-published folder\r\n• Publishes/deploys and configures IIS\r\n• Runs a health check before reporting success"));
        body.Controls.Add(InfoBox("Safe workflow", "Each prerequisite is validated before the Next button is enabled. Reusable installers and offline dependency packages are stored in the selected Deployment Repository, separate from the application source, so they can be reused across deployments."));
        _content.Controls.Add(body);
    }

    private void BuildProjectSourcePage()
    {
        _pageTitle.Text = "Select source and dependency strategy";
        _pageDescription.Text = "Choose the incoming source and all build/dependency settings here. Source preflight runs automatically. Persistent dependencies and stable build workspaces stay outside the versioned source folder.";
        var form = FormGrid();
        var source = new TextBox { Text = _profile.SourceFolder, Dock = DockStyle.Fill, ReadOnly = true, TabStop = false, BackColor = SystemColors.Window };
        var browse = new Button { Text = "Browse source...", AutoSize = true };
        var lockSource = new CheckBox { Text = "Lock source", AutoSize = true, Checked = false, Margin = new Padding(8, 7, 3, 3) };
        var saveProfile = new Button { Text = "Save Profile", AutoSize = true };
        var loadProfile = new Button { Text = "Load Profile", AutoSize = true };
        _saveProfileAction = saveProfile;
        _loadProfileAction = loadProfile;
        var exportSource = new Button { Text = "Export Clean Source ZIP", AutoSize = true };
        _lockSourceAction = lockSource;
        _exportSourceAction = exportSource;
        // A single compact action strip stays opposite the Deployment profile label.
        foreach (var action in new Control[] { saveProfile, loadProfile, lockSource, exportSource })
        {
            action.Font = new Font("Segoe UI", 8.5F);
            action.Margin = new Padding(2, 1, 3, 1);
        }
        var profileActions = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, WrapContents = false, FlowDirection = FlowDirection.LeftToRight };
        // Actions are exposed through the top menu; no duplicate controls on the page.
        var sourceRow = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 2 };
        sourceRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); sourceRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        sourceRow.Controls.Add(source, 0, 0); sourceRow.Controls.Add(browse, 1, 0);

        var detectedRoot = new Label { AutoSize = true, MaximumSize = new Size(850, 0), ForeColor = Color.DimGray };
        var app = new TextBox { Width = 260 };
        var site = new TextBox { Width = 260 };
        var database = new TextBox { Width = 260 };
        var buildMode = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 300 };
        buildMode.Items.AddRange(new object[] { "Fast Local Test", "Fast IIS Update", "Full IIS Deploy", "Visual Studio prepared", "Prepared IIS Publish" });
        buildMode.SelectedItem = buildMode.Items.Cast<object>().FirstOrDefault(x => string.Equals(x.ToString(), _profile.BuildMode, StringComparison.OrdinalIgnoreCase)) ?? buildMode.Items[0];
        var dependencyMode = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 220 };
        dependencyMode.Items.AddRange(new object[] { "Offline Only", "Hybrid", "Online" });
        dependencyMode.SelectedItem = NormalizeDependencyMode(_profile.DependencyResolutionMode);
        var modeMeaning = new Label { AutoSize = true, MaximumSize = new Size(800, 0), ForeColor = Color.DarkOrange };
        var preparedPublish = new TextBox { Text = _profile.ReadyPublishFolder, Dock = DockStyle.Fill };
        var browsePreparedPublish = new Button { Text = "Browse prepared publish...", AutoSize = true };
        var preparedPublishRow = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 2 };
        preparedPublishRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); preparedPublishRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        preparedPublishRow.Controls.Add(preparedPublish, 0, 0); preparedPublishRow.Controls.Add(browsePreparedPublish, 1, 0);
        var preparedValidation = new Label { AutoSize = true, MaximumSize = new Size(800, 0), ForeColor = Color.DimGray, Text = "Select Prepared IIS Publish mode to validate a prebuilt IIS artifact." };

        var repository = new TextBox { Text = _profile.DeploymentRepositoryFolder, Dock = DockStyle.Fill };
        var browseRepository = new Button { Text = "Browse repository...", AutoSize = true };
        var repositoryRow = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 2 };
        repositoryRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); repositoryRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        repositoryRow.Controls.Add(repository, 0, 0); repositoryRow.Controls.Add(browseRepository, 1, 0);

        var workspace = new TextBox { Text = _profile.WorkspaceRoot, Dock = DockStyle.Fill };
        var browseWorkspace = new Button { Text = "Browse workspace...", AutoSize = true };
        var workspaceRow = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 2 };
        workspaceRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); workspaceRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        workspaceRow.Controls.Add(workspace, 0, 0); workspaceRow.Controls.Add(browseWorkspace, 1, 0);

        var offlineAction = new Button { Text = "Sync Missing Online Once", AutoSize = true };
        var offlineTest = new Button { Text = "Test Offline Build", AutoSize = true };
        var offlineState = new Label { AutoSize = true, ForeColor = Color.DimGray, MaximumSize = new Size(800, 0), Text = "Dependency readiness has not been checked yet." };
        var dependencySyncProgress = new ProgressBar { Width = 360, Height = 18, Style = ProgressBarStyle.Marquee, MarqueeAnimationSpeed = 28, Visible = false };
        var dependencySyncActivity = new Label { AutoSize = true, ForeColor = Color.RoyalBlue, MaximumSize = new Size(800, 0), Visible = false, Text = "Checking dependencies..." };

        AddRow(form, "Source folder", sourceRow);

        AddRow(form, "Detected source root", detectedRoot);
        AddRow(form, "Application name", app);
        AddRow(form, "IIS site name", site);
        AddRow(form, "Database name", database);
        AddRow(form, "Build / deployment mode", buildMode);
        AddRow(form, "Dependency mode", dependencyMode);
        AddRow(form, "Selected behavior", modeMeaning);
        AddRow(form, "Prepared publish folder", preparedPublishRow);
        AddRow(form, "Prepared publish validation", preparedValidation);
        AddRow(form, "Shared Dependency Repository", repositoryRow);
        AddRow(form, "Repository behavior", new Label { AutoSize = true, ForeColor = Color.DimGray, MaximumSize = new Size(800, 0), Text = "Persistent across source versions. NuGet/npm/installers/offline packages live here; the incoming source folder is never used as the dependency repository." });
        AddRow(form, "Stable Build Workspace", workspaceRow);
        AddRow(form, "Workspace behavior", new Label { AutoSize = true, ForeColor = Color.DimGray, MaximumSize = new Size(800, 0), Text = "Persistent build working area. Versioned incoming sources may be deleted after deployment without losing reusable dependency/build state." });
        AddRow(form, "Dependency actions", Wrap(offlineAction, offlineTest));
        AddRow(form, "Dependency status", offlineState);
        AddRow(form, "Dependency sync", Wrap(dependencySyncProgress, dependencySyncActivity));
        var sourceTestResult = new Label { AutoSize = true, MaximumSize = new Size(800, 0), ForeColor = Color.DimGray, Text = "Automatic source preflight starts as soon as a valid source folder is selected." };
        AddRow(form, "Automatic source preflight", sourceTestResult);
        _content.Controls.Add(form);

        void RefreshPreparedPublishValidation()
        {
            var prepared = string.Equals(_profile.BuildMode, "Prepared IIS Publish", StringComparison.OrdinalIgnoreCase);
            preparedPublish.Enabled = browsePreparedPublish.Enabled = prepared;
            repository.Enabled = browseRepository.Enabled = workspace.Enabled = browseWorkspace.Enabled = dependencyMode.Enabled = !prepared;
            if (!prepared) { preparedValidation.Text = "Not applicable for source-build mode."; preparedValidation.ForeColor = Color.DimGray; return; }
            _profile.ReadyPublishFolder = preparedPublish.Text.Trim();
            var validation = DeploymentService.ValidatePreparedPublish(_profile.ReadyPublishFolder);
            preparedValidation.Text = validation.Detail;
            preparedValidation.ForeColor = validation.Ok ? Color.ForestGreen : Color.Firebrick;
            if (validation.Ok && string.IsNullOrWhiteSpace(app.Text) && !string.IsNullOrWhiteSpace(validation.ApplicationBinary))
            {
                var inferred = Path.GetFileNameWithoutExtension(validation.ApplicationBinary);
                if (inferred.EndsWith(".Api", StringComparison.OrdinalIgnoreCase)) inferred = inferred[..^4];
                app.Text = inferred;
                var safe = new string(inferred.Where(char.IsLetterOrDigit).ToArray());
                if (string.IsNullOrWhiteSpace(site.Text)) site.Text = safe;
                if (string.IsNullOrWhiteSpace(database.Text)) database.Text = safe;
            }
            _profile.RequiresNode = false;
        }

        void Detect()
        {
            _profile.SourceFolder = source.Text.Trim();
            var api = Directory.Exists(_profile.SourceFolder) ? _environment.DetectApiProject(_profile.SourceFolder) : null;
            if (api is null)
            {
                detectedRoot.Text = "No ASP.NET Core Web project detected."; detectedRoot.ForeColor = Color.Firebrick;
                app.Text = site.Text = database.Text = string.Empty;
            }
            else
            {
                _profile.ApiProjectPath = api;
                var root = DeploymentService.ResolveCanonicalSourceRoot(_profile.SourceFolder, api);
                detectedRoot.Text = root + "\r\nWeb project: " + api; detectedRoot.ForeColor = Color.ForestGreen;
                var inferred = InferApplicationName(_profile.SourceFolder, api);
                app.Text = inferred;
                var safe = new string(inferred.Where(char.IsLetterOrDigit).ToArray());
                site.Text = safe; database.Text = safe;
            }
            UpdateNavigationState();
        }

        static bool IsGeneratedFrontendCandidate(string root, string file)
        {
            var rel = "/" + Path.GetRelativePath(root, file).Replace('\\','/').Trim('/') + "/";
            string[] excluded = { "/node_modules/", "/bin/", "/obj/", "/dist/", "/build/", "/out/", "/.vite/", "/iis-publish/", "/publish/" };
            return excluded.Any(x => rel.Contains(x, StringComparison.OrdinalIgnoreCase));
        }

        // Only one source preflight may publish results for the current page.
        // Cancellation does not interrupt every filesystem API, but it prevents stale
        // results from being applied after a source/mode change or page disposal.
        CancellationTokenSource? sourcePreflightCts = null;
        var sourcePreflightGeneration = 0;

        void CancelSourcePreflight()
        {
            sourcePreflightGeneration++;
            sourcePreflightCts?.Cancel();
        }

        sourceTestResult.Disposed += (_, _) => CancelSourcePreflight();

        void RunSourceTests()
        {
            CancelSourcePreflight();
            var request = new CancellationTokenSource();
            sourcePreflightCts = request;
            var generation = sourcePreflightGeneration;
            var selectedMode = _profile.BuildMode;
            var selectedSource = source.Text.Trim();
            var selectedPrepared = preparedPublish.Text.Trim();
            sourceTestResult.Text = "Running source preflight tests...";
            sourceTestResult.ForeColor = Color.RoyalBlue;
            _ = RunSourceTestsAsync(request, generation, selectedMode, selectedSource, selectedPrepared);
        }

        async Task RunSourceTestsAsync(CancellationTokenSource request, int generation, string mode, string selectedSource, string selectedPrepared)
        {
            try
            {
                // Coalesce consecutive programmatic TextChanged events and large-folder selections.
                await Task.Delay(200, request.Token);
                // Worker code never reads or writes WinForms controls or mutable profile fields.
                var result = await Task.Run(
                    () => InspectSourcePreflight(_environment, mode, selectedSource, selectedPrepared, request.Token),
                    request.Token);
                if (request.IsCancellationRequested || generation != sourcePreflightGeneration ||
                    IsDisposed || Disposing || sourceTestResult.IsDisposed || _pageIndex != 0 ||
                    !string.Equals(mode, _profile.BuildMode, StringComparison.Ordinal) ||
                    !string.Equals(selectedSource, source.Text.Trim(), StringComparison.Ordinal) ||
                    (string.Equals(mode, "Prepared IIS Publish", StringComparison.OrdinalIgnoreCase) &&
                     !string.Equals(selectedPrepared, preparedPublish.Text.Trim(), StringComparison.Ordinal)))
                    return;

                sourceTestResult.Text = string.Join(Environment.NewLine, result.Lines);
                sourceTestResult.ForeColor = result.Failures == 0 ? Color.ForestGreen : Color.Firebrick;
                if (string.Equals(mode, "Prepared IIS Publish", StringComparison.OrdinalIgnoreCase))
                    RefreshPreparedPublishValidation();
                else
                    AppendLog($"Source preflight completed: {Math.Max(0, result.Lines.Length - result.Failures)} passed, {result.Failures} failed.");
            }
            catch (OperationCanceledException) when (request.IsCancellationRequested) { }
            catch (Exception ex)
            {
                InternalDiagnostics.Record(ex, "Source preflight background task");
                if (!request.IsCancellationRequested && generation == sourcePreflightGeneration &&
                    !IsDisposed && !Disposing && !sourceTestResult.IsDisposed && _pageIndex == 0)
                {
                    sourceTestResult.Text = "FAIL — " + SecretRedactor.Redact(ex.Message);
                    sourceTestResult.ForeColor = Color.Firebrick;
                }
            }
            finally
            {
                if (ReferenceEquals(sourcePreflightCts, request)) sourcePreflightCts = null;
                request.Dispose();
            }
        }

        static (string[] Lines, int Failures) InspectSourcePreflight(
            EnvironmentService environment, string mode, string selectedSource, string selectedPrepared, CancellationToken ct)
        {
            var lines = new List<string>();
            var failures = 0;
            try
            {
                ct.ThrowIfCancellationRequested();
                if (string.Equals(mode, "Prepared IIS Publish", StringComparison.OrdinalIgnoreCase))
                {
                    var validation = DeploymentService.ValidatePreparedPublish(selectedPrepared);
                    ct.ThrowIfCancellationRequested();
                    lines.Add(validation.Ok ? "PASS — " + validation.Detail : "FAIL — " + validation.Detail);
                    if (!validation.Ok) failures++;
                    return (lines.ToArray(), failures);
                }
                if (!Directory.Exists(selectedSource))
                    throw new DirectoryNotFoundException("Select an existing source folder first.");
                var api = environment.DetectApiProject(selectedSource);
                ct.ThrowIfCancellationRequested();
                var structureOk = !string.IsNullOrWhiteSpace(api);
                lines.Add($"{(structureOk ? "PASS" : "FAIL")} — Web project structure: {(structureOk ? api : "ASP.NET Core Web .csproj not found")}");
                if (!structureOk) failures++;
                var frameworkOk = structureOk && File.Exists(api);
                var major = frameworkOk ? environment.DetectTargetFrameworkMajor(api!) : 0;
                lines.Add($"{(frameworkOk ? "PASS" : "FAIL")} — Target framework: {(frameworkOk ? ".NET " + major : "cannot detect without Web project")}");
                if (!frameworkOk) failures++;
                ct.ThrowIfCancellationRequested();
                var required = environment.DetectNodeRequirement(selectedSource, api ?? string.Empty);
                var packages = Directory.EnumerateFiles(selectedSource, "package.json", SearchOption.AllDirectories)
                    .Where(p => { ct.ThrowIfCancellationRequested(); return !IsGeneratedFrontendCandidate(selectedSource, p); })
                    .Take(10).ToArray();
                var viteRoots = packages.Select(Path.GetDirectoryName).Where(p => !string.IsNullOrWhiteSpace(p))
                    .Where(p => File.Exists(Path.Combine(p!, "index.html")) &&
                        Directory.EnumerateFiles(p!, "vite.config.*", SearchOption.TopDirectoryOnly).Any())
                    .ToArray();
                ct.ThrowIfCancellationRequested();
                lines.Add($"PASS — Frontend / Node assets: {(required ? "detected" : "not required")} ({packages.Length} source package.json candidate(s))");
                if (required && viteRoots.Length > 0) lines.Add($"PASS — Frontend root: {viteRoots[0]}");
                else if (required) { lines.Add("FAIL — Frontend detected but no complete source frontend root (index.html + vite.config.*) was found."); failures++; }
                _ = Directory.EnumerateFileSystemEntries(selectedSource, "*", SearchOption.TopDirectoryOnly).Take(1).ToArray();
                ct.ThrowIfCancellationRequested();
                lines.Add("PASS — Source access/readability");
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                failures++;
                InternalDiagnostics.Record(ex, "Source preflight tests");
                lines.Add("FAIL — " + SecretRedactor.Redact(ex.Message));
            }
            return (lines.ToArray(), failures);
        }

        void ApplyDependencyMode()
        {
            _profile.DependencyResolutionMode = NormalizeDependencyMode(dependencyMode.SelectedItem?.ToString());
            _profile.OfflineBuild = string.Equals(_profile.DependencyResolutionMode, "Offline Only", StringComparison.OrdinalIgnoreCase);
            _appData.SaveDependencyResolutionMode(_profile.DependencyResolutionMode);
            _offlineDependenciesReady = false;
            offlineState.ForeColor = Color.DimGray;
            offlineState.Text = _profile.DependencyResolutionMode switch
            {
                "Offline Only" => "Local Repository -> System Cache -> FAIL. Use Sync Missing Online Once while Internet is available, then Test Offline Build.",
                "Hybrid" => "Local Repository -> System Cache -> Internet fallback. Sync is optional but recommended for future offline use.",
                _ => "Online mode: package restore may use Internet sources; offline readiness is not required."
            };
            var sourceBuild = !string.Equals(_profile.BuildMode, "Prepared IIS Publish", StringComparison.OrdinalIgnoreCase);
            offlineAction.Enabled = sourceBuild && _profile.DependencyResolutionMode != "Online" && !_dependencySyncRunning;
            offlineTest.Enabled = sourceBuild && _profile.DependencyResolutionMode == "Offline Only" && !_dependencySyncRunning;
            UpdateNavigationState();
        }

        lockSource.CheckedChanged += (_, _) =>
        {
            // The path is always browse-only; locking also prevents folder changes.
            browse.Enabled = !lockSource.Checked;
            lockSource.Text = lockSource.Checked ? "Source locked" : "Lock source";
        };
        saveProfile.Click += (_, _) =>
        {
            Directory.CreateDirectory(ProfilesFolder);
            using var dialog = new SaveFileDialog { Title = "Save deployment profile", Filter = "Deployment profile (*.json)|*.json", InitialDirectory = ProfilesFolder, FileName = string.IsNullOrWhiteSpace(_profile.ApplicationName) ? "deployment-profile.json" : _profile.ApplicationName.Replace(" ", "-") + ".json" };
            if (dialog.ShowDialog(this) != DialogResult.OK) return;
            var dto = DeploymentProfileSnapshot.From(_profile, lockSource.Checked);
            File.WriteAllText(dialog.FileName, JsonSerializer.Serialize(dto, new JsonSerializerOptions { WriteIndented = true }));
            AppendLog("[PROFILE] Saved: " + dialog.FileName + " (secrets excluded)");
        };
        loadProfile.Click += (_, _) =>
        {
            Directory.CreateDirectory(ProfilesFolder);
            using var dialog = new OpenFileDialog { Title = "Load deployment profile", Filter = "Deployment profile (*.json)|*.json", InitialDirectory = ProfilesFolder };
            if (dialog.ShowDialog(this) != DialogResult.OK) return;
            try
            {
                var dto = JsonSerializer.Deserialize<DeploymentProfileSnapshot>(File.ReadAllText(dialog.FileName)) ?? throw new InvalidDataException("Profile is empty.");
                dto.ApplyTo(_profile);
                source.Text = _profile.SourceFolder; app.Text = _profile.ApplicationName; site.Text = _profile.SiteName; database.Text = _profile.DatabaseName;
                repository.Text = _profile.DeploymentRepositoryFolder; workspace.Text = _profile.WorkspaceRoot; preparedPublish.Text = _profile.ReadyPublishFolder;
                buildMode.SelectedItem = buildMode.Items.Cast<object>().FirstOrDefault(x => string.Equals(x.ToString(), _profile.BuildMode, StringComparison.OrdinalIgnoreCase)) ?? buildMode.Items[0];
                dependencyMode.SelectedItem = NormalizeDependencyMode(_profile.DependencyResolutionMode);
                lockSource.Checked = dto.SourceLocked;
                AppendLog("[PROFILE] Loaded: " + dialog.FileName + " (secrets remain session-only)");
            }
            catch (Exception ex) { InternalDiagnostics.Record(ex, "Load deployment profile"); MessageBox.Show(this, SecretRedactor.Redact(ex.Message), "Profile", MessageBoxButtons.OK, MessageBoxIcon.Error); }
        };
        exportSource.Click += (_, _) =>
        {
            var src = source.Text.Trim(); if (!Directory.Exists(src)) { MessageBox.Show(this, "Select a valid source folder first.", "Clean Source Export", MessageBoxButtons.OK, MessageBoxIcon.Warning); return; }
            using var options = new CleanExportOptionsForm(); if (options.ShowDialog(this) != DialogResult.OK) return;
            using var dialog = new SaveFileDialog { Title = "Export clean source ZIP", Filter = "ZIP archive (*.zip)|*.zip", FileName = $"{SanitizeFileName(_profile.ApplicationName)}-Last-Sources.zip" };
            if (dialog.ShowDialog(this) != DialogResult.OK) return;
            try { ExportCleanSourceZip(src, dialog.FileName, options.Exclusions); MessageBox.Show(this, "Clean source ZIP created successfully.\r\n" + dialog.FileName, "Clean Source Export", MessageBoxButtons.OK, MessageBoxIcon.Information); AppendLog("[EXPORT] Clean source ZIP: " + dialog.FileName); }
            catch (Exception ex) { InternalDiagnostics.Record(ex, "Clean source export"); MessageBox.Show(this, SecretRedactor.Redact(ex.Message), "Clean Source Export", MessageBoxButtons.OK, MessageBoxIcon.Error); }
        };

        browse.Click += (_, _) =>
        {
            using var dialog = new FolderBrowserDialog { Description = "Select the new project release/source folder", SelectedPath = BrowseStartFolder(source.Text, BrowseContext.Source) };
            if (dialog.ShowDialog(this) == DialogResult.OK) { source.Text = dialog.SelectedPath; RememberBrowseSelection(dialog.SelectedPath, BrowseContext.Source); }
        };
        browsePreparedPublish.Click += (_, _) =>
        {
            using var dialog = new FolderBrowserDialog { Description = "Select the final prepared ASP.NET Core IIS publish folder", SelectedPath = BrowseStartFolder(preparedPublish.Text, BrowseContext.Publish) };
            if (dialog.ShowDialog(this) == DialogResult.OK) { preparedPublish.Text = dialog.SelectedPath; RememberBrowseSelection(dialog.SelectedPath, BrowseContext.Publish); }
        };
        void CommitAndValidatePreparedPublish()
        {
            var generation = ++_preparedValidationGeneration;
            var selected = preparedPublish.Text.Trim();
            try { selected = string.IsNullOrWhiteSpace(selected) ? string.Empty : Path.GetFullPath(selected); } catch { }
            _profile.ReadyPublishFolder = selected;
            if (preparedPublish.Text != selected) { preparedPublish.Text = selected; return; }
            RefreshPreparedPublishValidation();
            if (generation != _preparedValidationGeneration) return; // stale UI event must not overwrite newer selection
            if (string.Equals(_profile.BuildMode, "Prepared IIS Publish", StringComparison.OrdinalIgnoreCase)) RunSourceTests();
            UpdateNavigationState();
        }
        preparedPublish.TextChanged += (_, _) => CommitAndValidatePreparedPublish();
        browseRepository.Click += (_, _) =>
        {
            using var dialog = new FolderBrowserDialog { Description = @"Select persistent Shared Dependency Repository (for example G:\Developer-Packages)", SelectedPath = BrowseStartFolder(repository.Text, BrowseContext.Repository) };
            if (dialog.ShowDialog(this) == DialogResult.OK) { repository.Text = dialog.SelectedPath; RememberBrowseSelection(dialog.SelectedPath, BrowseContext.Repository); }
        };
        browseWorkspace.Click += (_, _) =>
        {
            using var dialog = new FolderBrowserDialog { Description = "Select the persistent Stable Build Workspace root", SelectedPath = BrowseStartFolder(workspace.Text, BrowseContext.Workspace) };
            if (dialog.ShowDialog(this) == DialogResult.OK) { workspace.Text = dialog.SelectedPath; RememberBrowseSelection(dialog.SelectedPath, BrowseContext.Workspace); }
        };
        source.TextChanged += async (_, _) =>
        {
            Detect();
            _offlineDependenciesReady = false;
            _offlineDependenciesDetail = "Source changed; dependency readiness must be rechecked if required.";
            _backgroundVerifyCts?.Cancel(); _backgroundVerifyCts?.Dispose(); _backgroundVerifyCts = new CancellationTokenSource();
            var token = _backgroundVerifyCts.Token;
            if (Directory.Exists(source.Text.Trim()))
            {
                RunSourceTests();
                try { await Task.Delay(250, token); if (!token.IsCancellationRequested) await RunBackgroundAutoVerificationAsync(token); }
                catch (OperationCanceledException) { }
            }
            else { CancelSourcePreflight(); sourceTestResult.Text = "Waiting for a valid source folder..."; sourceTestResult.ForeColor = Color.DimGray; }
        };
        repository.TextChanged += (_, _) =>
        {
            _profile.DeploymentRepositoryFolder = repository.Text.Trim();
            _profile.InstallerCacheFolder = Path.Combine(_profile.DeploymentRepositoryFolder, "Installers");
            if (!string.IsNullOrWhiteSpace(_profile.DeploymentRepositoryFolder))
                QueueDeferredSettingsSave("repository", () => _appData.SaveDeploymentRepositoryFolder(_profile.DeploymentRepositoryFolder));
            _offlineDependenciesReady = false;
            UpdateNavigationState();
        };
        workspace.TextChanged += (_, _) =>
        {
            _profile.WorkspaceRoot = workspace.Text.Trim();
            if (!string.IsNullOrWhiteSpace(_profile.WorkspaceRoot))
                QueueDeferredSettingsSave("workspace", () => _appData.SaveWorkspaceRoot(_profile.WorkspaceRoot));
            UpdateNavigationState();
        };
        app.TextChanged += (_, _) => { _profile.ApplicationName = app.Text.Trim(); UpdateNavigationState(); };
        repository.Leave += (_, _) => FlushDeferredSettingsSaves();
        workspace.Leave += (_, _) => FlushDeferredSettingsSaves();
        site.TextChanged += (_, _) => { _profile.SiteName = site.Text.Trim(); _profile.AppPoolName = _profile.SiteName; UpdateNavigationState(); };
        database.TextChanged += (_, _) => { _profile.DatabaseName = database.Text.Trim(); UpdateNavigationState(); };
        void RefreshModeMeaning() => modeMeaning.Text = _profile.BuildMode switch
        {
            "Fast Local Test" => "Compile/test only. The IIS website is NOT updated, so the browser can still show the previously hosted version.",
            "Fast IIS Update" => "IIS deployment with fresh frontend build (when needed) and dotnet publish. Hosted-release reuse and fingerprint-based build skips are disabled pending release/rollback safety verification; dependency caches may still be reused.",
            "Full IIS Deploy" => "Complete publish, IIS/database preparation, health check and rollback protection.",
            "Prepared IIS Publish" => "Direct deploy of an already-built IIS publish. npm, frontend build, dotnet restore/build/publish and dependency sync are bypassed.",
            _ => "Uses the selected prepared-source/publish workflow."
        };
        buildMode.SelectedIndexChanged += (_, _) => { _profile.BuildMode = buildMode.SelectedItem?.ToString() ?? "Fast Local Test"; RefreshModeMeaning(); ApplyDependencyMode(); if (_profile.BuildMode == "Prepared IIS Publish") CommitAndValidatePreparedPublish(); else { RefreshPreparedPublishValidation(); UpdateNavigationState(); RunSourceTests(); } };
        dependencyMode.SelectedIndexChanged += (_, _) => ApplyDependencyMode();
        RefreshModeMeaning();
        RefreshPreparedPublishValidation();

        offlineAction.Click += async (_, _) =>
        {
            if (_dependencySyncRunning) return;
            _dependencySyncRunning = true;
            try
            {
                _profile.SourceFolder = source.Text.Trim();
                if (!Directory.Exists(_profile.SourceFolder)) throw new InvalidOperationException("Select a valid source folder first.");
                if (string.IsNullOrWhiteSpace(_profile.DeploymentRepositoryFolder)) throw new InvalidOperationException("Select a persistent Shared Dependency Repository first.");
                _profile.ApiProjectPath = _environment.DetectApiProject(_profile.SourceFolder) ?? throw new InvalidOperationException("No ASP.NET Core Web .csproj was found under the selected source folder.");
                _profile.TargetFrameworkMajor = _environment.DetectTargetFrameworkMajor(_profile.ApiProjectPath);
                _profile.RequiresNode = _environment.DetectNodeRequirement(_profile.SourceFolder, _profile.ApiProjectPath);
                offlineAction.Enabled = false; offlineTest.Enabled = false;
                dependencySyncProgress.Visible = true; dependencySyncActivity.Visible = true;
                dependencySyncActivity.Text = "Checking NuGet and npm dependencies; downloading missing items...";
                offlineState.Text = "SYNC IN PROGRESS — reusable packages are being prepared in the Shared Dependency Repository.";
                offlineState.ForeColor = Color.RoyalBlue; _activity.Text = "Dependency sync running...";
                UpdateNavigationState();
                AppendLog("[ONLINE-ONCE] Operator requested one-time synchronization of missing dependencies.");
                var result = await PrepareOfflineRepositoryWithProgressAsync(CancellationToken.None);
                _offlineDependenciesReady = result.Ok; _offlineDependenciesDetail = result.Detail;
                offlineState.Text = result.Ok ? "OFFLINE REPOSITORY READY ✓ — " + result.Detail : "OFFLINE REPOSITORY INCOMPLETE ✕ — " + result.Detail;
                offlineState.ForeColor = result.Ok ? Color.ForestGreen : Color.Firebrick;
                dependencySyncActivity.Text = result.Ok ? "Dependency sync completed successfully." : "Dependency sync finished with missing/incomplete dependencies.";
                AppendLog(result.Ok ? "[ONLINE-ONCE] COMPLETE — OFFLINE REPOSITORY READY." : "[ONLINE-ONCE] COMPLETE — repository is still incomplete: " + result.Detail);
            }
            catch (Exception ex)
            {
                _offlineDependenciesReady = false; _offlineDependenciesDetail = GenericWeb.DeploymentManager.Services.SecretRedactor.Redact(ex.Message);
                offlineState.Text = "OFFLINE REPOSITORY INCOMPLETE ✕ — " + GenericWeb.DeploymentManager.Services.SecretRedactor.Redact(ex.Message); offlineState.ForeColor = Color.Firebrick;
                dependencySyncActivity.Text = "Dependency sync failed."; AppendLog("[ONLINE-ONCE] FAILED: " + GenericWeb.DeploymentManager.Services.SecretRedactor.Redact(ex.Message));
                InternalDiagnostics.Record(ex, "Source page dependency sync");
            }
            finally
            {
                _dependencySyncRunning = false; dependencySyncProgress.Visible = false;
                ApplyDependencyMode();
                _activity.Text = _offlineDependenciesReady ? "Offline repository ready." : "Dependency sync finished; repository needs attention.";
                ApplyInteractiveCursors(this);
            }
        };
        offlineTest.Click += async (_, _) =>
        {
            try
            {
                _profile.SourceFolder = source.Text.Trim();
                if (!Directory.Exists(_profile.SourceFolder)) throw new InvalidOperationException("Select a valid source folder first.");
                _profile.ApiProjectPath = _environment.DetectApiProject(_profile.SourceFolder) ?? throw new InvalidOperationException("No ASP.NET Core Web .csproj was found under the selected source folder.");
                _profile.TargetFrameworkMajor = _environment.DetectTargetFrameworkMajor(_profile.ApiProjectPath);
                _profile.RequiresNode = _environment.DetectNodeRequirement(_profile.SourceFolder, _profile.ApiProjectPath);
                offlineTest.Enabled = false; offlineState.Text = "Testing with online package sources disabled...";
                AppendLog("[OFFLINE-TEST] Starting local-only dependency restore test.");
                var result = await _deployment.ValidateOfflinePackageAsync(_profile);
                _offlineDependenciesReady = result.Ok; _offlineDependenciesDetail = result.Detail;
                offlineState.Text = result.Ok ? "OFFLINE BUILD READY ✓" : "OFFLINE BUILD FAILED ✕ — " + result.Detail;
                offlineState.ForeColor = result.Ok ? Color.ForestGreen : Color.Firebrick;
            }
            catch (Exception ex)
            {
                _offlineDependenciesReady = false; _offlineDependenciesDetail = GenericWeb.DeploymentManager.Services.SecretRedactor.Redact(ex.Message);
                offlineState.Text = "OFFLINE BUILD FAILED ✕ — " + GenericWeb.DeploymentManager.Services.SecretRedactor.Redact(ex.Message); offlineState.ForeColor = Color.Firebrick;
                AppendLog("[OFFLINE-TEST] FAILED: " + GenericWeb.DeploymentManager.Services.SecretRedactor.Redact(ex.Message)); InternalDiagnostics.Record(ex, "Source page offline dependency test");
            }
            finally { ApplyDependencyMode(); }
        };

        RefreshModeMeaning();
        ApplyDependencyMode();
        Detect();
        if (Directory.Exists(source.Text.Trim()) || string.Equals(_profile.BuildMode, "Prepared IIS Publish", StringComparison.OrdinalIgnoreCase))
        {
            // Defer the first asynchronous preflight until the form has a UI
            // message pump; later page visits already have a window handle.
            if (IsHandleCreated) RunSourceTests();
            else Shown += (_, _) => { if (!sourceTestResult.IsDisposed) RunSourceTests(); };
        }
    }

    private void BuildTargetPage()
    {
        _pageTitle.Text = "Target and deployment identity";
        _pageDescription.Text = "Configure deployment identity, local/remote target, and prepared publish input when applicable.";
        var form = FormGrid();
        var sourceAlreadySelected = Directory.Exists(_profile.SourceFolder);
        var appName = new TextBox { Text = sourceAlreadySelected ? _profile.ApplicationName : string.Empty, Width = 210 };
        var siteName = new TextBox { Text = sourceAlreadySelected ? _profile.SiteName : string.Empty, Width = 210 };
        var databaseName = new TextBox { Text = sourceAlreadySelected ? _profile.DatabaseName : string.Empty, Width = 210 };
        var targetMode = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 175 };
        targetMode.Items.AddRange(new object[] { "Local Server", "Remote IIS Server" });
        targetMode.SelectedItem = _profile.TargetMode;
        var host = new Ipv4AddressControl { Text = _profile.TargetMode == "Remote IIS Server" ? _profile.TargetHost : string.Empty };
        var remoteUser = new TextBox { Text = _profile.RemoteUserName, Width = 180 };
        var remotePassword = new TextBox { Text = _profile.RemotePassword, Width = 170, UseSystemPasswordChar = true };
        var remotePort = new NumericUpDown { Minimum = 1, Maximum = 65535, Value = _profile.RemoteWinRmPort, Width = 82 };
        var remoteSsl = new CheckBox { Text = "HTTPS / SSL", Checked = _profile.RemoteUseSsl, AutoSize = true };
        var savedRemote = _appData.GetRemoteCredential();
        var rememberRemote = new CheckBox { Text = "Save securely", Checked = savedRemote.Remember, AutoSize = true };
        var testRemote = new Button { Text = "Test / Bootstrap Remote", Width = 170 };
        var remoteState = new Label { AutoSize = true, ForeColor = Color.DimGray, MaximumSize = new Size(690,0), Text = "Remote readiness not tested." };
        var publish = new TextBox { Text = _profile.ReadyPublishFolder, Dock = DockStyle.Fill };
        var browsePublish = new Button { Text = "Browse publish folder...", AutoSize = true };
        var detected = new Label { AutoSize = true, ForeColor = Color.DimGray, MaximumSize = new Size(650, 0) };
        var framework = new Label { AutoSize = true, ForeColor = Color.DimGray };
        var node = new Label { AutoSize = true, ForeColor = Color.DimGray };
        var modeHelp = new Label { AutoSize = true, ForeColor = Color.DimGray, MaximumSize = new Size(700, 0) };

        var identityRow = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, WrapContents = false };
        identityRow.Controls.AddRange(new Control[]
        {
            new Label { Text = "App", AutoSize = true, Padding = new Padding(0, 7, 4, 0) }, appName,
            new Label { Text = "Site", AutoSize = true, Padding = new Padding(10, 7, 4, 0) }, siteName,
            new Label { Text = "Database", AutoSize = true, Padding = new Padding(10, 7, 4, 0) }, databaseName
        });
        AddRow(form, "Identity", identityRow);
        AddRow(form, "Database behavior", new Label { AutoSize = true, ForeColor = Color.DimGray, MaximumSize = new Size(760, 0), Text = "Changing the database name connects the application to a different database; Deployment Manager does not delete or migrate data from the previous database automatically." });

        var targetRow = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, WrapContents = false };
        targetRow.Controls.AddRange(new Control[]
        {
            targetMode,
            new Label { Text = "Host / IP", AutoSize = true, Padding = new Padding(10, 7, 3, 0) }, host,
            new Label { Text = "User", AutoSize = true, Padding = new Padding(8, 7, 3, 0) }, remoteUser,
            new Label { Text = "Password", AutoSize = true, Padding = new Padding(8, 7, 3, 0) }, remotePassword,
            new Label { Text = "WinRM", AutoSize = true, Padding = new Padding(8, 7, 3, 0) }, remotePort,
            remoteSsl, rememberRemote, testRemote
        });
        AddRow(form, "Deployment target", targetRow);
        AddRow(form, "Remote readiness", remoteState);

        var publishRow = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 2 };
        publishRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); publishRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        publishRow.Controls.Add(publish, 0, 0); publishRow.Controls.Add(browsePublish, 1, 0);
        AddRow(form, "Detected Web project", detected);
        AddRow(form, "Target framework", framework);
        AddRow(form, "Frontend", node);
        AddRow(form, "Mode behavior", modeHelp);
        _content.Controls.Add(form);

        void RefreshDetected()
        {
            var ready = _profile.BuildMode == "Prepared IIS Publish";
            publish.Enabled = browsePublish.Enabled = ready;
            if (ready)
            {
                detected.Text = "Prepared Publish: VALIDATION REQUIRED — source build is bypassed.";
                framework.Text = "Runtime/Hosting Bundle is still validated on the target machine.";
                node.Text = "Node.js/npm not required by Deployment Manager.";
                modeHelp.Text = "Use this when Visual Studio or another build machine has already produced a publish folder. The wizard only performs server/database/IIS deployment steps.";
                _profile.RequiresNode = false;
            }
            else
            {
                var api = _environment.DetectApiProject(_profile.SourceFolder);
                detected.Text = api ?? "Not detected";
                if (api is not null)
                {
                    _profile.ApiProjectPath = api;
                    _profile.TargetFrameworkMajor = _environment.DetectTargetFrameworkMajor(api);
                    _profile.RequiresNode = _environment.DetectNodeRequirement(_profile.SourceFolder, api);
                    framework.Text = $".NET {_profile.TargetFrameworkMajor}";
                    node.Text = _profile.RequiresNode ? "Node.js/npm detected" : "No Node.js frontend detected";
                }
                else { framework.Text = "—"; node.Text = "—"; }
                modeHelp.Text = _profile.BuildMode == "Visual Studio prepared"
                    ? "Open/build the solution once in Visual Studio first. Deployment Manager skips dependency restore where possible, but still performs the final frontend build (if needed) and dotnet publish."
                    : _profile.BuildMode == "Fast Local Test"
                        ? "Fast Local Test performs only change-aware frontend and incremental backend builds. It does not run dotnet publish, stage files, modify IIS, or prepare rollback."
                        : _profile.BuildMode == "Fast IIS Update"
                            ? "Fast IIS Update currently runs a fresh frontend build (if required) and dotnet publish before IIS activation. Hosted-binary/fingerprint skip shortcuts are disabled until release and rollback integrity are proven."
                            : "Full IIS Deploy creates a new publish and performs the complete IIS and database workflow.";
            }
            UpdateNavigationState();
        }

        bool IsValidIpv4(string value) => IPAddress.TryParse(value?.Trim(), out var ip) && ip.AddressFamily == AddressFamily.InterNetwork && string.Equals(ip.ToString(), value?.Trim(), StringComparison.Ordinal);
        bool ValidateRemoteInputs(bool showMessage)
        {
            if (!string.Equals(targetMode.SelectedItem?.ToString(), "Remote IIS Server", StringComparison.OrdinalIgnoreCase)) return true;
            var ipText = host.Text.Trim(); string? error = null; Control? focus = null;
            if (string.IsNullOrWhiteSpace(ipText) || !IsValidIpv4(ipText)) { error = "Enter a valid IPv4 address for the remote IIS server (example: 192.168.1.50)."; focus = host; }
            else if (string.IsNullOrWhiteSpace(remoteUser.Text)) { error = "Remote User is required before testing or continuing."; focus = remoteUser; }
            else if (string.IsNullOrWhiteSpace(remotePassword.Text)) { error = "Remote Password is required before testing or continuing."; focus = remotePassword; }
            if (error is null) return true;
            remoteState.Text = "Remote target validation failed — " + error; remoteState.ForeColor = Color.Firebrick;
            if (showMessage) MessageBox.Show(this, error, "Remote target validation", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            focus?.Focus(); return false;
        }
        void ApplyTargetModeState()
        {
            _profile.TargetMode = targetMode.SelectedItem?.ToString() ?? "Local Server";
            var remoteSelected = _profile.TargetMode == "Remote IIS Server";
            host.Enabled = remoteUser.Enabled = remotePassword.Enabled = remotePort.Enabled = remoteSsl.Enabled = rememberRemote.Enabled = testRemote.Enabled = remoteSelected;
            remoteState.Text = remoteSelected ? "Remote readiness not tested." : "Local deployment selected.";
            remoteState.ForeColor = Color.DimGray;
            if (!remoteSelected) { _profile.TargetHost = Environment.MachineName; host.Text = string.Empty; }
        }
        targetMode.SelectedIndexChanged += (_, _) =>
        {
            // The remote host control is cleared in Local mode. Commit any
            // pending remote credentials while their original host is visible.
            FlushDeferredSettingsSaves();
            ApplyTargetModeState();
            UpdateNavigationState();
        };
        ApplyTargetModeState();
        void QueueRememberedRemoteCredential()
        {
            // Host is not a credential when the Local Server mode is selected.
            // Do not overwrite a stored remote host with the cleared local UI.
            if (rememberRemote.Checked &&
                string.Equals(targetMode.SelectedItem?.ToString(), "Remote IIS Server", StringComparison.OrdinalIgnoreCase))
                QueueDeferredSettingsSave("remote-credential",
                    () => _appData.SaveRemoteCredential(host.Text.Trim(), remoteUser.Text.Trim(), remotePassword.Text, true));
        }
        remoteUser.TextChanged += (_, _) => { _profile.RemoteUserName = remoteUser.Text.Trim(); QueueRememberedRemoteCredential(); UpdateNavigationState(); };
        remotePassword.TextChanged += (_, _) => { _profile.RemotePassword = remotePassword.Text; QueueRememberedRemoteCredential(); UpdateNavigationState(); };
        remotePort.ValueChanged += (_, _) => _profile.RemoteWinRmPort = (int)remotePort.Value;
        remoteSsl.CheckedChanged += (_, _) => _profile.RemoteUseSsl = remoteSsl.Checked;
        rememberRemote.CheckedChanged += (_, _) =>
        {
            // Opting out must revoke the persisted secret immediately; do not
            // allow a queued Save(true) to re-create it after the checkbox is off.
            CancelDeferredSettingsSave("remote-credential");
            _appData.SaveRemoteCredential(host.Text.Trim(), remoteUser.Text.Trim(), remotePassword.Text, rememberRemote.Checked);
        };
        remoteUser.Leave += (_, _) => FlushDeferredSettingsSaves();
        remotePassword.Leave += (_, _) => FlushDeferredSettingsSaves();
        host.Leave += (_, _) => FlushDeferredSettingsSaves();
        testRemote.Click += async (_, _) =>
        {
            if (!ValidateRemoteInputs(showMessage: true)) return;
            try
            {
                testRemote.Enabled = false; remoteState.Text = "Checking remote server...";
                var rr = await _remote.CheckReadinessAsync(_profile);
                if (!rr.Connected && IsCredentialFailure(rr.Detail)) throw new InvalidOperationException("Remote username or password is incorrect.");
                remoteState.Text = rr.Detail; remoteState.ForeColor = rr.Connected && rr.IsAdministrator ? Color.ForestGreen : Color.Firebrick;
                if (rr.Connected && rr.IsAdministrator && (!rr.IisInstalled || !rr.HostingBundleInstalled))
                {
                    remoteState.Text += " Preparing missing prerequisites..."; await _remote.BootstrapAsync(_profile);
                    var rr2 = await _remote.CheckReadinessAsync(_profile); remoteState.Text = rr2.Detail;
                    remoteState.ForeColor = rr2.Connected && rr2.IsAdministrator && rr2.IisInstalled && rr2.HostingBundleInstalled ? Color.ForestGreen : Color.DarkOrange;
                }
            }
            catch (Exception ex) { remoteState.Text = "Remote NOT READY — " + GenericWeb.DeploymentManager.Services.SecretRedactor.Redact(ex.Message); remoteState.ForeColor = Color.Firebrick; InternalDiagnostics.Record(ex, "Remote readiness test"); }
            finally { testRemote.Enabled = string.Equals(targetMode.SelectedItem?.ToString(), "Remote IIS Server", StringComparison.OrdinalIgnoreCase); }
        };
        browsePublish.Click += (_, _) =>
        {
            using var dialog = new FolderBrowserDialog { Description = "Select the already-published application folder", SelectedPath = BrowseStartFolder(publish.Text, BrowseContext.Publish) };
            if (dialog.ShowDialog(this) == DialogResult.OK) { publish.Text = dialog.SelectedPath; RememberBrowseSelection(dialog.SelectedPath, BrowseContext.Publish); }
        };
        appName.TextChanged += (_, _) =>
        {
            var previousApplicationName = _profile.ApplicationName; _profile.ApplicationName = appName.Text.Trim();
            _profile.HostingRoot = UpdateAutoHostingRootForApplicationName(_profile.HostingRoot, previousApplicationName, _profile.ApplicationName);
            QueueDeferredSettingsSave("identity", () => _appData.SaveDeploymentIdentitySettings(_profile.ApplicationName, _profile.SiteName, _profile.DatabaseName));
            QueueDeferredSettingsSave("hosting", () => _appData.SaveHostingSettings(_profile.HostingRoot, _profile.DeploymentLayout, _profile.CleanupPreviousDeployments, _profile.UseDefaultIisWebsite));
        };
        siteName.TextChanged += (_, _) => { _profile.SiteName = siteName.Text.Trim(); _profile.AppPoolName = _profile.SiteName; QueueDeferredSettingsSave("identity", () => _appData.SaveDeploymentIdentitySettings(_profile.ApplicationName, _profile.SiteName, _profile.DatabaseName)); UpdateNavigationState(); };
        databaseName.TextChanged += (_, _) => { _profile.DatabaseName = databaseName.Text.Trim(); QueueDeferredSettingsSave("identity", () => _appData.SaveDeploymentIdentitySettings(_profile.ApplicationName, _profile.SiteName, _profile.DatabaseName)); UpdateNavigationState(); };
        appName.Leave += (_, _) => FlushDeferredSettingsSaves();
        siteName.Leave += (_, _) => FlushDeferredSettingsSaves();
        databaseName.Leave += (_, _) => FlushDeferredSettingsSaves();
        host.TextChanged += (_, _) =>
        {
            _profile.TargetHost = host.Text.Trim();
            if (string.Equals(targetMode.SelectedItem?.ToString(), "Remote IIS Server", StringComparison.OrdinalIgnoreCase))
            {
                var valid = IsValidIpv4(host.Text); remoteState.Text = valid ? "IPv4 format valid. Remote readiness not tested." : "Enter IPv4 only, for example 192.168.1.50.";
                remoteState.ForeColor = valid ? Color.DimGray : Color.Firebrick;
            }
            QueueRememberedRemoteCredential();
            UpdateNavigationState();
        };
        publish.TextChanged += (_, _) => { _profile.ReadyPublishFolder = publish.Text.Trim(); UpdateNavigationState(); };
        RefreshDetected();
    }

    private void ValidateTargetPage()
    {
        if (string.IsNullOrWhiteSpace(_profile.ApplicationName)) throw new InvalidOperationException("Application name is required.");
        if (string.IsNullOrWhiteSpace(_profile.SiteName)) throw new InvalidOperationException("IIS site name is required.");
        if (string.IsNullOrWhiteSpace(_profile.DatabaseName)) throw new InvalidOperationException("Database name is required.");
        _profile.AppPoolName = _profile.SiteName;
        _appData.SaveDeploymentIdentitySettings(_profile.ApplicationName, _profile.SiteName, _profile.DatabaseName);
        if (string.Equals(_profile.TargetMode, "Remote IIS Server", StringComparison.OrdinalIgnoreCase))
        {
            if (!IPAddress.TryParse(_profile.TargetHost, out var ip) || ip.AddressFamily != AddressFamily.InterNetwork)
                throw new InvalidOperationException("A valid IPv4 address is required for a remote IIS server.");
        }
        else if (!_environment.IsLocalTarget(_profile.TargetHost))
            throw new InvalidOperationException("Local deployment must target this computer.");
        if (_profile.BuildMode == "Prepared IIS Publish")
        {
            var prepared = DeploymentService.ValidatePreparedPublish(_profile.ReadyPublishFolder);
            if (!prepared.Ok) throw new InvalidOperationException(prepared.Detail);
            _profile.RequiresNode = false;
            return;
        }
        if (!Directory.Exists(_profile.SourceFolder)) throw new InvalidOperationException("Source folder does not exist.");
        _profile.ApiProjectPath = _environment.DetectApiProject(_profile.SourceFolder) ?? throw new InvalidOperationException("No ASP.NET Core Web .csproj was found under the selected source folder.");
        _profile.TargetFrameworkMajor = _environment.DetectTargetFrameworkMajor(_profile.ApiProjectPath);
        _profile.RequiresNode = _environment.DetectNodeRequirement(_profile.SourceFolder, _profile.ApiProjectPath);
    }

    private async Task RunBackgroundAutoVerificationAsync(CancellationToken ct)
    {
        if (_operationRunning || _autoDeployRunning) return;
        try
        {
            AppendLog("[AUTO-VERIFY] Background verification started after source selection.");
            await ScanEnvironmentAsync(); ct.ThrowIfCancellationRequested();
            if (_sqlInstances.Count > 0 && string.IsNullOrWhiteSpace(_profile.SqlInstance)) _profile.SqlInstance = _sqlInstances[0].ServerName;
            if (!string.IsNullOrWhiteSpace(_profile.SqlInstance) && !string.IsNullOrWhiteSpace(_profile.DatabaseName))
            {
                try { await _sql.TestConnectionWithSafeFallbackAsync(_profile);  _healthMonitor.ReportSqlSuccess("Background SQL/database verification passed"); }
                catch (Exception ex) {  AppendLog("[AUTO-VERIFY] SQL/database needs attention: " + GenericWeb.DeploymentManager.Services.SecretRedactor.Redact(ex.Message)); }
            }
            try { ValidateDeploymentLocationPage(); _deploymentLocationAutoVerified = true; }
            catch { _deploymentLocationAutoVerified = false; }
            if (!IsDisposed && !Disposing) BeginInvoke(() => { UpdateStepIndicator(); UpdateNavigationState(); });
            AppendLog("[AUTO-VERIFY] Background verification completed.");
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { AppendLog("[AUTO-VERIFY] Background verification stopped: " + GenericWeb.DeploymentManager.Services.SecretRedactor.Redact(ex.Message)); }
    }

    private void BuildScanPage()
    {
        _pageTitle.Text = "Environment scan";
        _pageDescription.Text = "Inspect the machine before changing anything. The results are shown as separate, readable status panels.";
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 2 };
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        var scan = new Button { Text = "Re-scan environment", AutoSize = true };
        root.Controls.Add(scan, 0, 0);

        var cards = new TableLayoutPanel { Dock = DockStyle.Fill, AutoScroll = true, ColumnCount = 2, RowCount = 3, Padding = new Padding(0, 12, 0, 0) };
        cards.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        cards.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        for (int i = 0; i < 3; i++) cards.RowStyles.Add(new RowStyle(SizeType.Percent, 33.333F));

        var win = StatusCard("Windows", _environment.GetWindowsDescription(), true);
        var iis = StatusCard("IIS Web Server", _iisState.Detail, _iisState.Installed);
        var hosting = StatusCard($"ASP.NET Core Hosting Bundle (.NET {_profile.TargetFrameworkMajor})", _hostingState.Detail, _hostingState.Installed);
        var sdk = StatusCard($".NET {_profile.TargetFrameworkMajor} SDK", _sdkState.Detail, _sdkState.Installed);
        var node = StatusCard("Node.js / npm", _nodeState.Detail, _nodeState.Installed);
        var sql = StatusCard("SQL Server Engine", _sqlInstances.Count == 0 ? "Not scanned" : string.Join("\r\n", _sqlInstances), _sqlInstances.Count > 0);
        cards.Controls.Add(win, 0, 0); cards.Controls.Add(iis, 1, 0);
        cards.Controls.Add(hosting, 0, 1); cards.Controls.Add(sdk, 1, 1);
        cards.Controls.Add(node, 0, 2); cards.Controls.Add(sql, 1, 2);
        root.Controls.Add(cards, 0, 1);
        _content.Controls.Add(root);

        scan.Click += async (_, _) =>
        {
            scan.Enabled = false;
            try
            {
                await ScanEnvironmentAsync();
                ReplaceCard(cards, 0, 0, StatusCard("Windows", _environment.GetWindowsDescription(), true));
                ReplaceCard(cards, 1, 0, StatusCard("IIS Web Server", _iisState.Detail, _iisState.Installed));
                ReplaceCard(cards, 0, 1, StatusCard($"ASP.NET Core Hosting Bundle (.NET {_profile.TargetFrameworkMajor})", _hostingState.Detail, _hostingState.Installed));
                ReplaceCard(cards, 1, 1, StatusCard($".NET {_profile.TargetFrameworkMajor} SDK", _sdkState.Detail, _sdkState.Installed));
                ReplaceCard(cards, 0, 2, StatusCard("Node.js / npm", _nodeState.Detail, _nodeState.Installed));
                ReplaceCard(cards, 1, 2, StatusCard("SQL Server Engine", _sqlInstances.Count == 0 ? "No SQL Server Database Engine instance detected." : string.Join("\r\n", _sqlInstances), _sqlInstances.Count > 0));
            }
            catch (Exception ex)
            {
                AppendLog("SCAN ERROR: " + ex);
                MessageBox.Show(this, SecretRedactor.Redact(ex.Message), "Scan failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally { scan.Enabled = true; UpdateNavigationState(); }
        };

        // Environment Scan is automatic on page entry. The button remains available only for an explicit re-scan.
        BeginInvoke(new Action(scan.PerformClick));
    }

    private async Task ScanEnvironmentAsync()
    {
        ValidateTargetPage();
        _iisState = await _environment.CheckIisAsync();
        _hostingState = _environment.CheckHostingBundle(_profile.TargetFrameworkMajor);
        var readyPublish = _profile.BuildMode == "Prepared IIS Publish";
        _sdkState = readyPublish ? new PrerequisiteState(true, "Not required — build is skipped because a ready publish folder was selected.") : await _environment.CheckDotNetSdkAsync(_profile.TargetFrameworkMajor);
        _nodeState = readyPublish ? new PrerequisiteState(true, "Not required — build is skipped because a ready publish folder was selected.") : await _environment.CheckNodeAsync(_profile.RequiresNode);
        _sqlInstances = await _sql.DetectInstancesAsync();
        // V67: Environment Scan is the gatekeeper. Only stages whose actual
        // acceptance criteria are already proven are marked Auto verified.
        _iisAutoVerified = _iisState.Installed;
        var needsSdk = !readyPublish;
        var needsNode = needsSdk && _profile.RequiresNode;
        _runtimesAutoVerified = _hostingState.Installed && (!needsSdk || _sdkState.Installed) && (!needsNode || _nodeState.Installed);
        _scanCompleted = true;
        AutoLocateCachedInstallers();
        UpdateStepIndicator();
    }

    private void BuildIisPage()
    {
        _pageTitle.Text = "IIS Web Server";
        _pageDescription.Text = "Detect/enable IIS and, when needed, manage test sites directly. Advanced IIS Management can start, stop, or completely remove selected IIS sites and their physical files.";
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 3 };
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        var status = StatusCard("IIS status", _iisState.Detail, _iisState.Installed);
        root.Controls.Add(status, 0, 0);
        var buttons = new FlowLayoutPanel { AutoSize = true, WrapContents = false };
        var install = new Button { Text = "Install / enable IIS", AutoSize = true, Enabled = !_iisState.Installed };
        var refresh = new Button { Text = "Re-check IIS", AutoSize = true };
        var advanced = new CheckBox { Text = "Advanced IIS Management", AutoSize = true, Margin = new Padding(20, 7, 3, 3), Enabled = _iisState.Installed };
        buttons.Controls.Add(install); buttons.Controls.Add(refresh); buttons.Controls.Add(advanced);
        var recoveryReview = new Button { Text = "Review & Resolve Previous Deployment", AutoSize = true };
        buttons.Controls.Add(recoveryReview);
        recoveryReview.Click += async (_, _) =>
        {
            if (_operationRunning || _autoDeployRunning || _dependencySyncRunning) return;
            using var dialog = new Form { Text = "Review previous deployment", Width = 680, Height = 260, StartPosition = FormStartPosition.CenterParent };
            var layout = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, Padding = new Padding(12) };
            var explanation = new Label { AutoSize = true, MaximumSize = new Size(620, 0), Text = "Enter the prior resolved attempt ID from the diagnostic report. Independently verify the database and backup before confirming. IIS, HTTP, API and frontend identity will be checked again. Unresolved journals remain blocked." };
            var owner = new TextBox { Width = 580 };
            var verified = new CheckBox { AutoSize = true, Text = "I verified the database and backup/recovery evidence." };
            var confirm = new Button { Text = "Verify and archive markers", AutoSize = true, DialogResult = DialogResult.OK };
            layout.Controls.AddRange(new Control[] { explanation, owner, verified, confirm });
            dialog.Controls.Add(layout);
            if (dialog.ShowDialog(this) != DialogResult.OK || !verified.Checked || string.IsNullOrWhiteSpace(owner.Text)) return;
            _operationRunning = true;
            recoveryReview.Enabled = false;
            UpdateNavigationState();
            try
            {
                await _deployment.ReviewResolvedDeploymentAsync(_profile, owner.Text.Trim());
                AppendLog("[RECOVERY] Verified prior resolved deployment; markers archived. Retry Auto Deploy.");
                MessageBox.Show(this, "Recovery markers archived. Retry Auto Deploy.", "Recovery review", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception ex) { AppendLog("[RECOVERY] " + ex.Message); MessageBox.Show(this, ex.Message, "Recovery review blocked", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
            finally { _operationRunning = false; recoveryReview.Enabled = true; UpdateNavigationState(); }
        };
        root.Controls.Add(buttons, 0, 1);

        var management = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 3, Visible = _iisState.Installed, Padding = new Padding(0, 10, 0, 0) };
        management.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        management.RowStyles.Add(new RowStyle(SizeType.Percent, 55));
        management.RowStyles.Add(new RowStyle(SizeType.Percent, 45));
        var mgmtActions = new FlowLayoutPanel { AutoSize = true, WrapContents = false };
        var refreshSites = new Button { Text = "Refresh sites", AutoSize = true };
        var startSites = new Button { Text = "Start selected", AutoSize = true };
        var stopSites = new Button { Text = "Stop selected", AutoSize = true };
        var removeSites = new Button { Text = "Remove selected + cleanup files", AutoSize = true, ForeColor = Color.Firebrick };
        var lastIisCheck = new Label { Text = "Last checked: —", AutoSize = true, ForeColor = Color.DimGray, Padding = new Padding(12,7,0,0) };
        mgmtActions.Controls.AddRange(new Control[] { refreshSites, startSites, stopSites, removeSites, lastIisCheck });
        management.Controls.Add(mgmtActions, 0, 0);
        var grid = new DataGridView
        {
            Dock = DockStyle.Fill, ReadOnly = false, AllowUserToAddRows = false, AllowUserToDeleteRows = false,
            AutoGenerateColumns = false, RowHeadersVisible = false, SelectionMode = DataGridViewSelectionMode.FullRowSelect,
            MultiSelect = true, BackgroundColor = Color.White, BorderStyle = BorderStyle.FixedSingle
        };
        grid.Columns.Add(new DataGridViewCheckBoxColumn { Name = "Pick", HeaderText = "", Width = 38 });
        grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Site", HeaderText = "IIS Site", Width = 170, ReadOnly = true });
        grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "State", HeaderText = "State", Width = 85, ReadOnly = true });
        grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Bindings", HeaderText = "Bindings / Ports", Width = 190, ReadOnly = true });
        grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Path", HeaderText = "Physical Path", AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill, ReadOnly = true });
        grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Pool", HeaderText = "App Pool", Width = 155, ReadOnly = true });
        management.Controls.Add(grid, 0, 1);
        var poolPanel = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 2 };
        poolPanel.RowStyles.Add(new RowStyle(SizeType.AutoSize)); poolPanel.RowStyles.Add(new RowStyle(SizeType.Percent,100));
        var poolActions = new FlowLayoutPanel { AutoSize=true, WrapContents=false };
        var startPool=new Button{Text="Start pool",AutoSize=true}; var stopPool=new Button{Text="Stop pool",AutoSize=true}; var recyclePool=new Button{Text="Recycle pool",AutoSize=true}; var deletePool=new Button{Text="Delete unused pool",AutoSize=true,ForeColor=Color.Firebrick};
        poolActions.Controls.AddRange(new Control[]{startPool,stopPool,recyclePool,deletePool}); poolPanel.Controls.Add(poolActions,0,0);
        var poolGrid=new DataGridView{Dock=DockStyle.Fill,ReadOnly=true,AllowUserToAddRows=false,RowHeadersVisible=false,SelectionMode=DataGridViewSelectionMode.FullRowSelect,MultiSelect=false,AutoGenerateColumns=false};
        poolGrid.Columns.Add(new DataGridViewTextBoxColumn{Name="PoolName",HeaderText="Application Pool",AutoSizeMode=DataGridViewAutoSizeColumnMode.Fill}); poolGrid.Columns.Add(new DataGridViewTextBoxColumn{Name="PoolState",HeaderText="State",Width=120});
        poolPanel.Controls.Add(poolGrid,0,1); management.Controls.Add(poolPanel,0,2);
        // V66: IIS target configuration is unified into this page; the separate IIS Site step was removed.
        var targetSettings = new GroupBox { Text = "Target IIS configuration", Dock = DockStyle.Bottom, AutoSize = true, Padding = new Padding(10) };
        var targetForm = FormGrid();
        var targetPort = new NumericUpDown { Minimum = 1, Maximum = 65535, Value = _profile.UseDefaultIisWebsite ? 80 : Math.Clamp(_profile.HttpPort, 1, 65535), Width = 120, Enabled = !_profile.UseDefaultIisWebsite };
        var targetHost = new TextBox { Text = _profile.HostHeader, Dock = DockStyle.Fill, Enabled = !_profile.UseDefaultIisWebsite };
        var targetHealth = new TextBox { Text = _profile.HealthPath, Dock = DockStyle.Fill };
        AddRow(targetForm, "Hosted site folder", new TextBox { Text = _profile.HostingRoot, Dock = DockStyle.Fill, ReadOnly = true });
        AddRow(targetForm, "IIS site name", new TextBox { Text = _profile.SiteName, Dock = DockStyle.Fill, ReadOnly = true });
        AddRow(targetForm, "Application pool", new TextBox { Text = _profile.AppPoolName, Dock = DockStyle.Fill, ReadOnly = true });
        AddRow(targetForm, "HTTP port", targetPort); AddRow(targetForm, "Host header", targetHost); AddRow(targetForm, "Health-check path", targetHealth);
        targetSettings.Controls.Add(targetForm);
        targetPort.ValueChanged += (_, _) => { _profile.HttpPort = _profile.UseDefaultIisWebsite ? 80 : (int)targetPort.Value; UpdateNavigationState(); };
        targetHost.TextChanged += (_, _) => _profile.HostHeader = _profile.UseDefaultIisWebsite ? string.Empty : targetHost.Text.Trim();
        targetHealth.TextChanged += (_, _) => _profile.HealthPath = targetHealth.Text.Trim();
        management.Controls.Add(targetSettings, 0, 3); management.RowCount = 4; management.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.Controls.Add(management, 0, 2);
        _content.Controls.Add(root);

        async Task RefreshIisStatus()
        {
            _iisState = await _environment.CheckIisAsync();
            status.Controls.Clear();
            var replacement = StatusCard("IIS status", _iisState.Detail, _iisState.Installed);
            foreach (Control c in replacement.Controls.Cast<Control>().ToArray()) { replacement.Controls.Remove(c); status.Controls.Add(c); }
            install.Enabled = !_iisState.Installed; advanced.Enabled = _iisState.Installed;
            if (!_iisState.Installed) { advanced.Checked = false; management.Visible = false; }
            UpdateNavigationState();
        }

        async Task LoadSites()
        {
            if (!_iisState.Installed) return;
            refreshSites.Enabled = false;
            try
            {
                var sites = await _environment.ListIisSitesAsync();
                if (grid.IsDisposed || grid.Disposing || !grid.IsHandleCreated || grid.Parent is null || grid.Columns.Count != 6) return;
                grid.SuspendLayout();
                try
                {
                    grid.Rows.Clear();
                    foreach (var x in sites)
                    {
                        if (grid.IsDisposed || grid.Disposing || grid.Parent is null || grid.Columns.Count != 6) return;
                        var i = grid.Rows.Add(false, x.Name, x.State, x.Bindings, x.PhysicalPath, x.AppPool);
                        grid.Rows[i].Tag = x;
                    }
                }
                finally { if (!grid.IsDisposed) grid.ResumeLayout(); }
                var pools = await _environment.ListIisAppPoolsAsync();
                if (poolGrid.IsDisposed || poolGrid.Disposing || !poolGrid.IsHandleCreated || poolGrid.Parent is null || poolGrid.Columns.Count != 2) return;
                poolGrid.Rows.Clear(); foreach(var ap in pools){ if(poolGrid.IsDisposed || poolGrid.Columns.Count != 2) return; var r=poolGrid.Rows.Add(ap.Name,ap.State); poolGrid.Rows[r].Tag=ap; }
                lastIisCheck.Text = "Last checked: " + DateTime.Now.ToString("HH:mm:ss");
            }
            catch (Exception ex) { AppendLog("[IIS] Could not enumerate sites: " + GenericWeb.DeploymentManager.Services.SecretRedactor.Redact(ex.Message)); MessageBox.Show(this, SecretRedactor.Redact(ex.Message), "IIS site list", MessageBoxButtons.OK, MessageBoxIcon.Error); }
            finally { refreshSites.Enabled = true; }
        }

        List<IisSiteInfo> PickedSites() => grid.Rows.Cast<DataGridViewRow>()
            .Where(r => Convert.ToBoolean(r.Cells["Pick"].Value ?? false) && r.Tag is IisSiteInfo)
            .Select(r => (IisSiteInfo)r.Tag!).ToList();

        install.Click += async (_, _) => await BusyAsync(install, async () => { AppendLog("Enabling IIS features with DISM..."); await _environment.InstallIisAsync(); await RefreshIisStatus(); });
        refresh.Click += async (_, _) => { await RefreshIisStatus(); if (advanced.Checked) await LoadSites(); };
        advanced.Checked = _iisState.Installed;
        advanced.CheckedChanged += async (_, _) => { if (_iisState.Installed && advanced.Checked) { management.Visible = true; await LoadSites(); } else management.Visible = false; };
        if (_iisState.Installed) BeginInvoke(new Action(async () => { if (!grid.IsDisposed && grid.Parent is not null) await LoadSites(); }));
        refreshSites.Click += async (_, _) => await LoadSites();
        IisAppPoolInfo? SelectedPool() => poolGrid.CurrentRow?.Tag as IisAppPoolInfo;
        startPool.Click += async (_, _) => { var x=SelectedPool(); if(x is null)return; await BusyAsync(startPool, async () => { await _environment.StartIisAppPoolAsync(x.Name); await LoadSites(); }); };
        stopPool.Click += async (_, _) => { var x=SelectedPool(); if(x is null)return; await BusyAsync(stopPool, async () => { await _environment.StopIisAppPoolAsync(x.Name); await LoadSites(); }); };
        recyclePool.Click += async (_, _) => { var x=SelectedPool(); if(x is null)return; await BusyAsync(recyclePool, async () => { await _environment.RecycleIisAppPoolAsync(x.Name); await LoadSites(); }); };
        deletePool.Click += async (_, _) => { var x=SelectedPool(); if(x is null)return; if(MessageBox.Show(this,$"Delete unused application pool '{x.Name}'?","Delete App Pool",MessageBoxButtons.YesNo,MessageBoxIcon.Warning,MessageBoxDefaultButton.Button2)!=DialogResult.Yes)return; try{await _environment.DeleteIisAppPoolAsync(x.Name);await LoadSites();}catch(Exception ex){MessageBox.Show(this,GenericWeb.DeploymentManager.Services.SecretRedactor.Redact(ex.Message),"Delete App Pool",MessageBoxButtons.OK,MessageBoxIcon.Error);} };
        startSites.Click += async (_, _) => await BusyAsync(startSites, async () =>
        {
            foreach (var x in PickedSites()) await _environment.StartIisSiteAsync(x.Name);
            await LoadSites();
        });
        stopSites.Click += async (_, _) => await BusyAsync(stopSites, async () =>
        {
            foreach (var x in PickedSites()) await _environment.StopIisSiteAsync(x.Name);
            await LoadSites();
        });
        removeSites.Click += async (_, _) =>
        {
            var picked = PickedSites();
            if (picked.Count == 0) { MessageBox.Show(this, "Tick at least one IIS site first.", "IIS cleanup", MessageBoxButtons.OK, MessageBoxIcon.Information); return; }
            var list = string.Join("\r\n", picked.Select(x => $"• {x.Name} — {x.Bindings}\r\n  {x.PhysicalPath}"));
            var answer = MessageBox.Show(this,
                "The selected IIS site configuration, its unused dedicated App Pool, AND its physical site folder/files will be removed. Databases are not deleted.\r\n\r\n" + list + "\r\n\r\nContinue?",
                "Remove IIS site + cleanup files", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2);
            if (answer != DialogResult.Yes) return;
            if (picked.Any(x => !string.IsNullOrWhiteSpace(x.PhysicalPath) && string.Equals(Path.GetFullPath(Environment.ExpandEnvironmentVariables(x.PhysicalPath.Trim('"'))).TrimEnd('\\'), @"C:\inetpub\wwwroot", StringComparison.OrdinalIgnoreCase)))
            {
                var second = MessageBox.Show(this, "C:\\inetpub\\wwwroot is included. This will delete the files currently in the IIS default web root. Confirm cleanup?", "Confirm wwwroot cleanup", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2);
                if (second != DialogResult.Yes) return;
            }
            removeSites.Enabled = false;
            try
            {
                foreach (var x in picked) await _environment.RemoveIisSiteAndFilesAsync(x, deletePhysicalFiles: true);
                AppendLog($"[IIS CLEANUP] Removed {picked.Count} selected site(s) and cleaned their physical files.");
                await LoadSites();
            }
            catch (Exception ex) { AppendLog("[IIS CLEANUP] " + ex); MessageBox.Show(this, SecretRedactor.Redact(ex.Message), "IIS cleanup failed", MessageBoxButtons.OK, MessageBoxIcon.Error); }
            finally { removeSites.Enabled = true; }
        };
    }

    private void BuildInstallersPage()
    {
        _pageTitle.Text = "Installers and runtimes";
        _pageDescription.Text = "Reuse local/cached installers whenever possible. If a download fails, open the official download page, download the file manually, then choose it here.";

        AutoLocateCachedInstallers();
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 3 };
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        var cacheRow = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, WrapContents = false };
        var cacheText = new TextBox { Text = _profile.InstallerCacheFolder, Width = 570 };
        var browseCache = new Button { Text = "Choose installer folder...", AutoSize = true };
        var openCache = new Button { Text = "Open folder", AutoSize = true };
        cacheRow.Controls.Add(new Label { Text = "Installer cache", AutoSize = true, Padding = new Padding(0, 7, 8, 0) });
        cacheRow.Controls.Add(cacheText); cacheRow.Controls.Add(browseCache); cacheRow.Controls.Add(openCache);
        root.Controls.Add(cacheRow, 0, 0);

        var versionRow = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, WrapContents = false, Padding = new Padding(0, 8, 0, 4) };
        var sqlVersion = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 250 };
        sqlVersion.Items.AddRange(CompatibleSqlVersions().Cast<object>().ToArray());
        var currentIndex = sqlVersion.Items.IndexOf(_profile.SqlExpressVersion);
        sqlVersion.SelectedIndex = currentIndex >= 0 ? currentIndex : 0;
        var sqlInstalled = _sqlInstances.Count > 0;
        sqlVersion.Enabled = !sqlInstalled;
        versionRow.Controls.Add(new Label { Text = "SQL Express package", AutoSize = true, Padding = new Padding(0, 7, 8, 0) });
        versionRow.Controls.Add(sqlVersion);
        versionRow.Controls.Add(new Label
        {
            Text = sqlInstalled ? "Already Installed — " + InstalledSqlSummary() + "; SQL download is skipped." : "Not installed — select the package to download.",
            AutoSize = true,
            ForeColor = sqlInstalled ? Color.ForestGreen : Color.DimGray,
            Padding = new Padding(10, 7, 0, 0)
        });
        var downloadAll = new Button { Text = "Download all missing installers", AutoSize = true, Margin = new Padding(18, 3, 3, 3) };
        versionRow.Controls.Add(downloadAll);
        root.Controls.Add(versionRow, 0, 1);

        var list = VerticalPanel();
        list.AutoScroll = true;
        root.Controls.Add(list, 0, 2);
        _content.Controls.Add(root);

        void RebuildCards()
        {
            list.Controls.Clear();
            var hostingPackage = _prereq.HostingBundle(_profile.TargetFrameworkMajor);
            var sdkPackage = _prereq.DotNetSdk(_profile.TargetFrameworkMajor);
            var nodePackage = _prereq.Node();
            var sqlPackage = _prereq.Sql(_profile.SqlExpressVersion);

            list.Controls.Add(InstallerCard(hostingPackage, () => _profile.HostingBundleInstallerPath, x => _profile.HostingBundleInstallerPath = x,
                _hostingState, required: true, installAction: async path =>
                {
                    if (!(await _environment.CheckIisAsync()).Installed) throw new InvalidOperationException("IIS must be installed first.");
                    await _prereq.InstallHostingBundleAsync(path);
                    _hostingState = _environment.CheckHostingBundle(_profile.TargetFrameworkMajor);
                }));

            list.Controls.Add(InstallerCard(sdkPackage, () => _profile.DotNetSdkInstallerPath, x => _profile.DotNetSdkInstallerPath = x,
                _sdkState, required: _profile.BuildMode != "Prepared IIS Publish", installAction: async path =>
                {
                    await _prereq.InstallDotNetSdkAsync(path);
                    _sdkState = await _environment.CheckDotNetSdkAsync(_profile.TargetFrameworkMajor);
                }));

            list.Controls.Add(InstallerCard(nodePackage, () => _profile.NodeInstallerPath, x => _profile.NodeInstallerPath = x,
                _nodeState, required: _profile.BuildMode != "Prepared IIS Publish" && _profile.RequiresNode, installAction: async path =>
                {
                    await _prereq.InstallNodeAsync(path);
                    _nodeState = await _environment.CheckNodeAsync(_profile.RequiresNode);
                }));

            var sqlState = _sqlInstances.Count > 0
                ? new PrerequisiteState(true, "Already Installed — " + InstalledSqlSummary() + ". No replacement SQL Server package is required.")
                : new PrerequisiteState(File.Exists(_profile.SqlInstallerPath), File.Exists(_profile.SqlInstallerPath) ? "Installer ready for the next step." : "SQL Server is missing; choose or download an installer.");
            list.Controls.Add(InstallerCard(sqlPackage, () => _profile.SqlInstallerPath, x => _profile.SqlInstallerPath = x,
                sqlState, required: _sqlInstances.Count == 0, installAction: null, disableInstallerActionsWhenInstalled: true));
            UpdateNavigationState();
        }

        cacheText.TextChanged += (_, _) => { _profile.InstallerCacheFolder = cacheText.Text.Trim(); if (!string.IsNullOrWhiteSpace(_profile.InstallerCacheFolder)) _appData.SaveInstallerCacheFolder(_profile.InstallerCacheFolder); AutoLocateCachedInstallers(); RebuildCards(); };
        browseCache.Click += (_, _) =>
        {
            using var dialog = new FolderBrowserDialog { Description = "Choose a reusable installer cache folder", SelectedPath = BrowseStartFolder(cacheText.Text, BrowseContext.Repository) };
            if (dialog.ShowDialog(this) == DialogResult.OK) { cacheText.Text = dialog.SelectedPath; RememberBrowseSelection(dialog.SelectedPath, BrowseContext.Repository); }
        };
        openCache.Click += (_, _) =>
        {
            Directory.CreateDirectory(_profile.InstallerCacheFolder);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(_profile.InstallerCacheFolder) { UseShellExecute = true });
        };
        sqlVersion.SelectedIndexChanged += (_, _) =>
        {
            _profile.SqlExpressVersion = sqlVersion.SelectedItem?.ToString() ?? "SQL Server 2019 Express";
            _profile.SqlInstallerPath = _prereq.FindCached(_profile.InstallerCacheFolder, _prereq.Sql(_profile.SqlExpressVersion)) ?? string.Empty;
            RebuildCards();
        };
        downloadAll.Click += async (_, _) => await BusyAsync(downloadAll, async () =>
        {
            await RefreshRuntimeStatesAsync();
            var packages = new List<(InstallerDescriptor pkg, Func<string> get, Action<string> set, bool needed)> {
                (_prereq.HostingBundle(_profile.TargetFrameworkMajor), () => _profile.HostingBundleInstallerPath, x => _profile.HostingBundleInstallerPath=x, !_hostingState.Installed),
                (_prereq.DotNetSdk(_profile.TargetFrameworkMajor), () => _profile.DotNetSdkInstallerPath, x => _profile.DotNetSdkInstallerPath=x, _profile.BuildMode != "Prepared IIS Publish" && !_sdkState.Installed),
                (_prereq.Node(), () => _profile.NodeInstallerPath, x => _profile.NodeInstallerPath=x, _profile.BuildMode != "Prepared IIS Publish" && _profile.RequiresNode && !_nodeState.Installed),
                (_prereq.Sql(_profile.SqlExpressVersion), () => _profile.SqlInstallerPath, x => _profile.SqlInstallerPath=x, _sqlInstances.Count==0)
            };
            foreach (var item in packages.Where(x => x.needed && !File.Exists(x.get())))
            {
                try { item.set(await DownloadPackageAsync(item.pkg)); }
                catch (Exception ex) { InternalDiagnostics.Record(ex, "Installer automatic download/browser fallback"); /* DownloadPackageAsync already offers browser fallback. */ }
            }
            RebuildCards();
        });

        RebuildCards();
    }

    private GroupBox InstallerCard(InstallerDescriptor package, Func<string> getPath, Action<string> setPath, PrerequisiteState state, bool required, Func<string, Task>? installAction, bool disableInstallerActionsWhenInstalled = false)
    {
        var box = new GroupBox { Text = required ? package.DisplayName + " — Required" : package.DisplayName + " — Optional", Width = 870, Height = 142, Padding = new Padding(12) };
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 3, ColumnCount = 1 };
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        var status = new Label { Text = state.Detail, AutoSize = true, ForeColor = state.Installed ? Color.ForestGreen : (required ? Color.Firebrick : Color.DimGray), MaximumSize = new Size(820, 0) };
        var path = new TextBox { Text = getPath(), Dock = DockStyle.Fill, ReadOnly = true };
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, WrapContents = false };
        var local = new Button { Text = "Use local file...", AutoSize = true };
        var download = new Button { Text = "Download", AutoSize = true };
        var official = new Button { Text = "Open official page", AutoSize = true };
        buttons.Controls.Add(local); buttons.Controls.Add(download); buttons.Controls.Add(official);
        if (disableInstallerActionsWhenInstalled && state.Installed)
            local.Enabled = download.Enabled = false;
        Button? install = null;
        if (installAction is not null)
        {
            install = new Button { Text = "Install", AutoSize = true, Enabled = File.Exists(getPath()) && !state.Installed };
            buttons.Controls.Add(install);
        }
        layout.Controls.Add(status, 0, 0); layout.Controls.Add(path, 0, 1); layout.Controls.Add(buttons, 0, 2);
        box.Controls.Add(layout);

        void UpdatePath(string value)
        {
            setPath(value);
            path.Text = value;
            if (install is not null) install.Enabled = File.Exists(value) && !state.Installed;
            UpdateNavigationState();
        }

        local.Click += (_, _) =>
        {
            using var dialog = new OpenFileDialog { Title = $"Select {package.DisplayName} installer", Filter = "Installer files (*.exe;*.msi)|*.exe;*.msi|All files (*.*)|*.*", InitialDirectory = BrowseStartFolder(getPath(), BrowseContext.Installer) };
            if (dialog.ShowDialog(this) == DialogResult.OK) { UpdatePath(dialog.FileName); RememberBrowseSelection(dialog.FileName, BrowseContext.Installer); }
        };
        download.Click += async (_, _) => await BusyAsync(download, async () => UpdatePath(await DownloadPackageAsync(package)));
        official.Click += (_, _) => _prereq.OpenOfficialPage(package.OfficialPageUrl);
        if (install is not null)
        {
            install.Click += async (_, _) => await BusyAsync(install, async () =>
            {
                await installAction!(getPath());
                MessageBox.Show(this, $"{package.DisplayName} installation completed. The wizard will re-check it.", "Installer", MessageBoxButtons.OK, MessageBoxIcon.Information);
                await RefreshRuntimeStatesAsync();
                ShowPage(4);
            });
        }
        return box;
    }

    private async Task<string> DownloadPackageAsync(InstallerDescriptor package)
    {
        try
        {
            Directory.CreateDirectory(_profile.InstallerCacheFolder);
            AppendLog($"Downloading {package.DisplayName} to {_profile.InstallerCacheFolder} ...");
            var progress = new Progress<int>(p => _progress.Value = Math.Clamp(p, 0, 100));
            var path = await _prereq.DownloadAsync(package, _profile.InstallerCacheFolder, progress);
            AppendLog($"Downloaded: {path}");
            return path;
        }
        catch (Exception ex)
        {
            AppendLog($"DOWNLOAD FAILED: {package.DisplayName}: {GenericWeb.DeploymentManager.Services.SecretRedactor.Redact(ex.Message)}");
            var answer = MessageBox.Show(this,
                $"The automatic download failed.\r\n\r\n{GenericWeb.DeploymentManager.Services.SecretRedactor.Redact(ex.Message)}\r\n\r\nOpen the official download page in your browser? After downloading, use 'Use local file...' to select the installer.",
                "Download failed", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
            if (answer == DialogResult.Yes) _prereq.OpenOfficialPage(package.OfficialPageUrl);
            throw;
        }
        finally { _progress.Value = (int)Math.Round((_pageIndex / (double)(_steps.Length - 1)) * 100); }
    }

    private void AutoLocateCachedInstallers()
    {
        Directory.CreateDirectory(_profile.InstallerCacheFolder);
        _profile.HostingBundleInstallerPath = ExistingOrCached(_profile.HostingBundleInstallerPath, _prereq.HostingBundle(_profile.TargetFrameworkMajor));
        _profile.DotNetSdkInstallerPath = ExistingOrCached(_profile.DotNetSdkInstallerPath, _prereq.DotNetSdk(_profile.TargetFrameworkMajor));
        _profile.NodeInstallerPath = ExistingOrCached(_profile.NodeInstallerPath, _prereq.Node());
        _profile.SqlInstallerPath = ExistingOrCached(_profile.SqlInstallerPath, _prereq.Sql(_profile.SqlExpressVersion));
    }

    private string ExistingOrCached(string current, InstallerDescriptor package)
    {
        if (File.Exists(current)) return current;
        return _prereq.FindCached(_profile.InstallerCacheFolder, package) ?? string.Empty;
    }

    private async Task RefreshRuntimeStatesAsync()
    {
        _hostingState = _environment.CheckHostingBundle(_profile.TargetFrameworkMajor);
        var readyPublish = _profile.BuildMode == "Prepared IIS Publish";
        _sdkState = readyPublish ? new PrerequisiteState(true, "Not required — build is skipped because a ready publish folder was selected.") : await _environment.CheckDotNetSdkAsync(_profile.TargetFrameworkMajor);
        _nodeState = readyPublish ? new PrerequisiteState(true, "Not required — build is skipped because a ready publish folder was selected.") : await _environment.CheckNodeAsync(_profile.RequiresNode);
        _sqlInstances = await _sql.DetectInstancesAsync();
        UpdateNavigationState();
    }

    private string InstalledSqlSummary()
    {
        return string.Join(", ", _sqlInstances.Select(x =>
        {
            var majorText = (x.ProductVersion ?? string.Empty).Split('.').FirstOrDefault();
            var product = int.TryParse(majorText, out var major) ? major switch
            {
                11 => "SQL Server 2012",
                12 => "SQL Server 2014",
                13 => "SQL Server 2016",
                14 => "SQL Server 2017",
                15 => "SQL Server 2019",
                16 => "SQL Server 2022",
                17 => "SQL Server 2025",
                _ => "SQL Server"
            } : "SQL Server";
            return $"{product} ({x.ServerName}, {x.ProductVersion})";
        }));
    }

    private void BuildSqlPage()
    {
        _pageTitle.Text = "SQL Server & Database";
        _pageDescription.Text = "Configure and verify SQL Server and the application database in one place. Healthy connections are verified automatically; repair and recovery tools remain available under troubleshooting.";
        
        var body = VerticalPanel();

        var existingRadio = new RadioButton { Text = "Use an existing SQL Server instance", AutoSize = true, Checked = _sqlInstances.Count > 0 };
        var existing = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 700 };
        foreach (var item in _sqlInstances) existing.Items.Add(item);
        if (existing.Items.Count > 0) existing.SelectedIndex = Math.Max(0, _sqlInstances.FindIndex(x => string.Equals(x.ServerName, _profile.SqlInstance, StringComparison.OrdinalIgnoreCase)));

        var installRadio = new RadioButton { Text = "Install SQL Server Express", AutoSize = true, Checked = _sqlInstances.Count == 0 };
        var installer = new TextBox { Text = _profile.SqlInstallerPath, Width = 670, ReadOnly = true };
        var browse = new Button { Text = "Choose installer...", AutoSize = true };
        var instanceName = new TextBox { Text = "NSPOFFICE", Width = 220 };
        var install = new Button { Text = "Run SQL Server installer", AutoSize = true };
        var rescan = new Button { Text = "Re-scan SQL Server", AutoSize = true };

        body.Controls.Add(existingRadio);
        body.Controls.Add(existing);
        body.Controls.Add(new Label { Height = 8 });
        body.Controls.Add(installRadio);
        body.Controls.Add(Wrap(installer, browse));
        body.Controls.Add(Wrap(new Label { Text = "New instance name", AutoSize = true, Padding = new Padding(0, 7, 8, 0) }, instanceName));
        body.Controls.Add(Wrap(install, rescan));
        body.Controls.Add(new Label { Height = 10 });

        var diagnosticsBox = new GroupBox { Text = "Selected instance diagnostics", Width = 850, Height = 190, Padding = new Padding(12) };
        var diagnosticsPanel = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 2, ColumnCount = 1 };
        diagnosticsPanel.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        diagnosticsPanel.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
        var diagnostics = new TextBox
        {
            Multiline = true,
            ReadOnly = true,
            ScrollBars = ScrollBars.Vertical,
            Dock = DockStyle.Fill,
            Font = new Font("Consolas", 9F),
            Text = "Select an instance, then click Refresh diagnostics."
        };
        var refreshDiagnostics = new Button { Text = "Refresh diagnostics", AutoSize = true };
        var repairConnectivity = new Button { Text = "Repair local SQL connectivity", AutoSize = true };
        diagnosticsPanel.Controls.Add(diagnostics, 0, 0);
        diagnosticsPanel.Controls.Add(Wrap(refreshDiagnostics, repairConnectivity), 0, 1);
        diagnosticsBox.Controls.Add(diagnosticsPanel);
        body.Controls.Add(diagnosticsBox);
        body.Controls.Add(new Label { Height = 8 });

        var authBox = new GroupBox { Text = "Administrative connection", Width = 850, Height = 320, Padding = new Padding(14) };
        var authPanel = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = false };
        var windowsAuth = new RadioButton { Text = "Windows Authentication", AutoSize = true, Checked = _profile.UseWindowsAuthentication };
        var sqlAuth = new RadioButton { Text = "SQL Authentication", AutoSize = true, Checked = !_profile.UseWindowsAuthentication };
        var user = new TextBox { Text = _profile.SqlUserName, Width = 170, Enabled = true };
        var passwordWasSuggested = false;
        var password = new TextBox { Text = _profile.SqlPassword, UseSystemPasswordChar = !passwordWasSuggested, Width = 260, Enabled = true };
        var generate = new Button { Text = "Generate strong password", AutoSize = true, Enabled = true };
        var copy = new Button { Text = "Copy password", AutoSize = true, Enabled = true };
        var showPassword = new CheckBox { Text = "Show password", AutoSize = true, Enabled = true, Checked = passwordWasSuggested };
        var resetSa = new Button { Text = "Configure / Reset sa", AutoSize = true, Enabled = existing.Items.Count > 0 };
        var emergency = new Button { Text = "Emergency recover SQL access", AutoSize = true, Enabled = existing.Items.Count > 0 };
        var test = new Button { Text = "Test administrative connection", AutoSize = true };
        var recentSql = _healthMonitor.HasRecentSuccessfulSql(TimeSpan.FromMinutes(10));
        var verified = new Label { Text = recentSql ? _sql.Connections.State(_profile).Detail : "Not verified", AutoSize = true, ForeColor = recentSql ? Color.ForestGreen : Color.Firebrick, Padding = new Padding(8, 7, 0, 0) };

        authPanel.Controls.Add(windowsAuth);
        authPanel.Controls.Add(sqlAuth);
        authPanel.Controls.Add(new Label
        {
            Text = "SQL user/password remain editable in both modes. Normal SA reset requires Windows Authentication to already be SQL sysadmin; emergency recovery is for a local instance when that SQL sysadmin access has been lost.",
            AutoSize = true,
            MaximumSize = new Size(790, 0),
            ForeColor = Color.DimGray
        });
        authPanel.Controls.Add(Wrap(
            new Label { Text = "SQL user", AutoSize = true, Padding = new Padding(0, 7, 8, 0) }, user,
            new Label { Text = "Password", AutoSize = true, Padding = new Padding(12, 7, 8, 0) }, password,
            showPassword, generate, copy));
        authPanel.Controls.Add(new Label { Text = "Enter an existing SQL credential, or generate a password and apply Configure / Reset sa. Passwords are session-only and excluded from profiles and diagnostics.", AutoSize = true, ForeColor = Color.DimGray, MaximumSize = new Size(790, 0) });
        authPanel.Controls.Add(Wrap(resetSa, emergency));
        authPanel.Controls.Add(Wrap(test, verified));
        authBox.Controls.Add(authPanel);
        body.Controls.Add(authBox);
        var databaseBox = new GroupBox { Text = "Application database", Width = 850, Height = 105, Padding = new Padding(14) };
        var databasePanel = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false };
        var databaseName = new TextBox { Text = _profile.DatabaseName, Width = 250 };
        var verifyDatabase = new Button { Text = "Verify database", AutoSize = true };
        var databaseState = new Label { Text = _databaseConnectionVerified ? "✓ SQL preflight verified" : "Auto verification pending", AutoSize = true, ForeColor = _databaseConnectionVerified ? Color.ForestGreen : Color.DimGray, Padding = new Padding(10,7,0,0) };
        databasePanel.Controls.Add(new Label { Text = "Database name", AutoSize = true, Padding = new Padding(0,7,8,0) });
        databasePanel.Controls.Add(databaseName); databasePanel.Controls.Add(verifyDatabase); databasePanel.Controls.Add(databaseState);
        databaseBox.Controls.Add(databasePanel); body.Controls.Add(databaseBox);
        databaseName.TextChanged += (_, _) => { _profile.DatabaseName = databaseName.Text.Trim();   databaseState.Text = "Not verified"; databaseState.ForeColor = Color.Firebrick; UpdateNavigationState(); };
        verifyDatabase.Click += async (_, _) => await BusyAsync(verifyDatabase, async () => { await _sql.TestConnectionWithSafeFallbackAsync(_profile);  databaseState.Text = "✓ Administrative SQL verified; database prepared at deploy"; databaseState.ForeColor = Color.ForestGreen; UpdateStepIndicator(); UpdateNavigationState(); });
        body.Controls.Add(new Label
        {
            AutoSize = true,
            MaximumSize = new Size(820, 0),
            ForeColor = Color.DimGray,
            Text = "Emergency recovery temporarily restarts the selected local SQL Server service in restricted single-user mode, grants the current Windows account SQL sysadmin, enables Mixed Mode/sa, then returns SQL Server to normal multi-user mode. Close SSMS and other SQL tools before using it."
        });
        _content.Controls.Add(body);

        void FitSqlPanels()
        {
            var available = Math.Max(850, body.ClientSize.Width - body.Padding.Horizontal - SystemInformation.VerticalScrollBarWidth - 12);
            diagnosticsBox.Width = available;
            authBox.Width = available;
            databaseBox.Width = available;
        }
        body.ClientSizeChanged += (_, _) => FitSqlPanels();
        BeginInvoke(FitSqlPanels);

        void InvalidateSqlVerification()
        {
            var current = _sql.Connections.State(_profile);
            verified.Text = current.Detail;
            verified.ForeColor = current.Verified ? Color.ForestGreen : Color.Firebrick;
            UpdateNavigationState();
        }

        void SyncAuth()
        {
            _profile.UseWindowsAuthentication = windowsAuth.Checked;
            user.Enabled = password.Enabled = showPassword.Enabled = generate.Enabled = copy.Enabled = true;
            resetSa.Enabled = existingRadio.Checked && existing.Items.Count > 0;
            emergency.Enabled = existingRadio.Checked && existing.Items.Count > 0;
        }

        void SyncSelection()
        {
            existing.Enabled = existingRadio.Checked;
            installer.Enabled = browse.Enabled = instanceName.Enabled = install.Enabled = installRadio.Checked;
            if (existingRadio.Checked && existing.SelectedItem is SqlInstanceInfo info)
                _profile.SqlInstance = info.ServerName;
            if (installRadio.Checked)
                _profile.SqlInstance = string.Empty;
            resetSa.Enabled = existingRadio.Checked && existing.Items.Count > 0;
            emergency.Enabled = existingRadio.Checked && existing.Items.Count > 0;
            repairConnectivity.Enabled = existingRadio.Checked && existing.Items.Count > 0;
            refreshDiagnostics.Enabled = existingRadio.Checked && existing.Items.Count > 0;
        }

        async Task RefreshDiagnosticsAsync()
        {
            if (string.IsNullOrWhiteSpace(_profile.SqlInstance))
            {
                diagnostics.Text = "Select an existing SQL Server instance first.";
                return;
            }
            var state = await _sql.DiagnoseAsync(_profile);
            diagnostics.Text = state.ToDisplayText();
        }

        existingRadio.CheckedChanged += (_, _) => SyncSelection();
        installRadio.CheckedChanged += (_, _) => SyncSelection();
        existing.SelectedIndexChanged += (_, _) =>
        {
            if (existing.SelectedItem is SqlInstanceInfo info)
                _profile.SqlInstance = info.ServerName;
            InvalidateSqlVerification();
        };
        windowsAuth.CheckedChanged += (_, _) => { SyncAuth(); InvalidateSqlVerification(); };
        sqlAuth.CheckedChanged += (_, _) => { SyncAuth(); InvalidateSqlVerification(); };
        user.TextChanged += (_, _) => { _profile.SqlUserName = user.Text.Trim(); InvalidateSqlVerification(); };
        password.TextChanged += (_, _) => { _profile.SqlPassword = password.Text; InvalidateSqlVerification(); };
        showPassword.CheckedChanged += (_, _) => password.UseSystemPasswordChar = !showPassword.Checked;

        generate.Click += (_, _) =>
        {
            var generated = GenerateStrongPassword();
            password.Text = generated;
            _profile.SqlPassword = generated;
            _profile.SqlCredentialConfigured = false;
            Clipboard.SetText(generated);
            MessageBox.Show(this, "A strong password was generated and copied to the clipboard.", "SQL password", MessageBoxButtons.OK, MessageBoxIcon.Information);
        };
        copy.Click += (_, _) =>
        {
            if (!string.IsNullOrWhiteSpace(password.Text)) Clipboard.SetText(password.Text);
        };

        browse.Click += (_, _) =>
        {
            using var dialog = new OpenFileDialog
            {
                Title = "Select SQL Server Express bootstrapper or full setup.exe",
                Filter = "Executable (*.exe)|*.exe|All files (*.*)|*.*",
                InitialDirectory = BrowseStartFolder(installer.Text, BrowseContext.Installer)
            };
            if (dialog.ShowDialog(this) == DialogResult.OK)
            {
                _profile.SqlInstallerPath = installer.Text = dialog.FileName;
                RememberBrowseSelection(dialog.FileName, BrowseContext.Installer);
                UpdateNavigationState();
            }
        };

        rescan.Click += async (_, _) => await BusyAsync(rescan, async () =>
        {
            _sqlInstances = await _sql.DetectInstancesAsync();
            existing.Items.Clear();
            foreach (var item in _sqlInstances) existing.Items.Add(item);
            if (existing.Items.Count > 0)
            {
                existing.SelectedIndex = 0;
                existingRadio.Checked = true;
            }
            SyncSelection();
            if (!string.IsNullOrWhiteSpace(_profile.SqlInstance))
                await RefreshDiagnosticsAsync();
        });

        refreshDiagnostics.Click += async (_, _) => await BusyAsync(refreshDiagnostics, RefreshDiagnosticsAsync);

        repairConnectivity.Click += async (_, _) => await BusyAsync(repairConnectivity, async () =>
        {
            if (string.IsNullOrWhiteSpace(_profile.SqlInstance))
                throw new InvalidOperationException("Select an existing SQL Server instance first.");
            AppendLog("Repairing selected local SQL service/TCP/Browser configuration...");
            await _sql.RepairLocalConnectivityAsync(_profile.SqlInstance, profile: _profile);
            await RefreshDiagnosticsAsync();
            InvalidateSqlVerification();
        });

        install.Click += async (_, _) => await BusyAsync(install, async () =>
        {
            if (!File.Exists(_profile.SqlInstallerPath))
                throw new InvalidOperationException("Choose or download a SQL Server installer first.");
            if (string.IsNullOrWhiteSpace(instanceName.Text))
                throw new InvalidOperationException("Instance name is required.");

            await _sql.InstallExpressFromFileAsync(_profile.SqlInstallerPath, instanceName.Text.Trim());
            _sqlInstances = await _sql.DetectInstancesAsync();
            existing.Items.Clear();
            foreach (var item in _sqlInstances) existing.Items.Add(item);
            var match = _sqlInstances.FirstOrDefault(x => x.ServerName.EndsWith("\\" + instanceName.Text.Trim(), StringComparison.OrdinalIgnoreCase));
            if (match is not null)
            {
                var item = existing.Items.Cast<object>().FirstOrDefault(x => x is SqlInstanceInfo i && i.ServerName == match.ServerName);
                if (item is not null) existing.SelectedItem = item;
                existingRadio.Checked = true;
                _profile.SqlInstance = match.ServerName;
            }
            else if (_sqlInstances.Count > 0)
            {
                existing.SelectedIndex = 0;
                existingRadio.Checked = true;
            }
            else
            {
                MessageBox.Show(this, "No SQL Server instance was detected after installation. Re-scan after the SQL service is running.", "SQL Server", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
            SyncSelection();
        });

        resetSa.Click += async (_, _) => await BusyAsync(resetSa, async () =>
        {
            if (string.IsNullOrWhiteSpace(_profile.SqlInstance))
                throw new InvalidOperationException("Select an existing SQL Server instance first.");
            _profile.SqlUserName = "sa";
            user.Text = "sa";
            _profile.SqlPassword = password.Text;
            AppendLog("Testing Windows SQL sysadmin access, then configuring/enabling SQL login 'sa' (password is never written to the log)...");
            await _sql.ConfigureOrResetSaAsync(_profile.SqlInstance, _profile.SqlPassword);
            _profile.SqlCredentialConfigured = true;
            _profile.UseWindowsAuthentication = false;
            windowsAuth.Checked = false;
            sqlAuth.Checked = true;
            
            await _sql.TestConnectionWithSafeFallbackAsync(_profile);
            _healthMonitor.ReportSqlSuccess(_sql.Connections.State(_profile).Detail);
            verified.Text = "Verified — sa login works";
            verified.ForeColor = Color.ForestGreen;
            await RefreshDiagnosticsAsync();
            UpdateNavigationState();
        });

        emergency.Click += async (_, _) => await BusyAsync(emergency, async () =>
        {
            if (string.IsNullOrWhiteSpace(_profile.SqlInstance))
                throw new InvalidOperationException("Select an existing SQL Server instance first.");

            var answer = MessageBox.Show(
                this,
                "Emergency recovery will temporarily stop the selected local SQL Server service, start it in restricted single-user mode, grant the current Windows account SQL sysadmin, reset/enable sa, and restart SQL normally.\r\n\r\nClose SSMS, Visual Studio database tools, and other SQL clients first. Continue?",
                "Emergency SQL recovery",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Warning,
                MessageBoxDefaultButton.Button2);
            if (answer != DialogResult.Yes) return;

            _profile.SqlUserName = "sa";
            user.Text = "sa";
            _profile.SqlPassword = password.Text;
            await _sql.EmergencyRecoverSqlAccessAsync(_profile.SqlInstance, _profile.SqlPassword);
            _profile.SqlCredentialConfigured = true;
            _profile.UseWindowsAuthentication = false;
            windowsAuth.Checked = false;
            sqlAuth.Checked = true;
            
            await _sql.TestConnectionWithSafeFallbackAsync(_profile);
            _healthMonitor.ReportSqlSuccess("Emergency recovery completed; sa verified");
            verified.Text = "Verified — emergency recovery completed; sa works";
            verified.ForeColor = Color.ForestGreen;
            await RefreshDiagnosticsAsync();
            UpdateNavigationState();
        });

        test.Click += async (_, _) => await BusyAsync(test, async () =>
        {
            if (string.IsNullOrWhiteSpace(_profile.SqlInstance))
                throw new InvalidOperationException("Select a SQL Server instance first.");
            _profile.UseWindowsAuthentication = windowsAuth.Checked;
            _profile.SqlUserName = user.Text.Trim();
            _profile.SqlPassword = password.Text;
            await _sql.TestConnectionWithSafeFallbackAsync(_profile);
            
            _healthMonitor.ReportSqlSuccess(_sql.Connections.State(_profile).Detail);
            verified.Text = _sql.Connections.State(_profile).Detail;
            verified.ForeColor = Color.ForestGreen;
            await RefreshDiagnosticsAsync();
            UpdateNavigationState();
        });

        SyncSelection();
        SyncAuth();

        BeginInvoke(async () =>
        {
            if (_pageIndex != 5 || string.IsNullOrWhiteSpace(_profile.SqlInstance) || _sqlAdminConnectionVerified) return;
            try
            {
                verified.Text = "Testing automatically...";
                verified.ForeColor = Color.DarkOrange;
                _profile.UseWindowsAuthentication = windowsAuth.Checked;
                _profile.SqlUserName = user.Text.Trim();
                _profile.SqlPassword = password.Text;
                await _sql.TestConnectionWithSafeFallbackAsync(_profile);
                
                
                
                databaseState.Text = _databaseConnectionVerified ? "✓ Administrative SQL verified; database prepared at deploy" : "Database name required";
                databaseState.ForeColor = _databaseConnectionVerified ? Color.ForestGreen : Color.Firebrick;
                UpdateStepIndicator();
                _healthMonitor.ReportSqlSuccess(_sql.Connections.State(_profile).Detail);
                verified.Text = _sql.Connections.State(_profile).Detail;
                verified.ForeColor = Color.ForestGreen;
                await RefreshDiagnosticsAsync();
            }
            catch (Exception ex)
            {
                
                verified.Text = "Automatic test failed — use Test / recovery";
                verified.ForeColor = Color.Firebrick;
                AppendLog("[SQL] Automatic administrative connection test failed: " + GenericWeb.DeploymentManager.Services.SecretRedactor.Redact(ex.Message));
            }
            UpdateNavigationState();
        });
    }

    private IEnumerable<string> CompatibleSqlVersions()
    {
        var windows = _environment.GetWindowsDescription();
        // Current supported shortlist for this tool. SQL Server 2019/2022 cover Windows 10/11 and Windows Server 2016-2025 in our intended environments.
        if (windows.Contains("Server 2016", StringComparison.OrdinalIgnoreCase)) return new[] { "SQL Server 2019 Express", "SQL Server 2022 Express" };
        return new[] { "SQL Server 2019 Express", "SQL Server 2022 Express" };
    }

    private void BuildDatabasePage()
    {
        _pageTitle.Text = "Database";
        _pageDescription.Text = "Verify the application database selected on the first page. This step verifies SQL Server administrative access; if the database does not yet exist, Deploy will create it automatically and wait until it is ONLINE.";
        
        var form = FormGrid();
        var server = new TextBox { Text = _profile.SqlInstance, Dock = DockStyle.Fill, ReadOnly = true };
        var database = new TextBox { Text = _profile.DatabaseName, Dock = DockStyle.Fill, ReadOnly = true };
        var auth = new TextBox { Text = _sql.Connections.State(_profile).Detail, Dock = DockStyle.Fill, ReadOnly = true };
        var test = new Button { Text = "Re-test SQL connection", AutoSize = true };
        var state = new Label { Text = "Not verified for this database step", AutoSize = true, ForeColor = Color.Firebrick, Padding = new Padding(8, 7, 0, 0) };
        AddRow(form, "SQL instance", server);
        AddRow(form, "Database name", database);
        AddRow(form, "Authentication", auth);
        AddRow(form, "", Wrap(test, state));
        _content.Controls.Add(form);

        database.TextChanged += (_, _) =>
        {
            _profile.DatabaseName = database.Text.Trim();
            
            state.Text = "Not verified for this database step";
            state.ForeColor = Color.Firebrick;
            UpdateNavigationState();
        };
        test.Click += async (_, _) => await BusyAsync(test, async () =>
        {
            await _sql.TestConnectionWithSafeFallbackAsync(_profile);
            
            state.Text = "SQL Server verified — database will be created automatically if missing";
            state.ForeColor = Color.ForestGreen;
            UpdateNavigationState();
        });

        BeginInvoke(async () =>
        {
            if (_pageIndex != 6 || string.IsNullOrWhiteSpace(_profile.SqlInstance) || string.IsNullOrWhiteSpace(_profile.DatabaseName)) return;
            try
            {
                state.Text = "Testing automatically...";
                state.ForeColor = Color.DarkOrange;
                await _sql.TestConnectionWithSafeFallbackAsync(_profile);
                
                state.Text = "SQL Server verified — database will be created automatically if missing";
                state.ForeColor = Color.ForestGreen;
            }
            catch (Exception ex)
            {
                
                state.Text = "Automatic test failed — use Re-test SQL connection";
                state.ForeColor = Color.Firebrick;
                AppendLog("[DATABASE] Automatic SQL connection test failed: " + GenericWeb.DeploymentManager.Services.SecretRedactor.Redact(ex.Message));
            }
            UpdateNavigationState();
        });
    }

    private void BuildDeploymentLocationPage()
    {
        _pageTitle.Text = "Deployment location";
        _pageDescription.Text = "Choose exactly where IIS should host the application. You can use a dedicated application folder or explicitly take over the IIS default web root on port 80.";
        var form = FormGrid();
        var lastCustomRoot = string.Equals(_profile.HostingRoot.TrimEnd('\\'), @"C:\inetpub\wwwroot", StringComparison.OrdinalIgnoreCase)
            ? Path.Combine(@"C:\inetpub\wwwroot", SafeApplicationFolderName(_profile.ApplicationName))
            : _profile.HostingRoot;
        var useDefault = new CheckBox
        {
            Text = @"Use IIS default web root (C:\inetpub\wwwroot) and port 80 — replace the existing IIS site/content on port 80",
            AutoSize = true,
            Checked = _profile.UseDefaultIisWebsite,
            ForeColor = Color.DarkRed
        };
        var hostingRoot = new TextBox { Text = _profile.HostingRoot, Dock = DockStyle.Fill, MinimumSize = new Size(650, 38), Font = new Font("Segoe UI", 10F) };
        var browse = new Button { Text = "Browse...", Width = 120, Height = 38 };
        var single = new RadioButton { Text = "Single Folder — always deploy the active site into this exact folder", AutoSize = true, Checked = string.Equals(_profile.DeploymentLayout, "Single Folder", StringComparison.OrdinalIgnoreCase) };
        var versioned = new RadioButton { Text = "Versioned Folders — keep releases as subfolders under this root", AutoSize = true, Checked = !string.Equals(_profile.DeploymentLayout, "Single Folder", StringComparison.OrdinalIgnoreCase) };
        var cleanup = new CheckBox { Text = "After a successful versioned deployment, delete older release folders", AutoSize = true, Checked = _profile.CleanupPreviousDeployments, Enabled = versioned.Checked };
        var hint = new Label { AutoSize = true, ForeColor = Color.DimGray, MaximumSize = new Size(720, 0), Text = @"Dedicated folder example: C:\inetpub\wwwroot\NSPOfficeAutomation
Default website mode: C:\inetpub\wwwroot, HTTP port 80. A different IIS site owning port 80 blocks deployment; reconfigure its binding explicitly first." };

        AddRow(form, "Application", new TextBox { Text = _profile.ApplicationName, Dock = DockStyle.Fill, ReadOnly = true });
        AddRow(form, "Default IIS website", useDefault);
        AddRow(form, "Hosted site folder", Wrap(hostingRoot, browse));
        AddRow(form, "Deployment layout", VerticalWrap(single, versioned, cleanup));
        AddRow(form, "", hint);
        _content.Controls.Add(form);

        void ApplyDefaultMode()
        {
            if (useDefault.Checked)
            {
                if (!string.Equals(hostingRoot.Text.TrimEnd('\\'), @"C:\inetpub\wwwroot", StringComparison.OrdinalIgnoreCase)) lastCustomRoot = hostingRoot.Text.Trim();
                hostingRoot.Text = @"C:\inetpub\wwwroot";
                single.Checked = true;
                _profile.HttpPort = 80;
                _profile.HostHeader = string.Empty;
            }
            else if (string.Equals(hostingRoot.Text.TrimEnd('\\'), @"C:\inetpub\wwwroot", StringComparison.OrdinalIgnoreCase))
            {
                hostingRoot.Text = string.IsNullOrWhiteSpace(lastCustomRoot) ? Path.Combine(@"C:\inetpub\wwwroot", SafeApplicationFolderName(_profile.ApplicationName)) : lastCustomRoot;
            }
            hostingRoot.ReadOnly = useDefault.Checked;
            browse.Enabled = !useDefault.Checked;
            single.Enabled = !useDefault.Checked;
            versioned.Enabled = !useDefault.Checked;
            cleanup.Enabled = !useDefault.Checked && versioned.Checked;
        }

        void Save()
        {
            _profile.UseDefaultIisWebsite = useDefault.Checked;
            _profile.HostingRoot = hostingRoot.Text.Trim();
            _profile.DeploymentLayout = useDefault.Checked || single.Checked ? "Single Folder" : "Versioned Folders";
            _profile.CleanupPreviousDeployments = cleanup.Checked;
            if (_profile.UseDefaultIisWebsite) { _profile.HttpPort = 80; _profile.HostHeader = string.Empty; }
            cleanup.Enabled = !useDefault.Checked && versioned.Checked;
            QueueDeferredSettingsSave("hosting", () => _appData.SaveHostingSettings(_profile.HostingRoot, _profile.DeploymentLayout, _profile.CleanupPreviousDeployments, _profile.UseDefaultIisWebsite));
            UpdateNavigationState();
        }

        hostingRoot.TextChanged += (_, _) => Save();
        hostingRoot.Leave += (_, _) => FlushDeferredSettingsSaves();
        single.CheckedChanged += (_, _) => Save();
        versioned.CheckedChanged += (_, _) => Save();
        cleanup.CheckedChanged += (_, _) => Save();
        useDefault.CheckedChanged += (_, _) => { ApplyDefaultMode(); Save(); };
        browse.Click += (_, _) =>
        {
            using var dialog = new FolderBrowserDialog { Description = "Choose the folder where IIS site files should be hosted", UseDescriptionForTitle = true, SelectedPath = Directory.Exists(hostingRoot.Text) ? hostingRoot.Text : string.Empty, ShowNewFolderButton = true };
            if (dialog.ShowDialog(this) == DialogResult.OK) hostingRoot.Text = dialog.SelectedPath;
        };
        ApplyDefaultMode();
        Save();
    }

    private static string NormalizeAutoHostingRoot(string hostingRoot, string applicationName)
    {
        if (string.IsNullOrWhiteSpace(hostingRoot))
            return Path.Combine(@"C:\inetpub\wwwroot", SafeApplicationFolderName(applicationName));

        var trimmed = hostingRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var leaf = Path.GetFileName(trimmed);
        if (string.Equals(leaf, "WebApplication", StringComparison.OrdinalIgnoreCase)
            || string.Equals(leaf, "NSPOfficeAutomation", StringComparison.OrdinalIgnoreCase))
        {
            var parent = Path.GetDirectoryName(trimmed);
            if (!string.IsNullOrWhiteSpace(parent))
                return Path.Combine(parent, SafeApplicationFolderName(applicationName));
        }
        return hostingRoot;
    }

    private static string UpdateAutoHostingRootForApplicationName(string hostingRoot, string previousApplicationName, string newApplicationName)
    {
        if (string.IsNullOrWhiteSpace(hostingRoot))
            return Path.Combine(@"C:\inetpub\wwwroot", SafeApplicationFolderName(newApplicationName));

        var trimmed = hostingRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var leaf = Path.GetFileName(trimmed);
        var previousLeaf = SafeApplicationFolderName(previousApplicationName);
        if (string.Equals(leaf, previousLeaf, StringComparison.OrdinalIgnoreCase)
            || string.Equals(leaf, "WebApplication", StringComparison.OrdinalIgnoreCase)
            || string.Equals(leaf, "NSPOfficeAutomation", StringComparison.OrdinalIgnoreCase))
        {
            var parent = Path.GetDirectoryName(trimmed);
            if (!string.IsNullOrWhiteSpace(parent))
                return Path.Combine(parent, SafeApplicationFolderName(newApplicationName));
        }
        return hostingRoot;
    }

    private static string SafeApplicationFolderName(string value)
    {
        var name = string.IsNullOrWhiteSpace(value) ? "WebApplication" : value.Trim();
        foreach (var c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '-');
        return name.Trim().TrimEnd('.');
    }

    private void ValidateDeploymentLocationPage()
    {
        if (string.IsNullOrWhiteSpace(_profile.HostingRoot)) throw new InvalidOperationException("Choose a hosted site folder before continuing.");
        if (!Path.IsPathRooted(_profile.HostingRoot)) throw new InvalidOperationException("The hosted site folder must be an absolute Windows path.");
        if (_profile.UseDefaultIisWebsite) { _profile.HostingRoot = @"C:\inetpub\wwwroot"; _profile.DeploymentLayout = "Single Folder"; _profile.HttpPort = 80; _profile.HostHeader = string.Empty; }
        _appData.SaveHostingSettings(_profile.HostingRoot, _profile.DeploymentLayout, _profile.CleanupPreviousDeployments, _profile.UseDefaultIisWebsite);
    }

    private void BuildIisSitePage()
    {
        _pageTitle.Text = "IIS site";
        _pageDescription.Text = _profile.UseDefaultIisWebsite
            ? "Default IIS website mode uses port 80. If another IIS site owns the port, deployment is blocked until its binding is explicitly reconfigured."
            : "Choose the HTTP port. Host Header and Health Check are available under Advanced IIS settings.";
        var form = FormGrid();
        var site = new TextBox { Text = _profile.SiteName, Dock = DockStyle.Fill, ReadOnly = true };
        var pool = new TextBox { Text = _profile.AppPoolName, Dock = DockStyle.Fill, ReadOnly = true };
        var port = new NumericUpDown { Minimum = 1, Maximum = 65535, Value = _profile.UseDefaultIisWebsite ? 80 : Math.Clamp(_profile.HttpPort, 1, 65535), Width = 160, Enabled = !_profile.UseDefaultIisWebsite };
        var hostHeader = new TextBox { Text = _profile.HostHeader, Dock = DockStyle.Fill, Enabled = !_profile.UseDefaultIisWebsite };
        var health = new TextBox { Text = _profile.HealthPath, Dock = DockStyle.Fill };
        var portState = new Label { AutoSize = true };
        var advancedToggle = new CheckBox { Text = "Show Advanced IIS settings", AutoSize = true };
        var advancedPanel = new TableLayoutPanel { AutoSize = true, Dock = DockStyle.Top, ColumnCount = 1, Visible = false, Padding = new Padding(0, 6, 0, 0) };
        advancedPanel.Controls.Add(new Label { Text = "Host header (optional)", AutoSize = true, ForeColor = Color.DimGray });
        advancedPanel.Controls.Add(hostHeader);
        advancedPanel.Controls.Add(new Label { Text = "Health-check path", AutoSize = true, ForeColor = Color.DimGray, Padding = new Padding(0, 8, 0, 0) });
        advancedPanel.Controls.Add(health);
        advancedToggle.CheckedChanged += (_, _) => advancedPanel.Visible = advancedToggle.Checked;

        AddRow(form, "Hosted site folder", new TextBox { Text = _profile.HostingRoot, Dock = DockStyle.Fill, ReadOnly = true });
        AddRow(form, "IIS site name", site);
        AddRow(form, "Application pool", pool);
        AddRow(form, _profile.UseDefaultIisWebsite ? "HTTP port (default)" : "HTTP port", Wrap(port, portState));
        AddRow(form, "Advanced IIS", VerticalWrap(advancedToggle, advancedPanel));
        _content.Controls.Add(form);
        void Update()
        {
            _profile.SiteName = site.Text.Trim(); _profile.AppPoolName = pool.Text.Trim();
            _profile.HttpPort = _profile.UseDefaultIisWebsite ? 80 : (int)port.Value;
            _profile.HostHeader = _profile.UseDefaultIisWebsite ? string.Empty : hostHeader.Text.Trim();
            _profile.HealthPath = health.Text.Trim();
            var usable = _environment.IsPortUsableForSite(_profile.HttpPort, _profile.SiteName, out var portDetail);
            portState.Text = portDetail;
            portState.ForeColor = usable ? (_profile.UseDefaultIisWebsite ? Color.DarkOrange : Color.ForestGreen) : Color.Firebrick;
            UpdateNavigationState();
        }
        port.ValueChanged += (_, _) => Update(); hostHeader.TextChanged += (_, _) => Update(); health.TextChanged += (_, _) => Update();
        Update();
    }

    private void ValidateIisSitePage()
    {
        if (string.IsNullOrWhiteSpace(_profile.SiteName)) throw new InvalidOperationException("IIS site name is required.");
        if (string.IsNullOrWhiteSpace(_profile.AppPoolName)) throw new InvalidOperationException("Application pool name is required.");
        if (_profile.UseDefaultIisWebsite) { _profile.HttpPort = 80; _profile.HostHeader = string.Empty; }
        if (!_environment.IsPortUsableForSite(_profile.HttpPort, _profile.SiteName, out var portDetail))
            throw new InvalidOperationException($"Port {_profile.HttpPort} cannot be used. {portDetail}. Choose another port or stop/reconfigure the conflicting service.");
    }

    private void BuildReviewPage()
    {
        _pageTitle.Text = "Review and deploy";
        _pageDescription.Text = "Review the plan. Depending on Build mode, Deploy either builds the source or uses prepared publish files, then prepares the database, configures IIS, and performs the health check.";
        var reviewRoot = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 4 };
        reviewRoot.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        reviewRoot.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        reviewRoot.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
        reviewRoot.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        var deployModePanel = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, FlowDirection = FlowDirection.LeftToRight, Padding = new Padding(0, 0, 0, 8) };
        var deployModeLabel = new Label { Text = "Deployment mode:", AutoSize = true, Padding = new Padding(0, 8, 8, 0), Font = new Font("Segoe UI Semibold", 10.5F) };
        var deployMode = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 260, Enabled = !_operationRunning };
        var sourceBuildMode = _profile.BuildMode is "Fast Local Test" or "Fast IIS Update" or "Full IIS Deploy";
        if (sourceBuildMode)
        {
            deployMode.Items.AddRange(new object[] { "Fast Local Test (Default)", "Fast IIS Update", "Full IIS Deploy" });
            deployMode.SelectedIndex = string.Equals(_profile.BuildMode, "Full IIS Deploy", StringComparison.OrdinalIgnoreCase) ? 2
                : string.Equals(_profile.BuildMode, "Fast IIS Update", StringComparison.OrdinalIgnoreCase) ? 1 : 0;
        }
        else
        {
            deployMode.Items.Add(_profile.BuildMode);
            deployMode.SelectedIndex = 0;
            deployMode.Enabled = false;
        }
        var deployModeHelp = new Label { AutoSize = true, ForeColor = Color.DimGray, MaximumSize = new Size(720, 0), Padding = new Padding(12, 8, 0, 0), Text = sourceBuildMode ? BuildModeHelp(deployMode.SelectedIndex) : "Prepared mode selected on the Source page is preserved; Review does not rewrite it." };
        deployMode.SelectedIndexChanged += (_, __) =>
        {
            if (!sourceBuildMode) return;
            _profile.BuildMode = deployMode.SelectedIndex switch { 1 => "Fast IIS Update", 2 => "Full IIS Deploy", _ => "Fast Local Test" };
            deployModeHelp.Text = BuildModeHelp(deployMode.SelectedIndex);
        };
        deployModePanel.Controls.Add(deployModeLabel);
        deployModePanel.Controls.Add(deployMode);
        deployModePanel.Controls.Add(deployModeHelp);
        // V159: policy settings are persisted per project and enforced by DeploymentService.
        var backupChoice = new CheckBox { Text = "Create Backup Before Deployment", AutoSize = true, Checked = _profile.CreateBackupBeforeDeployment, Enabled = !_operationRunning };
        var retentionChoice = new CheckBox { Text = "Keep Previous Deployment for Rollback", AutoSize = true, Checked = _profile.KeepPreviousDeploymentForRollback, Enabled = !_operationRunning };
        var retentionCount = new NumericUpDown { Minimum = 1, Maximum = 20, Value = Math.Clamp(_profile.PreviousDeploymentRetentionCount, 1, 20), Width = 55, Enabled = retentionChoice.Checked && !_operationRunning };
        backupChoice.CheckedChanged += (_, _) => { _profile.CreateBackupBeforeDeployment = backupChoice.Checked; _appData.SaveProjectProfile(_profile); };
        retentionChoice.CheckedChanged += (_, _) => { _profile.KeepPreviousDeploymentForRollback = retentionChoice.Checked; retentionCount.Enabled = retentionChoice.Checked && !_operationRunning; _appData.SaveProjectProfile(_profile); };
        retentionCount.ValueChanged += (_, _) => { _profile.PreviousDeploymentRetentionCount = (int)retentionCount.Value; _appData.SaveProjectProfile(_profile); };
        var policyPanel = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, WrapContents = true, Padding = new Padding(8, 2, 8, 2) };
        policyPanel.Controls.Add(backupChoice);
        policyPanel.Controls.Add(retentionChoice);
        policyPanel.Controls.Add(new Label { Text = "Keep count:", AutoSize = true, Padding = new Padding(8, 4, 0, 0) });
        policyPanel.Controls.Add(retentionCount);
        policyPanel.Controls.Add(new Label { Text = "Old releases are removed only after explicit confirmation in Deployment Cleanup.", AutoSize = true, ForeColor = Color.DarkOrange, MaximumSize = new Size(550, 0) });
        deployModePanel.Controls.Add(policyPanel);
        reviewRoot.RowCount = 5;
        reviewRoot.RowStyles.Clear();
        reviewRoot.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        reviewRoot.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        reviewRoot.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        reviewRoot.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
        reviewRoot.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        reviewRoot.Controls.Add(deployModePanel, 0, 0);
        var recoveryWarning = _deploymentStatus.Contains("UNSUCCESSFUL", StringComparison.OrdinalIgnoreCase)
            || _deploymentStatus.Contains("RECOVERY FAILED", StringComparison.OrdinalIgnoreCase)
            || _deploymentStatus.Contains("INTERRUPTED", StringComparison.OrdinalIgnoreCase);
        var statusBanner = new Label { Text = _deploymentStatus, AutoSize = true, Padding = new Padding(8), Font = new Font("Segoe UI Semibold", 11F),
            ForeColor = recoveryWarning ? Color.Firebrick : (_deploymentStatus.Contains("SUCCESSFUL", StringComparison.OrdinalIgnoreCase) ? Color.ForestGreen : Color.DimGray) };
        reviewRoot.Controls.Add(statusBanner, 0, 1);
        var split = new SplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Horizontal, SplitterDistance = 210, FixedPanel = FixedPanel.None };
        void KeepHalfSplit() { if (split.Height > 160) split.SplitterDistance = Math.Max(80, (split.Height - split.SplitterWidth) / 2); }
        split.Resize += (_, _) => KeepHalfSplit();
        split.HandleCreated += (_, _) => BeginInvoke(() => KeepHalfSplit());
        var summary = new TextBox { Multiline = true, ReadOnly = true, Dock = DockStyle.Fill, Font = new Font("Consolas", 9.5F) };
        summary.Text = $"Application: {_profile.ApplicationName}\r\nBuild mode (selected on Project Source): {_profile.BuildMode}\r\nHosted website action: {(_profile.BuildMode == "Fast Local Test" ? "UNCHANGED — compile only" : "UPDATE after successful build")}\r\nDependency mode: {_profile.DependencyResolutionMode}\r\nOffline build: {_profile.OfflineBuild}\r\nTarget mode: {_profile.TargetMode}\r\nTarget: {_profile.TargetHost}\r\nDeployment Repository: {_profile.DeploymentRepositoryFolder}\r\nStable Build Workspace: {_profile.WorkspaceRoot}\r\nIncoming Source: {_profile.SourceFolder}\r\nReady publish folder: {_profile.ReadyPublishFolder}\r\nAPI project: {_profile.ApiProjectPath}\r\nTarget framework: .NET {_profile.TargetFrameworkMajor}\r\nNode required: {_profile.RequiresNode}\r\nSQL: {_profile.SqlInstance}\r\nDatabase: {_profile.DatabaseName}\r\nHosted site folder: {_profile.HostingRoot}\r\nDeployment layout: {_profile.DeploymentLayout}\r\nDefault IIS website: {_profile.UseDefaultIisWebsite}\r\nPersistent data root: {_profile.DeploymentRoot}\r\nIIS site: {_profile.SiteName}\r\nApp pool: {_profile.AppPoolName}\r\nPort: {_profile.HttpPort}\r\nHealth: {_profile.HealthPath}";
        split.Panel1.Controls.Add(summary); split.Panel2.Controls.Add(_log); reviewRoot.Controls.Add(split, 0, 2);
        _reviewActivity = new Label { Text = _activity.Text, Dock = DockStyle.Fill, AutoEllipsis = true, Height = 32, Padding = new Padding(4, 6, 4, 0), ForeColor = Color.DimGray, Font = new Font("Segoe UI Semibold", 9.5F) };
        reviewRoot.Controls.Add(_reviewActivity, 0, 3);

        {
            var deploymentReady = (_persistentState.LastResult == "Successful" && !string.IsNullOrWhiteSpace(_lastSuccessfulPublishPath)) || Directory.Exists(_profile.HostingRoot);
            var actions = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 2, RowCount = 1, Padding = new Padding(0, 8, 0, 0) };
            actions.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            actions.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            var logActions = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.TopDown, WrapContents = false, Margin = new Padding(0) };
            var commandActions = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.LeftToRight, WrapContents = true, Margin = new Padding(12, 0, 0, 0) };
            var host = string.IsNullOrWhiteSpace(_profile.HostHeader) ? "localhost" : _profile.HostHeader;
            var siteUrl = _profile.HttpPort == 80 ? $"http://{host}" : $"http://{host}:{_profile.HttpPort}";
            var logModeLabel = new Label { Text = "Log:", AutoSize = true, Padding = new Padding(0, 8, 6, 0) };
            var logMode = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 110, Enabled = !_operationRunning };
            _logModeCombo = logMode;
            logMode.Items.AddRange(new object[] { "Minimal", "Normal", "Verbose" });
            logMode.SelectedItem = _logViewMode.ToString();
            logMode.SelectedIndexChanged += (_, __) =>
            {
                if (Enum.TryParse<LogViewMode>(logMode.SelectedItem?.ToString(), out var mode)) { _logViewMode = mode; RenderVisibleLog(); }
            };
            var logHeader = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.LeftToRight, WrapContents = false, Margin = new Padding(0) };
            logHeader.Controls.Add(logModeLabel);
            logHeader.Controls.Add(logMode);
            logActions.Controls.Add(logHeader);
            _deploymentElapsedLabel = new Label { Text = _deploymentElapsedText, AutoSize = true, Padding = new Padding(16, 6, 22, 0), ForeColor = Color.RoyalBlue, Font = new Font("Segoe UI Semibold", 11.5F) };
            commandActions.Controls.Add(_deploymentElapsedLabel);
            var openSite = new LinkLabel { Text = "Open deployed site", AutoSize = true, Padding = new Padding(16, 6, 24, 6), LinkColor = Color.RoyalBlue, Enabled = deploymentReady };
            openSite.LinkClicked += (_, __) =>
            {
                OpenSiteWithBrowser(siteUrl, new BrowserChoice("System Default", null));
            };
            logActions.Controls.Add(openSite);

            var deployPath = _lastSuccessfulPublishPath;
            if (string.IsNullOrWhiteSpace(deployPath))
                deployPath = _profile.HostingRoot;
            var openFolder = new LinkLabel { Text = "Open hosted folder", AutoSize = true, Padding = new Padding(0, 6, 0, 6), LinkColor = Color.RoyalBlue, Enabled = deploymentReady };
            var folderToOpen = deployPath;
            openFolder.LinkClicked += (_, __) => OpenTarget(folderToOpen!);
            logActions.Controls.Add(openFolder);


            actions.Controls.Add(logActions, 0, 0);
            actions.Controls.Add(commandActions, 1, 0);
            reviewRoot.Controls.Add(actions, 0, 4);
        }
        static string BuildModeHelp(int index) => index switch
        {
            1 => "Safe IIS update: fresh frontend build (if required) and dotnet publish. Hosted-output reuse and fingerprint-based build skips are disabled; IIS activation and recovery protections still apply.",
            2 => "Complete Release publish, IIS activation, database preparation, health check, and rollback protection.",
            _ => "Incremental local compile only. No dotnet publish, IIS activation, hosted-file changes, or rollback preparation."
        };

        _content.Controls.Add(reviewRoot);
    }

    private async Task ShowDeploymentCleanupAsync()
    {
        if (_operationRunning) return;
        using var dialog = new Form { Text = "Deployment Cleanup — review before deletion", Width = 1050, Height = 540, StartPosition = FormStartPosition.CenterParent };
        var list = new ListView { Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, MultiSelect = false };
        list.Columns.Add("Type", 170);
        list.Columns.Add("Path", 540);
        list.Columns.Add("Modified (local)", 160);
        list.Columns.Add("Size (GiB)", 90);
        list.Columns.Add("Status", 100);
        var toolbar = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 52 };
        var refresh = new Button { Text = "Refresh", AutoSize = true };
        var remove = new Button { Text = "Delete selected...", AutoSize = true, Enabled = false };
        var close = new Button { Text = "Close", AutoSize = true };
        toolbar.Controls.Add(refresh); toolbar.Controls.Add(remove); toolbar.Controls.Add(close);
        dialog.Controls.Add(list); dialog.Controls.Add(toolbar);
        close.Click += (_, _) => dialog.Close();
        async Task RefreshAsync()
        {
            refresh.Enabled = false; remove.Enabled = false; list.Items.Clear();
            try
            {
                var candidates = await _deployment.DiscoverDeploymentCleanupAsync(_profile);
                foreach (var candidate in candidates)
                {
                    var item = new ListViewItem(new[] { candidate.Kind, candidate.Path, candidate.ModifiedUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm"), (candidate.Bytes / 1073741824d).ToString("F2"), candidate.Kind.Contains("unverified", StringComparison.OrdinalIgnoreCase) ? "UNVERIFIED — LOCKED" : candidate.IsActive ? "ACTIVE — LOCKED" : "Verified / review" }) { Tag = candidate };
                    list.Items.Add(item);
                }
                AppendLog($"[CLEANUP] Inventory completed: {candidates.Count} candidates; no files deleted.");
            }
            catch (Exception ex) { MessageBox.Show(dialog, ex.Message, "Cleanup scan blocked", MessageBoxButtons.OK, MessageBoxIcon.Warning); AppendLog("[CLEANUP] Scan failed: " + ex.Message); }
            finally { refresh.Enabled = true; }
        }
        list.SelectedIndexChanged += (_, _) => remove.Enabled = list.SelectedItems.Count == 1 && list.SelectedItems[0].Tag is DeploymentService.CleanupCandidate c && !c.IsActive;
        refresh.Click += async (_, _) => await RefreshAsync();
        remove.Click += async (_, _) =>
        {
            if (list.SelectedItems.Count != 1 || list.SelectedItems[0].Tag is not DeploymentService.CleanupCandidate selected || selected.IsActive) return;
            var answer = MessageBox.Show(dialog, $"Permanently delete this directory and its contents?\n\n{selected.Path}\n\n{selected.Bytes / 1073741824d:F2} GiB\n\nThis cannot be undone.", "Confirm permanent deletion", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2);
            if (answer != DialogResult.Yes) return;
            remove.Enabled = false;
            try { await _deployment.DeleteDeploymentCleanupCandidateAsync(_profile, selected.Path); AppendLog("[CLEANUP] Deleted with explicit operator approval: " + selected.Path); }
            catch (Exception ex) { AppendLog("[CLEANUP] Deletion failed: " + ex.Message); MessageBox.Show(dialog, ex.Message, "Deletion blocked", MessageBoxButtons.OK, MessageBoxIcon.Error); }
            await RefreshAsync();
        };
        dialog.Shown += async (_, _) => await RefreshAsync();
        dialog.ShowDialog(this);
    }

    private void RequestDeploymentCancellation()
    {
        if (!_operationRunning || _deploymentCts is null || _deploymentCts.IsCancellationRequested) return;
        var answer = MessageBox.Show(this,
            "Cancel the current deployment? Running build processes will be stopped and Deployment Manager will clean staging/failed files and restore the previous hosted site where possible.",
            "Cancel deployment", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2);
        if (answer != DialogResult.Yes) return;
        _next.Text = "Cancelling...";
        _next.Enabled = false;
        AppendLog("[CANCEL] Operator requested deployment cancellation from the Deploy button. Stopping active process tree and preparing cleanup...");
        _deploymentCts.Cancel();
    }

    private async Task DeployAsync()
    {
        _appData.SaveProjectProfile(_profile);
        var isFastLocalTest = string.Equals(_profile.BuildMode, "Fast Local Test", StringComparison.OrdinalIgnoreCase);
        if (_profile.UseDefaultIisWebsite && !isFastLocalTest)
        {
            using var confirmDialog = new AutoYesConfirmationForm(
                "Confirm default IIS website takeover",
                @"Default IIS website mode deploys into C:\inetpub\wwwroot after a successful build. Another IIS site owning port 80 blocks deployment; it will not be stopped or replaced automatically. Confirm this folder is dedicated to the application. Persistent database/data folders are not deleted.",
                5);
            if (confirmDialog.ShowDialog(this) != DialogResult.Yes) return;
        }
        if (!isFastLocalTest)
        {
            var protection = $"Backup: {(_profile.CreateBackupBeforeDeployment ? "ENABLED" : "DISABLED")}; Keep previous release: {(_profile.KeepPreviousDeploymentForRollback ? "ENABLED" : "DISABLED")}; Automatic rollback: {(_profile.KeepPreviousDeploymentForRollback ? "ENABLED" : "DISABLED")}. " +
                "Without rollback, a failed deployment may require manual recovery. Continue?";
            if (MessageBox.Show(this, protection, "Deployment protection policy", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.Yes) return;
        }
        _operationRunning = true;
        AcknowledgeTrayAlert();
        _deploymentCts?.Dispose();
        _deploymentCts = new CancellationTokenSource();
        var ct = _deploymentCts.Token;
        _deploymentElapsedText = "Elapsed: 00:00:00";
        if (_deploymentElapsedLabel is not null && !_deploymentElapsedLabel.IsDisposed) _deploymentElapsedLabel.Text = _deploymentElapsedText;
        if (_logModeCombo is not null && !_logModeCombo.IsDisposed) _logModeCombo.Enabled = false;
        var deploymentStopwatch = System.Diagnostics.Stopwatch.StartNew();
        _deployment.ResetPerformanceSnapshot();
        DeploymentHistoryRecord? historyAttempt = null;
        try
        {
            historyAttempt = _history.BeginAttempt(_profile, _logService.LogFile);
            AppendLog($"[HISTORY] Deployment attempt recorded. Project ID: {historyAttempt.ProjectId}; Attempt ID: {historyAttempt.AttemptId}; Version: {(string.IsNullOrWhiteSpace(historyAttempt.ApplicationVersion) ? "unknown" : historyAttempt.ApplicationVersion)}");
        }
        catch (Exception historyEx)
        {
            AppendLog("[HISTORY] Warning: could not start persistent deployment-history record: " + historyEx.Message);
        }
        var codeQuality = new CodeQualitySession();
        await Task.Run(() => codeQuality.Start(_appData, _profile, historyAttempt?.ProjectId ?? "unknown", historyAttempt?.AttemptId ?? Guid.NewGuid().ToString("N")));
        _runner.CodeQuality = codeQuality;
        using var elapsedTimer = new System.Windows.Forms.Timer { Interval = 500 };
        elapsedTimer.Tick += (_, _) =>
        {
            _deploymentElapsedText = $"Elapsed: {deploymentStopwatch.Elapsed:hh\\:mm\\:ss}";
            if (_deploymentElapsedLabel is not null && !_deploymentElapsedLabel.IsDisposed)
                _deploymentElapsedLabel.Text = _deploymentElapsedText;
        };
        elapsedTimer.Start();
        _back.Enabled = false;
        _next.Enabled = true;
        _next.Text = "Cancel Deploy";
        _deploymentStatus = "DEPLOYMENT IN PROGRESS";
        _persistentState.LastResult = "InProgress";
        UpdateDeploymentIndicator();
        _appData.SaveState(_persistentState);
        string? stagedPublish = null;
        string? publish = null;
        string? previousSitePath = null;
        IDisposable? iisAttempt = null;
        try
        {
            var isRemote = _profile.TargetMode == "Remote IIS Server" && !isFastLocalTest;
            if (isRemote) throw new InvalidOperationException("Remote IIS deployment is blocked until equivalent target-side IIS safety checks and locking are verified. No remote changes were made.");
            if (!isFastLocalTest)
            {
                iisAttempt = _deployment.BeginIisAttempt(_profile, historyAttempt?.AttemptId);
                await _deployment.ValidateIisPreflightAsync(_profile, ct);
            }
            if (!isRemote && !isFastLocalTest) previousSitePath = await _deployment.GetSitePhysicalPathAsync(_profile, ct);
            AppendLog($"Starting deployment. Target mode: {_profile.TargetMode}; Build mode: {_profile.BuildMode}; Dependency mode: {_profile.DependencyResolutionMode}; Offline build: {_profile.OfflineBuild}");
            if (isRemote) { AppendLog("[REMOTE] Checking/bootstrapping target prerequisites..."); await _remote.BootstrapAsync(_profile, ct); }
            if (!isFastLocalTest && !isRemote) await _sql.TestConnectionWithSafeFallbackAsync(_profile, ct);
            _deployment.IisStage("BUILD", "RUNNING");
            stagedPublish = await _deployment.PublishAsync(_profile, ct);
            _deployment.IisStage("BUILD", "PASS");
            if (isFastLocalTest)
            {
                publish = stagedPublish;
                AppendLog("[LOCAL] Compile validation passed. IIS, database preparation, remote transfer, health request, and hosted files were not touched.");
            }
            else if (isRemote)
            {
                AppendLog("[REMOTE] Transferring prepared publish output and activating remote IIS site...");
                await _remote.DeployPublishAsync(_profile, stagedPublish, ct);
                publish = _profile.HostingRoot;
                var remoteHealth = await _remote.HealthCheckAsync(_profile, ct);
                AppendLog("Remote health check: " + remoteHealth.Detail);
                if (!remoteHealth.Ok) throw new InvalidOperationException("Remote deployment completed but the health check failed. Review the log.");
                AppendLog("[VERIFY] Remote health passed. File-level frontend fingerprint verification is performed for local IIS deployments; remote file-level verification remains transport-dependent.");
                try { Directory.Delete(stagedPublish, true); } catch (Exception ex) { InternalDiagnostics.Record(ex, "Cleanup staged publish folder"); }
                stagedPublish = null;
            }
            else
            {
                AppendLog("Activating selected hosted site folder...");
                publish = await _deployment.ActivateStagingAsync(_profile, stagedPublish, ct);
                stagedPublish = null;
                AppendLog("Configuring IIS site and application pool...");
                _deployment.IisStage("CONFIGURE", "RUNNING");
                await _deployment.ConfigureIisAsync(_profile, publish, ct);
                _deployment.IisStage("CONFIGURE", "PASS");
                AppendLog("Preparing database and IIS application-pool login...");
                await _sql.CreateDatabaseAndAppPoolLoginAsync(_profile, ct);
                _deployment.WriteConnectionString(publish, _profile);
                _deployment.IisStage("START", "RUNNING");
                await _deployment.RestartAppAsync(_profile, ct);
                _deployment.IisStage("START", "PASS");
                _deployment.IisStage("HEALTH", "RUNNING");
                await Task.Delay(2500, ct);
                var health = await _deployment.HealthCheckAsync(_profile, ct);
                AppendLog($"Health check [{health.Classification}]: " + health.Detail);
                _deployment.RecordDeploymentHealth($"{health.Classification}; HTTP={health.HttpStatus}; URL={health.Url}; {health.Detail}");
                if (!health.Ok)
                {
                    AppendLog("[DIAGNOSTICS] Health failure detected. Collecting startup diagnostics BEFORE rollback...");
                    var startupDiagnostics = await _deployment.CollectStartupDiagnosticsAsync(_profile, publish, health, CancellationToken.None);
                    AppendLog(startupDiagnostics);
                    var summary = health.Classification == "ASP.NET_CORE_STARTUP_FAILURE_500_30"
                        ? "ASP.NET Core Startup Failure — HTTP 500.30. The application process did not start successfully."
                        : $"Post-deploy health failure — {health.Classification}.";
                    throw new InvalidOperationException(summary + " Diagnostics were captured before rollback. Review the diagnostic report and log.");
                }
                var deployVerification = _deployment.VerifyActivatedDeployment(_profile, publish);
                AppendLog("Post-deploy verification: " + deployVerification.Detail);
                if (!deployVerification.Ok) throw new InvalidOperationException(deployVerification.Detail);
                var apiRelease = await _deployment.VerifyRunningApiReleaseAsync(_profile, publish, ct);
                AppendLog("Running API release verification: " + apiRelease.Detail);
                if (!apiRelease.Ok) throw new InvalidOperationException(apiRelease.Detail);
                var frontendRelease = await _deployment.VerifyRunningFrontendReleaseAsync(_profile, publish, ct);
                AppendLog("Running frontend release verification: " + frontendRelease.Detail);
                if (!frontendRelease.Ok) throw new InvalidOperationException(frontendRelease.Detail);
            }

            if (!isFastLocalTest)
            {
                _deployment.IisStage("HEALTH", "PASS");
                _deployment.IisStage("COMMIT", "RUNNING");
            }
            if (!isFastLocalTest)
            {
                _deployment.IisStage("COMMIT", "PASS");
                // Publish the success marker while the IIS recovery journal and rollback
                // ownership are still active. A marker-write failure must not disable rollback.
                _deployment.MarkDeploymentSuccessful(publish);
                _deployment.ResolveIisAttempt();
                // Persistent successful-build state must not advance before the
                // attempt and deployment completion markers have both succeeded.
                try { _deployment.FinalizeSuccessfulDeployment(_profile, publish); } catch (Exception cleanupEx) { AppendLog("[CLEANUP] Post-success cleanup warning: " + cleanupEx.Message); }
                try { _deployment.CleanupCommittedAttempt(_profile); } catch (Exception cleanupEx) { AppendLog("[CLEANUP] Current-attempt cleanup warning: " + cleanupEx.Message); }
                _lastSuccessfulPublishPath = publish;
            }
            _deploymentStatus = isFastLocalTest ? "FAST LOCAL TEST PASSED — HOSTED IIS UNCHANGED" : "DEPLOYMENT SUCCESSFUL";
            _persistentState.LastResult = "Successful";
            UpdateDeploymentIndicator();
            if (!isFastLocalTest) ShowTrayDeploymentResult(true);
            _progress.Value = 100;
            var host = string.IsNullOrWhiteSpace(_profile.HostHeader) ? "localhost" : _profile.HostHeader;
            var successUrl = _profile.HttpPort == 80 ? $"http://{host}" : $"http://{host}:{_profile.HttpPort}";
            AppendLog(isFastLocalTest ? "FAST LOCAL TEST SUCCESSFUL — no deployment was performed." : $"DEPLOYMENT SUCCESSFUL: {successUrl}");
            // Do not count time spent reading/dismissing the success dialog as deployment runtime.
            deploymentStopwatch.Stop();
            elapsedTimer.Stop();
            var successMessage = isFastLocalTest ? "Fast Local Test successful.\r\n\r\nRequired incremental builds completed; IIS and hosted files were not changed." : $"Deployment successful.\r\n\r\n{successUrl}";
            var successTitle = isFastLocalTest ? "Fast Local Test successful" : "Deployment successful";
            BeginInvoke(() => MessageBox.Show(this, successMessage, successTitle, MessageBoxButtons.OK, MessageBoxIcon.Information));
        }
        catch (OperationCanceledException) when (_deploymentCts?.IsCancellationRequested == true)
        {
            _deploymentStatus = "DEPLOYMENT CANCELLED";
            _persistentState.LastResult = "Cancelled";
            AppendLog("[CANCEL] Deployment cancellation acknowledged. Running cleanup/rollback...");
            var cleanupOk = true;
            var rollbackAttempted = false;
            try
            {
                try { if (!string.IsNullOrWhiteSpace(stagedPublish) && Directory.Exists(stagedPublish)) Directory.Delete(stagedPublish, recursive: true); }
                catch (Exception stagingEx) { AppendLog("[CLEANUP] Staging cleanup failed; IIS recovery will still be attempted: " + stagingEx); }
                if (!isFastLocalTest && _profile.TargetMode != "Remote IIS Server")
                {
                    rollbackAttempted = publish is not null;
                    await _deployment.CleanupFailedDeploymentAsync(_profile, publish, previousSitePath, CancellationToken.None);
                }

                AppendLog("[CANCEL] Recovery handler completed; inspect separate staging cleanup warnings. Rollback " + (rollbackAttempted ? "attempt completed (restoration not independently verified)." : "not required or not attempted."));
            }
            catch (Exception cleanupEx)
            {
                cleanupOk = false;
                _deploymentStatus = "DEPLOYMENT CANCELLED — RECOVERY FAILED";
                _persistentState.LastResult = "RecoveryFailed";
                AppendLog("[CANCEL] Cleanup/recovery FAILED: " + cleanupEx);
                ShowTrayDeploymentResult(false);
            }
            MessageBox.Show(this,
                cleanupOk ? "Deployment cancelled. Recovery handler completed; inspect the log for separate cleanup warnings. Restoration was not independently verified." : "Deployment cancelled and cleanup/recovery FAILED. Previous deployment state is uncertain. Review the log before retrying.",
                "Deployment cancelled", MessageBoxButtons.OK, cleanupOk ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
        }
        catch (Exception ex)
        {
            _deploymentStatus = "DEPLOYMENT UNSUCCESSFUL";
            _persistentState.LastResult = "Failed";
            AppendLog("DEPLOYMENT UNSUCCESSFUL: " + ex);
            try { if (iisAttempt is not null) await _deployment.CollectIisFailureDiagnosticsAsync(_profile); }
            catch (Exception diagnosticEx) { AppendLog("[DIAGNOSTICS] Collection failed; primary deployment error preserved: " + diagnosticEx); }
            var recoveryFailed = false;
            try
            {
                try { if (!string.IsNullOrWhiteSpace(stagedPublish) && Directory.Exists(stagedPublish)) Directory.Delete(stagedPublish, recursive: true); }
                catch (Exception stagingEx) { AppendLog("[CLEANUP] Staging cleanup failed; IIS recovery will still be attempted: " + stagingEx); }
                if (!isFastLocalTest && _profile.TargetMode != "Remote IIS Server") await _deployment.CleanupFailedDeploymentAsync(_profile, publish, previousSitePath, CancellationToken.None);

            }
            catch (Exception cleanupEx)
            {
                recoveryFailed = true;
                _deploymentStatus = "DEPLOYMENT FAILED — RECOVERY FAILED";
                _persistentState.LastResult = "RecoveryFailed";
                AppendLog("[CLEANUP] Recovery FAILED: " + cleanupEx);
            }
            ShowTrayDeploymentResult(false);
            _lastErrorReportPath = null;
            string diagnosticLocation;
            try
            {
                _lastErrorReportPath = CreateErrorReport(ex);
                diagnosticLocation = "Diagnostic report saved to:\r\n" + _lastErrorReportPath;
            }
            catch (Exception reportError)
            {
                // A secondary diagnostics failure must NEVER replace the
                // original deployment/rollback exception on screen or propagate
                // into Auto Deploy as though it were the primary failure.
                InternalDiagnostics.Record(reportError, "Create deployment failure report");
                try { AppendLog("[DIAGNOSTICS] Error report unavailable (original deploy error preserved): " + reportError.Message); }
                catch (Exception secondaryLogError)
                {
                    InternalDiagnostics.Record(secondaryLogError, "Log diagnostic report failure");
                }
                diagnosticLocation = "Diagnostic report unavailable; original deployment error shown above.\r\n" +
                    "Active log: " + _logService.LogFile;
            }
            MessageBox.Show(
                this,
                (recoveryFailed ? "Deployment failed AND recovery failed. Previous deployment state is uncertain.\r\n\r\n" :
                    "Deployment unsuccessful. Recovery handler completed; inspect separate cleanup warnings. Restoration was not independently verified.\r\n\r\n") +
                GenericWeb.DeploymentManager.Services.SecretRedactor.Redact(ex.Message) + "\r\n\r\n" +
                diagnosticLocation,
                "Deployment unsuccessful",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }
        finally
        {
            try { iisAttempt?.Dispose(); } catch (Exception journalEx) { AppendLog("[IIS] Could not finalize attempt journal: " + journalEx.Message); }
            codeQuality.Finish(_persistentState.LastResult == "Successful" ? "Successful" : _persistentState.LastResult == "Cancelled" ? "Cancelled" : "Failed");
            _runner.CodeQuality = null;
            deploymentStopwatch.Stop();
            if (historyAttempt is not null)
            {
                try
                {
                    var historyResult = _persistentState.LastResult == "Successful" ? "Successful" : (_persistentState.LastResult == "Cancelled" ? "Cancelled" : (_persistentState.LastResult == "RecoveryFailed" ? "RecoveryFailed" : "Failed"));
                    _history.CompleteAttempt(historyAttempt, _profile, historyResult, deploymentStopwatch.Elapsed, publish ?? _profile.HostingRoot, _deployment.LastPerformanceSnapshot);
                    AppendLog($"[HISTORY] Deployment attempt finalized: {historyResult}; elapsed {deploymentStopwatch.Elapsed:hh\\:mm\\:ss}.");
                }
                catch (Exception historyEx)
                {
                    AppendLog("[HISTORY] Warning: could not finalize persistent deployment-history record: " + historyEx.Message);
                }
            }
            elapsedTimer.Stop();
            _deploymentElapsedText = $"Elapsed: {deploymentStopwatch.Elapsed:hh\\:mm\\:ss}";
            if (_deploymentElapsedLabel is not null && !_deploymentElapsedLabel.IsDisposed)
            {
                _deploymentElapsedLabel.Text = _deploymentElapsedText;
                _deploymentElapsedLabel.ForeColor = _persistentState.LastResult == "Successful" ? Color.ForestGreen : ((_persistentState.LastResult == "Failed" || _persistentState.LastResult == "RecoveryFailed") ? Color.Firebrick : (_persistentState.LastResult == "Cancelled" ? Color.DarkOrange : Color.RoyalBlue));
            }
            AppendLog($"Deployment elapsed time: {deploymentStopwatch.Elapsed:hh\\:mm\\:ss}");
            _operationRunning = false;
            if (_logModeCombo is not null && !_logModeCombo.IsDisposed) _logModeCombo.Enabled = true;
            _persistentState.UpdatedAt = DateTime.Now;
            _appData.SaveState(_persistentState);
            UpdateDeploymentIndicator();
            if (_pageIndex == _steps.Length - 1)
            {
                _next.Text = (_persistentState.LastResult == "Failed" || _persistentState.LastResult == "RecoveryFailed") ? "Retry Deploy" : (_persistentState.LastResult == "Successful" ? "Deploy Again" : (_persistentState.LastResult == "Cancelled" ? "Deploy" : "Deploy"));
                BuildReviewPageRefresh();
            }
            UpdateNavigationState();
        }
    }


    private sealed record BrowserChoice(string Name, string? ExecutablePath)
    {
        public override string ToString() => Name;
    }

    private static List<BrowserChoice> DetectBrowsers()
    {
        var result = new List<BrowserChoice> { new("System Default", null) };
        var candidates = new[]
        {
            ("Google Chrome", new[] { @"C:\Program Files\Google\Chrome\Application\chrome.exe", @"C:\Program Files (x86)\Google\Chrome\Application\chrome.exe" }),
            ("Mozilla Firefox", new[] { @"C:\Program Files\Mozilla Firefox\firefox.exe", @"C:\Program Files (x86)\Mozilla Firefox\firefox.exe" }),
            ("Brave", new[] { @"C:\Program Files\BraveSoftware\Brave-Browser\Application\brave.exe", @"C:\Program Files (x86)\BraveSoftware\Brave-Browser\Application\brave.exe" }),
            ("Microsoft Edge", new[] { @"C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe", @"C:\Program Files\Microsoft\Edge\Application\msedge.exe" })
        };
        foreach (var (name, paths) in candidates)
        {
            var path = paths.FirstOrDefault(File.Exists);
            if (path is not null) result.Add(new BrowserChoice(name, path));
        }
        return result;
    }

    private static void OpenSiteWithBrowser(string url, BrowserChoice browser)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(browser.ExecutablePath) || !File.Exists(browser.ExecutablePath))
            {
                OpenTarget(url);
                return;
            }
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = browser.ExecutablePath,
                Arguments = "\"" + url + "\"",
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            MessageBox.Show("Could not open the selected browser. Falling back to the system default.\r\n\r\n" + GenericWeb.DeploymentManager.Services.SecretRedactor.Redact(ex.Message), "Browser launch failed", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            OpenTarget(url);
        }
    }

    private static void OpenTarget(string target)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = target,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            MessageBox.Show("Could not open:\r\n" + target + "\r\n\r\n" + GenericWeb.DeploymentManager.Services.SecretRedactor.Redact(ex.Message), "Open failed", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private string CreateErrorReport(Exception ex)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Generic Web Deployment Manager " + ProductInfo.DisplayVersion + " — Diagnostic Error Report");
        sb.AppendLine($"Time: {DateTime.Now:O}");
        sb.AppendLine($"Stage: {_steps[Math.Clamp(_pageIndex, 0, _steps.Length - 1)]}");
        sb.AppendLine($"Build mode: {_profile.BuildMode}");
        sb.AppendLine($"Target: {_profile.TargetHost}");
        sb.AppendLine($"Source: {_profile.SourceFolder}");
        sb.AppendLine($"API project: {_profile.ApiProjectPath}");
        sb.AppendLine($"SQL instance: {_profile.SqlInstance}");
        sb.AppendLine($"Database: {_profile.DatabaseName}");
        sb.AppendLine($"SQL authentication: {_sql.Connections.State(_profile).Detail}");
        sb.AppendLine($"IIS site: {_profile.SiteName}");
        sb.AppendLine($"App pool: {_profile.AppPoolName}");
        sb.AppendLine($"Port: {_profile.HttpPort}");
        sb.AppendLine($"Hosted site folder: {_profile.HostingRoot}");
        sb.AppendLine($"Deployment layout: {_profile.DeploymentLayout}");
        sb.AppendLine($"Persistent data root: {_profile.DeploymentRoot}");
        sb.AppendLine($"Installer cache: {_profile.InstallerCacheFolder}");
        sb.AppendLine($"Full log: {_logService.LogFile}");
        sb.AppendLine();
        sb.AppendLine("Exception:");
        sb.AppendLine(ex.ToString());
        sb.AppendLine();
        sb.AppendLine("NOTE: Passwords are intentionally not included in this report.");
        sb.AppendLine();
        sb.AppendLine("Recent deployment log (tail):");
        try
        {
            foreach (var line in ReadRecentLogLines(_logService.LogFile, 250)) sb.AppendLine(line);
        }
        catch (IOException logReadError)
        {
            InternalDiagnostics.Record(logReadError, "Read active deployment log for failure report");
            sb.AppendLine("[Log tail could not be read; original deployment exception is preserved above.]");
        }
        catch (UnauthorizedAccessException logReadError)
        {
            InternalDiagnostics.Record(logReadError, "Access active deployment log for failure report");
            sb.AppendLine("[Log tail access denied; original deployment exception is preserved above.]");
        }
        return _appData.SaveErrorReport(sb.ToString());
    }

    private static IReadOnlyList<string> ReadRecentLogLines(string path, int maxLines)
        => DeploymentLogTailReader.ReadLastLines(path, maxLines);


    private void BuildReviewPageRefresh()
    {
        if (_pageIndex != _steps.Length - 1) return;
        _content.Controls.Clear();
        BuildReviewPage();
    }

    private async Task BusyAsync(Control control, Func<Task> action)
    {
        control.Enabled = false;
        UseWaitCursor = true;
        try { await action(); }
        catch (Exception ex) { AppendLog("ERROR: " + ex); MessageBox.Show(this, SecretRedactor.Redact(ex.Message), "Operation failed", MessageBoxButtons.OK, MessageBoxIcon.Error); }
        finally { _operationRunning = false; control.Enabled = true; UseWaitCursor = false; UpdateNavigationState(); }
    }

    private static FlowLayoutPanel VerticalPanel() => new() { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = true };

    private string BrowseStartFolder(string? preferred, BrowseContext context = BrowseContext.General)
    {
        var resolved = AppDataService.ResolveExistingFolder(preferred);
        if (!string.IsNullOrWhiteSpace(resolved)) return resolved;
        return _appData.GetLastBrowseFolder(context);
    }

    private void RememberBrowseSelection(string path, BrowseContext context = BrowseContext.General) => _appData.SaveLastBrowseFolder(path, context);

    private static TableLayoutPanel FormGrid()
    {
        var grid = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, GrowStyle = TableLayoutPanelGrowStyle.AddRows, ColumnCount = 2, RowCount = 0, Padding = new Padding(0, 4, 0, 0) };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 220));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        return grid;
    }

    private static void AddRow(TableLayoutPanel grid, string label, Control control)
    {
        var row = grid.RowCount++;
        grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        grid.Controls.Add(new Label { Text = label, AutoSize = true, Margin = new Padding(3, 4, 3, 8), Padding = new Padding(0, 8, 10, 0) }, 0, row);
        control.Margin = new Padding(3, 4, 3, 8);
        grid.Controls.Add(control, 1, row);
    }

    private static FlowLayoutPanel VerticalWrap(params Control[] controls)
    {
        var panel = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false };
        panel.Controls.AddRange(controls);
        return panel;
    }

    private static FlowLayoutPanel Wrap(params Control[] controls)
    {
        var panel = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, WrapContents = false };
        panel.Controls.AddRange(controls);
        return panel;
    }

    private static GroupBox InfoBox(string title, string text)
    {
        var box = new GroupBox { Text = title, Width = 850, Height = 165, Padding = new Padding(14) };
        box.Controls.Add(new Label { Text = text, Dock = DockStyle.Fill, AutoSize = false });
        return box;
    }

    private static GroupBox StatusCard(string title, string detail, bool ok)
    {
        var box = new GroupBox { Text = title, Dock = DockStyle.Fill, Margin = new Padding(7), Padding = new Padding(12) };
        var status = new Label { Text = ok ? "● Ready" : "● Action required", AutoSize = true, ForeColor = ok ? Color.ForestGreen : Color.Firebrick, Font = new Font("Segoe UI Semibold", 9.5F) };
        var detailLabel = new Label { Text = detail, AutoSize = false, Dock = DockStyle.Fill, Padding = new Padding(0, 26, 0, 0), ForeColor = Color.FromArgb(55, 55, 55) };
        box.Controls.Add(detailLabel); box.Controls.Add(status);
        return box;
    }

    private static void ReplaceCard(TableLayoutPanel grid, int column, int row, Control newCard)
    {
        var old = grid.GetControlFromPosition(column, row);
        if (old is not null) { grid.Controls.Remove(old); old.Dispose(); }
        grid.Controls.Add(newCard, column, row);
    }

    private static string GenerateStrongPassword()
    {
        const string upper = "ABCDEFGHJKLMNPQRSTUVWXYZ";
        const string lower = "abcdefghijkmnopqrstuvwxyz";
        const string digits = "23456789";
        const string symbols = "!@#$%&*+-_";
        const string all = upper + lower + digits + symbols;
        static char Pick(string set) => set[RandomNumberGenerator.GetInt32(set.Length)];
        var chars = new List<char> { Pick(upper), Pick(lower), Pick(digits), Pick(symbols) };
        while (chars.Count < 20) chars.Add(Pick(all));
        for (var i = chars.Count - 1; i > 0; i--)
        {
            var j = RandomNumberGenerator.GetInt32(i + 1);
            (chars[i], chars[j]) = (chars[j], chars[i]);
        }
        return new string(chars.ToArray());
    }

    private DateTime _lastLogStateSaveUtc = DateTime.MinValue;
    private void AppendLog(string message)
    {
        // Write to disk on the producer thread, before any WinForms scheduling.
        // An overloaded message pump must not delay or drop deployment evidence.
        _logService.Write(message, (at, clean) =>
        {
            if (!_acceptUiLogLines) return;
            lock (_pendingUiLogSync)
            {
                if (!_acceptUiLogLines) return;
                if (_pendingUiLogLines.Count >= MaxPendingUiLogLines)
                {
                    _pendingUiLogLines.Dequeue();
                    _droppedUiLogLines++;
                }
                _pendingUiLogLines.Enqueue((at, clean));
            }
        });
    }

    private void FlushPendingUiLogLines()
    {
        // This method runs only on the WinForms UI thread (timer / log-mode change).
        if (IsDisposed || Disposing || _log.IsDisposed) return;
        var pending = new List<(DateTime At, string Line)>(MaxUiLogLinesPerFlush);
        int dropped;
        lock (_pendingUiLogSync)
        {
            dropped = _droppedUiLogLines;
            _droppedUiLogLines = 0;
            while (pending.Count < MaxUiLogLinesPerFlush && _pendingUiLogLines.Count > 0)
                pending.Add(_pendingUiLogLines.Dequeue());
        }
        if (pending.Count == 0 && dropped == 0) return;

        var batch = new StringBuilder();
        string? latestActivity = null;
        void AddToUi(DateTime at, string text)
        {
            var display = NormalizeLogForDisplay(text);
            _uiLogLines.Add((at, display));
            if (!ShouldShowLogLine(display)) return;
            batch.Append('[').Append(at.ToString("HH:mm:ss")).Append("] ").Append(display).Append("\r\n");
            if (!string.IsNullOrWhiteSpace(display))
                latestActivity = display.Replace('\r', ' ').Replace('\n', ' ');
        }
        if (dropped > 0)
            AddToUi(DateTime.Now,
                $"[DEPLOYMENT LOG] {dropped} older UI-only lines skipped under load; complete session log remains on disk.");
        foreach (var (at, line) in pending) AddToUi(at, line);
        if (_uiLogLines.Count > 5_000)
            _uiLogLines.RemoveRange(0, _uiLogLines.Count - 4_000);
        if (batch.Length > 0)
            _log.AppendText(batch.ToString());
        if (_log.TextLength > 300_000)
        {
            _log.Select(0, _log.TextLength - 150_000);
            _log.SelectedText = "[... older lines hidden here; full log remains on disk ...]\r\n";
        }
        if (latestActivity is not null)
        {
            _activity.Text = latestActivity;
            _activity.AutoEllipsis = true;
            if (_reviewActivity is not null && !_reviewActivity.IsDisposed)
                _reviewActivity.Text = latestActivity;
        }
        // Persist the wizard's latest state no more than once every 5 seconds.
        if ((DateTime.UtcNow - _lastLogStateSaveUtc).TotalSeconds >= 5)
        {
            _lastLogStateSaveUtc = DateTime.UtcNow;
            _persistentState.LastStage = _steps[Math.Clamp(_pageIndex, 0, _steps.Length - 1)];
            _persistentState.LastSourceFolder = _profile.SourceFolder;
            _persistentState.LastSiteName = _profile.SiteName;
            _persistentState.UpdatedAt = DateTime.Now;
            _appData.SaveState(_persistentState);
        }
    }

    private void RenderVisibleLog()
    {
        if (InvokeRequired) { BeginInvoke(RenderVisibleLog); return; }
        FlushPendingUiLogLines();
        var sb = new StringBuilder();
        foreach (var item in _uiLogLines)
            if (ShouldShowLogLine(item.Line)) sb.Append('[').Append(item.At.ToString("HH:mm:ss")).Append("] ").AppendLine(item.Line);
        _log.Text = sb.ToString();
        _log.SelectionStart = _log.TextLength;
        _log.ScrollToCaret();
    }

    private enum LogViewMode { Minimal, Normal, Verbose }

    private static string NormalizeLogForDisplay(string line)
    {
        if (!line.StartsWith("ERR: ", StringComparison.OrdinalIgnoreCase)) return line;
        var body = line[5..];
        if (IsBenignNpmDiagnostic(body)) return body;
        return line;
    }

    private bool ShouldShowLogLine(string line)
    {
        if (_logViewMode == LogViewMode.Verbose) return true;

        var low = line.ToLowerInvariant();
        var benignVerbose = low.StartsWith("npm timing ") || low.StartsWith("npm verbose ") ||
                            low.Contains("http fetch get 200") || low.Contains("reify failed optional dependency") ||
                            low.StartsWith("working directory:") || low.Contains("completed in ");
        if (_logViewMode == LogViewMode.Normal) return !benignVerbose;

        // Minimal: stages, real failures, success, health, exit codes and elapsed/heartbeat only.
        return low.Contains("[stage-") || low.Contains("deployment") || low.Contains("frontend") || low.Contains("backend") ||
               low.Contains("health check") || low.Contains("error ") || low.StartsWith("err:") ||
               low.Contains("failed") || low.Contains("successful") || low.Contains("exit code") ||
               low.StartsWith("running:") || low.Contains("elapsed") || low.Contains("configuring iis") ||
               low.Contains("preparing database");
    }

    private static bool IsBenignNpmDiagnostic(string body)
    {
        var low = body.ToLowerInvariant();
        return low.StartsWith("npm timing ") || low.StartsWith("npm verbose ") || low.StartsWith("npm info ") ||
               low.Contains("npm http fetch get 200") || low.Contains("reify failed optional dependency");
    }

    private static Icon CreateTrayStatusIcon(Color color, bool success)
    {
        using var bitmap = new Bitmap(32, 32);
        using (var graphics = Graphics.FromImage(bitmap))
        {
            graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            using var background = new SolidBrush(color);
            using var pen = new Pen(Color.White, 3.8f) { StartCap = System.Drawing.Drawing2D.LineCap.Round, EndCap = System.Drawing.Drawing2D.LineCap.Round };
            graphics.FillEllipse(background, 1, 1, 30, 30);
            if (success)
                graphics.DrawLines(pen, new[] { new Point(8, 16), new Point(14, 22), new Point(25, 10) });
            else
            {
                graphics.DrawLine(pen, 10, 10, 22, 22);
                graphics.DrawLine(pen, 22, 10, 10, 22);
            }
        }
        var handle = bitmap.GetHicon();
        try
        {
            using var temporary = Icon.FromHandle(handle);
            return (Icon)temporary.Clone();
        }
        finally { DestroyIcon(handle); }
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr handle);

    private void AcknowledgeTrayAlert()
    {
        _trayAlertActive = false;
        _trayBlinkTimer.Stop();
        _trayBlinkVisible = true;
        _trayIcon.Icon = Icon ?? SystemIcons.Application;
        _trayIcon.Text = "GWD — Ready";
    }

    private void ShowTrayDeploymentResult(bool success)
    {
        if (IsDisposed || Disposing) return;
        _trayAlertActive = true;
        _trayBlinkVisible = true;
        _trayIcon.Icon = success ? _traySuccessIcon : _trayFailureIcon;
        _trayIcon.Text = success ? "GWD — Deployment successful" : "GWD — Deployment failed";
        _trayBlinkTimer.Start();
        _trayIcon.ShowBalloonTip(5000, success ? "Deployment completed" : "Deployment failed",
            success ? "Deployment completed successfully. Click the tray icon to acknowledge." : "Deployment failed. Open GWD to review the error.",
            success ? ToolTipIcon.Info : ToolTipIcon.Error);
    }

    private void WizardForm_FormClosing(object? sender, FormClosingEventArgs e)
    {
        var warning = _operationRunning
            ? "A deployment/install operation is currently running. Closing now may interrupt it.\r\n\r\nDo you want to exit?"
            : "Do you want to close Deployment Manager?";
        if (MessageBox.Show(this, warning, "Confirm exit", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.Yes)
        { e.Cancel = true; return; }
        _acceptUiLogLines = false;
        _uiLogFlushTimer.Stop();
        _uiLogFlushTimer.Dispose();
        _trayBlinkTimer.Stop();
        _trayBlinkTimer.Dispose();
        _trayIcon.Visible = false;
        _trayIcon.Dispose();
        _traySuccessIcon.Dispose();
        _trayFailureIcon.Dispose();
        _runtimeMetricsTimer.Stop();
        _runtimeMetricsTimer.Dispose();
        _runtimeMetrics.Dispose();
        _healthMonitor.Updated -= OnHealthUpdated;
        _healthMonitor.Dispose();
        _healthTip.Dispose();
        // Commit pending edits before closing and before any later state I/O
        // can fail, then tear down the idle-save timer.
        FlushDeferredSettingsSaves();
        _settingsSaveTimer.Dispose();
        _persistentState.LastStage = _steps[Math.Clamp(_pageIndex,0,_steps.Length-1)];
        _persistentState.LastSourceFolder = _profile.SourceFolder;
        _persistentState.LastSiteName = _profile.SiteName;
        _persistentState.UpdatedAt = DateTime.Now;
        _appData.SaveState(_persistentState);
        try { _logService.CloseSession(); }
        catch (Exception ex) { InternalDiagnostics.Record(ex, "Close active log session"); }
    }
    private static string SanitizeFileName(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        return new string((string.IsNullOrWhiteSpace(value) ? "Project" : value).Select(c => invalid.Contains(c) ? '_' : c).ToArray()).Replace(" ", "-");
    }

    private static void ExportCleanSourceZip(string sourceRoot, string zipPath, IReadOnlyCollection<string> exclusions)
    {
        var root = Path.GetFullPath(sourceRoot);
        var destination = Path.GetFullPath(zipPath);
        if (DeploymentPathSafety.Contains(root.TrimEnd('\\', '/'), destination))
            throw new IOException("The export ZIP must be outside the source folder.");
        // Reject a linked source root or linked ancestor before reading any source files.
        var ancestor = root;
        while (!string.IsNullOrEmpty(ancestor))
        {
            if ((File.GetAttributes(ancestor) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("The source path contains a filesystem link: " + ancestor);
            ancestor = Path.GetDirectoryName(ancestor);
        }

        var excluded = new HashSet<string>(exclusions.Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x.Trim().Trim('\\', '/')), StringComparer.OrdinalIgnoreCase);
        // Validate the entire included tree before replacing an existing archive.
        void Visit(string directory, Action<string, string>? addFile)
        {
            foreach (var item in Directory.EnumerateFileSystemEntries(directory))
            {
                var relative = Path.GetRelativePath(root, item);
                if (relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Any(excluded.Contains))
                    continue;
                var attributes = File.GetAttributes(item);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("The source contains a filesystem link: " + relative);
                if ((attributes & FileAttributes.Directory) != 0) Visit(item, addFile);
                else addFile?.Invoke(item, relative);
            }
        }

        Visit(root, null);
        if (File.Exists(destination)) File.Delete(destination);
        try
        {
            using var archive = ZipFile.Open(destination, ZipArchiveMode.Create);
            Visit(root, (file, relative) => archive.CreateEntryFromFile(file, relative, CompressionLevel.Optimal));
        }
        catch
        {
            if (File.Exists(destination)) File.Delete(destination);
            throw;
        }
    }

    private sealed record DeploymentProfileSnapshot(
        string ApplicationName, string SourceFolder, string ApiProjectPath, string BuildMode, string DependencyResolutionMode,
        string ReadyPublishFolder, string DeploymentRepositoryFolder, string WorkspaceRoot, string DeploymentRoot, string BackupRoot,
        string HostingRoot, string DeploymentLayout, bool CleanupPreviousDeployments, bool UseDefaultIisWebsite,
        string SiteName, string AppPoolName, int HttpPort, string HostHeader, string HealthPath,
        string SqlInstance, string DatabaseName, bool UseWindowsAuthentication, string SqlUserName, bool SourceLocked)
    {
        public static DeploymentProfileSnapshot From(DeploymentProfile p, bool locked) => new(p.ApplicationName,p.SourceFolder,p.ApiProjectPath,p.BuildMode,p.DependencyResolutionMode,p.ReadyPublishFolder,p.DeploymentRepositoryFolder,p.WorkspaceRoot,p.DeploymentRoot,p.BackupRoot,p.HostingRoot,p.DeploymentLayout,p.CleanupPreviousDeployments,p.UseDefaultIisWebsite,p.SiteName,p.AppPoolName,p.HttpPort,p.HostHeader,p.HealthPath,p.SqlInstance,p.DatabaseName,p.UseWindowsAuthentication,p.SqlUserName,locked);
        public void ApplyTo(DeploymentProfile p) { p.ApplicationName=ApplicationName;p.SourceFolder=SourceFolder;p.ApiProjectPath=ApiProjectPath;p.BuildMode=BuildMode;p.DependencyResolutionMode=DependencyResolutionMode;p.OfflineBuild=DependencyResolutionMode.Equals("Offline Only",StringComparison.OrdinalIgnoreCase);p.ReadyPublishFolder=ReadyPublishFolder;p.DeploymentRepositoryFolder=DeploymentRepositoryFolder;p.WorkspaceRoot=WorkspaceRoot;p.DeploymentRoot=DeploymentRoot;p.BackupRoot=BackupRoot;p.HostingRoot=HostingRoot;p.DeploymentLayout=DeploymentLayout;p.CleanupPreviousDeployments=CleanupPreviousDeployments;p.UseDefaultIisWebsite=UseDefaultIisWebsite;p.SiteName=SiteName;p.AppPoolName=AppPoolName;p.HttpPort=HttpPort;p.HostHeader=HostHeader;p.HealthPath=HealthPath;p.SqlInstance=SqlInstance;p.DatabaseName=DatabaseName;p.UseWindowsAuthentication=UseWindowsAuthentication;p.SqlUserName=SqlUserName; }
    }

    private sealed class CleanExportOptionsForm : Form
    {
        private readonly CheckedListBox _list = new() { Dock = DockStyle.Fill, CheckOnClick = true };
        public IReadOnlyCollection<string> Exclusions => _list.Items.Cast<string>().Except(_list.CheckedItems.Cast<string>(), StringComparer.OrdinalIgnoreCase).ToArray();
        public CleanExportOptionsForm()
        {
            Text="Clean Source Export"; Width=460; Height=390; StartPosition=FormStartPosition.CenterParent; Font=new Font("Segoe UI",9.5F);
            var note=new Label{Text="Check folders you want INCLUDED in the ZIP. Unchecked folders are excluded.",Dock=DockStyle.Top,Height=42,Padding=new Padding(10)};
            foreach(var x in new[]{".git",".worktrees","IIS-Publish","bin","obj","node_modules"}) _list.Items.Add(x,false);
            var buttons=new FlowLayoutPanel{Dock=DockStyle.Bottom,Height=48,FlowDirection=FlowDirection.RightToLeft,Padding=new Padding(8)};
            var ok=new Button{Text="Export",DialogResult=DialogResult.OK,Width=100}; var cancel=new Button{Text="Cancel",DialogResult=DialogResult.Cancel,Width=100}; buttons.Controls.Add(ok);buttons.Controls.Add(cancel);
            Controls.Add(_list);Controls.Add(note);Controls.Add(buttons);AcceptButton=ok;CancelButton=cancel;
        }
    }

}

