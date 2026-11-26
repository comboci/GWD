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
        dismiss.Invoke(wizard, null);
        notify.Invoke(wizard, [Kind("Info"), "synthetic information"]);
        Check(Field("_noticeExpires").GetValue(wizard) is DateTimeOffset &&
            noticeText.Text.Contains("synthetic information"), "informational countdown");
        notify.Invoke(wizard, [Kind("Warning"), "synthetic recovery warning"]);
        Check(Field("_noticeExpires").GetValue(wizard) is null, "warning does not auto-expire");
        notify.Invoke(wizard, [Kind("Success"), "synthetic later information"]);
        Check(noticeText.Text.Contains("synthetic recovery warning"), "info cannot hide a persistent warning");
        dismiss.Invoke(wizard, null);
        Check(Field("_activeNotice").GetValue(wizard) is null &&
            ((string)historyText.Invoke(wizard, null)!).Contains("synthetic recovery warning"),
            "dismiss only clears banner, not session history");
        SecretRedactor.Register("SyntheticPhase05Secret");
        notify.Invoke(wizard, [Kind("Info"), "password=SyntheticPhase05Secret"]);
        Check(!((string)historyText.Invoke(wizard, null)!).Contains("SyntheticPhase05Secret"), "notification redaction");
        for (var i = 0; i < 110; i++) notify.Invoke(wizard, [Kind("Info"), "bounded notice " + i]);
        var notices = Field("_notices").GetValue(wizard)!;
        Check((int)notices.GetType().GetProperty("Count")!.GetValue(notices)! == 100, "notification history bounded");

        dismiss.Invoke(wizard, null);
        Field("_deploymentStatus").SetValue(wizard, "FAST LOCAL TEST PASSED — HOSTED IIS UNCHANGED");
        Method("UpdateDeploymentIndicator").Invoke(wizard, null);
        Check(noticeText.Text.Contains("hosted IIS is unchanged", StringComparison.OrdinalIgnoreCase),
            "local compile is not represented as hosted IIS deployment");
        Field("_deploymentStatus").SetValue(wizard, "DEPLOYMENT FAILED — RECOVERY FAILED");
        Method("UpdateDeploymentIndicator").Invoke(wizard, null);
        dismiss.Invoke(wizard, null);
        Check(((Label)Field("_deploymentIndicator").GetValue(wizard)!).Text.Contains("RECOVERY REVIEW REQUIRED"),
            "dismissal cannot remove persistent recovery warning");

        var menu = wizard.MainMenuStrip ?? throw new InvalidOperationException("Main menu missing");
        var commands = menu.Items.OfType<ToolStripMenuItem>()
            .SelectMany(x => x.DropDownItems.OfType<ToolStripMenuItem>()).ToArray();
        foreach (var name in new[] { "Save Profile", "Load Profile", "Settings", "Database Management",
            "Clean Previous Versions", "Deployment Cleanup...", "Lock Source", "Export Clean Source ZIP",
            "Diagnostics Center", "Email Notifications...", "Live Log", "Deployment History", "Notifications" })
            Check(commands.Any(c => c.Text == name), "command retained: " + name);
    }
}
