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

public sealed partial class WizardForm
{
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

    private double _reviewSplitRatio = 0.42;

    private void BuildReviewPage()
    {
        _pageTitle.Text = "Review and deploy";
        _pageDescription.Text = "Review the plan. Depending on Build mode, Deploy either builds the source or uses prepared publish files, then prepares the database, configures IIS, and performs the health check.";
        var reviewRoot = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 4 };
        reviewRoot.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        reviewRoot.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        reviewRoot.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
        reviewRoot.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        var deployModePanel = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, FlowDirection = FlowDirection.LeftToRight, WrapContents = true, Padding = new Padding(0, 0, 0, 8) };
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
            InvalidateEnvironmentEvidence();
            deployModeHelp.Text = BuildModeHelp(deployMode.SelectedIndex);
            UpdateDashboard();
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
        // Initialize the splitter once; subsequent resizes retain the user's ratio.
        var initialized = false;
        void InitializeSplit()
        {
            if (initialized || split.IsDisposed || split.Height <= split.Panel1MinSize + split.Panel2MinSize + split.SplitterWidth) return;
            initialized = true;
            split.SplitterDistance = Math.Clamp((int)((split.Height - split.SplitterWidth) * _reviewSplitRatio),
                split.Panel1MinSize, split.Height - split.SplitterWidth - split.Panel2MinSize);
        }
        split.Resize += (_, _) => InitializeSplit();
        split.HandleCreated += (_, _) => BeginInvoke(() => InitializeSplit());
        split.SplitterMoved += (_, _) =>
        {
            if (initialized && split.Height > split.SplitterWidth)
                _reviewSplitRatio = Math.Clamp(split.SplitterDistance / (double)(split.Height - split.SplitterWidth), 0.1, 0.9);
        };
        var summary = new TextBox { Multiline = true, ReadOnly = true, Dock = DockStyle.Fill, ScrollBars = ScrollBars.Both, WordWrap = false, Font = new Font("Consolas", 9.5F) };
        void RefreshSummary() => summary.Text = $"Application: {_profile.ApplicationName}\r\nBuild mode (selected on Project Source): {_profile.BuildMode}\r\nHosted website action: {(_profile.BuildMode == "Fast Local Test" ? "UNCHANGED — compile only" : "UPDATE after successful build")}\r\nDependency mode: {_profile.DependencyResolutionMode}\r\nOffline build: {_profile.OfflineBuild}\r\nTarget mode: {_profile.TargetMode}\r\nTarget: {_profile.TargetHost}\r\nDeployment Repository: {_profile.DeploymentRepositoryFolder}\r\nStable Build Workspace: {_profile.WorkspaceRoot}\r\nIncoming Source: {_profile.SourceFolder}\r\nReady publish folder: {_profile.ReadyPublishFolder}\r\nAPI project: {_profile.ApiProjectPath}\r\nTarget framework: .NET {_profile.TargetFrameworkMajor}\r\nNode required: {_profile.RequiresNode}\r\nSQL: {_profile.SqlInstance}\r\nDatabase: {_profile.DatabaseName}\r\nHosted site folder: {_profile.HostingRoot}\r\nDeployment layout: {_profile.DeploymentLayout}\r\nDefault IIS website: {_profile.UseDefaultIisWebsite}\r\nPersistent data root: {_profile.DeploymentRoot}\r\nIIS site: {_profile.SiteName}\r\nApp pool: {_profile.AppPoolName}\r\nPort: {_profile.HttpPort}\r\nHealth: {_profile.HealthPath}";
        RefreshSummary();
        deployMode.SelectedIndexChanged += (_, _) => RefreshSummary();
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

}
