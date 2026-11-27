using GenericWeb.DeploymentManager.Services;

namespace GenericWeb.DeploymentManager;

public sealed partial class WizardForm
{
    private Form? _dashboardWindow;

    private string? DashboardProjectId()
    {
        if (string.IsNullOrWhiteSpace(_profile.SourceFolder) && string.IsNullOrWhiteSpace(_profile.ReadyPublishFolder)) return null;
        if (string.IsNullOrWhiteSpace(_profile.ApplicationName) && string.IsNullOrWhiteSpace(_profile.SiteName) &&
            string.IsNullOrWhiteSpace(_profile.DatabaseName) && string.IsNullOrWhiteSpace(_profile.SourceFolder)) return null;
        return _history.GetProjectId(_profile);
    }

    private (string? Project, string Mode, string Host) DashboardScope() =>
        (DashboardProjectId(), _profile.TargetMode, _profile.TargetHost);

    private void OpenDashboardEvidence()
    {
        if (IsDisposed || Disposing || _visualResourcesStopped) return;
        if (_dashboardWindow is { IsDisposed: false }) { _dashboardWindow.Activate(); return; }
        var window = new Form { Text = "Deployment status and evidence", Width = 900, Height = 620, StartPosition = FormStartPosition.CenterParent };
        GwdUi.ConfigureForm(window);
        var table = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, Padding = new Padding(GwdUi.MediumSpace) };
        table.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        table.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        var toolbar = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, WrapContents = true };
        var refresh = new Button { Text = "Refresh evidence", AutoSize = true };
        var checkHealth = new Button { Text = "Check configured health", AutoSize = true,
            AccessibleDescription = "Read-only: local IIS and HTTP plus the configured administrative SQL connection." };
        var diagnostics = new Button { Text = "Diagnostics", AutoSize = true };
        var box = new TextBox { Dock = DockStyle.Fill, Font = GwdUi.MonoReading, Multiline = true,
            ReadOnly = true, ScrollBars = ScrollBars.Both, WordWrap = false, AccessibleName = "Deployment evidence" };
        toolbar.Controls.AddRange([refresh, checkHealth, diagnostics]);
        table.Controls.Add(toolbar, 0, 0);
        table.Controls.Add(box, 0, 1);
        window.Controls.Add(table);
        GwdUi.StyleTree(window);
        var lifetime = new CancellationTokenSource();
        Task? loading = null;

        async Task RefreshEvidence(bool probeHealth = false)
        {
            var ct = lifetime.Token;
            refresh.Enabled = checkHealth.Enabled = false;
            box.Text = probeHealth ? "Checking the configured health scope…" : "Reading the selected project's history…";
            try
            {
                var scope = DashboardScope();
                if (probeHealth)
                {
                    if (!CanNavigateInteractively())
                    {
                        box.Text = "Health refresh is available after the current operation finishes.";
                        return;
                    }
                    _healthMonitor.SubmitProfile(_profile);
                    await _healthMonitor.RefreshNowAsync(ct);
                }
                DashboardHistoryEvidence? history = scope.Project is null ? null :
                    await Task.Run(() => _history.ReadDashboardHistory(scope.Project, ct), ct);
                if (ct.IsCancellationRequested || IsDisposed || Disposing || window.IsDisposed) return;
                if (scope != DashboardScope()) { box.Text = "The project or target changed. Refresh evidence for the current scope."; return; }

                var health = _healthMonitor.Latest;
                var resource = _runtimeMetrics.GetLatestSample();
                static string At(DateTime? at) => at?.ToString("yyyy-MM-dd HH:mm:ss") ?? "Not checked";
                static string RecordedAt(DateTime? at) => At(at?.ToLocalTime());
                static string Value(string? value) => string.IsNullOrWhiteSpace(value) ? "Not Available" : SecretRedactor.Redact(value);
                var last = history?.Latest;
                var release = history?.LatestRecordedRelease;
                var lines = new List<string>
                {
                    "STATUS AND EVIDENCE",
                    "Shown at " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " (local)",
                    "",
                    "Active project: " + (scope.Project is null ? "Not selected" : Value(_profile.ApplicationName)),
                    "GWD version: " + ProductInfo.DisplayVersion,
                    "Build mode: " + Value(_profile.BuildMode),
                    "Target: " + Value(scope.Mode) + (string.IsNullOrWhiteSpace(scope.Host) ? "" : " / " + Value(scope.Host)),
                    "",
                    "IIS: " + (health?.IisCheckedAt is null ? "Not Verified" :
                        health.IisInstalled ? $"Installed; {health.SiteCount} sites, {health.AppPoolCount} pools (local)" : "Not installed or not verified"),
                    "IIS checked: " + At(health?.IisCheckedAt),
                    "Administrative SQL: " + (health?.SqlOk is null ? "Not Verified" : health.SqlOk.Value ? "Reachable (not database readiness)" : "Probe failed"),
                    "SQL checked: " + At(health?.SqlCheckedAt),
                    "HTTP: " + (health?.HttpStatus is int status ? $"HTTP {status} (localhost only)" : "Not Verified"),
                    "HTTP checked: " + At(health?.HttpCheckedAt),
                    "Installed app/release identity: Not Verified by this dashboard",
                    "",
                    "Last recorded attempt: " + RecordedAt(last?.StartedAtUtc),
                    "Last recorded result: " + (last is null ? "Not Available" : DeploymentHistoryService.DescribeAttemptResult(last)),
                    "Last attempt duration: " + (last?.EndedAtUtc is null ? "Not Available" : TimeSpan.FromSeconds(last.ElapsedSeconds).ToString("g")),
                    "Last recorded hosted version: " + (release is null ? "Not Available" : Value(release.ApplicationVersion)),
                    "History is project-wide; this metadata is not verification of the current target.",
                    "",
                    "Backup: " + (_profile.CreateBackupBeforeDeployment ? "Requested for this profile; copy Not Verified here" : "Not requested for this profile; copy Not Verified here"),
                    "Rollback: " + (_profile.KeepPreviousDeploymentForRollback ? "Retention configured; restoration Not Verified here" : "Retention not selected; restoration Not Verified here"),
                    "Recovery: " + (scope.Project is null ? "Not Available" : last?.Result == "RecoveryFailed" || last is { EndedAtUtc: null }
                        ? "Latest project record requires review; inspect diagnostics and recovery evidence." : "Not Verified; consult recovery evidence if indicated."),
                    "",
                    "GWD process CPU/RAM: " + (resource?.ToDisplayText() ?? "Not Available"),
                    "Sample time: " + At(resource?.At.LocalDateTime),
                    "Current session's last error: " + (string.IsNullOrWhiteSpace(_lastErrorReportPath)
                        ? "No report recorded in this session" : "A diagnostic report is recorded; see Diagnostics (session scope)."),
                    "Active notifications: " + VisibleNotificationCount,
                    "Session notification history: " + _notices.Count
                };
                box.Lines = lines.ToArray();
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
            catch (Exception ex)
            {
                InternalDiagnostics.Record(ex, "Dashboard read-only evidence");
                if (!window.IsDisposed && !ct.IsCancellationRequested)
                    box.Text = "Could not read dashboard evidence. See Diagnostics.";
            }
            finally { if (!refresh.IsDisposed) refresh.Enabled = checkHealth.Enabled = true; }
        }

        void BeginRefresh(bool probeHealth = false)
        {
            if (loading is null || loading.IsCompleted) loading = RefreshEvidence(probeHealth);
        }

        diagnostics.Click += (_, _) => { if (!_visualResourcesStopped) OpenDiagnostics(); };
        refresh.Click += (_, _) => BeginRefresh();
        checkHealth.Click += (_, _) => BeginRefresh(probeHealth: true);
        window.Shown += (_, _) => BeginRefresh();
        window.FormClosed += (_, _) =>
        {
            lifetime.Cancel();
            if (loading is { IsCompleted: false } pending)
                _ = pending.ContinueWith(_ => lifetime.Dispose(), CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            else lifetime.Dispose();
            if (ReferenceEquals(_dashboardWindow, window)) _dashboardWindow = null;
        };
        _dashboardWindow = window;
        window.Show(this);
    }
}
