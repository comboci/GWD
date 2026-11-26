namespace GenericWeb.DeploymentManager.UI.Controls;

internal static class WizardLayout
{
    internal static TableLayoutPanel FormGrid()
    {
        var grid = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, GrowStyle = TableLayoutPanelGrowStyle.AddRows, ColumnCount = 2, RowCount = 0, Padding = new Padding(10, 8, 10, 8) };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 210));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        grid.ClientSizeChanged += (_, _) =>
        {
            if (grid.ClientSize.Width <= 0) return;
            var width = grid.ClientSize.Width - grid.Padding.Horizontal;
            var caption = Math.Clamp(width / 3, ResponsiveLayout.Pixels(grid, 72), ResponsiveLayout.Pixels(grid, 210));
            if (grid.ColumnStyles[0].Width != caption) grid.ColumnStyles[0].Width = caption;
            foreach (var label in grid.Controls.OfType<Label>())
            {
                if (grid.GetColumn(label) != 0) continue;
                var limit = new Size(Math.Max(1, caption - label.Margin.Horizontal), 0);
                if (label.MaximumSize != limit) label.MaximumSize = limit;
            }
        };
        return grid;
    }

    internal static void AddRow(TableLayoutPanel grid, string label, Control control)
    {
        var row = grid.RowCount++;
        grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        grid.Controls.Add(new Label { Text = label, AccessibleName = label, AutoSize = true, MaximumSize = new Size(198, 0), Font = GwdUi.Section, Margin = new Padding(3, 4, 3, 8), Padding = new Padding(0, 4, 8, 0) }, 0, row);
        if (string.IsNullOrEmpty(control.AccessibleName)) control.AccessibleName = label;
        control.Margin = new Padding(3, 4, 3, 8);
        grid.Controls.Add(control, 1, row);
    }

    internal static FlowLayoutPanel VerticalWrap(params Control[] controls)
    {
        var panel = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false };
        panel.Controls.AddRange(controls);
        return panel;
    }

    internal static FlowLayoutPanel Wrap(params Control[] controls)
    {
        var panel = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, WrapContents = true };
        panel.Controls.AddRange(controls);
        return panel;
    }
}
