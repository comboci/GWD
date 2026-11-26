using GenericWeb.DeploymentManager.Models;
using GenericWeb.DeploymentManager.Services;

namespace GenericWeb.DeploymentManager;

public sealed partial class DiagnosticsCenterForm : Form
{
    private readonly AppDataService _appData;
    private readonly DeploymentHistoryService _history;
    private readonly DeploymentProfile _profile;
    private readonly DeploymentService _deployment;
    private readonly string _currentLog;
    private readonly DiagnosticBundleService _bundle = new();
    private readonly RuntimeMetricsService _runtimeMetrics;
    private readonly System.Windows.Forms.Timer _metricsTimer = new() { Interval = 5000 };
    private TabPage? _runtimeResourcesTab;
    private Action? _refreshRuntimeResources;
    private readonly TabControl _tabs = new() { Dock = DockStyle.Fill };
    private readonly Dictionary<TabPage, DiagnosticExportSection> _sectionByTab = new();
    private readonly Dictionary<TabPage, Func<TabPage>> _lazyTabs = new();
    private bool _materializingTab;
    private readonly List<ToolStripMenuItem> _exportCommands = new();
    private readonly ToolStripMenuItem _cancelExport = new("Cancel Export") { Enabled = false };
    private readonly ToolStripMenuItem _qualityExportMenu = new("Code Quality") { Enabled = false };
    private readonly ToolStripMenuItem _refreshSelectedMenu = new("Refresh Selected Tab") { Enabled = false };
    private readonly Dictionary<TabPage, Func<Task>> _tabRefreshActions = new();
    private readonly Dictionary<TabPage, Func<bool>> _tabRefreshAvailable = new();
    private readonly Dictionary<TabPage, Action<string>> _qualityExportActions = new();
    private readonly Dictionary<TabPage, Func<bool>> _qualityExportAvailable = new();
    private readonly Label _exportStatus = new() { AutoSize = true, ForeColor = Color.DimGray, Padding = new Padding(8, 8, 0, 0), Text = "Export idle." };
    private CancellationTokenSource? _exportCts;
    private int _exportGeneration;
    private bool _exportBusy;

    public DiagnosticsCenterForm(AppDataService appData, DeploymentHistoryService history, DeploymentProfile profile, DeploymentService deployment, string currentLog, RuntimeMetricsService runtimeMetrics)
    {
        _appData = appData;
        _history = history;
        _profile = profile;
        _deployment = deployment;
        _currentLog = currentLog;
        _runtimeMetrics = runtimeMetrics;

        Text = $"Diagnostics & Deployment History Center — {ProductInfo.DisplayVersion}";
        Width = 1260;
        Height = 820;
        StartPosition = FormStartPosition.CenterParent;
        KeyPreview = true;
        KeyDown += (_, e) => { if (e.KeyCode == Keys.Escape) { DialogResult = DialogResult.Cancel; Close(); } };

        // Executable operations live in the categorized top menu; the body
        // displays status, history, filters and navigation without duplicate buttons.
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 3 };
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        var menu = new MenuStrip { Dock = DockStyle.Fill };
        var fileMenu = new ToolStripMenuItem("&File");
        var exportMenu = new ToolStripMenuItem("&Export");
        var currentTabMenu = new ToolStripMenuItem("Current Tab");
        var allDiagnosticsMenu = new ToolStripMenuItem("All Diagnostics");
        var exportTab = new ToolStripMenuItem("To Desktop");
        var exportTabOther = new ToolStripMenuItem("Choose Location...");
        var exportAll = new ToolStripMenuItem("To Desktop");
        var exportAllOther = new ToolStripMenuItem("Choose Location...");
        exportTab.Click += async (_, _) => await ExportCurrentTabAsync(toDesktop: true);
        exportTabOther.Click += async (_, _) => await ExportCurrentTabAsync(toDesktop: false);
        exportAll.Click += async (_, _) => await ExportSectionAsync(DiagnosticExportSection.All, toDesktop: true);
        exportAllOther.Click += async (_, _) => await ExportSectionAsync(DiagnosticExportSection.All, toDesktop: false);
        exportTab.ShortcutKeys = Keys.Control | Keys.Shift | Keys.E;
        _exportCommands.AddRange(new[] { exportTab, exportTabOther, exportAll, exportAllOther });
        currentTabMenu.DropDownItems.AddRange(new ToolStripItem[] { exportTab, exportTabOther });
        allDiagnosticsMenu.DropDownItems.AddRange(new ToolStripItem[] { exportAll, exportAllOther });

