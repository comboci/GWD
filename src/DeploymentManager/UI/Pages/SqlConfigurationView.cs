using static GenericWeb.DeploymentManager.UI.Controls.WizardLayout;

namespace GenericWeb.DeploymentManager.UI.Pages;

internal sealed class SqlConfigurationView : UserControl
{
    internal FlowLayoutPanel Body { get; }
    internal RadioButton ExistingRadio { get; }
    internal ComboBox Existing { get; }
    internal RadioButton InstallRadio { get; }
    internal TextBox Installer { get; }
    internal Button Browse { get; }
    internal TextBox InstanceName { get; }
    internal Button Install { get; }
    internal Button Rescan { get; }
    internal GroupBox DiagnosticsBox { get; }
    internal TextBox Diagnostics { get; }
    internal Button RefreshDiagnostics { get; }
    internal Button RepairConnectivity { get; }
    internal GroupBox AuthBox { get; }
    internal RadioButton WindowsAuth { get; }
    internal RadioButton SqlAuth { get; }
    internal TextBox User { get; }
    internal TextBox Password { get; }
    internal Button Generate { get; }
    internal Button Copy { get; }
    internal CheckBox ShowPassword { get; }
    internal Button ResetSa { get; }
    internal Button Emergency { get; }
    internal Button Test { get; }
    internal Label Verified { get; }
    internal GroupBox DatabaseBox { get; }
    internal TextBox DatabaseName { get; }
    internal Button VerifyDatabase { get; }
    internal Label DatabaseState { get; }

    internal SqlConfigurationView(object[] instances, int selectedIndex, string installerPath, bool useWindowsAuthentication,
        string userName, string sessionPassword, bool recentSql, string verificationDetail, string targetDatabaseName, bool databaseVerified)
    {
        Dock = DockStyle.Fill;
        TabStop = false;
        Body = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = true };

