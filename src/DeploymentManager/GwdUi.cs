namespace GenericWeb.DeploymentManager;

/// <summary>Native WinForms light design tokens. Semantic colors, handlers,
/// data binding and enabled states are not replaced by recursive styling.</summary>
public static class GwdUi
{
    public static readonly Color Canvas = Color.FromArgb(243, 246, 250);
    public static readonly Color Surface = Color.White;
    public static readonly Color Ink = Color.FromArgb(28, 40, 58);
    public static readonly Color Muted = Color.FromArgb(75, 90, 110);
    public static readonly Color Border = Color.FromArgb(208, 217, 229);
    public static readonly Color Accent = Color.FromArgb(26, 78, 146);
    public static readonly Color AccentSoft = Color.FromArgb(226, 237, 251);
    public static readonly Color Success = Color.FromArgb(25, 103, 65);
    public static readonly Color Warning = Color.FromArgb(137, 78, 10);
    public static readonly Color Danger = Color.FromArgb(159, 36, 45);
    // Shared process-lifetime fonts, not newly allocated on each refresh.
    public static readonly Font Body = new("Segoe UI", 10F);
    public static readonly Font Heading = new("Segoe UI Semibold", 20F);
    public static readonly Font Section = new("Segoe UI Semibold", 11F);
    public static readonly Font Small = new("Segoe UI", 9F);
    public static bool HighContrast => SystemInformation.HighContrast;

    public static void ConfigureForm(Form form)
    {
        form.AutoScaleDimensions = new SizeF(96F, 96F);
        form.AutoScaleMode = AutoScaleMode.Dpi;
        form.Font = Body;
        UI.Controls.ResponsiveLayout.FitWorkingArea(form);
        if (!HighContrast) { form.BackColor = Canvas; form.ForeColor = Ink; }
    }

    public static void ButtonStyle(Button button, bool primary = false, bool danger = false)
    {
        if (HighContrast) return;
        button.UseVisualStyleBackColor = false;
        button.FlatStyle = FlatStyle.Flat;
        button.FlatAppearance.BorderSize = 1;
        button.FlatAppearance.BorderColor = danger ? Danger : primary ? Accent : Border;
        button.FlatAppearance.MouseOverBackColor = primary ? Color.FromArgb(20, 63, 119) : AccentSoft;
        button.BackColor = primary ? Accent : Surface;
        button.ForeColor = primary ? Color.White : danger ? Danger : Ink;
    }

    public static void StyleTree(Control root)
    {
        if (HighContrast || root.IsDisposed) return;
        switch (root)
        {
            case Button button:
                ButtonStyle(button, danger: button.ForeColor == Color.Firebrick || button.ForeColor == Color.DarkRed || button.ForeColor == Danger);
                break;
            case GroupBox group:
                group.BackColor = Surface;
                break;
            case DataGridView grid:
                grid.BackgroundColor = Surface;
                grid.GridColor = Border;
                grid.BorderStyle = BorderStyle.FixedSingle;
                grid.EnableHeadersVisualStyles = false;
                grid.ColumnHeadersDefaultCellStyle.BackColor = AccentSoft;
                grid.ColumnHeadersDefaultCellStyle.ForeColor = Ink;
                grid.DefaultCellStyle.BackColor = Surface;
                grid.DefaultCellStyle.ForeColor = Ink;
                grid.DefaultCellStyle.SelectionBackColor = Accent;
                grid.DefaultCellStyle.SelectionForeColor = Color.White;
                grid.AlternatingRowsDefaultCellStyle.BackColor = Color.FromArgb(247, 249, 252);
                break;
            case MenuStrip menu:
                menu.BackColor = Surface;
                menu.ForeColor = Ink;
                break;
            case TabPage tab:
                tab.BackColor = Surface;
                tab.UseVisualStyleBackColor = false;
                break;
            case LinkLabel link:
                link.LinkColor = Accent;
                break;
        }
        foreach (Control child in root.Controls) StyleTree(child);
    }
}
