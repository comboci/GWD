using GenericWeb.DeploymentManager.Models;
using GenericWeb.DeploymentManager.Services;

namespace GenericWeb.DeploymentManager;

public sealed class DeploymentHistoryForm : Form
{
    private readonly DeploymentHistoryService _history;
    private readonly string? _preferredProjectId;
    private readonly DeploymentProfile? _profile;
    private readonly string? _currentLog;
    private readonly ComboBox _projects = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 420 };
    private readonly RichTextBox _summary = new() { ReadOnly = true, Dock = DockStyle.Fill, Font = GwdUi.MonoReading, BackColor = Color.White };
    private readonly DataGridView _grid = new()
    {
        Dock = DockStyle.Fill,
        ReadOnly = true,
        AllowUserToAddRows = false,
        AllowUserToDeleteRows = false,
        VirtualMode = true,
        AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.DisplayedCells,
        SelectionMode = DataGridViewSelectionMode.FullRowSelect,
        MultiSelect = false
    };
    private readonly Label _status = new() { AutoSize = true, ForeColor = Color.DimGray, Padding = new Padding(8, 8, 8, 0) };
    private const int HistoryPageSize = 100;
    private readonly Button _newer = new() { Text = "Newer", AutoSize = true, Enabled = false };
    private readonly Button _older = new() { Text = "Older", AutoSize = true, Enabled = false };
    private int _pageIndex;
    private IReadOnlyList<DeploymentHistoryRecord> _currentAttempts = Array.Empty<DeploymentHistoryRecord>();
    private readonly HashSet<string> _expandedAttemptIds = new(StringComparer.OrdinalIgnoreCase);
    private readonly Font _boldAttemptFont;
    private readonly List<(DeploymentHistoryRecord Attempt, BuildPerformanceStageHistory? Stage)> _visibleRows = new();
    private bool _selectingProjects;
    private bool _loadingProjects;
    private int _generation;
    private CancellationTokenSource? _loadRequest;

    public DeploymentHistoryForm(DeploymentHistoryService history, string? preferredProjectId = null, DeploymentProfile? profile = null, string? currentLog = null)
    {
        _history = history;
        _preferredProjectId = preferredProjectId;
        _profile = profile;
        _currentLog = currentLog;

        Text = $"Deployment History / Stage Timing — {ProductInfo.DisplayVersion}";
        Width = 1180;
        Height = 760;
        MinimumSize = new Size(980, 620);
        StartPosition = FormStartPosition.CenterParent;
        KeyPreview = true;
        KeyDown += (_, e) => { if (e.KeyCode == Keys.Escape) { DialogResult = DialogResult.Cancel; Close(); } };
        GwdUi.ConfigureForm(this);

        BuildUi();
        GwdUi.StyleTree(this);
        // The grid is parented now and has inherited the form's DPI-aware font.
        _boldAttemptFont = new Font(_grid.Font, FontStyle.Bold);
        Shown += async (_, _) => await LoadProjectsAsync();
        FormClosed += (_, _) => { _generation++; _loadRequest?.Cancel(); };
    }

    private void BuildUi()
    {
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 3, Padding = new Padding(12) };
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        var header = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, WrapContents = true };
        header.Controls.Add(new Label { Text = "Project:", AutoSize = true, Padding = new Padding(0, 8, 6, 0), Font = GwdUi.Section });
        header.Controls.Add(_projects);
        var refresh = new Button { Text = "Refresh", Width = 95, Height = 32, Margin = new Padding(10, 2, 0, 2) };
        refresh.Click += async (_, _) => await LoadProjectsAsync();
        header.Controls.Add(refresh);
        _newer.Click += async (_, _) => await LoadSelectedProjectAsync(Math.Max(0, _pageIndex - 1));
        _older.Click += async (_, _) => await LoadSelectedProjectAsync(_pageIndex + 1);
        header.Controls.Add(_newer);
        header.Controls.Add(_older);
        var openFolder = new Button { Text = "Open History Folder", Width = 150, Height = 32, Margin = new Padding(10, 2, 0, 2) };
        openFolder.Click += (_, _) => OpenTarget(_history.Root);
        header.Controls.Add(openFolder);
        var export = new Button { Text = "Export Diagnostic Bundle", Width = 190, Height = 32, Margin = new Padding(10, 2, 0, 2), Enabled = _profile is not null };
        export.Click += async (_, _) =>
        {
            export.Enabled = false;
            try { await ExportDiagnosticBundleAsync(); }
            finally { if (!export.IsDisposed) export.Enabled = true; }
        };
        header.Controls.Add(export);
        root.Controls.Add(header, 0, 0);

        var tabs = new TabControl { Dock = DockStyle.Fill };
        var summaryTab = new TabPage("Lifetime Summary & Timeline");
        summaryTab.Controls.Add(_summary);
        tabs.TabPages.Add(summaryTab);

        var historyTab = new TabPage("Deployment History");
        _grid.AutoGenerateColumns = false;
        _grid.RowHeadersVisible = false;
        _grid.Columns.Add("Expand", "");
        _grid.Columns.Add("Started", "Started");
        _grid.Columns.Add("Result", "Result / Stage status");
        _grid.Columns.Add("Duration", "Duration");
        _grid.Columns.Add("BuildMode", "Build mode / Stage");
        _grid.Columns.Add("DependencyMode", "Dependency mode");
        _grid.Columns.Add("Detail", "Detail / Target");
        _grid.Columns[0].Width = 42;
        _grid.Columns[0].AutoSizeMode = DataGridViewAutoSizeColumnMode.None;
        _grid.Columns[6].AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill;
        var historyRoot = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 2 };
        historyRoot.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        historyRoot.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        historyRoot.Controls.Add(_grid, 0, 0);
        var actions = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, FlowDirection = FlowDirection.LeftToRight, Padding = new Padding(6) };
        var openLog = new Button { Text = "Open Session Log", Width = 145, Height = 32 };
        openLog.Click += (_, _) => OpenSelectedLog();
        actions.Controls.Add(openLog);
        actions.Controls.Add(new Label { Text = "Click a deployment row to expand/collapse its recorded Build Performance stages. Double-click opens its log.", AutoSize = true, ForeColor = Color.DimGray, Padding = new Padding(10, 8, 0, 0) });
        historyRoot.Controls.Add(actions, 0, 1);
        historyTab.Controls.Add(historyRoot);
        tabs.TabPages.Add(historyTab);
        root.Controls.Add(tabs, 0, 1);

        root.Controls.Add(_status, 0, 2);
        Controls.Add(root);

        foreach (DataGridViewColumn column in _grid.Columns)
            column.SortMode = DataGridViewColumnSortMode.NotSortable;
        _grid.CellValueNeeded += (_, e) =>
        {
            if (e.RowIndex < 0 || e.RowIndex >= _visibleRows.Count) return;
            e.Value = GetCellValue(_visibleRows[e.RowIndex], e.ColumnIndex);
        };
        _grid.CellFormatting += (_, e) =>
        {
            if (e.RowIndex < 0 || e.RowIndex >= _visibleRows.Count) return;
            var stage = _visibleRows[e.RowIndex].Stage;
            if (stage is null) e.CellStyle.Font = _boldAttemptFont;
            else
            {
                e.CellStyle.BackColor = Color.FromArgb(245, 248, 252);
                e.CellStyle.ForeColor = stage.Status.Equals("FAILED", StringComparison.OrdinalIgnoreCase) ? Color.Firebrick : Color.DimGray;
            }
        };
        _projects.SelectedIndexChanged += async (_, _) => { if (!_selectingProjects) await LoadSelectedProjectAsync(); };
        _grid.CellClick += (_, e) => ToggleSelectedPerformance(e.RowIndex);
        _grid.CellDoubleClick += (_, _) => OpenSelectedLog();
    }

    private async Task ExportDiagnosticBundleAsync()
    {
        if (_profile is null) return;
        try
        {
            var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            var profile = _profile;
            var path = await Task.Run(() =>
            {
                Directory.CreateDirectory(desktop);
                return new DiagnosticBundleService().Export(desktop, profile, _history, _currentLog ?? string.Empty);
            });
            if (IsDisposed || Disposing) return;
            MessageBox.Show(this, "Diagnostic bundle created with sensitive values redacted:\r\n" + path, "Diagnostic bundle", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception ex) { if (!IsDisposed && !Disposing) MessageBox.Show(this, SecretRedactor.Redact(ex.Message), "Diagnostic export failed", MessageBoxButtons.OK, MessageBoxIcon.Error); }
    }

    private async Task LoadProjectsAsync()
    {
        if (_loadingProjects || IsDisposed || Disposing) return;
        _loadingProjects = true;
        _projects.Enabled = false;
        _loadRequest?.Cancel();
        var request = new CancellationTokenSource();
        _loadRequest = request;
        var generation = ++_generation;
        var selectedId = (_projects.SelectedItem as ProjectChoice)?.ProjectId ?? _preferredProjectId;
        _status.Text = "Loading projects...";
        try
        {
            var catalog = await Task.Run(() => _history.GetProjectCatalog(request.Token), request.Token);
            if (request.IsCancellationRequested || generation != _generation || IsDisposed || Disposing) return;
            _selectingProjects = true;
            _projects.BeginUpdate();
            try
            {
                _projects.Items.Clear();
                foreach (var item in catalog)
                    _projects.Items.Add(new ProjectChoice(item.ProjectId, DisplayProjectName(item)));
                if (_projects.Items.Count > 0)
                    _projects.SelectedIndex = Enumerable.Range(0, _projects.Items.Count)
                        .FirstOrDefault(i => (_projects.Items[i] as ProjectChoice)?.ProjectId == selectedId);
            }
            finally { _projects.EndUpdate(); _selectingProjects = false; }
            await LoadSelectedProjectAsync();
        }
        catch (OperationCanceledException) when (request.IsCancellationRequested) { }
        catch (Exception ex)
        {
            if (!IsDisposed && !Disposing && generation == _generation)
                _status.Text = "Project list unavailable: " + SecretRedactor.Redact(ex.Message);
        }
        finally
        {
            if (ReferenceEquals(_loadRequest, request)) _loadRequest = null;
            request.Dispose();
            _loadingProjects = false;
            if (!IsDisposed && !Disposing) _projects.Enabled = true;
        }
    }

    private async Task LoadSelectedProjectAsync(int pageIndex = 0)
    {
        _loadRequest?.Cancel();
        var generation = ++_generation;
        _currentAttempts = Array.Empty<DeploymentHistoryRecord>();
        _newer.Enabled = _older.Enabled = false;
        _expandedAttemptIds.Clear();
        PopulateHistoryGrid();
        if (_projects.SelectedItem is not ProjectChoice choice)
        {
            _summary.Text = "No deployment history has been recorded yet.";
            _status.Text = "History folder: " + _history.Root;
            return;
        }
        var request = new CancellationTokenSource();
        _loadRequest = request;
        var id = choice.ProjectId;
        _summary.Text = "Loading project history...";
        GwdUi.ShowFeedback(_status, GwdUi.Feedback.Loading, "Loading history…");
        try
        {
            var result = await Task.Run(() =>
            {
                var data = _history.GetHistoryPage(id, pageIndex, HistoryPageSize, request.Token);
                return (Text: data.Summary is null ? "No deployment history has been recorded yet." : BuildSummaryText(data.Summary, data.Rows),
                    Page: data);
            }, request.Token);
            if (request.IsCancellationRequested || generation != _generation || IsDisposed || Disposing ||
                (_projects.SelectedItem as ProjectChoice)?.ProjectId != id) return;
            _currentAttempts = result.Page.Rows;
            _pageIndex = result.Page.PageIndex;
            _summary.Text = result.Text;
            PopulateHistoryGrid();
            var pageCount = result.Page.TotalAttempts == 0 ? 0 : (result.Page.TotalAttempts - 1) / result.Page.PageSize + 1;
            _newer.Enabled = _pageIndex > 0;
            _older.Enabled = _pageIndex + 1 < pageCount;
            GwdUi.ShowFeedback(_status, pageCount == 0 ? GwdUi.Feedback.Empty : GwdUi.Feedback.Info,
                pageCount == 0 ? "No deployment history recorded."
                    : $"Page {_pageIndex + 1}/{pageCount} · {result.Page.TotalAttempts} attempt(s) recorded. Persistent history: {_history.Root}");
        }
        catch (OperationCanceledException) when (request.IsCancellationRequested) { }
        catch (Exception ex)
        {
            if (!IsDisposed && !Disposing && generation == _generation)
                _summary.Text = "Deployment history unavailable: " + SecretRedactor.Redact(ex.Message);
        }
        finally
        {
            if (ReferenceEquals(_loadRequest, request)) _loadRequest = null;
            request.Dispose();
        }
    }

    private void PopulateHistoryGrid()
    {
        _visibleRows.Clear();
        foreach (var attempt in _currentAttempts)
        {
            _visibleRows.Add((attempt, null));
            if (_expandedAttemptIds.Contains(attempt.AttemptId) && attempt.BuildPerformance is { } performance)
                foreach (var stage in performance.Stages)
                    _visibleRows.Add((attempt, stage));
        }
        _grid.RowCount = _visibleRows.Count;
        _grid.Invalidate();
    }

    private object? GetCellValue((DeploymentHistoryRecord Attempt, BuildPerformanceStageHistory? Stage) row, int column)
    {
        var attempt = row.Attempt;
        var performance = attempt.BuildPerformance;
        if (row.Stage is { } stage)
            return column switch
            {
                0 or 1 => "",
                2 => stage.Status,
                3 => TimeSpan.FromMilliseconds(Math.Max(0, stage.ElapsedMilliseconds)).ToString(@"hh\:mm\:ss\.fff"),
                4 => "    " + stage.Name,
                5 => performance?.DependencyMode ?? "",
                6 => stage.Detail,
                _ => null
            };
        var canExpand = performance?.Stages.Count > 0;
        var total = performance is null
            ? (attempt.EndedAtUtc.HasValue ? TimeSpan.FromSeconds(attempt.ElapsedSeconds) : TimeSpan.Zero)
            : TimeSpan.FromMilliseconds(performance.TotalMilliseconds);
        return column switch
        {
            0 => canExpand ? (_expandedAttemptIds.Contains(attempt.AttemptId) ? "▼" : "▶") : "",
            1 => ToLocal(attempt.StartedAtUtc),
            2 => attempt.EndedAtUtc.HasValue ? attempt.Result : "In progress / interrupted",
            3 => FormatDuration(total),
            4 => attempt.BuildMode,
            5 => performance?.DependencyMode ?? (attempt.OfflineBuild ? "Offline Only" : "—"),
            6 => attempt.DeploymentTargetPath,
            _ => null
        };
    }

    private void ToggleSelectedPerformance(int rowIndex)
    {
        if (rowIndex < 0 || rowIndex >= _visibleRows.Count) return;
        var row = _visibleRows[rowIndex];
        if (row.Stage is not null || row.Attempt.BuildPerformance?.Stages.Count is not > 0) return;
        if (!_expandedAttemptIds.Add(row.Attempt.AttemptId)) _expandedAttemptIds.Remove(row.Attempt.AttemptId);
        PopulateHistoryGrid();
    }

    private static string BuildSummaryText(DeploymentProjectSummary s, IReadOnlyList<DeploymentHistoryRecord> page)
    {
        var lines = new List<string>
        {
            $"PROJECT LIFETIME — {s.ApplicationName}",
            new string('=', 72),
            $"IIS Site                 : {BlankAsDash(s.SiteName)}",
            $"Database                 : {BlankAsDash(s.DatabaseName)}",
            $"Project ID               : {s.ProjectId}",
            "",
            $"Total deployment attempts: {s.TotalAttempts}",
            $"Successful               : {s.SuccessfulAttempts}",
            $"Failed                   : {s.FailedAttempts}",
            $"Cancelled                : {s.CancelledAttempts}",
            $"Incomplete/interrupted   : {s.IncompleteAttempts}",
            $"Distinct released versions: {(s.DistinctVersions > 0 ? s.DistinctVersions.ToString() : "Version metadata unavailable")}",
            "",
            $"First deployment         : {FormatDate(s.FirstDeploymentUtc)}",
            $"Latest deployment        : {FormatDate(s.LatestDeploymentUtc)}",
            $"Calendar lifetime        : {FormatLifetime(s.CalendarLifetime)}",
            $"Cumulative deploy runtime: {FormatDuration(s.CumulativeRuntime)}",
            $"Average deployment       : {FormatDuration(s.AverageRuntime)}",
            $"Average successful deploy: {FormatDuration(s.AverageSuccessfulRuntime)}",
            $"Fastest deployment       : {FormatDuration(s.FastestRuntime)}",
            $"Slowest deployment       : {FormatDuration(s.SlowestRuntime)}",
            $"Average deploy interval  : {FormatDuration(s.AverageInterval)}",
            "",
            $"Fast local/IIS count      : {s.SmartDeployCount}",
            $"Full IIS Deploy count     : {s.FullDeployCount}",
            $"Prepared deploy count    : {s.PreparedDeployCount}",
            "",
            "RELEASE / DEPLOYMENT TIMELINE — current page",
            new string('-', 72)
        };

        foreach (var item in page.OrderBy(x => x.StartedAtUtc))
        {
            var version = string.IsNullOrWhiteSpace(item.ApplicationVersion) ? "version ?" : "V" + item.ApplicationVersion.TrimStart('V', 'v');
            var result = item.EndedAtUtc.HasValue ? item.Result : "In progress / interrupted";
            var duration = item.EndedAtUtc.HasValue ? FormatDuration(TimeSpan.FromSeconds(item.ElapsedSeconds)) : "—";
            var interval = item.IntervalSincePreviousSeconds.HasValue ? $" | since previous: {FormatDuration(TimeSpan.FromSeconds(item.IntervalSincePreviousSeconds.Value))}" : string.Empty;
            lines.Add($"{ToLocal(item.StartedAtUtc)} | {version} | {result} | {item.BuildMode} | {duration}{interval}");
        }

        return string.Join(Environment.NewLine, lines);
    }

    private void OpenSelectedLog()
    {
        var index = _grid.CurrentCell?.RowIndex ?? -1;
        if (index < 0 || index >= _visibleRows.Count) return;
        var row = _visibleRows[index].Attempt;
        if (string.IsNullOrWhiteSpace(row.SessionLog)) return;
        if (!File.Exists(row.SessionLog))
        {
            MessageBox.Show(this, "The session log no longer exists at:\r\n" + row.SessionLog, "Session log not found", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        // Modal WinForms dialogs are not automatically disposed by ShowDialog.
        using var viewer = new LogForm(row.SessionLog);
        viewer.ShowDialog(this);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) { _generation++; _loadRequest?.Cancel(); }
        try { base.Dispose(disposing); }
        finally
        {
            // Grid rows share this font; dispose after the grid itself.
            if (disposing) _boldAttemptFont?.Dispose();
        }
    }

    private static void OpenTarget(string target)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo { FileName = target, UseShellExecute = true });
        }
        catch (Exception ex)
        {
            MessageBox.Show("Could not open:\r\n" + target + "\r\n\r\n" + ex.Message, "Open failed", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private static string DisplayProjectName(DeploymentProjectCatalogEntry s)
    {
        var name = string.IsNullOrWhiteSpace(s.ApplicationName) ? "Unknown application" : s.ApplicationName;
        return string.IsNullOrWhiteSpace(s.SiteName) ? name : $"{name} — {s.SiteName}";
    }

    private static string ToLocal(DateTime utc) => utc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");
    private static string FormatDate(DateTime? utc) => utc.HasValue ? ToLocal(utc.Value) : "—";
    private static string BlankAsDash(string value) => string.IsNullOrWhiteSpace(value) ? "—" : value;

    private static string FormatDuration(TimeSpan duration)
    {
        if (duration < TimeSpan.Zero) duration = TimeSpan.Zero;
        if (duration.TotalDays >= 1) return $"{(int)duration.TotalDays}d {duration:hh\\:mm\\:ss}";
        return duration.ToString(@"hh\:mm\:ss");
    }

    private static string FormatLifetime(TimeSpan duration)
    {
        if (duration < TimeSpan.Zero) duration = TimeSpan.Zero;
        if (duration.TotalDays >= 1) return $"{duration.TotalDays:0.0} days";
        return FormatDuration(duration);
    }

    private sealed record ProjectChoice(string ProjectId, string Display)
    {
        public override string ToString() => Display;
    }

}