        ExistingRadio = new RadioButton { Text = "Use an existing SQL Server instance", AutoSize = true, Checked = instances.Length > 0 };
        Existing = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 700 };
        foreach (var item in instances) Existing.Items.Add(item);
        if (Existing.Items.Count > 0) Existing.SelectedIndex = Math.Max(0, selectedIndex);

        InstallRadio = new RadioButton { Text = "Install SQL Server Express", AutoSize = true, Checked = instances.Length == 0 };
        Installer = new TextBox { Text = installerPath, Width = 670, ReadOnly = true };
        Browse = new Button { Text = "Choose installer...", AutoSize = true };
        InstanceName = new TextBox { Text = "NSPOFFICE", Width = 220 };
        Install = new Button { Text = "Run SQL Server installer", AutoSize = true };
        Rescan = new Button { Text = "Re-scan SQL Server", AutoSize = true };

        Body.Controls.Add(ExistingRadio);
        Body.Controls.Add(Existing);
        Body.Controls.Add(new Label { Height = 8 });
        Body.Controls.Add(InstallRadio);
        Body.Controls.Add(Wrap(Installer, Browse));
        Body.Controls.Add(Wrap(new Label { Text = "New instance name", AutoSize = true, Padding = new Padding(0, 7, 8, 0) }, InstanceName));
        Body.Controls.Add(Wrap(Install, Rescan));
        Body.Controls.Add(new Label { Height = 10 });

        DiagnosticsBox = new GroupBox { Text = "Selected instance diagnostics", Width = 850, Height = 190, Padding = new Padding(12) };
        var diagnosticsPanel = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 2, ColumnCount = 1 };
        diagnosticsPanel.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        diagnosticsPanel.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
        Diagnostics = new TextBox
        {
            Multiline = true,
            ReadOnly = true,
            ScrollBars = ScrollBars.Vertical,
            Dock = DockStyle.Fill,
            Font = new Font("Consolas", 9F),
            Text = "Select an instance, then click Refresh diagnostics."
        };
        RefreshDiagnostics = new Button { Text = "Refresh diagnostics", AutoSize = true };
        RepairConnectivity = new Button { Text = "Repair local SQL connectivity", AutoSize = true };
        diagnosticsPanel.Controls.Add(Diagnostics, 0, 0);
        diagnosticsPanel.Controls.Add(Wrap(RefreshDiagnostics, RepairConnectivity), 0, 1);
        DiagnosticsBox.Controls.Add(diagnosticsPanel);
        Body.Controls.Add(DiagnosticsBox);
        Body.Controls.Add(new Label { Height = 8 });

        AuthBox = new GroupBox { Text = "Administrative connection", Width = 850, Height = 320, Padding = new Padding(14) };
        var authPanel = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = false };
        WindowsAuth = new RadioButton { Text = "Windows Authentication", AutoSize = true, Checked = useWindowsAuthentication };
        SqlAuth = new RadioButton { Text = "SQL Authentication", AutoSize = true, Checked = !useWindowsAuthentication };
        User = new TextBox { Text = userName, Width = 170, Enabled = true };
        var passwordWasSuggested = false;
        Password = new TextBox { Text = sessionPassword, UseSystemPasswordChar = !passwordWasSuggested, Width = 260, Enabled = true };
        Generate = new Button { Text = "Generate strong password", AutoSize = true, Enabled = true };
        Copy = new Button { Text = "Copy password", AutoSize = true, Enabled = true };
        ShowPassword = new CheckBox { Text = "Show password", AutoSize = true, Enabled = true, Checked = passwordWasSuggested };
        ResetSa = new Button { Text = "Configure / Reset sa", AutoSize = true, Enabled = Existing.Items.Count > 0 };
        Emergency = new Button { Text = "Emergency recover SQL access", AutoSize = true, Enabled = Existing.Items.Count > 0 };
        Test = new Button { Text = "Test administrative connection", AutoSize = true };
        Verified = new Label { Text = verificationDetail, AutoSize = true, ForeColor = recentSql ? Color.ForestGreen : Color.Firebrick, Padding = new Padding(8, 7, 0, 0) };

        authPanel.Controls.Add(WindowsAuth);
        authPanel.Controls.Add(SqlAuth);
        authPanel.Controls.Add(new Label
        {
            Text = "SQL user/password remain editable in both modes. Normal SA reset requires Windows Authentication to already be SQL sysadmin; emergency recovery is for a local instance when that SQL sysadmin access has been lost.",
            AutoSize = true,
            MaximumSize = new Size(790, 0),
            ForeColor = Color.DimGray
        });
        authPanel.Controls.Add(Wrap(
            new Label { Text = "SQL user", AutoSize = true, Padding = new Padding(0, 7, 8, 0) }, User,
            new Label { Text = "Password", AutoSize = true, Padding = new Padding(12, 7, 8, 0) }, Password,
            ShowPassword, Generate, Copy));
        authPanel.Controls.Add(new Label { Text = "Enter an existing SQL credential, or generate a password and apply Configure / Reset sa. Passwords are session-only and excluded from profiles and diagnostics.", AutoSize = true, ForeColor = Color.DimGray, MaximumSize = new Size(790, 0) });
        authPanel.Controls.Add(Wrap(ResetSa, Emergency));
        authPanel.Controls.Add(Wrap(Test, Verified));
        AuthBox.Controls.Add(authPanel);
        Body.Controls.Add(AuthBox);
        DatabaseBox = new GroupBox { Text = "Application database", Width = 850, Height = 105, Padding = new Padding(14) };
        var databasePanel = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false };
        DatabaseName = new TextBox { Text = targetDatabaseName, Width = 250 };
        VerifyDatabase = new Button { Text = "Verify database", AutoSize = true };
        DatabaseState = new Label { Text = databaseVerified ? "✓ SQL preflight verified" : "Auto verification pending", AutoSize = true, ForeColor = databaseVerified ? Color.ForestGreen : Color.DimGray, Padding = new Padding(10,7,0,0) };
        databasePanel.Controls.Add(new Label { Text = "Database name", AutoSize = true, Padding = new Padding(0,7,8,0) });
        databasePanel.Controls.Add(DatabaseName); databasePanel.Controls.Add(VerifyDatabase); databasePanel.Controls.Add(DatabaseState);
        DatabaseBox.Controls.Add(databasePanel); Body.Controls.Add(DatabaseBox);
        Body.Controls.Add(new Label
        {
            AutoSize = true,
            MaximumSize = new Size(820, 0),
            ForeColor = Color.DimGray,
            Text = "Emergency recovery temporarily restarts the selected local SQL Server service in restricted single-user mode, grants the current Windows account SQL sysadmin, enables Mixed Mode/sa, then returns SQL Server to normal multi-user mode. Close SSMS and other SQL tools before using it."
        });
        Controls.Add(Body);
    }
}
