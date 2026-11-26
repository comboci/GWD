using static GenericWeb.DeploymentManager.UI.Controls.WizardLayout;

namespace GenericWeb.DeploymentManager.UI.Pages;

internal sealed class DeploymentSourceView : UserControl
{
    internal TextBox Source { get; }
    internal Button Browse { get; }
    internal CheckBox LockSource { get; }
    internal Button SaveProfile { get; }
    internal Button LoadProfile { get; }
    internal Button ExportSource { get; }
    internal Label DetectedRoot { get; }
    internal TextBox App { get; }
    internal TextBox SiteInput { get; }
    internal TextBox Database { get; }
    internal ComboBox BuildMode { get; }
    internal ComboBox DependencyMode { get; }
    internal Label ModeMeaning { get; }
    internal TextBox PreparedPublish { get; }
    internal Button BrowsePreparedPublish { get; }
    internal Label PreparedValidation { get; }
    internal TextBox Repository { get; }
    internal Button BrowseRepository { get; }
    internal TextBox Workspace { get; }
    internal Button BrowseWorkspace { get; }
    internal Button OfflineAction { get; }
    internal Button OfflineTest { get; }
    internal Label OfflineState { get; }
    internal ProgressBar DependencySyncProgress { get; }
    internal Label DependencySyncActivity { get; }
    internal Label SourceTestResult { get; }
    internal Button RecheckSource { get; }

