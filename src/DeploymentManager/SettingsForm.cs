using GenericWeb.DeploymentManager.Services;

namespace GenericWeb.DeploymentManager;

public sealed class SettingsForm : Form
{
    private readonly AppDataService _appData;
    private readonly DeploymentManagerSettings _settings;
    private readonly (string Release, string Repository, string Workspace) _initial;
    private readonly TextBox _releaseRoot = new() { Dock = DockStyle.Fill };
    private readonly TextBox _repository = new() { Dock = DockStyle.Fill };
    private readonly TextBox _workspace = new() { Dock = DockStyle.Fill };

    public SettingsForm(AppDataService appData)
    {
        GwdUi.ConfigureForm(this);
        _appData = appData;
        Text = $"Deployment Manager Settings — {ProductInfo.DisplayVersion}";
        Width = 900;
        Height = 460;
        MinimumSize = new Size(560, 330);
        StartPosition = FormStartPosition.CenterParent;
        MinimizeBox = false;
        MaximizeBox = false;

        var status = appData.GetSettingsAccessStatus();
        _settings = appData.GetSettings();
        _releaseRoot.Text = string.IsNullOrWhiteSpace(_settings.ManagerReleaseRoot) ? appData.GetManagerReleaseRoot() : _settings.ManagerReleaseRoot;
        _repository.Text = appData.GetDeploymentRepositoryFolder();
        _workspace.Text = appData.GetWorkspaceRoot();
        _initial = (_releaseRoot.Text, _repository.Text, _workspace.Text);

        var root = new TableLayoutPanel { Dock = DockStyle.Fill, AutoScroll = true, Padding = new Padding(18), ColumnCount = 1, RowCount = 3 };
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.Controls.Add(new Label
        {
            Text = "Paths below are persisted in settings.json. Build scripts read the Manager release/output folder, so no G:\\... path is hard-coded.",
            AutoSize = true,
            MaximumSize = new Size(820, 0),
            ForeColor = Color.DimGray
        }, 0, 0);

        var grid = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 3, Padding = new Padding(0, 14, 0, 0) };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 220));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        AddPathRow(grid, "Manager release/output folder", _releaseRoot, BrowseContext.ReleaseOutput);
        AddPathRow(grid, "Shared dependency / deployment repository", _repository, BrowseContext.Repository);
        AddPathRow(grid, "Stable build workspace", _workspace, BrowseContext.Workspace);
        root.Controls.Add(grid, 0, 1);
        grid.Enabled = status.Warning is null && !_settings.IsReadOnlyFallback;

        var buttons = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft };
        var save = new Button { Text = "Save", Width = 110, Height = 36 };
        var cancel = new Button { Text = "Cancel", Width = 110, Height = 36 };
        save.Enabled = grid.Enabled;
        save.Click += (_, _) => SaveAndClose();
        if (!grid.Enabled)
        {
            var recovery = new Button { Text = "Preserve and reset…", AutoSize = true, Enabled = status.CanReset };
            recovery.Click += (_, _) => RecoverSettings();
            buttons.Controls.Add(recovery);
            grid.Visible = false;
            root.Controls.Add(new Label
            {
                Text = status.Warning ?? "Settings changed while opening. Close and reopen to reload.",
                AutoSize = true,
                MaximumSize = new Size(760, 0)
            }, 0, 1);
        }
        cancel.Click += (_, _) => { DialogResult = DialogResult.Cancel; Close(); };
        CancelButton = cancel; // Esc closes this modal.
        buttons.Controls.Add(save);
        buttons.Controls.Add(cancel);
        root.Controls.Add(buttons, 0, 2);
        Controls.Add(root);
        GwdUi.StyleTree(this);
        GwdUi.ButtonStyle(save, primary: true);
    }

    private void AddPathRow(TableLayoutPanel grid, string label, TextBox text, BrowseContext context)
    {
        var row = grid.RowCount++;
        grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        grid.Controls.Add(new Label { Text = label, AutoSize = true, Padding = new Padding(0, 8, 8, 0) }, 0, row);
        text.Margin = new Padding(3, 4, 3, 8);
        grid.Controls.Add(text, 1, row);
        var browse = new Button { Text = "Browse...", AutoSize = true, Margin = new Padding(6, 3, 3, 8) };
        browse.Click += (_, _) =>
        {
            using var dialog = new FolderBrowserDialog
            {
                Description = label,
                SelectedPath = AppDataService.ResolveExistingFolder(text.Text) is { Length: > 0 } p ? p : _appData.GetLastBrowseFolder(context)
            };
            if (dialog.ShowDialog(this) != DialogResult.OK) return;
            text.Text = dialog.SelectedPath;
            // Only a successful Save persists changes from this dialog.
        };
        grid.Controls.Add(browse, 2, row);
    }

    private void SaveAndClose()
    {
        try
        {
            string? Changed(string current, string initial) =>
                !string.IsNullOrWhiteSpace(current) && !string.Equals(current.Trim(), initial, StringComparison.Ordinal)
                    ? Path.GetFullPath(current.Trim()) : null;
            var release = Changed(_releaseRoot.Text, _initial.Release);
            var repository = Changed(_repository.Text, _initial.Repository);
            var workspace = Changed(_workspace.Text, _initial.Workspace);
            if (release is not null || repository is not null || workspace is not null)
                _appData.SaveManagerPaths(_settings.Revision, release, repository, workspace);
            DialogResult = DialogResult.OK;
            Close();
        }
        catch (Exception ex)
        {
            InternalDiagnostics.Record(ex, "Settings form save");
            MessageBox.Show(this, ex.Message, "Could not save settings", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void RecoverSettings()
    {
        if (MessageBox.Show(this,
            "Create a new settings.json and preserve the existing malformed file unchanged in a separate backup? Remembered remote credentials will not be imported. Registry settings are not changed.",
            "Confirm settings recovery", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.Yes)
            return;
        try
        {
            var backup = _appData.PreserveAndResetCorruptSettings();
            MessageBox.Show(this, "Original preserved as " + Path.GetFileName(backup) + ". Reopen Settings to configure the new file.",
                "Recovery complete", MessageBoxButtons.OK, MessageBoxIcon.Information);
            DialogResult = DialogResult.Cancel;
            Close();
        }
        catch (Exception ex)
        {
            InternalDiagnostics.Record(new IOException("Settings recovery failed (" + ex.GetType().Name + ")."), "Settings recovery");
            MessageBox.Show(this, "Recovery did not complete. Do not remove the old file. Reopen Settings to check its current status.",
                "Recovery not completed", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }
}
