using GenericWeb.DeploymentManager.Services;

namespace GenericWeb.DeploymentManager;

public sealed partial class WizardForm
{
    private readonly Label _dashboardProject = new() { AutoSize = false, Dock = DockStyle.Fill, AutoEllipsis = true, Font = GwdUi.Section };
    private readonly Label _dashboardMode = new() { AutoSize = false, Dock = DockStyle.Fill, AutoEllipsis = true, Font = GwdUi.Section };
    private readonly Label _dashboardOperation = new() { AutoSize = false, Dock = DockStyle.Fill, AutoEllipsis = true, Font = GwdUi.Section };
    private readonly Label _noticeText = new() { AutoSize = false, Dock = DockStyle.Fill, AutoEllipsis = true, TextAlign = ContentAlignment.MiddleLeft };
    private readonly Button _noticeHistory = new() { Text = "Notifications (0)", AutoSize = true };
    private readonly Button _dismissNotice = new() { Text = "Dismiss", AutoSize = true, Enabled = false };
    private readonly System.Windows.Forms.Timer _noticeTimer = new() { Interval = 1000 };
    private readonly Queue<UiNotice> _notices = new();
    private UiNotice? _activeNotice;
    private DateTimeOffset? _noticeExpires;
    private string? _lastNotifiedStatus;
    private LogForm? _liveLogWindow;
    private string? _liveLogPath;
    private Form? _noticeWindow;
    private TextBox? _noticeHistoryText;
    private bool _visualResourcesStopped;
    private enum UiNoticeKind { Info, Success, Warning, Error }
    private sealed record UiNotice(DateTimeOffset At, UiNoticeKind Kind, string Text);
    private const int MaxNotices = 100;
    private const int NoticeSeconds = 6;

