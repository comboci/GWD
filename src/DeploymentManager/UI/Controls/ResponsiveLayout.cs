namespace GenericWeb.DeploymentManager.UI.Controls;

internal static class ResponsiveLayout
{
    internal static int Pixels(Control owner, int logical) =>
        (int)Math.Round(logical * Math.Max(96, owner.DeviceDpi) / 96d);

    // Keep persisted/operator-selected positions unless the new working area
    // would hide current controls. Recompute the minimum on monitor changes;
    // don't permanently shrink a preferred minimum after using a small display.
    internal static void FitWorkingArea(Form form)
    {
        Size? logicalMinimum = null;
        void Fit()
        {
            if (form.IsDisposed || form.Disposing || !form.IsHandleCreated) return;
            var scale = Math.Max(96, form.DeviceDpi) / 96d;
            var area = Screen.FromHandle(form.Handle).WorkingArea;
            if (area.Width <= 0 || area.Height <= 0) return;
            logicalMinimum ??= new Size((int)Math.Round(form.MinimumSize.Width / scale),
                (int)Math.Round(form.MinimumSize.Height / scale));

            var limits = logicalMinimum.Value;
            var minimum = new Size(Math.Min(Pixels(form, limits.Width), area.Width),
                Math.Min(Pixels(form, limits.Height), area.Height));
            if (form.MinimumSize != minimum) form.MinimumSize = minimum;
            if (form.WindowState != FormWindowState.Normal) return;

            var current = form.Bounds;
            var width = Math.Min(current.Width, area.Width);
            var height = Math.Min(current.Height, area.Height);
            var adjusted = new Rectangle(Math.Clamp(current.Left, area.Left, area.Right - width),
                Math.Clamp(current.Top, area.Top, area.Bottom - height), width, height);
            if (adjusted != current) form.Bounds = adjusted;
        }
        form.Shown += (_, _) => Fit();
        form.DpiChanged += (_, _) =>
        {
            if (!form.IsDisposed && !form.Disposing && form.IsHandleCreated)
                form.BeginInvoke(new Action(Fit));
        };
    }
}
