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
            if (list.IsDisposed) return;
            var oldScroll = list.AutoScrollPosition;
            list.SuspendLayout();
            try
            {
            var previous = list.Controls.Cast<Control>().ToArray();
            list.Controls.Clear();
            foreach (var card in previous) card.Dispose();
            var hostingPackage = _prereq.HostingBundle(_profile.TargetFrameworkMajor);
            var sdkPackage = _prereq.DotNetSdk(_profile.TargetFrameworkMajor);
            var nodePackage = _prereq.Node();
            var sqlPackage = _prereq.Sql(_profile.SqlExpressVersion);

            list.Controls.Add(InstallerCard(hostingPackage, () => _profile.HostingBundleInstallerPath, x => _profile.HostingBundleInstallerPath = x,
                _hostingState, required: true, installAction: async path =>
                {
                    if (!(await _environment.CheckIisAsync()).Installed) throw new InvalidOperationException("IIS must be installed first.");
                    await _prereq.InstallHostingBundleAsync(path);
                    _hostingState = await _environment.CheckHostingBundleAsync(_profile.TargetFrameworkMajor);
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
            }
            finally
            {
                list.ResumeLayout(true);
                list.AutoScrollPosition = new Point(-oldScroll.X, -oldScroll.Y);
            }
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
                if (_pageIndex == 4) RebuildCurrentPage();
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
        _hostingState = await _environment.CheckHostingBundleAsync(_profile.TargetFrameworkMajor);
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

        var sqlDiagnosticGeneration = 0;
        async Task RefreshDiagnosticsAsync()
        {
            var generation = ++sqlDiagnosticGeneration;
            var fingerprint = _sql.Connections.Fingerprint(_profile);
            if (string.IsNullOrWhiteSpace(_profile.SqlInstance))
            {
                diagnostics.Text = "Select an existing SQL Server instance first.";
                return;
            }
            diagnostics.Text = "Checking SQL Server...";
            try
            {
                var state = await _sql.DiagnoseAsync(_profile);
                if (!diagnostics.IsDisposed && generation == sqlDiagnosticGeneration &&
                    fingerprint == _sql.Connections.Fingerprint(_profile))
                    diagnostics.Text = state.ToDisplayText();
            }
            catch (OperationCanceledException) when (fingerprint != _sql.Connections.Fingerprint(_profile))
            {
                if (!diagnostics.IsDisposed && generation == sqlDiagnosticGeneration)
                    diagnostics.Text = "SQL configuration changed. Refresh diagnostics for the current configuration.";
            }
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
            if (body.IsDisposed || _pageIndex != 5 || _autoDeployRunning || string.IsNullOrWhiteSpace(_profile.SqlInstance) || _sqlAdminConnectionVerified) return;
            try
            {
                verified.Text = "Testing automatically...";
                verified.ForeColor = Color.DarkOrange;
                _profile.UseWindowsAuthentication = windowsAuth.Checked;
                _profile.SqlUserName = user.Text.Trim();
                _profile.SqlPassword = password.Text;
                await _sql.TestConnectionWithSafeFallbackAsync(_profile);
                if (body.IsDisposed || _pageIndex != 5) return;
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
                
                if (body.IsDisposed || _pageIndex != 5) return;
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

}