    internal DeploymentSourceView(string sourceFolder, bool sourceLocked, string applicationName,
        string siteName, string databaseName, string selectedBuildMode, string selectedDependencyMode,
        string readyPublishFolder, string repositoryFolder, string workspaceRoot)
    {
        Dock = DockStyle.Top;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        TabStop = false;
        var form = FormGrid();
        Source = new TextBox { Text = sourceFolder, Dock = DockStyle.Fill, ReadOnly = true, TabStop = false, BackColor = SystemColors.Window };
        Browse = new Button { Text = "Browse source...", AutoSize = true };
        LockSource = new CheckBox { Text = sourceLocked ? "Source locked" : "Lock source", AutoSize = true, Checked = sourceLocked, Margin = new Padding(8, 7, 3, 3) };
        Browse.Enabled = !sourceLocked;
        SaveProfile = new Button { Text = "Save Profile", AutoSize = true };
        LoadProfile = new Button { Text = "Load Profile", AutoSize = true };
        ExportSource = new Button { Text = "Export Clean Source ZIP", AutoSize = true };
        // The quick actions and menu commands invoke the same live controls.
        // Parenting them also gives them normal focus and disposal lifetimes.
        foreach (var action in new Control[] { SaveProfile, LoadProfile, LockSource, ExportSource })
            action.Margin = new Padding(2, 1, 3, 3);
        var profileActions = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, WrapContents = true, FlowDirection = FlowDirection.LeftToRight };
        profileActions.Controls.AddRange(new Control[] { SaveProfile, LoadProfile, LockSource, ExportSource });
        var sourceRow = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 2 };
        sourceRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); sourceRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        sourceRow.Controls.Add(Source, 0, 0); sourceRow.Controls.Add(Browse, 1, 0);

        DetectedRoot = new Label { AutoSize = true, MaximumSize = new Size(850, 0), ForeColor = Color.DimGray };
        App = new TextBox { Text = applicationName, Width = 260 };
        SiteInput = new TextBox { Text = siteName, Width = 260 };
        Database = new TextBox { Text = databaseName, Width = 260 };
        BuildMode = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 300 };
        BuildMode.Items.AddRange(new object[] { "Fast Local Test", "Fast IIS Update", "Full IIS Deploy", "Visual Studio prepared", "Prepared IIS Publish" });
        BuildMode.SelectedItem = BuildMode.Items.Cast<object>().FirstOrDefault(x => string.Equals(x.ToString(), selectedBuildMode, StringComparison.OrdinalIgnoreCase)) ?? BuildMode.Items[0];
        DependencyMode = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 220 };
        DependencyMode.Items.AddRange(new object[] { "Offline Only", "Hybrid", "Online" });
        DependencyMode.SelectedItem = selectedDependencyMode;
        ModeMeaning = new Label { AutoSize = true, MaximumSize = new Size(800, 0), ForeColor = Color.DarkOrange };
        PreparedPublish = new TextBox { Text = readyPublishFolder, Dock = DockStyle.Fill };
        BrowsePreparedPublish = new Button { Text = "Browse prepared publish...", AutoSize = true };
        var preparedPublishRow = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 2 };
        preparedPublishRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); preparedPublishRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        preparedPublishRow.Controls.Add(PreparedPublish, 0, 0); preparedPublishRow.Controls.Add(BrowsePreparedPublish, 1, 0);
        PreparedValidation = new Label { AutoSize = true, MaximumSize = new Size(800, 0), ForeColor = Color.DimGray, Text = "Select Prepared IIS Publish mode to validate a prebuilt IIS artifact." };

        Repository = new TextBox { Text = repositoryFolder, Dock = DockStyle.Fill };
        BrowseRepository = new Button { Text = "Browse repository...", AutoSize = true };
        var repositoryRow = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 2 };
        repositoryRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); repositoryRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        repositoryRow.Controls.Add(Repository, 0, 0); repositoryRow.Controls.Add(BrowseRepository, 1, 0);

        Workspace = new TextBox { Text = workspaceRoot, Dock = DockStyle.Fill };
        BrowseWorkspace = new Button { Text = "Browse workspace...", AutoSize = true };
        var workspaceRow = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 2 };
        workspaceRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); workspaceRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        workspaceRow.Controls.Add(Workspace, 0, 0); workspaceRow.Controls.Add(BrowseWorkspace, 1, 0);

        OfflineAction = new Button { Text = "Sync Missing Online Once", AutoSize = true };
        OfflineTest = new Button { Text = "Test Offline Build", AutoSize = true };
        OfflineState = new Label { AutoSize = true, ForeColor = Color.DimGray, MaximumSize = new Size(800, 0), Text = "Dependency readiness has not been checked yet." };
        DependencySyncProgress = new ProgressBar { Width = 360, Height = 18, Style = ProgressBarStyle.Marquee, MarqueeAnimationSpeed = 28, Visible = false };
        DependencySyncActivity = new Label { AutoSize = true, ForeColor = Color.RoyalBlue, MaximumSize = new Size(800, 0), Visible = false, Text = "Checking dependencies..." };

        AddRow(form, "Profile actions", profileActions);
        AddRow(form, "Source folder", sourceRow);

        AddRow(form, "Detected source root", DetectedRoot);
        AddRow(form, "Application name", App);
        AddRow(form, "IIS site name", SiteInput);
        AddRow(form, "Database name", Database);
        AddRow(form, "Build / deployment mode", BuildMode);
        AddRow(form, "Dependency mode", DependencyMode);
        AddRow(form, "Selected behavior", ModeMeaning);
        AddRow(form, "Prepared publish folder", preparedPublishRow);
        AddRow(form, "Prepared publish validation", PreparedValidation);
        AddRow(form, "Shared Dependency Repository", repositoryRow);
        AddRow(form, "Repository behavior", new Label { AutoSize = true, ForeColor = Color.DimGray, MaximumSize = new Size(800, 0), Text = "Persistent across source versions. NuGet/npm/installers/offline packages live here; the incoming source folder is never used as the dependency repository." });
        AddRow(form, "Stable Build Workspace", workspaceRow);
        AddRow(form, "Workspace behavior", new Label { AutoSize = true, ForeColor = Color.DimGray, MaximumSize = new Size(800, 0), Text = "Persistent build working area. Versioned incoming sources may be deleted after deployment without losing reusable dependency/build state." });
        AddRow(form, "Dependency actions", Wrap(OfflineAction, OfflineTest));
        AddRow(form, "Dependency status", OfflineState);
        AddRow(form, "Dependency sync", Wrap(DependencySyncProgress, DependencySyncActivity));
        SourceTestResult = new Label { AutoSize = true, MaximumSize = new Size(800, 0), ForeColor = Color.DimGray, Text = "Automatic source preflight starts as soon as a valid source folder is selected." };
        RecheckSource = new Button { Text = "Re-check source / publish", AutoSize = true };
        var preflightPanel = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, FlowDirection = FlowDirection.TopDown, WrapContents = false };
        preflightPanel.Controls.Add(RecheckSource);
        preflightPanel.Controls.Add(SourceTestResult);
        AddRow(form, "Automatic source preflight", preflightPanel);
        Controls.Add(form);
    }
}
