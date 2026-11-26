using System.Reflection;
using GenericWeb.DeploymentManager;

namespace GenericWeb.DeploymentManager.SmokeTests;

internal static class UiPerformancePhase03Tests
{
    private const BindingFlags Instance = BindingFlags.NonPublic | BindingFlags.Instance;
    private const BindingFlags Static = BindingFlags.NonPublic | BindingFlags.Static;

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
        Console.WriteLine("PASS: " + message);
    }

    public static void Run()
    {
        using var form = new WizardForm();
        var type = typeof(WizardForm);
        MethodInfo Method(string name) => type.GetMethod(name, Instance) ?? throw new MissingMethodException(name);
        FieldInfo Field(string name) => type.GetField(name, Instance) ?? throw new MissingFieldException(name);
        var show = Method("ShowPage");
        var rebuild = Method("RebuildCurrentPage");
        var dispose = Method("DisposeContentControls");
        var content = (Panel)Field("_content").GetValue(form)!;
        var log = (TextBox)Field("_log").GetValue(form)!;

        // No actual IIS or SQL operation: exercise only source and review views.
        show.Invoke(form, [7]);
        var first = content.Controls[0];
        log.Text = "Synthetic lifecycle test";
        for (int i = 0; i < 10; i++)
        {
            show.Invoke(form, [7]);
            Check(ReferenceEquals(first, content.Controls[0]) && !first.IsDisposed,
                "same-page selection preserves Review instance " + i);
        }
        Check(log.Text == "Synthetic lifecycle test" && !log.IsDisposed,
            "same-page navigation preserves live log content");

        rebuild.Invoke(form, null);
        var second = content.Controls[0];
        Check(first.IsDisposed && !ReferenceEquals(first, second) && !log.IsDisposed &&
            log.Text == "Synthetic lifecycle test", "forced rebuild disposes old view and preserves log");

        dispose.Invoke(form, null);
        Check(second.IsDisposed && content.Controls.Count == 0 &&
            (int)Field("_renderedPageIndex").GetValue(form)! == -1 && !log.IsDisposed,
            "view teardown invalidates render identity and detaches shared log");
        show.Invoke(form, [7]);
        Check(content.Controls.Count > 0 && !ReferenceEquals(second, content.Controls[0]),
            "same step rebuilt after explicit teardown");

        show.Invoke(form, [0]);
        var source = content.Controls[0];
        show.Invoke(form, [0]);
        Check(ReferenceEquals(source, content.Controls[0]) && !source.IsDisposed,
            "source view reused on current-step selection");

        var canNavigate = Method("CanNavigateInteractively");
        Check((bool)canNavigate.Invoke(form, null)!, "idle navigation allowed");
        foreach (var flag in new[] { "_navigationInProgress", "_pageActionRunning",
            "_operationRunning", "_autoDeployRunning", "_dependencySyncRunning" })
        {
            var field = Field(flag);
            field.SetValue(form, true);
            try { Check(!(bool)canNavigate.Invoke(form, null)!, "navigation blocked during " + flag); }
            finally { field.SetValue(form, false); }
        }

        var create = type.GetMethod("StatusCard", Static) ?? throw new MissingMethodException("StatusCard");
        var update = type.GetMethod("UpdateStatusCard", Static) ?? throw new MissingMethodException("UpdateStatusCard");
        using var card = (GroupBox)create.Invoke(null, ["Old", "Old detail", false])!;
        var status = card.Controls["StatusText"]!;
        var detail = card.Controls["StatusDetail"]!;
        update.Invoke(null, [card, "New", "New detail", true]);
        Check(ReferenceEquals(status, card.Controls["StatusText"]) &&
            ReferenceEquals(detail, card.Controls["StatusDetail"]) && card.Text == "New" &&
            detail.Text == "New detail" && status.Text == "● Ready", "status refresh updates existing labels");
    }
}
