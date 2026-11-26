namespace GenericWeb.DeploymentManager.UI.Pages;

internal sealed record ReviewStageState(string BuildMode, string Summary, string Status,
    bool Backup, bool KeepPrevious, int KeepCount, bool OperationRunning,
    bool CanOpenTarget, string Activity, string Elapsed, Color ElapsedColor, string LogMode, double SplitRatio);

internal sealed record ReviewStageActions(Func<int, string> ChangeBuildMode,
    Action<bool?, bool?, int?> ChangeProtection, Action<string> ChangeLogMode,
    Action<double> TrackSplitRatio, Action OpenSite, Action OpenFolder);

internal sealed class ReviewStageView : UserControl
{
    private readonly Control _sharedLog;
    private readonly Label _activity;
    private readonly Label _elapsed;
    private ReviewStageActions? _actions;
    private readonly Color _defaultElapsedColor;

    internal ReviewStageView(ReviewStageState state, Control sharedLog)
    {
        _sharedLog = sharedLog;
        _defaultElapsedColor = GwdUi.HighContrast ? SystemColors.ControlText : Color.RoyalBlue;
        Dock = DockStyle.Fill;
        TabStop = false;
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 5 };
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        var top = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, WrapContents = true, Padding = new Padding(0, 0, 0, 8) };
        top.Controls.Add(new Label { Text = "Deployment mode:", AutoSize = true, Padding = new Padding(0, 8, 8, 0), Font = GwdUi.Section });
        var sourceMode = state.BuildMode is "Fast Local Test" or "Fast IIS Update" or "Full IIS Deploy";
        var mode = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 260, Enabled = sourceMode && !state.OperationRunning };
        if (sourceMode)
        {
            mode.Items.AddRange(new object[] { "Fast Local Test (Default)", "Fast IIS Update", "Full IIS Deploy" });
            mode.SelectedIndex = state.BuildMode == "Full IIS Deploy" ? 2 : state.BuildMode == "Fast IIS Update" ? 1 : 0;
        }
        else
        {
            mode.Items.Add(state.BuildMode);
            mode.SelectedIndex = 0;
        }
        var modeHelp = new Label { AutoSize = true, MaximumSize = new Size(720, 0), Padding = new Padding(12, 8, 0, 0),
            Text = sourceMode ? BuildModeHelp(mode.SelectedIndex) : "Prepared mode selected on the Source page is preserved; Review does not rewrite it." };
        top.Controls.Add(mode);
        top.Controls.Add(modeHelp);

        var protection = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, WrapContents = true, Padding = new Padding(8, 2, 8, 2) };
        var backup = new CheckBox { Text = "Create Backup Before Deployment", AutoSize = true, Checked = state.Backup, Enabled = !state.OperationRunning };
        var keep = new CheckBox { Text = "Keep Previous Deployment for Rollback", AutoSize = true, Checked = state.KeepPrevious, Enabled = !state.OperationRunning };
        var count = new NumericUpDown { Minimum = 1, Maximum = 20, Value = Math.Clamp(state.KeepCount, 1, 20), Width = 55, Enabled = keep.Checked && !state.OperationRunning };
        protection.Controls.Add(backup);
        protection.Controls.Add(keep);
        protection.Controls.Add(new Label { Text = "Keep count:", AutoSize = true, Padding = new Padding(8, 4, 0, 0) });
        protection.Controls.Add(count);
        protection.Controls.Add(new Label { Text = "Old releases are removed only after explicit confirmation in Deployment Cleanup.", AutoSize = true, MaximumSize = new Size(550, 0),
            ForeColor = GwdUi.HighContrast ? SystemColors.ControlText : Color.DarkOrange });
        top.Controls.Add(protection);
        root.Controls.Add(top, 0, 0);

        var recoveryWarning = state.Status.Contains("UNSUCCESSFUL", StringComparison.OrdinalIgnoreCase)
            || state.Status.Contains("RECOVERY FAILED", StringComparison.OrdinalIgnoreCase)
            || state.Status.Contains("INTERRUPTED", StringComparison.OrdinalIgnoreCase);
        root.Controls.Add(new Label { Text = state.Status, AutoSize = true, Padding = new Padding(8), Font = GwdUi.Section,
            ForeColor = GwdUi.HighContrast ? SystemColors.ControlText : recoveryWarning ? Color.Firebrick :
                state.Status.Contains("SUCCESSFUL", StringComparison.OrdinalIgnoreCase) ? Color.ForestGreen : Color.DimGray }, 0, 1);

        var split = new SplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Horizontal, FixedPanel = FixedPanel.None };
        var initialized = false;
        void InitializeSplit()
        {
            if (initialized || split.IsDisposed || !split.IsHandleCreated ||
                split.Height <= split.Panel1MinSize + split.Panel2MinSize + split.SplitterWidth) return;
            var height = split.Height - split.SplitterWidth;
            split.SplitterDistance = Math.Clamp((int)(height * state.SplitRatio), split.Panel1MinSize, height - split.Panel2MinSize);
            initialized = true;
        }
        split.Resize += (_, _) => InitializeSplit();
        split.HandleCreated += (_, _) => InitializeSplit();
        split.SplitterMoved += (_, _) =>
        {
            if (initialized && split.Height > split.SplitterWidth)
                _actions?.TrackSplitRatio(Math.Clamp(split.SplitterDistance / (double)(split.Height - split.SplitterWidth), 0.1, 0.9));
        };

        var summary = new TextBox { Text = state.Summary, Multiline = true, ReadOnly = true, Dock = DockStyle.Fill, ScrollBars = ScrollBars.Both, WordWrap = false };
        split.Panel1.Controls.Add(summary);
        split.Panel2.Controls.Add(sharedLog);
        root.Controls.Add(split, 0, 2);
        _activity = new Label { Text = state.Activity, Dock = DockStyle.Fill, AutoEllipsis = true, Height = 32,
            Padding = new Padding(4, 6, 4, 0), Font = GwdUi.Small };
        root.Controls.Add(_activity, 0, 3);

        var footer = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 2, RowCount = 1, Padding = new Padding(0, 8, 0, 0) };
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        var logActions = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.TopDown, WrapContents = false, Margin = new Padding(0) };
        var commands = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.LeftToRight, WrapContents = true, Margin = new Padding(12, 0, 0, 0) };
        var logHeader = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0) };
        var modeFilter = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 110, Enabled = !state.OperationRunning };
        modeFilter.Items.AddRange(new object[] { "Minimal", "Normal", "Verbose" });
        modeFilter.SelectedItem = state.LogMode;
        logHeader.Controls.Add(new Label { Text = "Log:", AutoSize = true, Padding = new Padding(0, 8, 6, 0) });
        logHeader.Controls.Add(modeFilter);
        logActions.Controls.Add(logHeader);
        var site = new LinkLabel { Text = "Open deployed site", AutoSize = true, Padding = new Padding(16, 6, 24, 6), Enabled = state.CanOpenTarget };
        var folder = new LinkLabel { Text = "Open hosted folder", AutoSize = true, Padding = new Padding(0, 6, 0, 6), Enabled = state.CanOpenTarget };
        logActions.Controls.Add(site);
        logActions.Controls.Add(folder);
        _elapsed = new Label { Text = state.Elapsed, AutoSize = true, Padding = new Padding(16, 6, 22, 0),
            ForeColor = GwdUi.HighContrast ? SystemColors.ControlText : state.ElapsedColor, Font = GwdUi.Section };
        commands.Controls.Add(_elapsed);
        footer.Controls.Add(logActions, 0, 0);
        footer.Controls.Add(commands, 1, 0);
        root.Controls.Add(footer, 0, 4);
        Controls.Add(root);

        mode.SelectedIndexChanged += (_, _) =>
        {
            if (!sourceMode || _actions is not { } actions) return;
            summary.Text = actions.ChangeBuildMode(mode.SelectedIndex);
            modeHelp.Text = BuildModeHelp(mode.SelectedIndex);
        };
        backup.CheckedChanged += (_, _) => _actions?.ChangeProtection(backup.Checked, null, null);
        keep.CheckedChanged += (_, _) =>
        {
            count.Enabled = keep.Checked && !state.OperationRunning;
            _actions?.ChangeProtection(null, keep.Checked, null);
        };
        count.ValueChanged += (_, _) => _actions?.ChangeProtection(null, null, (int)count.Value);
        modeFilter.SelectedIndexChanged += (_, _) =>
        {
            if (modeFilter.SelectedItem is string selected) _actions?.ChangeLogMode(selected);
        };
        site.LinkClicked += (_, _) => _actions?.OpenSite();
        folder.LinkClicked += (_, _) => _actions?.OpenFolder();
    }

    internal void Activate(ReviewStageActions actions)
    {
        if (!IsDisposed && !Disposing) _actions = actions;
    }

    internal void Deactivate() => _actions = null;

    internal void ShowActivity(string value)
    {
        if (!IsDisposed && !Disposing) _activity.Text = value;
    }

    internal void ShowElapsed(string value, Color? color = null)
    {
        if (IsDisposed || Disposing) return;
        _elapsed.Text = value;
        _elapsed.ForeColor = GwdUi.HighContrast ? SystemColors.ControlText : color ?? _defaultElapsedColor;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            Deactivate();
            if (!_sharedLog.IsDisposed && _sharedLog.Parent is { } parent && Contains(_sharedLog))
                parent.Controls.Remove(_sharedLog);
        }
        base.Dispose(disposing);
    }

    private static string BuildModeHelp(int index) => index switch
    {
        1 => "Safe IIS update: fresh frontend build (if required) and dotnet publish. Hosted-output reuse and fingerprint-based build skips are disabled; IIS activation and recovery protections still apply.",
        2 => "Complete Release publish, IIS activation, database preparation, health check, and rollback protection.",
        _ => "Incremental local compile only. No dotnet publish, IIS activation, hosted-file changes, or rollback preparation."
    };
}
