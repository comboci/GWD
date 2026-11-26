using GenericWeb.DeploymentManager.Models;
using GenericWeb.DeploymentManager.Services;
using System.Text;
using System.Net;
using System.Net.Sockets;
using System.Text.RegularExpressions;
using System.Security.Cryptography;
using System.Text.Json;
using System.IO.Compression;
using GenericWeb.DeploymentManager.UI.Pages;
using static GenericWeb.DeploymentManager.UI.Controls.StatusCardUi;
using static GenericWeb.DeploymentManager.UI.Controls.WizardLayout;

namespace GenericWeb.DeploymentManager;

public sealed partial class WizardForm : Form
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
    private Task<RuntimeMetricSample>? _runtimeMetricsPending;
    private bool _runtimeMetricsClosing;
    private readonly Label _resourceUsage = new() { AutoSize = true, ForeColor = Color.DimGray, Padding = new Padding(8, 8, 8, 0), Text = "CPU/RAM: sampling..." };
    // Persistent settings are coalesced on the UI thread, not written for each
    // TextChanged character. Flush before a page's controls are disposed.
    private readonly System.Windows.Forms.Timer _settingsSaveTimer = new() { Interval = 650 };
    private readonly Dictionary<string, (long Order, Action Save)> _deferredSettingsSaves = new(StringComparer.Ordinal);
    private readonly OrderedSettingsWriter _settingsWriter = new();
    private long _settingsWriteOrder;


    private readonly List<Button> _stepLabels = new();
    private readonly FlowLayoutPanel _stepPanel = new();
    private readonly Panel _content = new();
    private readonly Button _back = new() { Text = "< Back", Width = 135, Height = 40 };
    private readonly Button _next = new() { Text = "Next >", Width = 135, Height = 40 };
    private readonly Button _autoDeploy = new() { Text = "Auto Deploy", Width = 145, Height = 40 };
    private bool _autoDeployRunning;
    private bool _offlineDependenciesReady;
    private bool _dependencySyncRunning;
    private string _offlineDependenciesDetail = "Not checked";
    private readonly Label _pageTitle = new() { AutoSize = true, Font = GwdUi.Heading };
    private readonly Label _pageDescription = new() { AutoSize = true, MaximumSize = new Size(820, 0), ForeColor = Color.DimGray };
    private readonly Label _stepIndicator = new() { AutoSize = true, ForeColor = Color.DimGray, Font = GwdUi.Small };
    private readonly TextBox _log = new() { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Dock = DockStyle.Fill, Font = new Font("Consolas", 9F) };
    private readonly ProgressBar _progress = new() { Dock = DockStyle.Bottom, Height = 16 };
    private readonly Label _activity = new() { AutoSize = false, Dock = DockStyle.Fill, Height = 36, Padding = new Padding(8, 0, 8, 0), TextAlign = ContentAlignment.MiddleLeft, ForeColor = Color.DimGray, Text = "Ready" };

    private Button? _saveProfileAction;
    private Button? _loadProfileAction;
    private CheckBox? _lockSourceAction;
    private bool _sourceLocked;
    private Button? _exportSourceAction;
    private int _pageIndex;
    private int _renderedPageIndex = -1;
    private bool _navigationInProgress;
    private bool _pageActionRunning;
    private EnvironmentScanView? _scanView;
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
    // Navigation consumes only the latest source readiness snapshot. Actual deploy
    // and Next validation still perform their original authoritative checks.
    private (bool Prepared, string Folder)? _navigationSourceKey;
    private bool _navigationSourceReady;
    private CancellationTokenSource? _navigationProbeCts;
    private string? _lastErrorReportPath;
    private const string InterruptedDeploymentStatus = "PREVIOUS DEPLOYMENT INTERRUPTED — RECOVERY UNVERIFIED";
    private string _deploymentStatus = "Not started";
    private string? _lastSuccessfulPublishPath;
    private string _deploymentElapsedText = "Elapsed: 00:00:00";
    private LogViewMode _logViewMode = LogViewMode.Normal;
    private readonly List<(DateTime At, string Line)> _uiLogLines = new();
    private const int MaxPendingUiLogLines = 2_048;
    private const int MaxUiLogLinesPerFlush = 256;
    private const int MaxUiLineChars = 4_096;
    private const int MaxUiBatchChars = 64_000;
    private const int MaxUiHistoryChars = 1_000_000;
    private int _uiLogChars;
    private readonly object _pendingUiLogSync = new();
    private readonly Queue<(DateTime At, string Line)> _pendingUiLogLines = new();
    private readonly System.Windows.Forms.Timer _uiLogFlushTimer = new() { Interval = 100 };
    private int _droppedUiLogLines;
    private volatile bool _acceptUiLogLines = true;
    private ReviewStageView? _reviewView;
    private DeploymentLocationView? _locationView;
    private CancellationTokenSource? _deploymentCts;
    private bool _deploymentCancelConfirmationOpen;
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
            "Cancelled" => "PREVIOUS DEPLOYMENT CANCELLED",
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
        MinimumSize = new Size(800, 480);
        StartPosition = FormStartPosition.CenterScreen;
        WindowState = FormWindowState.Maximized;
        GwdUi.ConfigureForm(this);

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
        _runtimeMetricsTimer.Tick += async (_, _) => await UpdateRuntimeMetricsAsync();
        Shown += async (_, _) =>
        {
            if (_runtimeMetricsClosing) return;
            _runtimeMetricsTimer.Start();
            await UpdateRuntimeMetricsAsync();
        };
        Disposed += (_, _) => StopRuntimeMetrics();
        Disposed += (_, _) => StopVisualResources();
        Disposed += (_, _) => { if (!_log.IsDisposed) _log.Dispose(); };
        FormClosing += WizardForm_FormClosing;
        Shown += (_, _) => UpdateNavigationState();
        Disposed += (_, _) => CancelNavigationSourceProbe();
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
        if (IsDisposed || Disposing || !IsHandleCreated || _visualResourcesStopped) return;
        try
        {
            BeginInvoke((Action)(() =>
            {
                // A scheduled health callback can arrive after the form closes.
                if (IsDisposed || Disposing || _visualResourcesStopped) return;
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
        ObserveRecordedStatus(needsReview);
        UpdateDashboard();
    }

    private void BuildShell()
    {
        // Independent health/status row and full-width action footer avoid the
        // old competition for horizontal space at 125%/150% DPI.
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 3 };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 246));
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        _stepPanel.Dock = DockStyle.Fill;
        _stepPanel.FlowDirection = FlowDirection.TopDown;
        _stepPanel.WrapContents = false;
        _stepPanel.AutoScroll = true;
        _stepPanel.BackColor = GwdUi.HighContrast ? SystemColors.Control : GwdUi.Surface;
        _stepPanel.Padding = new Padding(10, 16, 10, 8);
        _stepPanel.Controls.Add(new Label { Text = "GWD / " + ProductInfo.DisplayVersion, AutoSize = true, Font = GwdUi.Section, Margin = new Padding(8, 3, 3, 8) });
        _stepPanel.Controls.Add(new Label { Text = "DEPLOYMENT WORKFLOW", AutoSize = true, Font = GwdUi.Small, Margin = new Padding(8, 5, 3, 7) });
        foreach (var step in _steps)
        {
            var label = new Button
            {
                Text = step, AccessibleName = step, TabStop = true, AutoSize = false,
                Width = 212, Height = 52, Padding = new Padding(8, 3, 5, 3),
                Margin = new Padding(2, 2, 2, 4), TextAlign = ContentAlignment.MiddleLeft,
                Font = GwdUi.Body, Cursor = Cursors.Hand
            };
            var stepIndex = _stepLabels.Count;
            label.Click += (_, _) => { if (CanNavigateInteractively()) ShowPage(stepIndex); };
            _stepLabels.Add(label);
            _stepPanel.Controls.Add(label);
        }
        _stepPanel.ClientSizeChanged += (_, _) =>
        {
            var width = _stepPanel.ClientSize.Width - _stepPanel.Padding.Horizontal - SystemInformation.VerticalScrollBarWidth - 6;
            if (width > 0) foreach (var button in _stepLabels) button.Width = width;
        };
        root.Controls.Add(_stepPanel, 0, 0);
        root.SetRowSpan(_stepPanel, 2);

        var right = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 4, Padding = new Padding(18, 14, 18, 4) };
        right.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        right.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        right.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        right.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        right.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        right.Controls.Add(BuildDashboard(), 0, 0);
        _pageTitle.Font = GwdUi.Heading;

        var header = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 2 };
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        header.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        header.Controls.Add(_pageTitle, 0, 0);
        _stepIndicator.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        header.Controls.Add(_stepIndicator, 1, 0);

        right.Controls.Add(header, 0, 1);
        right.Controls.Add(_pageDescription, 0, 2);
        right.ClientSizeChanged += (_, _) =>
        {
            var width = Math.Max(160, right.ClientSize.Width - right.Padding.Horizontal - 16);
            _pageDescription.MaximumSize = new Size(width, 0);
            _pageTitle.MaximumSize = new Size(Math.Max(140, width - _stepIndicator.PreferredSize.Width - 16), 0);
        };
        _content.Dock = DockStyle.Fill;
        // Auto-sized wizard rows can exceed the available height, especially at 125%/150% DPI.
        // Keep the footer visible and scroll the page instead of clipping/overlapping fields.
        _content.AutoScroll = true;
        _content.Padding = new Padding(0, 18, 0, 0);
        right.Controls.Add(_content, 0, 3);
        root.Controls.Add(right, 1, 0);

        // These health labels were updated by OnHealthUpdated but were never
        // parented to a visible control. Keep the recovery warning in the top
        // line, with horizontally scrollable status details below it.
        var healthBar = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 1, RowCount = 2, Padding = new Padding(12, 4, 12, 6), Margin = new Padding(0) };
        healthBar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        healthBar.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        healthBar.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        _deploymentIndicator.AutoSize = true;
        _deploymentIndicator.AutoEllipsis = false;
        _deploymentIndicator.Dock = DockStyle.Top;
        _deploymentIndicator.Font = GwdUi.Section;
        healthBar.Controls.Add(_deploymentIndicator, 0, 0);
        healthBar.ClientSizeChanged += (_, _) =>
            _deploymentIndicator.MaximumSize = new Size(Math.Max(160, healthBar.ClientSize.Width - healthBar.Padding.Horizontal - 8), 0);
        var healthDetails = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill, FlowDirection = FlowDirection.LeftToRight,
            WrapContents = true, AutoSize = true, Margin = new Padding(0), Padding = new Padding(3, 0, 3, 0)
        };
        healthDetails.Controls.Add(_iisHealth);
        healthDetails.Controls.Add(_sqlHealth);
        healthDetails.Controls.Add(_httpHealth);
        healthDetails.Controls.Add(_healthChecked);
        healthBar.Controls.Add(healthDetails, 0, 1);
        root.Controls.Add(healthBar, 1, 1);
        var footer = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 1, RowCount = 4, Padding = new Padding(10, 4, 10, 6) };
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        for (var i = 0; i < 4; i++) footer.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        _activity.AutoEllipsis = true;
        _activity.Dock = DockStyle.Top;
        _activity.Height = 28;
        footer.Controls.Add(_activity, 0, 0);
        _resourceUsage.Padding = new Padding(8, 2, 0, 2);
        footer.Controls.Add(_resourceUsage, 0, 1);
        footer.Controls.Add(BuildNotificationBar(), 0, 2);

        var footerButtons = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft, WrapContents = true, Margin = new Padding(0) };
        foreach (var button in new[] { _next, _back, _autoDeploy })
        {
            button.AutoSize = true;
            button.MinimumSize = new Size(108, 36);
            button.Padding = new Padding(8, 3, 8, 3);
        }
        footerButtons.Controls.Add(_next);
        footerButtons.Controls.Add(_back);
        var resetWizard = new Button { Text = "Start Over", AutoSize = true, MinimumSize = new Size(108, 36) };
        var startOverMenu = new ContextMenuStrip();
        resetWizard.ContextMenuStrip = startOverMenu;
        startOverMenu.Items.Add("Start Over — Clear Log", null, (_, _) => ResetWizard(clearLog: true));
        startOverMenu.Items.Add("Start Over — Keep Log", null, (_, _) => ResetWizard(clearLog: false));
        resetWizard.Click += (_, _) => { if (CanNavigateInteractively()) startOverMenu.Show(resetWizard, new Point(0, resetWizard.Height)); };
        footerButtons.Controls.Add(resetWizard);
        footerButtons.Controls.Add(_autoDeploy);
        var viewLogs = new Button { Text = "Diagnostics", AccessibleName = "Diagnostics Center", AutoSize = true, MinimumSize = new Size(108, 36) };
        viewLogs.Click += (_, _) => OpenDiagnostics();
        footerButtons.Controls.Add(viewLogs);
        var liveLog = new Button { Text = "Live log", AutoSize = true, MinimumSize = new Size(90, 36) };
        liveLog.Click += (_, _) => OpenLiveLog();
        footerButtons.Controls.Add(liveLog);
        footer.Controls.Add(footerButtons, 0, 3);
        root.Controls.Add(footer, 0, 2);
        root.SetColumnSpan(footer, 2);

        Controls.Add(root);
        Controls.Add(_progress);
        _progress.AccessibleName = "Wizard position (not deployment completion)";
        var menu = new MenuStrip { Dock = DockStyle.Top };
        var fileMenu = new ToolStripMenuItem("&File");
        var saveMenu = new ToolStripMenuItem("Save Profile") { ShortcutKeys = Keys.Control | Keys.S };
        saveMenu.Click += (_, _) => { if (!CanNavigateInteractively()) return; ShowPage(0); _saveProfileAction?.PerformClick(); };
        var loadMenu = new ToolStripMenuItem("Load Profile") { ShortcutKeys = Keys.Control | Keys.O };
        loadMenu.Click += (_, _) => { if (!CanNavigateInteractively()) return; ShowPage(0); _loadProfileAction?.PerformClick(); };
        fileMenu.DropDownItems.Add(saveMenu);
        fileMenu.DropDownItems.Add(loadMenu);
        fileMenu.DropDownItems.Add(new ToolStripSeparator());
        var settingsMenu = new ToolStripMenuItem("Settings");
        settingsMenu.Click += (_, _) =>
        {
            if (!CanNavigateInteractively()) return;
            if (!DrainSettingsSaves()) return;
            using var dialog = new SettingsForm(_appData);
            if (dialog.ShowDialog(this) == DialogResult.OK)
            {
                _profile.DeploymentRepositoryFolder = _appData.GetDeploymentRepositoryFolder();
                _profile.WorkspaceRoot = _appData.GetWorkspaceRoot();
                AppendLog("Settings updated. Release output: " + _appData.GetManagerReleaseRoot());
                NotifyUi(UiNoticeKind.Info, "Settings saved.");
                // These views display the settings that just changed.
                if (_pageIndex == 0 || _pageIndex == 4) RebuildCurrentPage();
            }
        };


        fileMenu.DropDownItems.Add(settingsMenu);
        var databaseMenu = new ToolStripMenuItem("Database Management");
        databaseMenu.Click += (_, _) =>
        {
            if (!CanNavigateInteractively())
            {
                MessageBox.Show(this, "Stop the current operation before opening Database Management.");
                return;
            }
            using var dialog = new DatabaseRelocationForm(_profile, _sql);
            dialog.ShowDialog(this);
        };
        fileMenu.DropDownItems.Add(databaseMenu);
        menu.Items.Add(fileMenu);
        var actionsMenu = new ToolStripMenuItem("&Actions");
        var cleanupMenu = new ToolStripMenuItem("Clean Previous Versions");
        cleanupMenu.Click += async (_, _) => { if (CanNavigateInteractively()) await CleanupPreviousVersionsAsync(); };
        actionsMenu.DropDownItems.Add(cleanupMenu);
        var deploymentCleanupMenu = new ToolStripMenuItem("Deployment Cleanup...");
        deploymentCleanupMenu.Click += async (_, _) => { if (CanNavigateInteractively()) await ShowDeploymentCleanupAsync(); };
        actionsMenu.DropDownItems.Add(deploymentCleanupMenu);
        menu.Items.Add(actionsMenu);
        actionsMenu.DropDownItems.Add(new ToolStripSeparator());
        var lockSourceMenu = new ToolStripMenuItem("Lock Source") { CheckOnClick = false };
        lockSourceMenu.Click += (_, _) => { if (!CanNavigateInteractively()) return; if (_pageIndex != 0) ShowPage(0); if (_lockSourceAction is { IsDisposed: false } control) { control.Checked = !control.Checked; lockSourceMenu.Checked = control.Checked; } };
        actionsMenu.DropDownItems.Add(lockSourceMenu);
        actionsMenu.DropDownOpening += (_, _) => lockSourceMenu.Checked = _sourceLocked;
        var exportSourceMenu = new ToolStripMenuItem("Export Clean Source ZIP");
        exportSourceMenu.Click += (_, _) => { if (!CanNavigateInteractively()) return; if (_pageIndex != 0) ShowPage(0); _exportSourceAction?.PerformClick(); };
        actionsMenu.DropDownItems.Add(exportSourceMenu);
        var diagnosticsMenu = new ToolStripMenuItem("Diagnostics Center");
        diagnosticsMenu.Click += (_, _) => OpenDiagnostics();
        actionsMenu.DropDownItems.Add(diagnosticsMenu);
        actionsMenu.DropDownItems.Add(new ToolStripSeparator());
        var emailNotificationsMenu = new ToolStripMenuItem("Email Notifications...");
        emailNotificationsMenu.Click += (_, _) =>
        {
            if (!CanNavigateInteractively())
            {
                MessageBox.Show(this, "Stop the current operation before editing Email Notifications.");
                return;
            }
            try
            {
                using var dialog = new EmailNotificationSettingsForm();
                dialog.ShowDialog(this);
            }
            catch (Exception)
            {
                MessageBox.Show(this, "Email notification settings could not be opened. Check the Windows user credential store.", "Email Notifications", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        };
        actionsMenu.DropDownItems.Add(emailNotificationsMenu);
        var viewMenu = new ToolStripMenuItem("&View");
        viewMenu.DropDownItems.Add(new ToolStripMenuItem("Live Log", null, (_, _) => OpenLiveLog()) { ShortcutKeys = Keys.Control | Keys.L });
        viewMenu.DropDownItems.Add(new ToolStripMenuItem("Diagnostics Center", null, (_, _) => OpenDiagnostics()) { ShortcutKeys = Keys.Control | Keys.Shift | Keys.D });
        viewMenu.DropDownItems.Add(new ToolStripMenuItem("Deployment History", null, (_, _) => OpenHistory()) { ShortcutKeys = Keys.Control | Keys.Shift | Keys.H });
        viewMenu.DropDownItems.Add(new ToolStripMenuItem("Notifications", null, (_, _) => OpenNotifications()) { ShortcutKeys = Keys.Control | Keys.Shift | Keys.N });
        menu.Items.Add(viewMenu);
        MainMenuStrip = menu;
        Controls.Add(menu);
        GwdUi.StyleTree(root);
        GwdUi.StyleTree(menu);
        GwdUi.ButtonStyle(_next, primary: true);
        UpdateDeploymentIndicator();


        _back.Click += (_, _) => { if (CanNavigateInteractively() && _pageIndex > 0) ShowPage(_pageIndex - 1); };
        _next.Click += async (_, _) => await NextAsync();
        _autoDeploy.Click += async (_, _) =>
        {
            if (_autoDeployRunning)
            {
                // Once automatic navigation enters DeployAsync, cancellation
                // belongs to the actual deployment token and confirmation.
                if (_operationRunning && _pageIndex == _steps.Length - 1)
                {
                    RequestDeploymentCancellation();
                    return;
                }
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

    private async Task UpdateRuntimeMetricsAsync()
    {
        if (_runtimeMetricsClosing || IsDisposed || Disposing || _runtimeMetricsPending is not null) return;
        var pending = Task.Run(_runtimeMetrics.Sample);
        _runtimeMetricsPending = pending;
        try
        {
            var sample = await pending;
            if (_runtimeMetricsClosing || IsDisposed || Disposing) return;
            if (!ReferenceEquals(sample, _runtimeMetrics.GetLatestSample()))
            {
                _resourceUsage.Text = "CPU/RAM sampling unavailable";
                _resourceUsage.ForeColor = Color.DimGray;
                return;
            }
            _resourceUsage.Text = sample.ToDisplayText();
            _resourceUsage.ForeColor = sample.WorkingSetBytes > 768L * 1024 * 1024 ? Color.Firebrick : (sample.WorkingSetBytes > 384L * 1024 * 1024 ? Color.DarkOrange : Color.DimGray);
        }
        catch (Exception ex) { InternalDiagnostics.Record(ex, "Runtime metrics worker"); }
        finally { if (ReferenceEquals(_runtimeMetricsPending, pending)) _runtimeMetricsPending = null; }
    }

    private void StopRuntimeMetrics()
    {
        if (_runtimeMetricsClosing) return;
        _runtimeMetricsClosing = true;
        _runtimeMetricsTimer.Stop();
        _runtimeMetricsTimer.Dispose();
        var pending = _runtimeMetricsPending;
        if (pending is null || pending.IsCompleted) _runtimeMetrics.Dispose();
        else _ = pending.ContinueWith(_ => _runtimeMetrics.Dispose(), TaskScheduler.Default);
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
        if (!CanNavigateInteractively())
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
        InvalidateNavigationSourceReadiness();
        CancelBackgroundVerification();
        DisposeContentControls();

        // Keep the selected project so Start Over returns to Project Source ready for confirmation/change.
        _profile.ReadyPublishFolder = string.Empty;
        _profile.BuildMode = "Fast Local Test";
        _sourceLocked = false;
        _profile.DependencyResolutionMode = NormalizeDependencyMode(_profile.DependencyResolutionMode);
        _profile.OfflineBuild = string.Equals(_profile.DependencyResolutionMode, "Offline Only", StringComparison.OrdinalIgnoreCase);
        SaveDependencyMode(_profile.DependencyResolutionMode);
        _profile.SqlInstance = string.Empty;
        _profile.HostingBundleInstallerPath = string.Empty;
        _profile.DotNetSdkInstallerPath = string.Empty;
        _profile.NodeInstallerPath = string.Empty;
        _profile.SqlInstallerPath = string.Empty;

        _offlineDependenciesReady = false;
        _offlineDependenciesDetail = "Not checked";
        _scanCompleted = false;
        _iisAutoVerified = _runtimesAutoVerified = _deploymentLocationAutoVerified = false;
        
        
        _iisState = new(false, "Not scanned");
        _hostingState = new(false, "Not scanned");
        _sdkState = new(false, "Not scanned");
        _nodeState = new(false, "Not scanned");
        _sqlInstances = new();
        _lastErrorReportPath = null;
        _lastSuccessfulPublishPath = null;
        // Start Over must not erase an unresolved prior deployment warning.
        _deploymentStatus = _persistentState.LastResult switch
        {
            "InProgress" => InterruptedDeploymentStatus,
            "RecoveryFailed" => "PREVIOUS DEPLOYMENT — RECOVERY FAILED",
            "Failed" => "PREVIOUS DEPLOYMENT UNSUCCESSFUL",
            "Cancelled" => "PREVIOUS DEPLOYMENT CANCELLED",
            "Successful" => "PREVIOUS DEPLOYMENT SUCCESSFUL",
            _ => "Not started"
        };
        _deploymentElapsedText = "Elapsed: 00:00:00";
        _activity.Text = "Ready";
        _progress.Value = 0;

        _uiLogLines.Clear();
        _uiLogChars = 0;
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
        // Allow normal GC scheduling; Start Over need not trigger a full-generation collection.
    }

    private void UpdateStepIndicator()
    {
        for (var i = 0; i < _stepLabels.Count; i++)
        {
            var button = _stepLabels[i];
            var current = i == _pageIndex;
            var checkedState = (i == 2 && _scanCompleted) || (i == 3 && _iisAutoVerified) ||
                (i == 4 && _runtimesAutoVerified) || (i == 5 && _sqlDatabaseAutoVerified) ||
                (i == 6 && _deploymentLocationAutoVerified);
            var state = current ? "Current" : checkedState ? "Checked" : i < _pageIndex ? "Earlier" : "Pending";
            var caption = $"{(current ? "▶ " : checkedState ? "• " : "")}{i + 1:00}  {_steps[i]}";
            // Never label an earlier step as passed solely because of position.
            if (button.Text != caption) button.Text = caption;
            button.AccessibleDescription = state + "; checks are not deployment authorization.";
            _healthTip.SetToolTip(button, state);
            if (!GwdUi.HighContrast)
            {
                button.FlatAppearance.BorderColor = current ? GwdUi.Accent : GwdUi.Border;
                button.BackColor = current ? GwdUi.AccentSoft : GwdUi.Surface;
                button.ForeColor = current ? GwdUi.Accent : checkedState ? GwdUi.Success : GwdUi.Ink;
            }
        }
    }

    private (bool Prepared, string Folder) NavigationSourceKey()
    {
        var prepared = string.Equals(_profile.BuildMode, "Prepared IIS Publish", StringComparison.OrdinalIgnoreCase);
        return (prepared, (prepared ? _profile.ReadyPublishFolder : _profile.SourceFolder).Trim());
    }

    private void CancelNavigationSourceProbe()
    {
        var request = _navigationProbeCts;
        _navigationProbeCts = null;
        request?.Cancel(); // The worker owns disposal, even when superseded.
    }

    private void InvalidateNavigationSourceReadiness()
    {
        CancelNavigationSourceProbe();
        _navigationSourceKey = null;
        _navigationSourceReady = false;
    }

    private void RememberNavigationSourceReadiness(bool prepared, string folder, bool ready)
    {
        var key = (Prepared: prepared, Folder: folder.Trim());
        if (key != NavigationSourceKey()) return;
        CancelNavigationSourceProbe();
        _navigationSourceKey = key;
        _navigationSourceReady = ready;
    }

    private bool EnsureNavigationSourceReadiness()
    {
        var key = NavigationSourceKey();
        if (_navigationSourceKey == key) return _navigationSourceReady;
        InvalidateNavigationSourceReadiness();
        if (string.IsNullOrWhiteSpace(key.Folder))
        {
            _navigationSourceKey = key;
            return false;
        }
        // During construction there is not necessarily a UI message pump yet.
        // Shown starts the probe if the source page has not already supplied a result.
        if (!IsHandleCreated || IsDisposed || Disposing) return false;
        _navigationSourceKey = key;
        var request = new CancellationTokenSource();
        _navigationProbeCts = request;
        _ = ProbeNavigationSourceAsync(key, request);
        return false;
    }

    private async Task ProbeNavigationSourceAsync((bool Prepared, string Folder) key, CancellationTokenSource request)
    {
        var ready = false;
        try
        {
            await Task.Delay(200, request.Token);
            ready = await Task.Run(() => key.Prepared
                ? DeploymentService.ValidatePreparedPublish(key.Folder).Ok
                : !string.IsNullOrWhiteSpace(_environment.DetectApiProject(key.Folder)), request.Token);
        }
        catch (OperationCanceledException) when (request.IsCancellationRequested) { }
        catch (Exception ex) { InternalDiagnostics.Record(ex, "Navigation source readiness"); }
        finally
        {
            // Compare both request identity and input key. A result from an old
            // source/mode cannot enable controls for a new selection.
            var current = ReferenceEquals(_navigationProbeCts, request);
            var cancelled = request.IsCancellationRequested;
            if (current) _navigationProbeCts = null;
            request.Dispose();
            if (current && !cancelled && !IsDisposed && !Disposing &&
                _navigationSourceKey == key && NavigationSourceKey() == key)
            {
                _navigationSourceReady = ready;
                UpdateNavigationState();
            }
        }
    }

    private void UpdateNavigationState()
    {
        if (IsDisposed || Disposing || _visualResourcesStopped) return;
        _back.Enabled = _pageIndex > 0;
        var navigationSourceReady = EnsureNavigationSourceReadiness();
        var localOnly = string.Equals(_profile.BuildMode, "Fast Local Test", StringComparison.OrdinalIgnoreCase);
        bool canNext = _pageIndex switch
        {
            0 => navigationSourceReady && !string.IsNullOrWhiteSpace(_profile.ApplicationName),
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
            var cancelling = _deploymentCts?.IsCancellationRequested == true;
            _next.Enabled = !cancelling && !_deploymentCancelConfirmationOpen;
            _next.Text = cancelling ? "Cancelling..." : "Cancel Deploy";
        }
        else
        {
            _next.Enabled = canNext && CanNavigateInteractively();
        }
        _back.Enabled = _pageIndex > 0 && CanNavigateInteractively();
        if (_autoDeployRunning)
            _autoDeploy.Enabled = _autoDeployCts?.IsCancellationRequested != true &&
                _deploymentCts?.IsCancellationRequested != true && !_deploymentCancelConfirmationOpen;
        else
            _autoDeploy.Enabled = CanNavigateInteractively() && CanAutoDeployStart();
        var canNavigate = CanNavigateInteractively();
        // The active operation owns the mutable profile. Freeze page inputs,
        // while keeping the shell's cancellation and log commands available.
        _content.Enabled = canNavigate;
        foreach (var button in _stepLabels) button.Enabled = canNavigate;
        foreach (var action in new Control?[] { _saveProfileAction, _loadProfileAction, _lockSourceAction, _exportSourceAction })
            if (action is { IsDisposed: false }) action.Enabled = canNavigate;
        UpdateDashboard();
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

        if (string.Equals(_profile.BuildMode, "Fast Local Test", StringComparison.OrdinalIgnoreCase) && string.IsNullOrWhiteSpace(_profile.WorkspaceRoot)) return false;
        return EnsureNavigationSourceReadiness();
    }

    private bool CanAutoDeployStart() => EnsureNavigationSourceReadiness();

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
        if (!CanNavigateInteractively()) return;
        CancelBackgroundVerification();

        _autoDeployCts?.Dispose();
        _autoDeployCts = new CancellationTokenSource();
        var ct = _autoDeployCts.Token;
        _autoDeployRunning = true;
        UpdateNavigationState();
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
                        if (string.Equals(_profile.BuildMode, "Prepared IIS Publish", StringComparison.OrdinalIgnoreCase))
                        {
                            var prepared = DeploymentService.ValidatePreparedPublish(_profile.ReadyPublishFolder);
                            if (!prepared.Ok) throw new InvalidOperationException(prepared.Detail);
                        }
                        else
                        {
                            if (!Directory.Exists(_profile.SourceFolder)) throw new InvalidOperationException("Select a valid source folder.");
                            _profile.ApiProjectPath = _environment.DetectApiProject(_profile.SourceFolder)
                                ?? throw new InvalidOperationException("No ASP.NET Core Web project was found in this source.");
                        }
                        if (string.IsNullOrWhiteSpace(_profile.ApplicationName)) throw new InvalidOperationException("Application name is required.");
                        break;

                    case 1:
                        ValidateTargetPage();
                        break;

                    case 2:
                        await ScanEnvironmentAsync(ct);
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
                        await _sql.TestConnectionWithSafeFallbackAsync(_profile, ct);
                        ct.ThrowIfCancellationRequested();
                        break;

                    case 6:
                        if (string.Equals(_profile.BuildMode, "Fast Local Test", StringComparison.OrdinalIgnoreCase)) { AppendLog("[AUTO][LOCAL] Deployment location step skipped."); break; }
                        ValidateDeploymentLocationPage();
                        _deploymentLocationAutoVerified = true;
                        break;

                    case 7:
                        // Direct navigation to Review is not validation evidence.
                        ValidateTargetPage();
                        ct.ThrowIfCancellationRequested();
                        if (!string.Equals(_profile.BuildMode, "Fast Local Test", StringComparison.OrdinalIgnoreCase)) ValidateIisSitePage();
                        AppendLog("[AUTO] Starting deployment after target validation; engine safety checks remain authoritative.");
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
            if (_pageIndex == _steps.Length - 1) RequestDeploymentCancellation();
            return;
        }
        if (!CanNavigateInteractively()) return;
        _navigationInProgress = true;
        UpdateNavigationState();
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
                    if (string.Equals(_profile.BuildMode, "Fast Local Test", StringComparison.OrdinalIgnoreCase)) break;
                    _iisState = await _environment.CheckIisAsync();
                    if (!_iisState.Installed) throw new InvalidOperationException("IIS must be installed before continuing.");
                    break;
                case 4:
                    await RefreshRuntimeStatesAsync();
                    var needsSdk = _profile.BuildMode != "Prepared IIS Publish";
                    var needsNode = needsSdk && _profile.RequiresNode;
                    var localOnly = string.Equals(_profile.BuildMode, "Fast Local Test", StringComparison.OrdinalIgnoreCase);
                    if ((!localOnly && !_hostingState.Installed) || (needsSdk && !_sdkState.Installed) || (needsNode && !_nodeState.Installed))
                        throw new InvalidOperationException("Install all prerequisites required by the selected build mode before continuing.");
                    if (!localOnly && _sqlInstances.Count == 0 && !File.Exists(_profile.SqlInstallerPath))
                        throw new InvalidOperationException("No SQL Server engine is installed. Download or choose a SQL Server Express installer before continuing.");
                    break;
                case 5:
                    if (string.Equals(_profile.BuildMode, "Fast Local Test", StringComparison.OrdinalIgnoreCase)) break;
                    if (string.IsNullOrWhiteSpace(_profile.SqlInstance)) throw new InvalidOperationException("Select or install a SQL Server instance.");
                    if (string.IsNullOrWhiteSpace(_profile.DatabaseName)) throw new InvalidOperationException("Database name is required.");
                    if (!_sqlAdminConnectionVerified || !_databaseConnectionVerified) throw new InvalidOperationException("Verify SQL Server and database connectivity before continuing.");
                    await _sql.TestConnectionWithSafeFallbackAsync(_profile);
                    break;
                case 6:
                    if (string.Equals(_profile.BuildMode, "Fast Local Test", StringComparison.OrdinalIgnoreCase)) break;
                    ValidateDeploymentLocationPage();
                    break;
                case 7:
                    ValidateTargetPage();
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
            if (_pageIndex <= 1) InvalidateNavigationSourceReadiness();
            MessageBox.Show(this, SecretRedactor.Redact(ex.Message), "Cannot continue", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            AppendLog("ERROR: " + GenericWeb.DeploymentManager.Services.SecretRedactor.Redact(ex.Message));
        }
        finally
        {
            _navigationInProgress = false;
            if (!IsDisposed && !Disposing) UpdateNavigationState();
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

    private void ValidateTargetPage()
    {
        if (string.IsNullOrWhiteSpace(_profile.ApplicationName)) throw new InvalidOperationException("Application name is required.");
        if (string.IsNullOrWhiteSpace(_profile.SiteName)) throw new InvalidOperationException("IIS site name is required.");
        if (string.IsNullOrWhiteSpace(_profile.DatabaseName)) throw new InvalidOperationException("Database name is required.");
        _profile.AppPoolName = _profile.SiteName;
        SaveIdentitySettings(defer: false);
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

    private void InvalidateEnvironmentEvidence()
    {
        CancelBackgroundVerification();
        _scanCompleted = false;
        _iisAutoVerified = _runtimesAutoVerified = _deploymentLocationAutoVerified = false;
        UpdateStepIndicator();
    }

    private void CancelBackgroundVerification()
    {
        var request = _backgroundVerifyCts;
        _backgroundVerifyCts = null;
        request?.Cancel(); // The initiating event owns disposal.
    }

    private async Task RunBackgroundAutoVerificationAsync(CancellationToken ct)
    {
        if (ct.IsCancellationRequested || !CanNavigateInteractively() || _pageIndex != 0) return;
        try
        {
            AppendLog("[AUTO-VERIFY] Background verification started after source selection.");
            await ScanEnvironmentAsync(ct); ct.ThrowIfCancellationRequested();
            if (_sqlInstances.Count > 0 && string.IsNullOrWhiteSpace(_profile.SqlInstance)) _profile.SqlInstance = _sqlInstances[0].ServerName;
            if (!string.IsNullOrWhiteSpace(_profile.SqlInstance) && !string.IsNullOrWhiteSpace(_profile.DatabaseName))
            {
                try { await _sql.TestConnectionWithSafeFallbackAsync(_profile, ct); ct.ThrowIfCancellationRequested(); _healthMonitor.ReportSqlSuccess("Background SQL/database verification passed"); }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) {  AppendLog("[AUTO-VERIFY] SQL/database needs attention: " + GenericWeb.DeploymentManager.Services.SecretRedactor.Redact(ex.Message)); }
            }
            ct.ThrowIfCancellationRequested();
            try { ValidateDeploymentLocationPage(); _deploymentLocationAutoVerified = true; }
            catch { _deploymentLocationAutoVerified = false; }
            if (!IsDisposed && !Disposing && !ct.IsCancellationRequested) { UpdateStepIndicator(); UpdateNavigationState(); }
            AppendLog("[AUTO-VERIFY] Background verification completed.");
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { AppendLog("[AUTO-VERIFY] Background verification stopped: " + GenericWeb.DeploymentManager.Services.SecretRedactor.Redact(ex.Message)); }
    }

    private void BuildScanPage()
    {
        _pageTitle.Text = "Environment scan";
        _pageDescription.Text = "Inspect the machine before changing anything. The results are shown as separate, readable status panels.";
        var scan = new EnvironmentScanView(GetScanCards());
        _scanView = scan;
        _content.Controls.Add(scan);
        scan.Activate(button => BusyAsync(button, ScanEnvironmentAsyncWithoutToken));

        // Auto Deploy owns its explicit scan; never schedule a duplicate page scan.
        if (!_autoDeployRunning)
            BeginInvoke(new Action(() =>
            {
                if (ReferenceEquals(scan, _scanView) && !scan.IsDisposed && _pageIndex == 2 && CanNavigateInteractively())
                    scan.RequestScan();
            }));
    }

    private ScanCardState[] GetScanCards() =>
    [
        new("Windows", _environment.GetWindowsDescription(), true),
        new("IIS Web Server", _iisState.Detail, _iisState.Installed),
        new($"ASP.NET Core Hosting Bundle (.NET {_profile.TargetFrameworkMajor})", _hostingState.Detail, _hostingState.Installed),
        new($".NET {_profile.TargetFrameworkMajor} SDK", _sdkState.Detail, _sdkState.Installed),
        new("Node.js / npm", _nodeState.Detail, _nodeState.Installed),
        new("SQL Server Engine", _sqlInstances.Count > 0 ? string.Join("\r\n", _sqlInstances) :
            _scanCompleted ? "No SQL Server Database Engine instance detected." : "Not scanned", _sqlInstances.Count > 0)
    ];

    private Task ScanEnvironmentAsyncWithoutToken() => ScanEnvironmentAsync();

    private async Task ScanEnvironmentAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        _scanCompleted = false;
        _iisAutoVerified = _runtimesAutoVerified = _deploymentLocationAutoVerified = false;
        UpdateStepIndicator();
        ValidateTargetPage();
        var mode = _profile.BuildMode;
        var source = _profile.SourceFolder;
        var prepared = _profile.ReadyPublishFolder;
        var targetMode = _profile.TargetMode;
        var targetHost = _profile.TargetHost;
        var major = _profile.TargetFrameworkMajor;
        var requiresNode = _profile.RequiresNode;
        var readyPublish = mode == "Prepared IIS Publish";
        // Gather a full snapshot before publication; a cancelled/stale scan cannot
        // publish partially updated global flags.
        var iisState = await _environment.CheckIisAsync();
        ct.ThrowIfCancellationRequested();
        var hostingState = await _environment.CheckHostingBundleAsync(major, ct);
        ct.ThrowIfCancellationRequested();
        var sdkState = readyPublish ? new PrerequisiteState(true, "Not required — build is skipped because a ready publish folder was selected.") : await _environment.CheckDotNetSdkAsync(major);
        ct.ThrowIfCancellationRequested();
        var nodeState = readyPublish ? new PrerequisiteState(true, "Not required — build is skipped because a ready publish folder was selected.") : await _environment.CheckNodeAsync(requiresNode);
        ct.ThrowIfCancellationRequested();
        var sqlInstances = await _sql.DetectInstancesAsync(ct);
        ct.ThrowIfCancellationRequested();
        if (IsDisposed || Disposing || mode != _profile.BuildMode || source != _profile.SourceFolder ||
            prepared != _profile.ReadyPublishFolder || targetMode != _profile.TargetMode || targetHost != _profile.TargetHost ||
            major != _profile.TargetFrameworkMajor || requiresNode != _profile.RequiresNode)
            throw new OperationCanceledException("Environment selection changed during scan.");

        _iisState = iisState; _hostingState = hostingState; _sdkState = sdkState; _nodeState = nodeState; _sqlInstances = sqlInstances;
        // V67: mark only stages whose actual acceptance criteria are proven.
        _iisAutoVerified = _iisState.Installed;
        var needsSdk = !readyPublish;
        var needsNode = needsSdk && requiresNode;
        _runtimesAutoVerified = _hostingState.Installed && (!needsSdk || _sdkState.Installed) && (!needsNode || _nodeState.Installed);
        _scanCompleted = true;
        AutoLocateCachedInstallers();
        UpdateStepIndicator();
        if (_pageIndex == 2) _scanView?.RefreshCards(GetScanCards());
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
        SaveHostingSettings(defer: false);
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
        var request = _deploymentCts;
        if (!_operationRunning || request is null || request.IsCancellationRequested || _deploymentCancelConfirmationOpen) return;
        _deploymentCancelConfirmationOpen = true;
        try
        {
            var answer = MessageBox.Show(this,
                "Cancel the current deployment? Running build processes will be stopped and Deployment Manager will clean staging/failed files and restore the previous hosted site where possible.",
                "Cancel deployment", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2);
            // WinForms modal loops pump async continuations. The original
            // deployment may already have finished while the prompt was open.
            if (answer != DialogResult.Yes || !_operationRunning || !ReferenceEquals(request, _deploymentCts)) return;
            if (request.IsCancellationRequested || IsDisposed || Disposing) return;
            request.Cancel();
            AppendLog("[CANCEL] Operator requested deployment cancellation. Stopping active process tree and preparing cleanup...");
        }
        finally
        {
            _deploymentCancelConfirmationOpen = false;
            if (!IsDisposed && !Disposing) UpdateNavigationState();
        }
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
            using var protectionDialog = new AutoYesConfirmationForm("Deployment protection policy", protection, 3);
            if (protectionDialog.ShowDialog(this) != DialogResult.Yes) return;
        }
        using var request = new CancellationTokenSource();
        var ct = request.Token;
        var deploymentStopwatch = System.Diagnostics.Stopwatch.StartNew();
        using var elapsedTimer = new System.Windows.Forms.Timer { Interval = 500 };
        var codeQuality = new CodeQualitySession();
        DeploymentHistoryRecord? historyAttempt = null;
        string? stagedPublish = null;
        string? publish = null;
        string? previousSitePath = null;
        IDisposable? iisAttempt = null;
        var engineEntered = false;
        _deploymentCts = request;
        _operationRunning = true;
        try
        {
            UpdateNavigationState();
            AcknowledgeTrayAlert();
            _deploymentElapsedText = "Elapsed: 00:00:00";
            _reviewView?.ShowElapsed(_deploymentElapsedText);
            _deployment.ResetPerformanceSnapshot();
            try
            {
                historyAttempt = _history.BeginAttempt(_profile, _logService.LogFile);
                AppendLog($"[HISTORY] Deployment attempt recorded. Project ID: {historyAttempt.ProjectId}; Attempt ID: {historyAttempt.AttemptId}; Version: {(string.IsNullOrWhiteSpace(historyAttempt.ApplicationVersion) ? "unknown" : historyAttempt.ApplicationVersion)}");
            }
            catch (Exception historyEx)
            {
                AppendLog("[HISTORY] Warning: could not start persistent deployment-history record: " + historyEx.Message);
            }
            await Task.Run(() => codeQuality.Start(_appData, _profile, historyAttempt?.ProjectId ?? "unknown", historyAttempt?.AttemptId ?? Guid.NewGuid().ToString("N")), ct);
            ct.ThrowIfCancellationRequested();
            _runner.CodeQuality = codeQuality;
            elapsedTimer.Tick += (_, _) =>
            {
                _deploymentElapsedText = $"Elapsed: {deploymentStopwatch.Elapsed:hh\\:mm\\:ss}";
                _reviewView?.ShowElapsed(_deploymentElapsedText);
            };
            elapsedTimer.Start();
            _deploymentStatus = "DEPLOYMENT IN PROGRESS";
            _persistentState.LastResult = "InProgress";
            UpdateDeploymentIndicator();
            _appData.SaveState(_persistentState);
            ct.ThrowIfCancellationRequested();
            engineEntered = true;
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
            await NotifyDeploymentOutcomeAsync(isFastLocalTest, historyAttempt?.ApplicationVersion, historyAttempt?.AttemptId, deploymentStopwatch.Elapsed, isFastLocalTest ? null : successUrl);
            BeginInvoke(() => MessageBox.Show(this, successMessage, successTitle, MessageBoxButtons.OK, MessageBoxIcon.Information));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            _deploymentStatus = "DEPLOYMENT CANCELLED";
            _persistentState.LastResult = "Cancelled";
            if (!engineEntered)
            {
                AppendLog("[CANCEL] Deployment cancelled during preparation; the deployment engine was not started.");
                return;
            }
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
            deploymentStopwatch.Stop();
            elapsedTimer.Stop();
            await NotifyDeploymentOutcomeAsync(isFastLocalTest, historyAttempt?.ApplicationVersion, historyAttempt?.AttemptId, deploymentStopwatch.Elapsed, null);
            MessageBox.Show(this,
                cleanupOk ? "Deployment cancelled. Recovery handler completed; inspect the log for separate cleanup warnings. Restoration was not independently verified." : "Deployment cancelled and cleanup/recovery FAILED. Previous deployment state is uncertain. Review the log before retrying.",
                "Deployment cancelled", MessageBoxButtons.OK, cleanupOk ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
        }
        catch (Exception ex)
        {
            _deploymentStatus = "DEPLOYMENT UNSUCCESSFUL";
            _persistentState.LastResult = "Failed";
            AppendLog("DEPLOYMENT UNSUCCESSFUL: " + ex);
            if (!engineEntered)
            {
                MessageBox.Show(this, "Deployment preparation failed; the deployment engine was not started.\r\n\r\n" +
                    SecretRedactor.Redact(ex.Message), "Deployment preparation failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }
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
            deploymentStopwatch.Stop();
            elapsedTimer.Stop();
            await NotifyDeploymentOutcomeAsync(isFastLocalTest, historyAttempt?.ApplicationVersion, historyAttempt?.AttemptId, deploymentStopwatch.Elapsed, null);
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
            try
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
                _reviewView?.ShowElapsed(_deploymentElapsedText, ReviewElapsedColor());
                AppendLog($"Deployment elapsed time: {deploymentStopwatch.Elapsed:hh\\:mm\\:ss}");
                _persistentState.UpdatedAt = DateTime.Now;
                try { _appData.SaveState(_persistentState); }
                catch (Exception stateError)
                {
                    InternalDiagnostics.Record(stateError, "Persist final deployment state");
                    NotifyUi(UiNoticeKind.Warning, "Deployment result could not be written to the local state file. See the log and attempt history.");
                }
            }
            finally
            {
                _deploymentCts = null;
                _operationRunning = false;
                if (!IsDisposed && !Disposing)
                {
                    try
                    {
                        UpdateDeploymentIndicator();
                        if (_pageIndex == _steps.Length - 1)
                        {
                            _next.Text = _persistentState.LastResult switch
                            {
                                "Failed" or "RecoveryFailed" => "Retry Deploy",
                                "Successful" => "Deploy Again",
                                _ => "Deploy"
                            };
                            // BuildReviewPage uses the operation flag when setting
                            // local Enabled values, not just the parent panel.
                            BuildReviewPageRefresh();
                        }
                    }
                    finally { UpdateNavigationState(); }
                }
            }
        }
    }


    private async Task NotifyDeploymentOutcomeAsync(bool fastLocalTest, string? version, string? attemptId, TimeSpan elapsed, string? url)
    {
        // A notification is best effort. It must never trigger deployment rollback
        // or replace the real deployment result, including recovery failures.
        try
        {
            var result = _persistentState.LastResult;
            var settings = EmailNotificationService.Load();
            if (!settings.ShouldNotify(result)) return;
            var operation = fastLocalTest ? "Fast Local Test (no IIS deployment)" : "Deployment";
            var project = EmailNotificationService.SingleLine(_profile.ApplicationName);
            var release = EmailNotificationService.SingleLine(version ?? "Unknown");
            var subject = $"[GWD] {project} {release} - {operation}: {result}";
            var body = $"Project: {project}\r\nVersion: {release}\r\nOperation: {operation}\r\nResult: {result}\r\nAttempt: {EmailNotificationService.SingleLine(attemptId ?? "Unknown")}\r\nFinished: {DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}\r\nElapsed: {elapsed:c}\r\n";
            // Do not email raw logs, exception messages, credentials, or database details.
            if (!string.IsNullOrWhiteSpace(url)) body += $"Site: {EmailNotificationService.SingleLine(url)}\r\n";
            if (result == "RecoveryFailed") body += "Recovery failed. Previous deployment state is uncertain. Review GWD on the deployment computer.\r\n";
            else if (result != "Successful") body += "Review GWD locally for failure/cancellation and recovery details.\r\n";
            await EmailNotificationService.SendAsync(settings, subject, body);
            AppendLog("[EMAIL] Deployment result notification accepted by SMTP server.");
        }
        catch (Exception)
        {
            // No SMTP exception payload or credential material enters diagnostics.
            try { AppendLog("[EMAIL] Result notification failed. Check Email Notifications settings and Test Email. Deployment result is unchanged."); }
            catch { /* Never fail deployment due to notification or notification logging. */ }
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
        _content.SuspendLayout();
        try
        {
            // Detach the persistent log, then dispose the previous view and its handlers.
            DisposeContentControls();
            BuildReviewPage();
            _renderedPageIndex = _pageIndex;
            GwdUi.StyleTree(_content);
            ApplyInteractiveCursors(_content);
        }
        finally { _content.ResumeLayout(true); }
    }

    private async Task BusyAsync(Control control, Func<Task> action)
    {
        if (!CanNavigateInteractively() || control.IsDisposed) return;
        _pageActionRunning = true;
        control.Enabled = false;
        UseWaitCursor = true;
        UpdateNavigationState();
        try { await action(); }
        catch (Exception ex)
        {
            AppendLog("ERROR: " + ex);
            if (!IsDisposed && !Disposing)
            {
                NotifyUi(UiNoticeKind.Error, SecretRedactor.Redact(ex.Message));
                MessageBox.Show(this, SecretRedactor.Redact(ex.Message), "Operation failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }
        finally
        {
            // A page operation must not reset the deployment-owned flag.
            _pageActionRunning = false;
            if (!control.IsDisposed && !control.Disposing) control.Enabled = true;
            if (!IsDisposed && !Disposing) { UseWaitCursor = false; UpdateNavigationState(); }
        }
    }

    private static FlowLayoutPanel VerticalPanel() => new() { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = true };

    private string BrowseStartFolder(string? preferred, BrowseContext context = BrowseContext.General)
    {
        var resolved = AppDataService.ResolveExistingFolder(preferred);
        if (!string.IsNullOrWhiteSpace(resolved)) return resolved;
        return _appData.GetLastBrowseFolder(context);
    }

    private void RememberBrowseSelection(string path, BrowseContext context = BrowseContext.General) =>
        PostSettingsSave("browse:" + context, () => _appData.SaveLastBrowseFolder(path, context, throwOnFailure: true));

    private static GroupBox InfoBox(string title, string text)
    {
        var box = new GroupBox { Text = title, Width = 850, Height = 165, Padding = new Padding(14) };
        box.Controls.Add(new Label { Text = text, Dock = DockStyle.Fill, AutoSize = false });
        return box;
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
                // Clip only the display copy; the complete line is already persisted.
                var display = clean.Length > MaxUiLineChars
                    ? clean[..MaxUiLineChars] + " [... UI preview clipped; full line on disk ...]" : clean;
                _pendingUiLogLines.Enqueue((at, display));
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
            var chars = 0;
            while (pending.Count < MaxUiLogLinesPerFlush && _pendingUiLogLines.Count > 0 &&
                   (pending.Count == 0 || chars + _pendingUiLogLines.Peek().Line.Length <= MaxUiBatchChars))
            {
                var item = _pendingUiLogLines.Dequeue();
                pending.Add(item);
                chars += item.Line.Length;
            }
        }
        if (pending.Count == 0 && dropped == 0) return;

        var batch = new StringBuilder();
        string? latestActivity = null;
        void AddToUi(DateTime at, string text)
        {
            var display = NormalizeLogForDisplay(text);
            _uiLogLines.Add((at, display));
            _uiLogChars += display.Length;
            if (!ShouldShowLogLine(display)) return;
            batch.Append('[').Append(at.ToString("HH:mm:ss")).Append("] ").Append(display).Append("\r\n");
            if (!string.IsNullOrWhiteSpace(display))
                latestActivity = display.Replace('\r', ' ').Replace('\n', ' ');
        }
        if (dropped > 0)
            AddToUi(DateTime.Now,
                $"[DEPLOYMENT LOG] {dropped} older UI-only lines skipped under load; complete session log remains on disk.");
        foreach (var (at, line) in pending) AddToUi(at, line);
        var remove = 0;
        while (remove < _uiLogLines.Count && (_uiLogLines.Count - remove > 5_000 || _uiLogChars > MaxUiHistoryChars))
        {
            _uiLogChars -= _uiLogLines[remove].Line.Length;
            remove++;
        }
        if (remove > 0) _uiLogLines.RemoveRange(0, remove);
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
            _reviewView?.ShowActivity(latestActivity);
        }
        // Persist the wizard's latest state no more than once every 5 seconds.
        if ((DateTime.UtcNow - _lastLogStateSaveUtc).TotalSeconds >= 5)
        {
            _lastLogStateSaveUtc = DateTime.UtcNow;
            _persistentState.LastStage = _steps[Math.Clamp(_pageIndex, 0, _steps.Length - 1)];
            _persistentState.LastSourceFolder = _profile.SourceFolder;
            _persistentState.LastSiteName = _profile.SiteName;
            _persistentState.UpdatedAt = DateTime.Now;
            try { _appData.SaveState(_persistentState); }
            catch (Exception ex) { InternalDiagnostics.Record(ex, "Persist state from UI log timer"); }
        }
    }

    private void RenderVisibleLog()
    {
        if (InvokeRequired) { BeginInvoke(RenderVisibleLog); return; }
        FlushPendingUiLogLines();
        var lines = new Stack<string>();
        var chars = 0;
        for (var i = _uiLogLines.Count - 1; i >= 0; i--)
        {
            var item = _uiLogLines[i];
            if (!ShouldShowLogLine(item.Line)) continue;
            var line = $"[{item.At:HH:mm:ss}] {item.Line}\r\n";
            if (chars + line.Length > 300_000) break;
            lines.Push(line);
            chars += line.Length;
        }
        _log.Text = string.Concat(lines);
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
        var warning = !CanNavigateInteractively()
            ? "A deployment/install operation is currently running. Closing now may interrupt it.\r\n\r\nDo you want to exit?"
            : "Do you want to close Deployment Manager?";
        if (MessageBox.Show(this, warning, "Confirm exit", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.Yes)
        { e.Cancel = true; return; }
        if (!DrainSettingsSaves()) { e.Cancel = true; return; }
        if (_settingsWriter.FailureCount > 0 && MessageBox.Show(this,
            "Some settings could not be saved. Check Diagnostics. Close without those changes?",
            "Settings not fully saved", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.Yes)
        { e.Cancel = true; return; }
        CancelNavigationSourceProbe();
        CancelBackgroundVerification();
        _acceptUiLogLines = false;
        _uiLogFlushTimer.Stop();
        _uiLogFlushTimer.Dispose();
        _trayBlinkTimer.Stop();
        _trayBlinkTimer.Dispose();
        _trayIcon.Visible = false;
        _trayIcon.Dispose();
        _traySuccessIcon.Dispose();
        _trayFailureIcon.Dispose();
        StopRuntimeMetrics();
        _healthMonitor.Updated -= OnHealthUpdated;
        _healthMonitor.Dispose();
        StopVisualResources();
        _healthTip.Dispose();
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

    private void ExportCleanSourceWithProgress(string sourceRoot, string zipPath, IReadOnlyCollection<string> exclusions)
    {
        // Existing exclusive export workflow; compression runs off the UI thread.
        var excluded = exclusions.ToArray();
        Exception? failure = null;
        var running = true;
        using var progress = new Form
        {
            Text = "Export clean source", Width = 480, Height = 132,
            FormBorderStyle = FormBorderStyle.FixedDialog, StartPosition = FormStartPosition.CenterParent,
            MaximizeBox = false, MinimizeBox = false, ControlBox = false, ShowInTaskbar = false
        };
        var message = new Label { Text = "Creating source archive...", Dock = DockStyle.Top, Height = 42, TextAlign = ContentAlignment.MiddleCenter };
        var bar = new ProgressBar { Dock = DockStyle.Bottom, Height = 20, Style = ProgressBarStyle.Marquee };
        progress.Controls.Add(message);
        progress.Controls.Add(bar);
        progress.FormClosing += (_, e) => { if (running) e.Cancel = true; };
        progress.Shown += async (_, _) =>
        {
            try { await Task.Run(() => ExportCleanSourceZip(sourceRoot, zipPath, excluded)); }
            catch (Exception ex) { failure = ex; }
            finally { running = false; progress.Close(); }
        };
        progress.ShowDialog(this);
        if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
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
        // Validate included paths during compression; never replace an existing archive until compression finishes.
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

        var temporary = Path.Combine(Path.GetDirectoryName(destination)!, "." + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            using (var archive = ZipFile.Open(temporary, ZipArchiveMode.Create))
                Visit(root, (file, relative) => archive.CreateEntryFromFile(file, relative, CompressionLevel.Optimal));
            // Same-directory promotion only after a complete archive has closed.
            File.Move(temporary, destination, overwrite: true);
        }
        finally
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); }
            catch (Exception ex) { InternalDiagnostics.Record(ex, "Clean source temporary ZIP cleanup"); }
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

