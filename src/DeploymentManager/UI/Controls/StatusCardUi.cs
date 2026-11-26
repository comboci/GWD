namespace GenericWeb.DeploymentManager.UI.Controls;

internal static class StatusCardUi
{
    internal static GroupBox StatusCard(string title, string detail, bool ok)
    {
        var box = new GroupBox { Text = title, Dock = DockStyle.Fill, Margin = new Padding(7), Padding = new Padding(12) };
        var status = new Label { Name = "StatusText", Text = ok ? "● Ready" : "● Action required", AutoSize = true, ForeColor = GwdUi.HighContrast ? SystemColors.ControlText : ok ? GwdUi.Success : GwdUi.Danger, Font = GwdUi.Section };
        var detailLabel = new Label { Name = "StatusDetail", Text = detail, AutoSize = false, Dock = DockStyle.Fill, Padding = new Padding(0, 26, 0, 0), ForeColor = Color.FromArgb(55, 55, 55) };
        box.Controls.Add(detailLabel); box.Controls.Add(status);
        return box;
    }

    internal static void UpdateStatusCard(GroupBox card, string title, string detail, bool ok)
    {
        if (card.IsDisposed) return;
        card.Text = title;
        if (card.Controls["StatusText"] is Label status)
        {
            status.Text = ok ? "● Ready" : "● Action required";
            status.ForeColor = GwdUi.HighContrast ? SystemColors.ControlText : ok ? GwdUi.Success : GwdUi.Danger;
        }
        if (card.Controls["StatusDetail"] is Label description) description.Text = detail;
    }
}
