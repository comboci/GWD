using static GenericWeb.DeploymentManager.UI.Controls.WizardLayout;

namespace GenericWeb.DeploymentManager.UI.Pages;

internal readonly record struct LocationChoice(string Folder, string Layout, bool UseDefault, bool CleanupOlder);

internal sealed class DeploymentLocationView : UserControl
{
    private const string DefaultRoot = @"C:\inetpub\wwwroot";
    private readonly CheckBox _useDefault;
    private readonly TextBox _hostingRoot;
    private readonly RadioButton _single;
    private readonly RadioButton _versioned;
    private readonly CheckBox _cleanup;
    private readonly Button _browse;
    private readonly string _fallbackRoot;
    private string _lastCustomRoot;
    private bool _applyingMode;
    private Action<LocationChoice>? _changed;
    private Action? _flush;

    internal DeploymentLocationView(string application, string folder, string layout, bool defaultSite, bool cleanup, string fallbackRoot)
    {
        Dock = DockStyle.Top;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        TabStop = false;
        _fallbackRoot = fallbackRoot;
        _lastCustomRoot = IsDefaultRoot(folder) ? fallbackRoot : folder;
        _useDefault = new CheckBox { Text = @"Use IIS default web root (C:\inetpub\wwwroot) and port 80 — replace the existing IIS site/content on port 80",
            AutoSize = true, Checked = defaultSite, ForeColor = GwdUi.HighContrast ? SystemColors.ControlText : Color.DarkRed };
        _hostingRoot = new TextBox { Text = folder, Dock = DockStyle.Fill, MinimumSize = new Size(650, 38), Font = GwdUi.Body };
        _browse = new Button { Text = "Browse...", Width = 120, Height = 38 };
        var single = string.Equals(layout, "Single Folder", StringComparison.OrdinalIgnoreCase);
        _single = new RadioButton { Text = "Single Folder — always deploy the active site into this exact folder", AutoSize = true, Checked = single };
        _versioned = new RadioButton { Text = "Versioned Folders — keep releases as subfolders under this root", AutoSize = true, Checked = !single };
        _cleanup = new CheckBox { Text = "After a successful versioned deployment, delete older release folders", AutoSize = true, Checked = cleanup };
        var hint = new Label { AutoSize = true, MaximumSize = new Size(720, 0), Text = @"Dedicated folder example: C:\inetpub\wwwroot\NSPOfficeAutomation
Default website mode: C:\inetpub\wwwroot, HTTP port 80. A different IIS site owning port 80 blocks deployment; reconfigure its binding explicitly first." };

        var form = FormGrid();
        AddRow(form, "Application", new TextBox { Text = application, Dock = DockStyle.Fill, ReadOnly = true });
        AddRow(form, "Default IIS website", _useDefault);
        AddRow(form, "Hosted site folder", Wrap(_hostingRoot, _browse));
        AddRow(form, "Deployment layout", VerticalWrap(_single, _versioned, _cleanup));
        AddRow(form, "", hint);
        Controls.Add(form);
        ApplyDefaultMode();
        _hostingRoot.TextChanged += (_, _) => PublishCurrent();
        _hostingRoot.Leave += (_, _) => _flush?.Invoke();
        _single.CheckedChanged += (_, _) => { if (_single.Checked) PublishCurrent(); };
        _versioned.CheckedChanged += (_, _) => { if (_versioned.Checked) PublishCurrent(); };
        _cleanup.CheckedChanged += (_, _) => PublishCurrent();
        _useDefault.CheckedChanged += (_, _) => { ApplyDefaultMode(); PublishCurrent(); };
        _browse.Click += (_, _) =>
        {
            if (_changed is null || IsDisposed || Disposing) return;
            using var dialog = new FolderBrowserDialog { Description = "Choose the folder where IIS site files should be hosted",
                UseDescriptionForTitle = true, SelectedPath = Directory.Exists(_hostingRoot.Text) ? _hostingRoot.Text : string.Empty, ShowNewFolderButton = true };
            if (dialog.ShowDialog(FindForm()) == DialogResult.OK && !IsDisposed && !Disposing && _changed is not null)
                _hostingRoot.Text = dialog.SelectedPath;
        };
    }

    internal void Activate(Action<LocationChoice> changed, Action flush)
    {
        if (IsDisposed || Disposing) return;
        _changed = changed;
        _flush = flush;
    }

    internal void Deactivate() { _changed = null; _flush = null; }

    internal void PublishCurrent()
    {
        if (_applyingMode || IsDisposed || Disposing || _changed is not { } changed) return;
        _cleanup.Enabled = !_useDefault.Checked && _versioned.Checked;
        changed(new LocationChoice(_hostingRoot.Text.Trim(), _useDefault.Checked || _single.Checked ? "Single Folder" : "Versioned Folders",
            _useDefault.Checked, _cleanup.Checked));
    }

    private void ApplyDefaultMode()
    {
        _applyingMode = true;
        try
        {
            if (_useDefault.Checked)
            {
                if (!IsDefaultRoot(_hostingRoot.Text)) _lastCustomRoot = _hostingRoot.Text.Trim();
                _hostingRoot.Text = DefaultRoot;
                _single.Checked = true;
            }
            else if (IsDefaultRoot(_hostingRoot.Text))
            {
                _hostingRoot.Text = string.IsNullOrWhiteSpace(_lastCustomRoot) ? _fallbackRoot : _lastCustomRoot;
            }
            _hostingRoot.ReadOnly = _useDefault.Checked;
            _browse.Enabled = !_useDefault.Checked;
            _single.Enabled = !_useDefault.Checked;
            _versioned.Enabled = !_useDefault.Checked;
            _cleanup.Enabled = !_useDefault.Checked && _versioned.Checked;
        }
        finally { _applyingMode = false; }
    }

    private static bool IsDefaultRoot(string value) => string.Equals(value.TrimEnd('\\'), DefaultRoot, StringComparison.OrdinalIgnoreCase);

    protected override void Dispose(bool disposing)
    {
        if (disposing) Deactivate();
        base.Dispose(disposing);
    }
}
