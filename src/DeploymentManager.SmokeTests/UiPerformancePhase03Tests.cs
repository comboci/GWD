using System.Reflection;
using GenericWeb.DeploymentManager;
using GenericWeb.DeploymentManager.Models;

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

        var profile = (DeploymentProfile)Field("_profile").GetValue(form)!;
        profile.ApplicationName = "Synthetic navigation project";
        profile.SiteName = "SyntheticNavigationSite";
        var expectedViews = new[]
        {
            "DeploymentSourceView", "DeploymentTargetView", "EnvironmentScanView", "IisManagementView",
            "InstallerStageView", "SqlConfigurationView", "DeploymentLocationView", "ReviewStageView"
        };
        var previous = (UserControl)source;
        for (var i = 0; i < expectedViews.Length; i++)
        {
            show.Invoke(form, [i]);
            Check(content.Controls.Count == 1 && content.Controls[0] is UserControl view &&
                view.GetType().Name == expectedViews[i] && !view.IsDisposed, "owned stage " + expectedViews[i]);
            var current = (UserControl)content.Controls[0];
            if (i != 0) Check(previous.IsDisposed, "outgoing stage disposed for " + expectedViews[i]);
            show.Invoke(form, [i]);
            Check(ReferenceEquals(current, content.Controls[0]), "same stage is not re-subscribed");
            previous = current;
        }
        Check(profile.ApplicationName == "Synthetic navigation project" && profile.SiteName == "SyntheticNavigationSite" &&
            ReferenceEquals(profile, Field("_profile").GetValue(form)), "project state survives all eight stage transitions");
        Check(!log.IsDisposed && log.Text.Contains("Synthetic lifecycle test", StringComparison.Ordinal),
            "shared Log remains live throughout all eight stage transitions");

        var review = previous;
        var reviewActions = review.GetType().GetField("_actions", Instance) ?? throw new MissingFieldException("Review actions");
        Check(reviewActions.GetValue(review) is not null, "Review activation receives its controller");
        show.Invoke(form, [2]);
        Check(review.IsDisposed && reviewActions.GetValue(review) is null && !log.IsDisposed,
            "Review teardown deactivates callbacks and releases the shared Log before disposal");

        var scan = (UserControl)content.Controls[0];
        var scanType = scan.GetType();
        var activateScan = scanType.GetMethod("Activate", Instance) ?? throw new MissingMethodException("Scan Activate");
        var deactivateScan = scanType.GetMethod("Deactivate", Instance) ?? throw new MissingMethodException("Scan Deactivate");
        var scanButton = (Button)(scanType.GetField("_scan", Instance) ?? throw new MissingFieldException("Scan button")).GetValue(scan)!;
        var raiseButtonClick = typeof(Button).GetMethod("OnClick", Instance) ?? throw new MissingMethodException("Button OnClick");
        var oldCalls = 0;
        var newCalls = 0;
        Func<Button, Task> oldCallback = _ => { oldCalls++; return Task.CompletedTask; };
        Func<Button, Task> newCallback = _ => { newCalls++; return Task.CompletedTask; };
        activateScan.Invoke(scan, [oldCallback]);
        activateScan.Invoke(scan, [newCallback]);
        activateScan.Invoke(scan, [newCallback]);
        raiseButtonClick.Invoke(scanButton, [EventArgs.Empty]);
        Check(oldCalls == 0 && newCalls == 1, "repeat Activate replaces callback; click is handled exactly once");
        deactivateScan.Invoke(scan, null);
        raiseButtonClick.Invoke(scanButton, [EventArgs.Empty]);
        Check(newCalls == 1, "Deactivate removes event subscription");
        show.Invoke(form, [6]);
        var location = (UserControl)content.Controls[0];
        var changed = location.GetType().GetField("_changed", Instance) ?? throw new MissingFieldException("Location callback");
        Check(changed.GetValue(location) is not null, "Location view receives controller");
        show.Invoke(form, [7]);
        Check(location.IsDisposed && changed.GetValue(location) is null && !log.IsDisposed,
            "Location disposal clears callbacks and Review remounts a live shared Log");

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

        var statusUi = type.Assembly.GetType("GenericWeb.DeploymentManager.UI.Controls.StatusCardUi", throwOnError: true)!;
        var create = statusUi.GetMethod("StatusCard", Static) ?? throw new MissingMethodException("StatusCard");
        var update = statusUi.GetMethod("UpdateStatusCard", Static) ?? throw new MissingMethodException("UpdateStatusCard");
        using var card = (GroupBox)create.Invoke(null, ["Old", "Old detail", false])!;
        var status = card.Controls["StatusText"]!;
        var detail = card.Controls["StatusDetail"]!;
        update.Invoke(null, [card, "New", "New detail", true]);
        Check(ReferenceEquals(status, card.Controls["StatusText"]) &&
            ReferenceEquals(detail, card.Controls["StatusDetail"]) && card.Text == "New" &&
            detail.Text == "New detail" && status.Text == "● Ready", "status refresh updates existing labels");
        var noticeTimer = (System.Windows.Forms.Timer)Field("_noticeTimer").GetValue(form)!;
        noticeTimer.Start();
        form.Dispose();
        Check(form.IsDisposed && !noticeTimer.Enabled && (bool)Field("_visualResourcesStopped").GetValue(form)!,
            "disposal stops the visual shell timer and releases notification resources");
    }
}
