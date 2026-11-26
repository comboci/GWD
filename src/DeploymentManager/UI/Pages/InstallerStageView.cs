using static GenericWeb.DeploymentManager.UI.Controls.WizardLayout;

namespace GenericWeb.DeploymentManager.UI.Pages;

internal sealed class InstallerStageView : UserControl
{
    internal TextBox CacheText { get; }
    internal Button BrowseCache { get; }
    internal Button OpenCache { get; }
    internal ComboBox SqlVersion { get; }
    internal Button DownloadAll { get; }
    internal FlowLayoutPanel List { get; }

    internal InstallerStageView(string installerCache, object[] compatibleSqlVersions, string selectedSqlVersion,
        bool sqlInstalled, string installedSummary)
    {
        Dock = DockStyle.Fill;
        TabStop = false;
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 3 };
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        var cacheRow = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, WrapContents = false };
        CacheText = new TextBox { Text = installerCache, Width = 570 };
        BrowseCache = new Button { Text = "Choose installer folder...", AutoSize = true };
        OpenCache = new Button { Text = "Open folder", AutoSize = true };
        cacheRow.Controls.Add(new Label { Text = "Installer cache", AutoSize = true, Padding = new Padding(0, 7, 8, 0) });
        cacheRow.Controls.Add(CacheText); cacheRow.Controls.Add(BrowseCache); cacheRow.Controls.Add(OpenCache);
        root.Controls.Add(cacheRow, 0, 0);

        var versionRow = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, WrapContents = false, Padding = new Padding(0, 8, 0, 4) };
        SqlVersion = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 250 };
        SqlVersion.Items.AddRange(compatibleSqlVersions);
        var currentIndex = SqlVersion.Items.IndexOf(selectedSqlVersion);
        SqlVersion.SelectedIndex = currentIndex >= 0 ? currentIndex : 0;
        SqlVersion.Enabled = !sqlInstalled;
        versionRow.Controls.Add(new Label { Text = "SQL Express package", AutoSize = true, Padding = new Padding(0, 7, 8, 0) });
        versionRow.Controls.Add(SqlVersion);
        versionRow.Controls.Add(new Label
        {
            Text = sqlInstalled ? "Already Installed — " + installedSummary + "; SQL download is skipped." : "Not installed — select the package to download.",
            AutoSize = true,
            ForeColor = sqlInstalled ? Color.ForestGreen : Color.DimGray,
            Padding = new Padding(10, 7, 0, 0)
        });
        DownloadAll = new Button { Text = "Download all missing installers", AutoSize = true, Margin = new Padding(18, 3, 3, 3) };
        versionRow.Controls.Add(DownloadAll);
        root.Controls.Add(versionRow, 0, 1);

        List = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = true };
        List.AutoScroll = true;
        root.Controls.Add(List, 0, 2);
        Controls.Add(root);
    }
}