    private Control BuildDashboard()
    {
        var dashboard = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 3, RowCount = 1, Margin = new Padding(0, 0, 0, 10) };
        for (var i = 0; i < 3; i++) dashboard.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33.333F));
        dashboard.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        dashboard.Controls.Add(DashboardCard("PROJECT", _dashboardProject), 0, 0);
        dashboard.Controls.Add(DashboardCard("BUILD MODE", _dashboardMode), 1, 0);
        dashboard.Controls.Add(DashboardCard("WORKFLOW", _dashboardOperation), 2, 0);
        return dashboard;
    }

    private static Control DashboardCard(string title, Label value)
    {
        var card = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 1, RowCount = 2, Padding = new Padding(10, 7, 10, 7), Margin = new Padding(0, 0, 6, 0) };
        card.BackColor = GwdUi.HighContrast ? SystemColors.Control : GwdUi.Surface;
        card.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        card.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        card.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));
        card.Controls.Add(new Label { Text = title, AutoSize = true, Font = GwdUi.Small }, 0, 0);
        value.TextAlign = ContentAlignment.MiddleLeft;
        card.Controls.Add(value, 0, 1);
        return card;
    }

    private void UpdateDashboard()
    {
        if (IsDisposed || Disposing || _visualResourcesStopped) return;
        var name = string.IsNullOrWhiteSpace(_profile.ApplicationName) ? "No project selected" : SecretRedactor.Redact(_profile.ApplicationName);
        var mode = string.IsNullOrWhiteSpace(_profile.BuildMode) ? "Not selected" : _profile.BuildMode;
        var operation = _operationRunning ? "Operation running" : _autoDeployRunning ? "Auto Deploy running" :
            _dependencySyncRunning ? "Dependency sync" : _pageActionRunning || _navigationInProgress ? "Checking..." : "Idle";
        SetDashboardValue(_dashboardProject, name);
        SetDashboardValue(_dashboardMode, mode);
        SetDashboardValue(_dashboardOperation, operation);
    }

    private void SetDashboardValue(Label label, string text)
    {
        if (label.Text == text) return;
        label.Text = text;
        _healthTip.SetToolTip(label, text);
    }

    private Control BuildNotificationBar()
    {
        var bar = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 3, RowCount = 1, Margin = new Padding(3, 1, 3, 1) };
        bar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        bar.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        bar.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        bar.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        _noticeText.MinimumSize = new Size(0, 30);
        _noticeText.Text = "No new notifications";
        bar.Controls.Add(_noticeText, 0, 0);
        bar.Controls.Add(_dismissNotice, 1, 0);
        bar.Controls.Add(_noticeHistory, 2, 0);
        _noticeHistory.Click += (_, _) => OpenNotifications();
        _dismissNotice.Click += (_, _) => DismissNotice();
        _noticeTimer.Tick += (_, _) => RenderNotice();
        return bar;
    }

    private void NotifyUi(UiNoticeKind kind, string message)
    {
        if (IsDisposed || Disposing || _visualResourcesStopped) return;
        var safe = SecretRedactor.Redact(message).Replace('\r', ' ').Replace('\n', ' ');
        if (safe.Length > 1500) safe = safe[..1500] + " … (see full diagnostic log)";
        var notice = new UiNotice(DateTimeOffset.Now, kind, safe);
        _notices.Enqueue(notice);
        while (_notices.Count > MaxNotices) _notices.Dequeue();
        _noticeHistory.Text = $"Notifications ({_notices.Count})";
        if (_noticeHistoryText is { IsDisposed: false }) _noticeHistoryText.Text = NotificationText();
        // An informational notice never replaces a still-visible safety warning.
        if (_activeNotice is { Kind: UiNoticeKind.Warning or UiNoticeKind.Error } &&
            kind is UiNoticeKind.Info or UiNoticeKind.Success) return;
        _activeNotice = notice;
        _noticeExpires = kind is UiNoticeKind.Info or UiNoticeKind.Success
            ? DateTimeOffset.Now.AddSeconds(NoticeSeconds) : null;
        RenderNotice();
    }

    private void RenderNotice()
    {
        if (_activeNotice is null) { _noticeTimer.Stop(); return; }
        if (_noticeExpires is { } expiry && expiry <= DateTimeOffset.Now) { DismissNotice(); return; }
        var seconds = _noticeExpires is { } at ? $"  ·  {Math.Max(1, (int)Math.Ceiling((at - DateTimeOffset.Now).TotalSeconds))}s" : "";
        _noticeText.Text = $"{_activeNotice.Kind}: {_activeNotice.Text}{seconds}";
        _healthTip.SetToolTip(_noticeText, _activeNotice.Text);
        if (!GwdUi.HighContrast)
            _noticeText.ForeColor = _activeNotice.Kind switch
            {
                UiNoticeKind.Error => GwdUi.Danger,
                UiNoticeKind.Warning => GwdUi.Warning,
                UiNoticeKind.Success => GwdUi.Success,
                _ => GwdUi.Ink
            };
        _dismissNotice.Enabled = true;
        if (_noticeExpires.HasValue) _noticeTimer.Start(); else _noticeTimer.Stop();
    }

    private void DismissNotice()
    {
        _noticeTimer.Stop();
        _activeNotice = null; _noticeExpires = null;
        _noticeText.Text = "No new notifications";
        _healthTip.SetToolTip(_noticeText, "");
        if (!GwdUi.HighContrast) _noticeText.ForeColor = GwdUi.Muted;
        _dismissNotice.Enabled = false;
    }

    private string NotificationText() => string.Join(Environment.NewLine,
        _notices.Reverse().Select(x => $"[{x.At:HH:mm:ss}] {x.Kind}: {x.Text}"));

    private void ObserveRecordedStatus(bool needsReview)
    {
        if (_lastNotifiedStatus == _deploymentStatus) return;
        _lastNotifiedStatus = _deploymentStatus;
        if (needsReview) NotifyUi(UiNoticeKind.Warning, _deploymentStatus + " — review recovery evidence.");
        else if (_deploymentStatus.StartsWith("FAST LOCAL TEST PASSED", StringComparison.OrdinalIgnoreCase))
            NotifyUi(UiNoticeKind.Success, "Local build passed; hosted IIS is unchanged.");
        else if (_deploymentStatus == "DEPLOYMENT SUCCESSFUL")
            NotifyUi(UiNoticeKind.Success, "Deployment recorded as successful. See Diagnostics for evidence.");
    }

    private void OpenNotifications()
    {
        if (_noticeWindow is { IsDisposed: false }) { _noticeWindow.Activate(); return; }
        var dialog = new Form { Text = "Session notifications", Width = 820, Height = 460, StartPosition = FormStartPosition.CenterParent };
        GwdUi.ConfigureForm(dialog);
        var box = new TextBox { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Both, WordWrap = true, Text = NotificationText() };
        dialog.Controls.Add(box);
        _noticeWindow = dialog; _noticeHistoryText = box;
        dialog.FormClosed += (_, _) => { _noticeWindow = null; _noticeHistoryText = null; };
        dialog.Show(this);
    }

    private void OpenLiveLog()
    {
        var path = _logService.LogFile;
        if (_liveLogWindow is { IsDisposed: false } existing && _liveLogPath == path) { existing.Activate(); return; }
        _liveLogWindow?.Close();
        var viewer = new LogForm(path);
        _liveLogWindow = viewer; _liveLogPath = path;
        viewer.FormClosed += (_, _) =>
        {
            if (ReferenceEquals(_liveLogWindow, viewer)) { _liveLogWindow = null; _liveLogPath = null; }
        };
        viewer.Show(this); // Non-modal: Cancel Deploy remains available.
    }

    private void OpenDiagnostics()
    {
        // Preserve the existing modal Diagnostics access, including during
        // deployment. Live Log is also available in a non-modal window.
        using var dialog = new DiagnosticsCenterForm(_appData, _history, _profile, _deployment, _logService.LogFile, _runtimeMetrics);
        dialog.ShowDialog(this);
    }

    private void OpenHistory()
    {
        if (!CanNavigateInteractively()) return;
        using var dialog = new DeploymentHistoryForm(_history, profile: _profile, currentLog: _logService.LogFile);
        dialog.ShowDialog(this);
    }

    private void StopVisualResources()
    {
        if (_visualResourcesStopped) return;
        _visualResourcesStopped = true;
        _noticeTimer.Stop(); _noticeTimer.Dispose();
        if (_noticeWindow is { IsDisposed: false }) _noticeWindow.Close();
        if (_liveLogWindow is { IsDisposed: false }) _liveLogWindow.Close();
    }
}