        var qualityJson = new ToolStripMenuItem("Export Selected Build as JSON");
        var qualityMarkdown = new ToolStripMenuItem("Export Selected Build as Markdown");
        var qualityHandoff = new ToolStripMenuItem("Copy Development Handoff");
        qualityJson.Click += (_, _) => ExportSelectedCodeQuality("JSON");
        qualityMarkdown.Click += (_, _) => ExportSelectedCodeQuality("Markdown");
        qualityHandoff.Click += (_, _) => ExportSelectedCodeQuality("Copy Development Handoff");
        _qualityExportMenu.DropDownItems.AddRange(new ToolStripItem[] { qualityJson, qualityMarkdown, qualityHandoff });

        _cancelExport.Click += (_, _) =>
        {
            if (!_exportBusy || _exportCts is null || _exportCts.IsCancellationRequested) return;
            _exportCts.Cancel();
            _cancelExport.Enabled = false;
            _exportStatus.Text = "Cancelling export; previous ZIP remains unchanged...";
        };
        exportMenu.DropDownItems.AddRange(new ToolStripItem[]
        {
            currentTabMenu, allDiagnosticsMenu, _qualityExportMenu,
            new ToolStripSeparator(), _cancelExport
        });
        fileMenu.DropDownItems.Add(exportMenu);
        fileMenu.DropDownItems.Add(new ToolStripSeparator());
        fileMenu.DropDownItems.Add(new ToolStripMenuItem("Close", null, (_, _) => Close()));
        menu.Items.Add(fileMenu);

        var viewMenu = new ToolStripMenuItem("&View");
        _refreshSelectedMenu.ShortcutKeys = Keys.Control | Keys.R;
        _refreshSelectedMenu.Click += async (_, _) => await RefreshSelectedTabAsync();
        viewMenu.DropDownItems.Add(_refreshSelectedMenu);
        menu.Items.Add(viewMenu);
        MainMenuStrip = menu;
        root.Controls.Add(menu, 0, 0);

