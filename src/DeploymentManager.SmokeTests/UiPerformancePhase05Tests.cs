using System.Collections;
using System.Diagnostics;
using System.Reflection;
using GenericWeb.DeploymentManager;
using GenericWeb.DeploymentManager.Services;

namespace GenericWeb.DeploymentManager.SmokeTests;

internal static class UiPerformancePhase05Tests
{
    private const BindingFlags Private = BindingFlags.NonPublic | BindingFlags.Instance;

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
        Console.WriteLine("PASS: " + message);
    }

    public static void Run()
    {
        // This test constructs controls only; no Deployment, IIS, SQL, email
        // send, cleanup, or database relocation command is invoked.
        using (var test = new Form())
        {
            GwdUi.ConfigureForm(test);
            using var button = new Button { Text = "Unsafe operation", Enabled = false };
            using var grid = new DataGridView { VirtualMode = true, AllowUserToAddRows = false };
            using var secret = new TextBox { UseSystemPasswordChar = true, Text = "synthetic" };
            test.Controls.AddRange(new Control[] { button, grid, secret });
            GwdUi.StyleTree(test);
            Check(test.AutoScaleMode == AutoScaleMode.Dpi, "DPI-aware form baseline");
            Check(!button.Enabled && button.Text == "Unsafe operation" && grid.VirtualMode &&
                secret.UseSystemPasswordChar && secret.Text == "synthetic", "styling preserves enabled, virtual mode and password state");
        }

        using var wizard = new WizardForm();
        var type = typeof(WizardForm);
        FieldInfo Field(string name) => type.GetField(name, Private) ?? throw new MissingFieldException(name);
        MethodInfo Method(string name) => type.GetMethod(name, Private) ?? throw new MissingMethodException(name);
        var steps = (List<Button>)Field("_stepLabels").GetValue(wizard)!;
        Check(steps.Count == 8 && steps.All(b => b.TabStop && !string.IsNullOrWhiteSpace(b.AccessibleName)),
            "eight original steps exposed as keyboard-focusable controls");
        Field("_pageIndex").SetValue(wizard, 3);
        Method("UpdateStepIndicator").Invoke(wizard, null);
        Check(steps[0].AccessibleDescription!.StartsWith("Earlier", StringComparison.Ordinal) &&
            steps[3].AccessibleDescription!.StartsWith("Current", StringComparison.Ordinal),
            "earlier position is not proof of successful validation");
        Field("_pageIndex").SetValue(wizard, 0);
        Method("UpdateStepIndicator").Invoke(wizard, null);

        var lockSource = (CheckBox)Field("_lockSourceAction").GetValue(wizard)!;
        Check(lockSource.Parent is not null, "source lock is an accessible mounted control");
        lockSource.Checked = true;
        Method("ShowPage").Invoke(wizard, [7]);
        Method("ShowPage").Invoke(wizard, [0]);
        Check(((CheckBox)Field("_lockSourceAction").GetValue(wizard)!).Checked,
            "source lock survives actual page reconstruction");

        var notify = Method("NotifyUi");
        var kind = notify.GetParameters()[0].ParameterType;
        object Kind(string name) => Enum.Parse(kind, name);
        var noticeText = (Label)Field("_noticeText").GetValue(wizard)!;
        var historyText = Method("NotificationText");
        var dismiss = Method("DismissNotice");
        var render = Method("RenderNotice");
        var timer = (System.Windows.Forms.Timer)Field("_noticeTimer").GetValue(wizard)!;
        var notices = (IList)Field("_notices").GetValue(wizard)!;
        var queue = (IList)Field("_pendingNotices").GetValue(wizard)!;
        var stamp = Field("_noticeShownStamp");
        var active = Field("_activeNotice");
        void Send(string level, string text) => notify.Invoke(wizard, [Kind(level), text]);
        string History() => (string)historyText.Invoke(wizard, null)!;
        void DismissAll()
        {
            for (var i = 0; i < 102 && active.GetValue(wizard) is not null; i++) dismiss.Invoke(wizard, null);
            Check(active.GetValue(wizard) is null && queue.Count == 0 && !timer.Enabled,
                "draining explicitly ends queue and countdown");
        }
        void Banner(string text) => Check(noticeText.Text.Contains(text, StringComparison.Ordinal),
            "visible notification: " + text);

        DismissAll();
        var before = Stopwatch.GetTimestamp();
        Send("Info", "synthetic information");
        Check(stamp.GetValue(wizard) is long shown && shown >= before &&
            shown <= Stopwatch.GetTimestamp() && timer.Enabled, "informational countdown uses Stopwatch clock and arms timer");
        Banner("synthetic information");
        stamp.SetValue(wizard, Stopwatch.GetTimestamp() - Stopwatch.Frequency * 7);
        render.Invoke(wizard, null);
        Check(active.GetValue(wizard) is null && !timer.Enabled, "expired countdown advances without wall-clock wait");

        Send("Success", "synthetic success");
        Check(stamp.GetValue(wizard) is long && timer.Enabled, "success uses countdown");
        Send("Info", "synthetic queue info");
        Send("Warning", "synthetic warning");
        Check(stamp.GetValue(wizard) is null && !timer.Enabled, "warning persists and preempts success");
        Send("Error", "synthetic error");
        Check(stamp.GetValue(wizard) is null && !timer.Enabled, "error persists and preempts warning");
        Send("Critical", "synthetic critical");
        Send("Success", "synthetic later success");
        var historyCount = notices.Count;
        Send("Critical", "synthetic critical");
        Send("Warning", "synthetic warning");
        Check(notices.Count == historyCount && History().Contains("synthetic warning ×2", StringComparison.Ordinal),
            "duplicate active and queued notices reuse history and increment repetitions");
        Banner("synthetic critical ×2");
        Check(stamp.GetValue(wizard) is null && !timer.Enabled, "critical stays visible over success");
        dismiss.Invoke(wizard, null); Banner("synthetic error");
        dismiss.Invoke(wizard, null); Banner("synthetic warning ×2");
        dismiss.Invoke(wizard, null); Banner("synthetic success");
        dismiss.Invoke(wizard, null); Banner("synthetic later success");
        dismiss.Invoke(wizard, null); Banner("synthetic queue info");
        DismissAll();
        Check(History().Contains("synthetic critical ×2", StringComparison.Ordinal), "dismissal preserves redacted session history");

        SecretRedactor.Register("SyntheticPhase05Secret");
        Send("Info", "password=SyntheticPhase05Secret");
        Check(!History().Contains("SyntheticPhase05Secret", StringComparison.Ordinal) &&
            !noticeText.Text.Contains("SyntheticPhase05Secret", StringComparison.Ordinal), "banner and history redact credentials");
        for (var i = 0; i < 110; i++) Send("Info", "bounded notice " + i);
        Check(notices.Count == 100 && queue.Count == 100 && History().Contains("Banner limit", StringComparison.Ordinal),
            "history and pending queue are separately bounded and dropped messages are disclosed");
        DismissAll();

        Field("_deploymentStatus").SetValue(wizard, "FAST LOCAL TEST PASSED — HOSTED IIS UNCHANGED");
        Method("UpdateDeploymentIndicator").Invoke(wizard, null);
        Check(noticeText.Text.Contains("hosted IIS is unchanged", StringComparison.OrdinalIgnoreCase),
            "local compile is not represented as hosted IIS deployment");
        DismissAll();

        // Synthetic journal/marker in an isolated temporary root: do not touch IIS or an actual site's markers.
        var root = Path.Combine(Path.GetTempPath(), "gwd-notice-recovery-" + Guid.NewGuid().ToString("N"));
        try
        {
            var locks = Path.Combine(root, "locks");
            using var attempt = new IisDeploymentAttempt(locks, Path.Combine(root, "journal"),
                ["synthetic-notification-target"], "qa-notification-dismiss", _ => { });
            attempt.BeginMutation();
            var marker = Directory.GetFiles(locks, "*.unresolved").Single();
            var intact = File.ReadAllBytes(marker);
            var persistent = (DeploymentState)Field("_persistentState").GetValue(wizard)!;
            persistent.LastResult = "RecoveryFailed";
            Field("_deploymentStatus").SetValue(wizard, "DEPLOYMENT FAILED — RECOVERY FAILED");
            Method("UpdateDeploymentIndicator").Invoke(wizard, null);
            dismiss.Invoke(wizard, null);
            Check(((Label)Field("_deploymentIndicator").GetValue(wizard)!).Text.Contains("RECOVERY REVIEW REQUIRED") &&
                persistent.LastResult == "RecoveryFailed" && !attempt.Resolved &&
                File.ReadAllBytes(marker).SequenceEqual(intact), "dismissing a critical banner preserves unresolved recovery evidence");
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }

        var menu = wizard.MainMenuStrip ?? throw new InvalidOperationException("Main menu missing");
        var commands = menu.Items.OfType<ToolStripMenuItem>()
            .SelectMany(x => x.DropDownItems.OfType<ToolStripMenuItem>()).ToArray();
        foreach (var name in new[] { "Save Profile", "Load Profile", "Settings", "Database Management",
            "Clean Previous Versions", "Deployment Cleanup...", "Lock Source", "Export Clean Source ZIP",
            "Diagnostics Center", "Email Notifications...", "Live Log", "Deployment History", "Notifications" })
            Check(commands.Any(c => c.Text == name), "command retained: " + name);
    }
}
