using GenericWeb.DeploymentManager.CodeQuality;
using GenericWeb.DeploymentManager.Services;

namespace GenericWeb.DeploymentManager;

public sealed partial class DiagnosticsCenterForm
{
    private TabPage CreateCodeQualityTab()
    {
        var tab = new TabPage("Code Quality");
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 4, Padding = new Padding(8) };
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 55));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 45));
        var bar = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true };
        var builds = new ComboBox { Width = 440, DropDownStyle = ComboBoxStyle.DropDownList };
        var counters = new Label { AutoSize = true, Padding = new Padding(4, 6, 4, 0) };
        bar.Controls.Add(counters);
        var refresh = new Button { Text = "Refresh", AutoSize = true };
        bar.Controls.Add(builds); bar.Controls.Add(refresh);
        var filters = new[] { "Project", "Tool", "Severity", "Code" }.Select(name => new TextBox { Width = 120, PlaceholderText = name + " filter" }).ToArray();
        foreach (var filter in filters) bar.Controls.Add(filter);
        var previous = new Button { Text = "Previous", AutoSize = true, Enabled = false };
        var next = new Button { Text = "Next", AutoSize = true, Enabled = false };
        var pageInfo = new Label { AutoSize = true, ForeColor = Color.DimGray, Padding = new Padding(4, 7, 4, 0) };
        bar.Controls.Add(previous); bar.Controls.Add(next); bar.Controls.Add(pageInfo);
        var summary = new Label { AutoSize = true, Dock = DockStyle.Fill, Padding = new Padding(0, 5, 0, 5), Text = "Loading Code Quality records..." };
        var grid = new DataGridView { Dock = DockStyle.Fill, ReadOnly = true, AllowUserToAddRows = false, AllowUserToDeleteRows = false,
            AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill, SelectionMode = DataGridViewSelectionMode.FullRowSelect, MultiSelect = false };
        foreach (var column in new[] { "Code", "Severity", "Category", "File", "Line", "Occurrences" }) grid.Columns.Add(column, column);
        var detail = new TextBox { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Both, WordWrap = false };
        BuildDiagnostics? Selected() => (builds.SelectedItem as QualityBuildChoice)?.Build;
        const int pageSize = 200;
        var page = 0;
        var generation = 0;
        var filtered = Array.Empty<Issue>();
        var selectingBuild = false;
        var filterDelay = new System.Windows.Forms.Timer { Interval = 250 };

        void RenderPage()
        {
            if (tab.IsDisposed) return;
            var pages = Math.Max(1, (filtered.Length + pageSize - 1) / pageSize);
            page = Math.Clamp(page, 0, pages - 1);
            grid.SuspendLayout();
            try
            {
                grid.Rows.Clear();
                detail.Clear();
                foreach (var issue in filtered.Skip(page * pageSize).Take(pageSize))
                {
                    var d = issue.Diagnostic;
                    var row = grid.Rows[grid.Rows.Add(d.Code, d.Severity, d.Category, d.File ?? "", d.Line?.ToString() ?? "", issue.Occurrences.Count)];
                    row.Tag = issue;
                }
            }
            finally { grid.ResumeLayout(); }
            previous.Enabled = page > 0;
            next.Enabled = page < pages - 1;
            pageInfo.Text = $"Page {page + 1}/{pages} — {filtered.Length} issue(s)";
        }

        void Filter()
        {
            var build = Selected();
            if (build is null) { filtered = Array.Empty<Issue>(); page = 0; RenderPage(); return; }
            var project = filters[0].Text.Trim();
            var tool = filters[1].Text.Trim();
            var severity = filters[2].Text.Trim();
            var code = filters[3].Text.Trim();
            bool Contains(string? value, string filter) => (value ?? "").Contains(filter, StringComparison.OrdinalIgnoreCase);
            filtered = build.Items.Where(i => Contains(build.ProjectId, project) &&
                i.Occurrences.Any(o => Contains(o.Tool, tool)) &&
                Contains(i.Diagnostic.Severity, severity) && Contains(i.Diagnostic.Code, code)).ToArray();
            page = 0;
            RenderPage();
        }

        async Task LoadBuildsAsync()
        {
            var requested = ++generation;
            var selectedId = Selected()?.BuildId;
            refresh.Enabled = false;
            summary.Text = "Loading Code Quality records...";
            try
            {
                var projectId = _history.GetProjectId(_profile);
                var all = await Task.Run(() => CodeQualitySession.Store(_appData).Read().ToArray());
                if (requested != generation || IsDisposed || Disposing || tab.IsDisposed) return;
                selectingBuild = true;
                builds.BeginUpdate();
                try
                {
                    builds.Items.Clear();
                    foreach (var build in all) builds.Items.Add(new QualityBuildChoice(build));
                    if (builds.Items.Count > 0)
                    {
                        var selection = Enumerable.Range(0, builds.Items.Count)
                            .FirstOrDefault(i => (builds.Items[i] as QualityBuildChoice)?.Build.BuildId == selectedId);
                        builds.SelectedIndex = selection;
                    }
                }
                finally { builds.EndUpdate(); selectingBuild = false; }
                var projectBuilds = all.Where(b => b.ProjectId == projectId).ToArray();
                var success = projectBuilds.FirstOrDefault(b => b.Result == "Successful");
                var failed = projectBuilds.FirstOrDefault(b => b.Result == "Failed");
                summary.Text = $"Current project: {projectId} | Last successful: {success?.Started.ToLocalTime().ToString("g") ?? "—"} | Last failed: {failed?.Started.ToLocalTime().ToString("g") ?? "—"}";
                if (all.Length == 0) summary.Text += " | No collected builds yet.";
                UpdateSelectedBuild();
            }
            catch (Exception ex)
            {
                InternalDiagnostics.Record(ex, "Code Quality UI");
                if (requested == generation && !IsDisposed && !Disposing && !tab.IsDisposed)
                    summary.Text = "Code Quality records unavailable: " + SecretRedactor.Redact(ex.Message);
            }
            finally
            {
                if (!IsDisposed && !Disposing && !tab.IsDisposed) refresh.Enabled = true;
            }
        }

        void UpdateSelectedBuild()
        {
            Filter();
            if (Selected() is not { } build) { counters.Text = ""; return; }
            counters.Text = $"Error: {build.Items.Count(i => i.Diagnostic.Severity == "Error")} | Warning: {build.Items.Count(i => i.Diagnostic.Severity == "Warning")} | Security: {build.Items.Count(i => i.Diagnostic.Category == "Security")} | Performance: {build.Items.Count(i => i.Diagnostic.Category == "Performance")}";
            detail.Text = $"Build {build.BuildId}\r\nAttempt {build.AttemptId}\r\nProject {build.ProjectId}\r\nCollection: {build.CollectionStatus}\r\n" +
                $"Error: {build.Items.Count(i => i.Diagnostic.Severity == "Error")} | Warning: {build.Items.Count(i => i.Diagnostic.Severity == "Warning")} | Security: {build.Items.Count(i => i.Diagnostic.Category == "Security")} | Performance: {build.Items.Count(i => i.Diagnostic.Category == "Performance")}\r\n\r\n" +
                string.Join("\r\n", build.StageCoverage.Select(s => $"{s.Status} | {s.Name} | exit {s.ExitCode} | {s.Started:u} → {s.Ended:u}"));
        }

        grid.SelectionChanged += (_, _) =>
        {
            if (grid.CurrentRow?.Tag is not Issue issue) return;
            var d = issue.Diagnostic;
            detail.Text = $"{d.Code} | Risk: {d.Risk} | {d.File}:{d.Line}\r\n{d.Message}\r\n\r\nSuggested fix: {d.Remedy}\r\n\r\n" +
                string.Join("\r\n", issue.Occurrences.Select(o => $"{o.At:u} | stage {o.StageId} | {o.Tool} | {o.Stream}\r\n" + string.Join("\r\n", o.Context)));
        };
        builds.SelectedIndexChanged += (_, _) => { if (!selectingBuild) UpdateSelectedBuild(); };
        foreach (var filter in filters)
            filter.TextChanged += (_, _) => { filterDelay.Stop(); filterDelay.Start(); };
        filterDelay.Tick += (_, _) => { filterDelay.Stop(); Filter(); };
        previous.Click += (_, _) => { if (page > 0) { page--; RenderPage(); } };
        next.Click += (_, _) => { if ((page + 1) * pageSize < filtered.Length) { page++; RenderPage(); } };
        refresh.Click += async (_, _) => await LoadBuildsAsync();
        foreach (var format in new[] { "JSON", "Markdown", "Copy Development Handoff" })
        {
            var button = new Button { Text = format, AutoSize = true };
            button.Click += (_, _) =>
            {
                if (Selected() is not { } build) return;
                try
                {
                    if (format.StartsWith("Copy")) { Clipboard.SetText(SecretRedactor.Redact(DiagnosticExport.Handoff(build))); return; }
                    using var dialog = new SaveFileDialog { Filter = format == "JSON" ? "JSON|*.json" : "Markdown|*.md", FileName = "code-quality-" + build.BuildId, AddExtension = true };
                    if (dialog.ShowDialog(this) == DialogResult.OK) File.WriteAllText(dialog.FileName, SecretRedactor.Redact(format == "JSON" ? DiagnosticExport.Json(build) : DiagnosticExport.Markdown(build)));
                }
                catch (Exception ex) { InternalDiagnostics.Record(ex, "Code Quality export"); MessageBox.Show(this, SecretRedactor.Redact(ex.Message), "Export failed"); }
            };
            bar.Controls.Add(button);
        }
        tab.Disposed += (_, _) => { generation++; filterDelay.Stop(); filterDelay.Dispose(); };
        root.Controls.Add(bar, 0, 0); root.Controls.Add(summary, 0, 1); root.Controls.Add(grid, 0, 2); root.Controls.Add(detail, 0, 3);
        tab.Controls.Add(root);
        _ = LoadBuildsAsync(); // Starts asynchronously after selection, not in the Diagnostics constructor.
        return tab;
    }
    private sealed record QualityBuildChoice(BuildDiagnostics Build)
    {
        public override string ToString() => $"{Build.Started.ToLocalTime():g} | {Build.ProjectId} | {Build.Result} | {Build.BuildId[..8]}";
    }
}
