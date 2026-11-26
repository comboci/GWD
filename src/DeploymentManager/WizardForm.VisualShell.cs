using GenericWeb.DeploymentManager.Services;
using System.Diagnostics;

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
    private readonly List<UiNotice> _notices = new();
    private readonly List<UiNotice> _pendingNotices = new();
    private long _noticeSequence;
    private int _unshownNotices;
    private int VisibleNotificationCount => _pendingNotices.Count + (_activeNotice is null ? 0 : 1);
    private UiNotice? _activeNotice;
    private long? _noticeShownStamp;
    private string? _lastNotifiedStatus;
    private LogForm? _liveLogWindow;
    private string? _liveLogPath;
    private Form? _noticeWindow;
    private TextBox? _noticeHistoryText;
    private bool _visualResourcesStopped;
    private enum UiNoticeKind { Info, Success, Warning, Error, Critical }
    private sealed class UiNotice(long sequence, DateTimeOffset at, UiNoticeKind kind, string text)
    {
        internal long Sequence { get; } = sequence;
        internal DateTimeOffset At { get; } = at;
        internal UiNoticeKind Kind { get; } = kind;
        internal string Text { get; } = text;
        internal int Repetitions { get; set; } = 1;
    }
    private const int MaxNotices = 100;
    private const int NoticeSeconds = 6;

    private Control BuildDashboard()
    {
        var dashboard = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 3, RowCount = 2, Margin = new Padding(0, 0, 0, 10) };
        for (var i = 0; i < 3; i++) dashboard.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33.333F));
        dashboard.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        dashboard.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        dashboard.Controls.Add(DashboardCard("PROJECT", _dashboardProject), 0, 0);
        dashboard.Controls.Add(DashboardCard("BUILD MODE", _dashboardMode), 1, 0);
        dashboard.Controls.Add(DashboardCard("WORKFLOW", _dashboardOperation), 2, 0);
        var status = new Button { Text = "Deployment status & evidence…", AutoSize = true, AccessibleName = "Open deployment status and evidence",
            Anchor = AnchorStyles.Top | AnchorStyles.Left, Margin = new Padding(0, GwdUi.SmallSpace, 0, 0) };
        status.Click += (_, _) => OpenDashboardEvidence();
        dashboard.Controls.Add(status, 0, 1);
        dashboard.SetColumnSpan(status, 3);
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
        var name = (string.IsNullOrWhiteSpace(_profile.SourceFolder) && string.IsNullOrWhiteSpace(_profile.ReadyPublishFolder)) ||
            string.IsNullOrWhiteSpace(_profile.ApplicationName) ? "No project selected" : SecretRedactor.Redact(_profile.ApplicationName);
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
        var duplicate = _activeNotice is { } active && active.Kind == kind && active.Text == safe ? active
            : _pendingNotices.FirstOrDefault(x => x.Kind == kind && x.Text == safe);
        if (duplicate is not null)
        {
            if (duplicate.Repetitions < int.MaxValue) duplicate.Repetitions++;
        }
        else
        {
            var notice = new UiNotice(++_noticeSequence, DateTimeOffset.Now, kind, safe);
            _notices.Add(notice);
            if (_notices.Count > MaxNotices) _notices.RemoveAt(0);
            if (_activeNotice is null) ActivateNotice(notice);
            else if ((int)kind > (int)_activeNotice.Kind)
            {
                EnqueueNotice(_activeNotice);
                ActivateNotice(notice);
            }
            else EnqueueNotice(notice);
        }
        UpdateNoticeHistory();
        RenderNotice();
    }

    private void EnqueueNotice(UiNotice notice)
    {
        if (_pendingNotices.Count >= MaxNotices)
        {
            var lowest = _pendingNotices.Min(x => (int)x.Kind);
            if (_unshownNotices < int.MaxValue) _unshownNotices++;
            if ((int)notice.Kind <= lowest) return;
            _pendingNotices.RemoveAt(_pendingNotices.FindLastIndex(x => (int)x.Kind == lowest));
        }
        _pendingNotices.Add(notice);
    }

    private void ActivateNotice(UiNotice notice)
    {
        _activeNotice = notice;
        _noticeShownStamp = notice.Kind is UiNoticeKind.Info or UiNoticeKind.Success ? Stopwatch.GetTimestamp() : null;
    }

    private void UpdateNoticeHistory()
    {
        _noticeHistory.Text = $"Notifications ({VisibleNotificationCount} active)";
        if (_noticeHistoryText is { IsDisposed: false }) _noticeHistoryText.Text = NotificationText();
    }

    private void RenderNotice()
    {
        if (_activeNotice is null) { _noticeTimer.Stop(); return; }
        var remaining = _noticeShownStamp is { } stamp ? NoticeSeconds - Stopwatch.GetElapsedTime(stamp).TotalSeconds : (double?)null;
        if (remaining is <= 0) { DismissNotice(); return; }
        var seconds = remaining is { } time ? $" · {Math.Max(1, (int)Math.Ceiling(time))}s" : "";
        var repeated = _activeNotice.Repetitions > 1 ? $" ×{_activeNotice.Repetitions}" : "";
        var flavor = _activeNotice.Kind switch
        {
            UiNoticeKind.Critical or UiNoticeKind.Error => GwdUi.Feedback.Error,
            UiNoticeKind.Warning => GwdUi.Feedback.Warning,
            UiNoticeKind.Success => GwdUi.Feedback.Success,
            _ => GwdUi.Feedback.Info
        };
        GwdUi.ShowFeedback(_noticeText, flavor, $"{_activeNotice.Kind}: {_activeNotice.Text}{repeated}{seconds}");
        _healthTip.SetToolTip(_noticeText, _activeNotice.Text);
        _dismissNotice.Enabled = true;
        if (_noticeShownStamp.HasValue) _noticeTimer.Start(); else _noticeTimer.Stop();
    }

    private void DismissNotice()
    {
        _noticeTimer.Stop();
        _activeNotice = null;
        _noticeShownStamp = null;
        if (_pendingNotices.Count > 0)
        {
            var next = _pendingNotices.OrderByDescending(x => x.Kind).ThenBy(x => x.Sequence).First();
            _pendingNotices.Remove(next);
            ActivateNotice(next);
            UpdateNoticeHistory();
            RenderNotice();
            return;
        }
        GwdUi.ShowFeedback(_noticeText, GwdUi.Feedback.Empty, "No new notifications");
        _healthTip.SetToolTip(_noticeText, "");
        _dismissNotice.Enabled = false;
        UpdateNoticeHistory();
    }

    private string NotificationText()
    {
        var lines = _notices.AsEnumerable().Reverse().Select(x =>
            $"[{x.At:HH:mm:ss}] {x.Kind}: {x.Text}" + (x.Repetitions > 1 ? $" ×{x.Repetitions}" : "")).ToList();
        if (_unshownNotices > 0) lines.Insert(0, $"Banner limit: {_unshownNotices} message(s) not shown. Check Diagnostics for retained logs.");
        return string.Join(Environment.NewLine, lines);
    }

    private void ObserveRecordedStatus(bool needsReview)
    {
        if (_lastNotifiedStatus == _deploymentStatus) return;
        _lastNotifiedStatus = _deploymentStatus;
        if (needsReview)
        {
            var critical = _deploymentStatus.Contains("RECOVERY FAILED", StringComparison.OrdinalIgnoreCase) ||
                _deploymentStatus.Contains("INTERRUPTED", StringComparison.OrdinalIgnoreCase);
            NotifyUi(critical ? UiNoticeKind.Critical : UiNoticeKind.Warning, _deploymentStatus + " — review recovery evidence.");
        }
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
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 2, ColumnCount = 1, Padding = new Padding(GwdUi.MediumSpace) };
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        var go = new Button { Text = "Open related Diagnostics", AutoSize = true };
        go.Click += (_, _) => OpenDiagnostics();
        layout.Controls.Add(go, 0, 0);
        layout.Controls.Add(box, 0, 1);
        dialog.Controls.Add(layout);
        GwdUi.StyleTree(dialog);
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
        if (_dashboardWindow is { IsDisposed: false }) _dashboardWindow.Close();
        if (_liveLogWindow is { IsDisposed: false }) _liveLogWindow.Close();
    }
}