        var statusBar = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, Padding = new Padding(12, 4, 8, 5), WrapContents = false };
        statusBar.Controls.Add(_exportStatus);
        statusBar.Controls.Add(new Label
        {
            Text = "Use File → Export for ZIP and Code Quality outputs; View → Refresh for the selected tab.",
            AutoSize = true, ForeColor = Color.DimGray, Padding = new Padding(8, 8, 0, 0)
        });
        root.Controls.Add(statusBar, 0, 1);

        // The initial History tab only constructs controls. Its disk scan starts
        // after Shown; other tabs do not touch logs/diagnostics until selected.
        AddTab(CreateHistoryTab(), DiagnosticExportSection.DeploymentHistory);
        AddLazyTab("Current Log", CreateLogTab, DiagnosticExportSection.CurrentLog);
        AddLazyTab("Crash Reports", () => CreateFilesTab("Crash Reports", _appData.CrashReports, "*.txt"), DiagnosticExportSection.CrashReports);
        AddLazyTab("Error Reports", () => CreateFilesTab("Error Reports", _appData.ErrorReports, "*.txt"), DiagnosticExportSection.ErrorReports);
        AddLazyTab("Code Quality", CreateCodeQualityTab, DiagnosticExportSection.CodeQuality);
        AddLazyTab("Build Performance", CreatePerformanceTab, DiagnosticExportSection.BuildPerformance);
        AddLazyTab("CPU / Memory", CreateRuntimeResourcesTab, DiagnosticExportSection.RuntimeResources);
        AddLazyTab("Internal Diagnostics", () => CreateFilesTab("Internal Diagnostics", _appData.InternalDiagnosticsFolder, "*.log"), DiagnosticExportSection.InternalDiagnostics);
        _tabs.SelectedIndexChanged += (_, _) =>
        {
            MaterializeSelectedTab();
            UpdateActionMenuState();
            UpdateRuntimeResourcesVisibility();
        };

        root.Controls.Add(_tabs, 0, 2);
        Controls.Add(root);
        UpdateActionMenuState();
        // Closing the dialog stops further progress callbacks and requests
        // cancellation; the worker disposes its temporary ZIP in its own finally.
        FormClosing += (_, _) => { _exportGeneration++; _exportCts?.Cancel(); };
        FormClosed += (_, _) => { _metricsTimer.Stop(); _metricsTimer.Dispose(); };
    }

    private void UpdateRuntimeResourcesVisibility()
    {
        var active = _runtimeResourcesTab is { IsDisposed: false } tab &&
            ReferenceEquals(_tabs.SelectedTab, tab);
        if (!active)
        {
            _metricsTimer.Stop();
            return;
        }

        // Only the main Wizard owns CPU sampling. Activate this view on demand,
        // and refresh immediately on return without recording a new sample.
        if (!_metricsTimer.Enabled)
        {
            _metricsTimer.Start();
            _refreshRuntimeResources?.Invoke();
        }
    }

    private void UpdateActionMenuState()
    {
        if (IsDisposed || Disposing) return;
        var selected = _tabs.SelectedTab;
        _refreshSelectedMenu.Enabled = !_exportBusy && selected is not null &&
            _tabRefreshActions.ContainsKey(selected) &&
            (!_tabRefreshAvailable.TryGetValue(selected, out var ready) || ready());
        _qualityExportMenu.Enabled = !_exportBusy && selected is not null &&
            _qualityExportActions.ContainsKey(selected) &&
            _qualityExportAvailable.TryGetValue(selected, out var canExport) && canExport();
    }

    private async Task RefreshSelectedTabAsync()
    {
        var selected = _tabs.SelectedTab;
        if (_exportBusy || selected is null ||
            !_tabRefreshActions.TryGetValue(selected, out var refresh) ||
            (_tabRefreshAvailable.TryGetValue(selected, out var ready) && !ready()))
            return;
        await refresh();
    }

    private void ExportSelectedCodeQuality(string format)
    {
        var selected = _tabs.SelectedTab;
        if (_exportBusy || selected is null ||
            !_qualityExportActions.TryGetValue(selected, out var export) ||
            !_qualityExportAvailable.TryGetValue(selected, out var allowed) || !allowed())
            return;
        export(format);
    }

    private void AddTab(TabPage tab, DiagnosticExportSection section)
    {
        _tabs.TabPages.Add(tab);
        _sectionByTab[tab] = section;
    }

    private void AddLazyTab(string title, Func<TabPage> factory, DiagnosticExportSection section)
    {
        var placeholder = new TabPage(title);
        _tabs.TabPages.Add(placeholder);
        _sectionByTab[placeholder] = section;
        _lazyTabs[placeholder] = factory;
    }

    private void MaterializeSelectedTab()
    {
        if (_materializingTab) return;
        var placeholder = _tabs.SelectedTab;
        if (placeholder is null || !_lazyTabs.TryGetValue(placeholder, out var factory)) return;
        TabPage? created = null;
        var inserted = false;
        try
        {
            _materializingTab = true;
            created = factory();
            var index = _tabs.TabPages.IndexOf(placeholder);
            var section = _sectionByTab[placeholder];
            // Keep the actual tab alive: its event handlers, metrics timer and
            // asynchronous Code Quality loader reference this TabPage lifetime.
            // Moving only its controls and disposing it would cancel that loader.
            _tabs.TabPages.RemoveAt(index);
            _tabs.TabPages.Insert(index, created);
            inserted = true;
            _sectionByTab.Remove(placeholder);
            _sectionByTab[created] = section;
            _lazyTabs.Remove(placeholder);
            _tabs.SelectedTab = created;
            placeholder.Dispose();
        }
        catch (Exception ex)
        {
            InternalDiagnostics.Record(ex, "Load diagnostics tab: " + placeholder.Text);
            if (!inserted)
                created?.Dispose();
            // Retry remains available if construction failed before insertion.
            if (!placeholder.IsDisposed)
            {
                placeholder.Controls.Clear();
                placeholder.Controls.Add(new Label
                {
                    Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleCenter,
                    Text = "Unable to load this tab. Select another tab, then retry.\r\n" +
                        SecretRedactor.Redact(ex.Message)
                });
            }
        }
        finally { _materializingTab = false; }
    }

    private TabPage CreateHistoryTab()
    {
        var tab = new TabPage("Deployment History & Timeline");
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 4, Padding = new Padding(8) };
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 42));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 58));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        var projects = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 520 };
        var header = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, WrapContents = false };
        header.Controls.Add(new Label { Text = "Project", AutoSize = true, Padding = new Padding(0, 7, 8, 0) });
        header.Controls.Add(projects);
        root.Controls.Add(header, 0, 0);

        var summary = new TextBox { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Both, WordWrap = false, Dock = DockStyle.Fill, Font = new Font("Consolas", 9F), Text = "Loading deployment history..." };
        var grid = new DataGridView
        {
            Dock = DockStyle.Fill,
            ReadOnly = true,
            AllowUserToAddRows = false,
            AllowUserToDeleteRows = false,
            VirtualMode = true,
            AutoGenerateColumns = false,
            AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
            SelectionMode = DataGridViewSelectionMode.FullRowSelect,
            MultiSelect = false
        };
        grid.Columns.Add("Started", "Started");
        grid.Columns.Add("Result", "Result");
        grid.Columns.Add("Duration", "Duration");
        grid.Columns.Add("Mode", "Build mode");
        grid.Columns.Add("Version", "Version");
        grid.Columns.Add("Target", "Target");
        root.Controls.Add(summary, 0, 1);
        root.Controls.Add(grid, 0, 2);

        const int pageSize = 200;
        var records = Array.Empty<DeploymentHistoryRecord>();
        var page = 0;
        var generation = 0;
        var selectingProjects = false;
        var loadingProjects = false;
        CancellationTokenSource? historyRequest = null;
        CancellationTokenSource? catalogRequest = null;
        var previous = new Button { Text = "Previous", AutoSize = true, Enabled = false };
        var next = new Button { Text = "Next", AutoSize = true, Enabled = false };
        var pageInfo = new Label { AutoSize = true, ForeColor = Color.DimGray, Padding = new Padding(8, 7, 0, 0), Text = "Loading..." };
        var navigation = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, WrapContents = false };
        navigation.Controls.Add(previous);
        navigation.Controls.Add(next);
        navigation.Controls.Add(pageInfo);
        root.Controls.Add(navigation, 0, 3);

        foreach (DataGridViewColumn column in grid.Columns)
            column.SortMode = DataGridViewColumnSortMode.NotSortable;
        grid.CellValueNeeded += (_, e) =>
        {
            var index = page * pageSize + e.RowIndex;
            if (e.RowIndex < 0 || index < 0 || index >= records.Length) return;
            var a = records[index];
            e.Value = e.ColumnIndex switch
            {
                0 => a.StartedAtUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"),
                1 => a.EndedAtUtc.HasValue ? a.Result : "Interrupted",
                2 => a.EndedAtUtc.HasValue ? TimeSpan.FromSeconds(Math.Max(0, a.ElapsedSeconds)).ToString(@"hh\:mm\:ss") : "—",
                3 => a.BuildMode,
                4 => string.IsNullOrWhiteSpace(a.ApplicationVersion) ? "—" : a.ApplicationVersion,
                5 => a.DeploymentTargetPath,
                _ => null
            };
        };

        void RenderPage()
        {
            if (tab.IsDisposed) return;
            var pages = Math.Max(1, (records.Length + pageSize - 1) / pageSize);
            page = Math.Clamp(page, 0, pages - 1);
            grid.RowCount = Math.Min(pageSize, Math.Max(0, records.Length - page * pageSize));
            grid.ClearSelection();
            grid.Invalidate();
            previous.Enabled = page > 0;
            next.Enabled = page < pages - 1;
            pageInfo.Text = $"Page {page + 1}/{pages} — {records.Length} deployment(s)";
        }

        async Task LoadProjectAsync()
        {
            historyRequest?.Cancel();
            var requested = ++generation;
            if (projects.SelectedItem is not ProjectChoice choice)
            {
                records = Array.Empty<DeploymentHistoryRecord>();
                summary.Text = "No deployment history has been recorded yet.";
                RenderPage();
                return;
            }
            var request = new CancellationTokenSource();
            historyRequest = request;
            var id = choice.ProjectId;
            records = Array.Empty<DeploymentHistoryRecord>();
            page = 0;
            RenderPage();
            summary.Text = "Loading project history...";
            pageInfo.Text = "Loading...";
            try
            {
                var result = await Task.Run(() =>
                {
                    var s = _history.GetSummary(id, request.Token);
                    return (Text: s is null ? string.Empty : BuildSummaryText(s),
                        Records: s?.Attempts.OrderByDescending(x => x.StartedAtUtc).ToArray() ?? Array.Empty<DeploymentHistoryRecord>());
                }, request.Token);
                if (request.IsCancellationRequested || requested != generation || IsDisposed || Disposing || tab.IsDisposed ||
                    (projects.SelectedItem as ProjectChoice)?.ProjectId != id) return;
                records = result.Records;
                summary.Text = records.Length == 0 ? "No deployment history has been recorded yet." : result.Text;
                page = 0;
                RenderPage();
            }
            catch (OperationCanceledException) when (request.IsCancellationRequested) { }
            catch (Exception ex)
            {
                InternalDiagnostics.Record(ex, "Load diagnostic project history");
                if (requested != generation || IsDisposed || Disposing || tab.IsDisposed) return;
                records = Array.Empty<DeploymentHistoryRecord>();
                summary.Text = "Deployment history unavailable: " + SecretRedactor.Redact(ex.Message);
                RenderPage();
            }
            finally
            {
                if (ReferenceEquals(historyRequest, request)) historyRequest = null;
                request.Dispose();
            }
        }

        async Task LoadProjectsAsync()
        {
            if (loadingProjects) return;
            loadingProjects = true;
            projects.Enabled = false;
            historyRequest?.Cancel();
            var request = new CancellationTokenSource();
            catalogRequest = request;
            UpdateActionMenuState();
            var requested = ++generation;
            pageInfo.Text = "Loading projects...";
            var selectedId = (projects.SelectedItem as ProjectChoice)?.ProjectId;
            try
            {
                var preferred = selectedId ?? _history.GetProjectId(_profile);
                var summaries = await Task.Run(() => _history.GetProjectCatalog(request.Token), request.Token);
                if (requested != generation || IsDisposed || Disposing || tab.IsDisposed) return;
                selectingProjects = true;
                projects.BeginUpdate();
                try
                {
                    projects.Items.Clear();
                    foreach (var item in summaries)
                        projects.Items.Add(new ProjectChoice(item.ProjectId, DisplayProjectName(item)));
                    if (projects.Items.Count > 0)
                    {
                        var chosen = Enumerable.Range(0, projects.Items.Count)
                            .FirstOrDefault(i => (projects.Items[i] as ProjectChoice)?.ProjectId == preferred);
                        projects.SelectedIndex = chosen;
                    }
                }
                finally { projects.EndUpdate(); selectingProjects = false; }
                await LoadProjectAsync();
            }
            catch (OperationCanceledException) when (request.IsCancellationRequested) { }
            catch (Exception ex)
            {
                InternalDiagnostics.Record(ex, "Load diagnostics project list");
                if (requested == generation && !IsDisposed && !Disposing && !tab.IsDisposed)
                {
                    summary.Text = "Project list unavailable: " + SecretRedactor.Redact(ex.Message);
                    pageInfo.Text = "Unable to load projects";
                }
            }
            finally
            {
                loadingProjects = false;
                if (ReferenceEquals(catalogRequest, request)) catalogRequest = null;
                request.Dispose();
                if (!IsDisposed && !Disposing && !tab.IsDisposed)
                {
                    projects.Enabled = true;
                    UpdateActionMenuState();
                }
            }
        }

        projects.SelectedIndexChanged += async (_, _) =>
        {
            if (!selectingProjects) await LoadProjectAsync();
        };
        _tabRefreshActions[tab] = LoadProjectsAsync;
        _tabRefreshAvailable[tab] = () => !loadingProjects;
        previous.Click += (_, _) => { if (page > 0) { page--; RenderPage(); } };
        next.Click += (_, _) => { if ((page + 1) * pageSize < records.Length) { page++; RenderPage(); } };
        tab.Disposed += (_, _) =>
        {
            generation++;
            historyRequest?.Cancel();
            catalogRequest?.Cancel();
            _tabRefreshActions.Remove(tab);
            _tabRefreshAvailable.Remove(tab);
        };
        Shown += async (_, _) => { if (!tab.IsDisposed) await LoadProjectsAsync(); };
        tab.Controls.Add(root);
        return tab;
    }

    // A bounded preview is not the evidence file. Export and Open continue
    // to use the original, complete files. No disk I/O runs in tab construction.
    private const int MaxDiagnosticPreviewChars = 500_000;

    private static string ReadDiagnosticPreview(string path, bool tail)
    {
        if (!File.Exists(path)) return "No file is available.";
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        var offset = tail ? Math.Max(0L, stream.Length - MaxDiagnosticPreviewChars) : 0L;
        stream.Seek(offset, SeekOrigin.Begin);
        using var reader = new StreamReader(stream);
        var buffer = new char[MaxDiagnosticPreviewChars];
        var count = reader.ReadBlock(buffer, 0, buffer.Length);
        var more = reader.Peek() >= 0;
        var text = new string(buffer, 0, count);
        if (offset > 0) text = "[... latest log preview; export contains the complete log ...]\r\n" + text;
        if (more) text += "\r\n[... preview limit reached; open or export the complete original file ...]";
        return text;
    }

    private TabPage CreateLogTab()
    {
        var tab = new TabPage("Current Log");
        var box = new RichTextBox { Dock = DockStyle.Fill, ReadOnly = true, WordWrap = false, Font = new Font("Consolas", 9.5F) };
        tab.Controls.Add(box);
        var generation = 0;
        async Task LoadLogAsync()
        {
            var request = ++generation;
            box.Text = "Loading current log...";
            string text;
            try { text = await Task.Run(() => ReadDiagnosticPreview(_currentLog, tail: true)); }
            catch (Exception ex)
            {
                InternalDiagnostics.Record(ex, "Diagnostics current log tab");
                text = SecretRedactor.Redact(ex.Message);
            }
            if (tab.IsDisposed || box.IsDisposed || request != generation) return;
            box.Text = text;
            box.SelectionStart = box.TextLength;
            box.ScrollToCaret();
        }
        _tabRefreshActions[tab] = LoadLogAsync;
        _ = LoadLogAsync();
        return tab;
    }

    private TabPage CreateFilesTab(string title, string folder, string pattern)
    {
        var tab = new TabPage(title);
        var split = new SplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Vertical, SplitterDistance = 430 };
        var list = new ListBox { Dock = DockStyle.Fill, Font = new Font("Consolas", 9F) };
        var preview = new RichTextBox { Dock = DockStyle.Fill, ReadOnly = true, WordWrap = false, Font = new Font("Consolas", 9F) };
        var listGeneration = 0;
        var previewGeneration = 0;

        async Task LoadFilesAsync()
        {
            var request = ++listGeneration;
            var previous = list.SelectedItem as string;
            preview.Text = "Loading diagnostic files...";
            string[] files;
            try
            {
                files = await Task.Run(() => Directory.Exists(folder)
                    ? Directory.EnumerateFiles(folder, pattern).OrderByDescending(File.GetLastWriteTime).Take(500).ToArray()
                    : Array.Empty<string>());
            }
            catch (Exception ex)
            {
                InternalDiagnostics.Record(ex, "Diagnostics file list: " + folder);
                if (!tab.IsDisposed && !preview.IsDisposed && request == listGeneration)
                    preview.Text = SecretRedactor.Redact(ex.Message);
                return;
            }
            if (tab.IsDisposed || list.IsDisposed || preview.IsDisposed || request != listGeneration) return;
            ++previewGeneration;
            list.BeginUpdate();
            try
            {
                list.Items.Clear();
                list.Items.AddRange(files);
            }
            finally { list.EndUpdate(); }
            preview.Text = files.Length == 0 ? "No diagnostic files found." : "Select a file. Double-click to open the full original.";
            if (previous is not null && Array.IndexOf(files, previous) >= 0)
                list.SelectedItem = previous;
        }

        list.SelectedIndexChanged += async (_, _) =>
        {
            var request = ++previewGeneration;
            if (list.SelectedItem is not string file) return;
            preview.Text = "Loading preview...";
            string text;
            try { text = await Task.Run(() => ReadDiagnosticPreview(file, tail: false)); }
            catch (Exception ex)
            {
                InternalDiagnostics.Record(ex, "Diagnostics file preview: " + file);
                text = SecretRedactor.Redact(ex.Message);
            }
            if (tab.IsDisposed || preview.IsDisposed || list.IsDisposed || request != previewGeneration ||
                !string.Equals(list.SelectedItem as string, file, StringComparison.Ordinal)) return;
            preview.Text = text;
        };
        list.DoubleClick += (_, _) => { if (list.SelectedItem is string file) OpenTarget(file); };
        split.Panel1.Controls.Add(list);
        split.Panel2.Controls.Add(preview);
        tab.Controls.Add(split);
        _tabRefreshActions[tab] = LoadFilesAsync;
        _ = LoadFilesAsync();
        return tab;
    }

    private TabPage CreatePerformanceTab()
    {
        var tab = new TabPage("Build Performance");
        var box = new TextBox { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Both, WordWrap = false, Font = new Font("Consolas", 9F) };
        box.Text = _deployment.LastPerformanceSnapshot?.ToPlainText() ?? "No build performance snapshot is available in the current session. Historical stage timings remain available in Deployment History.";
        tab.Controls.Add(box);
        return tab;
    }

    private TabPage CreateRuntimeResourcesTab()
    {
        var tab = new TabPage("CPU / Memory");
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 3, Padding = new Padding(10) };
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        var current = new Label { AutoSize = true, Font = new Font("Segoe UI Semibold", 11F), Text = "Sampling..." };
        var history = new TextBox { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Both, WordWrap = false, Font = new Font("Consolas", 9F) };
        var note = new Label { AutoSize = true, ForeColor = Color.DimGray, MaximumSize = new Size(1000, 0), Text = "CPU/RAM is sampled by the main Wizard every 5 seconds. This tab displays the latest measured sample and never creates extra samples. History is bounded to 720 measurements; no forced GC runs." };
        root.Controls.Add(current, 0, 0); root.Controls.Add(history, 0, 1); root.Controls.Add(note, 0, 2);
        DateTimeOffset? lastRenderedAt = null;
        void Refresh(bool force = false)
        {
            if (tab.IsDisposed || !ReferenceEquals(_tabs.SelectedTab, tab)) return;
            var sample = _runtimeMetrics.GetLatestSample();
            if (sample is null)
            {
                if (force)
                {
                    current.Text = "Waiting for the first CPU/RAM measurement...";
                    history.Text = "No measurements recorded yet.";
                }
                return;
            }
            if (!force && lastRenderedAt == sample.At) return;

            lastRenderedAt = sample.At;
            current.Text = $"Measured {sample.At:HH:mm:ss} | {sample.ToDisplayText()}";
            var lines = _runtimeMetrics.GetRecentHistory(240)
                .Select(x => $"{x.At:HH:mm:ss} | {x.ToDisplayText()}");
            history.Text = string.Join(Environment.NewLine, lines);
            history.SelectionStart = history.TextLength;
            history.ScrollToCaret();
        }

        _runtimeResourcesTab = tab;
        _refreshRuntimeResources = () => Refresh(force: true);
        _metricsTimer.Tick += (_, _) =>
        {
            if (!tab.IsDisposed && ReferenceEquals(_tabs.SelectedTab, tab))
                Refresh();
        };
        tab.Disposed += (_, _) =>
        {
            if (!ReferenceEquals(_runtimeResourcesTab, tab)) return;
            _metricsTimer.Stop();
            _runtimeResourcesTab = null;
            _refreshRuntimeResources = null;
        };
        tab.Controls.Add(root);
        return tab;
    }

    private Task ExportCurrentTabAsync(bool toDesktop)
    {
        if (_exportBusy || _tabs.SelectedTab is null ||
            !_sectionByTab.TryGetValue(_tabs.SelectedTab, out var section))
            return Task.CompletedTask;
        return ExportSectionAsync(section, toDesktop);
    }

    private void SetExportBusy(bool busy)
    {
        _exportBusy = busy;
        foreach (var command in _exportCommands)
            if (!command.IsDisposed) command.Enabled = !busy;
        if (!_cancelExport.IsDisposed) _cancelExport.Enabled = busy;
        UpdateActionMenuState();
    }

    private async Task ExportSectionAsync(DiagnosticExportSection section, bool toDesktop)
    {
        if (_exportBusy || IsDisposed || Disposing) return;
        string destinationFile;
        try
        {
            // Path selection stays on the UI thread. No WinForms control is
            // accessed from the worker that creates and publishes the archive.
            if (toDesktop)
            {
                var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
                if (string.IsNullOrWhiteSpace(desktop) || !Directory.Exists(desktop))
                    desktop = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Desktop");
                Directory.CreateDirectory(desktop);
                destinationFile = Path.Combine(desktop, DiagnosticBundleService.GetSuggestedFileName(section));
            }
            else
            {
                using var dialog = new SaveFileDialog
                {
                    Title = section == DiagnosticExportSection.All ? "Save complete diagnostic bundle" : $"Save {section} diagnostic export",
                    Filter = "ZIP diagnostic package (*.zip)|*.zip|All files (*.*)|*.*",
                    DefaultExt = "zip",
                    AddExtension = true,
                    OverwritePrompt = true,
                    FileName = DiagnosticBundleService.GetSuggestedFileName(section),
                    InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
                    RestoreDirectory = true
                };
                if (dialog.ShowDialog(this) != DialogResult.OK) return;
                destinationFile = dialog.FileName;
            }
        }
        catch (Exception ex)
        {
            InternalDiagnostics.Record(ex, "Select diagnostic export destination");
            MessageBox.Show(this, SecretRedactor.Redact(ex.Message), "Diagnostic export failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }

        using var request = new CancellationTokenSource();
        var operation = ++_exportGeneration;
        _exportCts = request;
        SetExportBusy(true);
        _exportStatus.Text = "Preparing ZIP...";
        try
        {
            var performance = _deployment.LastPerformanceSnapshot;
            var progress = new Progress<string>(message =>
            {
                if (_exportBusy && operation == _exportGeneration && !IsDisposed && !Disposing)
                    _exportStatus.Text = message;
            });
            var path = await Task.Run(
                () => _bundle.ExportSectionToFile(destinationFile, section, _profile, _history, _currentLog,
                    performance, _runtimeMetrics, request.Token, progress), request.Token);
            if (operation == _exportGeneration && !IsDisposed && !Disposing)
            {
                _exportStatus.Text = "Diagnostic export completed.";
                ShowExportComplete(path);
            }
        }
        catch (OperationCanceledException) when (request.IsCancellationRequested)
        {
            if (operation == _exportGeneration && !IsDisposed && !Disposing)
                _exportStatus.Text = "Export cancelled. Previous ZIP was preserved.";
        }
        catch (Exception ex)
        {
            InternalDiagnostics.Record(ex, "Diagnostic export: " + section);
            if (operation == _exportGeneration && !IsDisposed && !Disposing)
            {
                _exportStatus.Text = "Export failed. Previous ZIP was preserved.";
                MessageBox.Show(this, SecretRedactor.Redact(ex.Message), "Diagnostic export failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }
        finally
        {
            if (ReferenceEquals(_exportCts, request)) _exportCts = null;
            if (operation == _exportGeneration && !IsDisposed && !Disposing)
                SetExportBusy(false);
        }
    }

    private void ShowExportComplete(string path)
    {
        using var dialog = new Form
        {
            Text = "Diagnostic export complete",
            Width = 720,
            Height = 210,
            StartPosition = FormStartPosition.CenterParent,
            FormBorderStyle = FormBorderStyle.FixedDialog,
            MaximizeBox = false,
            MinimizeBox = false,
            ShowInTaskbar = false,
            KeyPreview = true
        };
        dialog.KeyDown += (_, e) => { if (e.KeyCode == Keys.Escape) dialog.Close(); };
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 3, Padding = new Padding(14) };
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.Controls.Add(new Label { Text = "Diagnostic file created successfully:", AutoSize = true, Font = new Font("Segoe UI Semibold", 10F) }, 0, 0);
        var pathBox = new TextBox { Text = path, ReadOnly = true, Dock = DockStyle.Fill };
        root.Controls.Add(pathBox, 0, 1);
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, FlowDirection = FlowDirection.RightToLeft };
        var close = new Button { Text = "Close", Width = 100 };
        var openFolder = new Button { Text = "Open Folder", Width = 120 };
        var openFile = new Button { Text = "Open File", Width = 110 };
        close.Click += (_, _) => dialog.Close();
        openFolder.Click += (_, _) => OpenTarget(Path.GetDirectoryName(path) ?? path);
        openFile.Click += (_, _) => OpenTarget(path);
        buttons.Controls.Add(close); buttons.Controls.Add(openFolder); buttons.Controls.Add(openFile);
        root.Controls.Add(buttons, 0, 2);
        dialog.Controls.Add(root);
        dialog.AcceptButton = openFile;
        dialog.CancelButton = close;
        dialog.ShowDialog(this);
    }

    private static string BuildSummaryText(DeploymentProjectSummary s)
    {
        return string.Join(Environment.NewLine, new[]
        {
            $"PROJECT LIFETIME — {s.ApplicationName}",
            new string('=', 80),
            $"IIS Site                  : {Dash(s.SiteName)}",
            $"Database                  : {Dash(s.DatabaseName)}",
            $"Project ID                : {s.ProjectId}",
            $"Total attempts            : {s.TotalAttempts}",
            $"Successful / Failed       : {s.SuccessfulAttempts} / {s.FailedAttempts}",
            $"Cancelled / Interrupted   : {s.CancelledAttempts} / {s.IncompleteAttempts}",
            $"Distinct released versions: {s.DistinctVersions}",
            $"First deployment          : {FormatDate(s.FirstDeploymentUtc)}",
            $"Latest deployment         : {FormatDate(s.LatestDeploymentUtc)}",
            $"Calendar lifetime         : {FormatDuration(s.CalendarLifetime)}",
            $"Cumulative deploy runtime : {FormatDuration(s.CumulativeRuntime)}",
            $"Average deployment        : {FormatDuration(s.AverageRuntime)}",
            $"Average successful deploy : {FormatDuration(s.AverageSuccessfulRuntime)}",
            $"Fastest / Slowest         : {FormatDuration(s.FastestRuntime)} / {FormatDuration(s.SlowestRuntime)}",
            $"Fast / Full / Prepared    : {s.SmartDeployCount} / {s.FullDeployCount} / {s.PreparedDeployCount}"
        });
    }

    private static string DisplayProjectName(DeploymentProjectCatalogEntry s) => string.IsNullOrWhiteSpace(s.SiteName) ? s.ApplicationName : $"{s.ApplicationName} — {s.SiteName}";
    private static string Dash(string value) => string.IsNullOrWhiteSpace(value) ? "—" : value;
    private static string FormatDate(DateTime? utc) => utc.HasValue ? utc.Value.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss") : "—";
    private static string FormatDuration(TimeSpan duration) => duration.TotalDays >= 1 ? $"{duration.TotalDays:0.0} days" : duration.ToString(@"hh\:mm\:ss");

    private static void OpenTarget(string target)
    {
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo { FileName = target, UseShellExecute = true }); }
        catch (Exception ex) { InternalDiagnostics.Record(ex, "Open diagnostics target: " + target); }
    }

    private sealed record ProjectChoice(string ProjectId, string Display)
    {
        public override string ToString() => Display;
    }
}
