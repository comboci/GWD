using System.Runtime.CompilerServices;

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
    public static readonly Color Focus = Color.FromArgb(7, 52, 112);
    public static readonly Color DisabledSurface = Color.FromArgb(233, 238, 244);
    public static readonly Color DisabledInk = Color.FromArgb(86, 99, 119);
    public static readonly Color Success = Color.FromArgb(25, 103, 65);
    public static readonly Color Warning = Color.FromArgb(137, 78, 10);
    public static readonly Color Danger = Color.FromArgb(159, 36, 45);
    // Shared process-lifetime fonts, not newly allocated on each refresh.
    public static readonly Font Body = new("Segoe UI", 10F);
    public static readonly Font Heading = new("Segoe UI Semibold", 20F);
    public static readonly Font Section = new("Segoe UI Semibold", 11F);
    public static readonly Font Small = new("Segoe UI", 9F);
    public static readonly Font Mono = new("Consolas", 9F);
    public static readonly Font MonoReading = new("Consolas", 9.5F);
    public static readonly Font DialogHeading = new("Segoe UI Semibold", 13F);
    public static readonly Font BodyCompact = new("Segoe UI", 9.5F);
    public const int SmallSpace = 6;
    public const int MediumSpace = 12;
    public const int ComfortableButtonHeight = 34;
    private static readonly ConditionalWeakTable<Button, ButtonAppearance> ButtonStyles = new();

    private sealed class ButtonAppearance
    {
        internal bool Primary { get; set; }
        internal bool Destructive { get; set; }
    }

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
        var role = ButtonStyles.GetValue(button, RegisterButton);
        role.Primary = primary;
        role.Destructive = danger;
        UpdateButton(button, role);
    }

    private static ButtonAppearance RegisterButton(Button button)
    {
        var role = new ButtonAppearance
        {
            Destructive = button.ForeColor == Color.Firebrick || button.ForeColor == Color.DarkRed || button.ForeColor == Danger
        };
        button.GotFocus += (_, _) => UpdateButton(button, role);
        button.LostFocus += (_, _) => UpdateButton(button, role);
        button.EnabledChanged += (_, _) => UpdateButton(button, role);
        button.SystemColorsChanged += (_, _) => UpdateButton(button, role);
        return role;
    }

    private static void UpdateButton(Button button, ButtonAppearance role)
    {
        if (button.IsDisposed || button.Disposing) return;
        if (HighContrast)
        {
            button.UseVisualStyleBackColor = true;
            button.FlatStyle = FlatStyle.Standard;
            button.BackColor = SystemColors.Control;
            button.ForeColor = SystemColors.ControlText;
            return;
        }

        button.UseVisualStyleBackColor = false;
        button.FlatStyle = FlatStyle.Flat;
        button.FlatAppearance.BorderSize = button.Focused && button.Enabled ? 2 : 1;
        button.FlatAppearance.BorderColor = !button.Enabled ? Border : button.Focused ? Focus :
            role.Destructive ? Danger : role.Primary ? Accent : Border;
        button.FlatAppearance.MouseOverBackColor = role.Primary ? Color.FromArgb(20, 63, 119) : AccentSoft;
        button.BackColor = !button.Enabled ? DisabledSurface : role.Primary ? Accent : Surface;
        button.ForeColor = !button.Enabled ? DisabledInk : role.Primary ? Color.White : role.Destructive ? Danger : Ink;
    }

    internal enum Feedback { Loading, Empty, Error, Warning, Success, Info }

    internal static void ShowFeedback(Label label, Feedback state, string text)
    {
        label.Text = text;
        if (HighContrast) { label.ForeColor = SystemColors.ControlText; return; }
        label.ForeColor = state switch
        {
            Feedback.Error => Danger,
            Feedback.Warning => Warning,
            Feedback.Success => Success,
            Feedback.Loading => Accent,
            _ => Muted,
        };
    }

    public static void StyleTree(Control root)
    {
        if (root.IsDisposed) return;
        if (HighContrast)
        {
            if (root is Button nativeButton) UpdateButton(nativeButton, ButtonStyles.GetValue(nativeButton, RegisterButton));
            if (root is TextBoxBase text) { text.BackColor = SystemColors.Window; text.ForeColor = SystemColors.WindowText; }
            foreach (Control child in root.Controls) StyleTree(child);
            return;
        }
        switch (root)
        {
            case Button button:
                UpdateButton(button, ButtonStyles.GetValue(button, RegisterButton));
                break;
            case GroupBox group:
                group.BackColor = Surface;
                break;
            case DataGridView grid:
                grid.BackgroundColor = Surface;
                grid.GridColor = Border;
                grid.BorderStyle = BorderStyle.FixedSingle;
                grid.ColumnHeadersBorderStyle = DataGridViewHeaderBorderStyle.Single;
                grid.ColumnHeadersDefaultCellStyle.Font = Section;
                grid.ColumnHeadersDefaultCellStyle.Padding = new Padding(SmallSpace, 2, SmallSpace, 2);
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
