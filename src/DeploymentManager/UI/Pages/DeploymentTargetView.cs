using static GenericWeb.DeploymentManager.UI.Controls.WizardLayout;

namespace GenericWeb.DeploymentManager.UI.Pages;

internal sealed class DeploymentTargetView : UserControl
{
    internal TextBox AppName { get; }
    internal TextBox SiteName { get; }
    internal TextBox DatabaseName { get; }
    internal ComboBox TargetMode { get; }
    internal Ipv4AddressControl Host { get; }
    internal TextBox RemoteUser { get; }
    internal TextBox RemotePassword { get; }
    internal NumericUpDown RemotePort { get; }
    internal CheckBox RemoteSsl { get; }
    internal CheckBox RememberRemote { get; }
    internal Button TestRemote { get; }
    internal Label RemoteState { get; }
    internal TextBox Publish { get; }
    internal Button BrowsePublish { get; }
    internal Label Detected { get; }
    internal Label Framework { get; }
    internal Label Node { get; }
    internal Label ModeHelp { get; }

    internal DeploymentTargetView(bool sourceAlreadySelected, string applicationName, string siteNameValue,
        string databaseNameValue, string targetModeValue, string targetHost, string remoteUserName,
        string remotePasswordValue, int remoteWinRmPort, bool remoteUseSsl, bool rememberCredential,
        string readyPublishFolder)
    {
        Dock = DockStyle.Top;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        TabStop = false;
        var form = FormGrid();
        AppName = new TextBox { Text = sourceAlreadySelected ? applicationName : string.Empty, Width = 210 };
        SiteName = new TextBox { Text = sourceAlreadySelected ? siteNameValue : string.Empty, Width = 210 };
        DatabaseName = new TextBox { Text = sourceAlreadySelected ? databaseNameValue : string.Empty, Width = 210 };
        TargetMode = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 175 };
        TargetMode.Items.AddRange(new object[] { "Local Server", "Remote IIS Server" });
        TargetMode.SelectedItem = targetModeValue;
        Host = new Ipv4AddressControl { Text = targetModeValue == "Remote IIS Server" ? targetHost : string.Empty };
        RemoteUser = new TextBox { Text = remoteUserName, Width = 180 };
        RemotePassword = new TextBox { Text = remotePasswordValue, Width = 170, UseSystemPasswordChar = true };
        RemotePort = new NumericUpDown { Minimum = 1, Maximum = 65535, Value = remoteWinRmPort, Width = 82 };
        RemoteSsl = new CheckBox { Text = "HTTPS / SSL", Checked = remoteUseSsl, AutoSize = true };
        RememberRemote = new CheckBox { Text = "Save securely", Checked = rememberCredential, AutoSize = true };
        TestRemote = new Button { Text = "Test / Bootstrap Remote", Width = 170 };
        RemoteState = new Label { AutoSize = true, ForeColor = Color.DimGray, MaximumSize = new Size(690,0), Text = "Remote readiness not tested." };
        Publish = new TextBox { Text = readyPublishFolder, Dock = DockStyle.Fill };
        BrowsePublish = new Button { Text = "Browse publish folder...", AutoSize = true };
        Detected = new Label { AutoSize = true, ForeColor = Color.DimGray, MaximumSize = new Size(650, 0) };
        Framework = new Label { AutoSize = true, ForeColor = Color.DimGray };
        Node = new Label { AutoSize = true, ForeColor = Color.DimGray };
        ModeHelp = new Label { AutoSize = true, ForeColor = Color.DimGray, MaximumSize = new Size(700, 0) };

        var identityRow = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, WrapContents = true };
        identityRow.Controls.AddRange(new Control[]
        {
            new Label { Text = "App", AutoSize = true, Padding = new Padding(0, 7, 4, 0) }, AppName,
            new Label { Text = "Site", AutoSize = true, Padding = new Padding(10, 7, 4, 0) }, SiteName,
            new Label { Text = "Database", AutoSize = true, Padding = new Padding(10, 7, 4, 0) }, DatabaseName
        });
        AddRow(form, "Identity", identityRow);
        AddRow(form, "Database behavior", new Label { AutoSize = true, ForeColor = Color.DimGray, MaximumSize = new Size(760, 0), Text = "Changing the database name connects the application to a different database; Deployment Manager does not delete or migrate data from the previous database automatically." });

        var targetRow = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, WrapContents = true };
        targetRow.Controls.AddRange(new Control[]
        {
            TargetMode,
            new Label { Text = "Host / IP", AutoSize = true, Padding = new Padding(10, 7, 3, 0) }, Host,
            new Label { Text = "User", AutoSize = true, Padding = new Padding(8, 7, 3, 0) }, RemoteUser,
            new Label { Text = "Password", AutoSize = true, Padding = new Padding(8, 7, 3, 0) }, RemotePassword,
            new Label { Text = "WinRM", AutoSize = true, Padding = new Padding(8, 7, 3, 0) }, RemotePort,
            RemoteSsl, RememberRemote, TestRemote
        });
        AddRow(form, "Deployment target", targetRow);
        AddRow(form, "Remote readiness", RemoteState);

        var publishRow = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 2 };
        publishRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); publishRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        publishRow.Controls.Add(Publish, 0, 0); publishRow.Controls.Add(BrowsePublish, 1, 0);
        AddRow(form, "Prepared publish folder", publishRow);
        AddRow(form, "Detected Web project", Detected);
        AddRow(form, "Target framework", Framework);
        AddRow(form, "Frontend", Node);
        AddRow(form, "Mode behavior", ModeHelp);
        Controls.Add(form);
    }
}
