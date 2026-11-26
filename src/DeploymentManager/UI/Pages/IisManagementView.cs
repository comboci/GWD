using static GenericWeb.DeploymentManager.UI.Controls.StatusCardUi;
using static GenericWeb.DeploymentManager.UI.Controls.WizardLayout;

namespace GenericWeb.DeploymentManager.UI.Pages;

internal sealed class IisManagementView : UserControl
{
    internal Button Install { get; }
    internal Button RefreshButton { get; }
    internal CheckBox Advanced { get; }
    internal Button RecoveryReview { get; }
    internal TableLayoutPanel Management { get; }
    internal Button RefreshSites { get; }
    internal Button StartSites { get; }
    internal Button StopSites { get; }
    internal Button RemoveSites { get; }
    internal Label LastIisCheck { get; }
    internal DataGridView Grid { get; }
    internal Button StartPool { get; }
    internal Button StopPool { get; }
    internal Button RecyclePool { get; }
    internal Button DeletePool { get; }
    internal DataGridView PoolGrid { get; }
    internal NumericUpDown TargetPort { get; }
    internal TextBox TargetHost { get; }
    internal TextBox TargetHealth { get; }
    internal GroupBox Status { get; }

    internal IisManagementView(bool installed, string detail, bool defaultSite, int httpPort,
        string hostHeader, string healthPath, string hostingRoot, string siteName, string appPoolName)
    {
        Dock = DockStyle.Fill;
        TabStop = false;
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 3 };
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        Status = StatusCard("IIS status", detail, installed);
        root.Controls.Add(Status, 0, 0);
        var buttons = new FlowLayoutPanel { AutoSize = true, WrapContents = false };
        Install = new Button { Text = "Install / enable IIS", AutoSize = true, Enabled = !installed };
        RefreshButton = new Button { Text = "Re-check IIS", AutoSize = true };
        Advanced = new CheckBox { Text = "Advanced IIS Management", AutoSize = true, Margin = new Padding(20, 7, 3, 3), Enabled = installed };
        buttons.Controls.Add(Install); buttons.Controls.Add(RefreshButton); buttons.Controls.Add(Advanced);
        RecoveryReview = new Button { Text = "Review & Resolve Previous Deployment", AutoSize = true };
        buttons.Controls.Add(RecoveryReview);
        root.Controls.Add(buttons, 0, 1);

        Management = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 3, Visible = installed, Padding = new Padding(0, 10, 0, 0) };
        Management.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        Management.RowStyles.Add(new RowStyle(SizeType.Percent, 55));
        Management.RowStyles.Add(new RowStyle(SizeType.Percent, 45));
        var mgmtActions = new FlowLayoutPanel { AutoSize = true, WrapContents = false };
        RefreshSites = new Button { Text = "Refresh sites", AutoSize = true };
        StartSites = new Button { Text = "Start selected", AutoSize = true };
        StopSites = new Button { Text = "Stop selected", AutoSize = true };
        RemoveSites = new Button { Text = "Remove selected + cleanup files", AutoSize = true, ForeColor = Color.Firebrick };
        LastIisCheck = new Label { Text = "Last checked: —", AutoSize = true, ForeColor = Color.DimGray, Padding = new Padding(12,7,0,0) };
        mgmtActions.Controls.AddRange(new Control[] { RefreshSites, StartSites, StopSites, RemoveSites, LastIisCheck });
        Management.Controls.Add(mgmtActions, 0, 0);
        Grid = new DataGridView
        {
            Dock = DockStyle.Fill, ReadOnly = false, AllowUserToAddRows = false, AllowUserToDeleteRows = false,
            AutoGenerateColumns = false, RowHeadersVisible = false, SelectionMode = DataGridViewSelectionMode.FullRowSelect,
            MultiSelect = true, BackgroundColor = Color.White, BorderStyle = BorderStyle.FixedSingle
        };
        Grid.Columns.Add(new DataGridViewCheckBoxColumn { Name = "Pick", HeaderText = "", Width = 38 });
        Grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Site", HeaderText = "IIS Site", Width = 170, ReadOnly = true });
        Grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "State", HeaderText = "State", Width = 85, ReadOnly = true });
        Grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Bindings", HeaderText = "Bindings / Ports", Width = 190, ReadOnly = true });
        Grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Path", HeaderText = "Physical Path", AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill, ReadOnly = true });
        Grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Pool", HeaderText = "App Pool", Width = 155, ReadOnly = true });
        Management.Controls.Add(Grid, 0, 1);
        var poolPanel = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 2 };
        poolPanel.RowStyles.Add(new RowStyle(SizeType.AutoSize)); poolPanel.RowStyles.Add(new RowStyle(SizeType.Percent,100));
        var poolActions = new FlowLayoutPanel { AutoSize=true, WrapContents=false };
        StartPool =new Button{Text="Start pool",AutoSize=true}; StopPool =new Button{Text="Stop pool",AutoSize=true}; RecyclePool =new Button{Text="Recycle pool",AutoSize=true}; DeletePool =new Button{Text="Delete unused pool",AutoSize=true,ForeColor=Color.Firebrick};
        poolActions.Controls.AddRange(new Control[]{StartPool,StopPool,RecyclePool,DeletePool}); poolPanel.Controls.Add(poolActions,0,0);
        PoolGrid =new DataGridView{Dock=DockStyle.Fill,ReadOnly=true,AllowUserToAddRows=false,RowHeadersVisible=false,SelectionMode=DataGridViewSelectionMode.FullRowSelect,MultiSelect=false,AutoGenerateColumns=false};
        PoolGrid.Columns.Add(new DataGridViewTextBoxColumn{Name="PoolName",HeaderText="Application Pool",AutoSizeMode=DataGridViewAutoSizeColumnMode.Fill}); PoolGrid.Columns.Add(new DataGridViewTextBoxColumn{Name="PoolState",HeaderText="State",Width=120});
        poolPanel.Controls.Add(PoolGrid,0,1); Management.Controls.Add(poolPanel,0,2);
        // V66: IIS target configuration is unified into this page; the separate IIS Site step was removed.
        var targetSettings = new GroupBox { Text = "Target IIS configuration", Dock = DockStyle.Bottom, AutoSize = true, Padding = new Padding(10) };
        var targetForm = FormGrid();
        TargetPort = new NumericUpDown { Minimum = 1, Maximum = 65535, Value = defaultSite ? 80 : Math.Clamp(httpPort, 1, 65535), Width = 120, Enabled = !defaultSite };
        TargetHost = new TextBox { Text = hostHeader, Dock = DockStyle.Fill, Enabled = !defaultSite };
        TargetHealth = new TextBox { Text = healthPath, Dock = DockStyle.Fill };
        AddRow(targetForm, "Hosted site folder", new TextBox { Text = hostingRoot, Dock = DockStyle.Fill, ReadOnly = true });
        AddRow(targetForm, "IIS site name", new TextBox { Text = siteName, Dock = DockStyle.Fill, ReadOnly = true });
        AddRow(targetForm, "Application pool", new TextBox { Text = appPoolName, Dock = DockStyle.Fill, ReadOnly = true });
        AddRow(targetForm, "HTTP port", TargetPort); AddRow(targetForm, "Host header", TargetHost); AddRow(targetForm, "Health-check path", TargetHealth);
        targetSettings.Controls.Add(targetForm);
        Management.Controls.Add(targetSettings, 0, 3); Management.RowCount = 4; Management.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.Controls.Add(Management, 0, 2);
        Controls.Add(root);
    }
}
