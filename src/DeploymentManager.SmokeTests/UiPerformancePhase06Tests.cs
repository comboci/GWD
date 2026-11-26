using System.Reflection;
using System.Text.Json;
using GenericWeb.DeploymentManager;
using GenericWeb.DeploymentManager.Models;
using GenericWeb.DeploymentManager.Services;

namespace GenericWeb.DeploymentManager.SmokeTests;

internal static class UiPerformancePhase06Tests
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    private static void Check(bool ok, string name)
    {
        if (!ok) throw new InvalidOperationException(name);
        Console.WriteLine("PASS: " + name);
    }

    public static void Run(string root)
    {
        // Use an isolated Windows VM/account. Wizard startup may access HKCU.
        var artifact = Path.Combine(root, "prepared");
        Directory.CreateDirectory(artifact);
        Directory.CreateDirectory(Path.Combine(artifact, "wwwroot"));
        File.WriteAllText(Path.Combine(artifact, "web.config"), "<configuration/>");
        File.WriteAllText(Path.Combine(artifact, "Example.dll"), "synthetic");
        using (var wizard = new WizardForm())
        {
            var type = typeof(WizardForm);
            FieldInfo Field(string name) => type.GetField(name, Private)!;
            MethodInfo Method(string name) => type.GetMethod(name, Private)!;
            var p = (DeploymentProfile)Field("_profile").GetValue(wizard)!;
            p.ApplicationName = "Prepared custom application";
            p.SiteName = "Prepared custom site";
            p.DatabaseName = "Prepared custom database";
            p.BuildMode = "Prepared IIS Publish";
            p.SourceFolder = "";
            p.ReadyPublishFolder = artifact;
            Method("RebuildCurrentPage").Invoke(wizard, null);
            Check(p.ApplicationName == "Prepared custom application" && p.SiteName == "Prepared custom site" &&
                p.DatabaseName == "Prepared custom database", "prepared artifact does not erase identity without source");
            Field("_scanCompleted").SetValue(wizard, true);
            Field("_iisAutoVerified").SetValue(wizard, true);
            Method("InvalidateEnvironmentEvidence").Invoke(wizard, null);
            Check(!(bool)Field("_scanCompleted").GetValue(wizard)! &&
                !(bool)Field("_iisAutoVerified").GetValue(wizard)!, "old environment evidence invalidated");
            Field("_dependencySyncRunning").SetValue(wizard, true);
            Method("UpdateNavigationState").Invoke(wizard, null);
            Check(!((Panel)Field("_content").GetValue(wizard)!).Enabled, "profile inputs frozen during dependency work");
            Field("_dependencySyncRunning").SetValue(wizard, false);
            Method("UpdateNavigationState").Invoke(wizard, null);
            Check(((Panel)Field("_content").GetValue(wizard)!).Enabled, "profile inputs restored after work");
        }

        var record = new DeploymentHistoryRecord { StartedAtUtc = DateTime.UtcNow, BuildMode = null! };
        var rejected = false;
        try { DeploymentHistoryService.ReadHistoryRecord(JsonSerializer.Serialize(record)); }
        catch (JsonException) { rejected = true; }
        Check(rejected, "explicit null build mode is isolated as malformed history");
        record.BuildMode = "Fast Local Test";
        record.BuildPerformance = new BuildPerformanceHistory { Stages = null! };
        rejected = false;
        try { DeploymentHistoryService.ReadHistoryRecord(JsonSerializer.Serialize(record)); }
        catch (JsonException) { rejected = true; }
        Check(rejected, "null performance stages are isolated as malformed history");
        var log = Path.Combine(root, "same-path.log");
        File.WriteAllText(log, "original evidence");
        rejected = false;
        try { LogPreviewReader.CopySnapshotAsync(log, log).GetAwaiter().GetResult(); }
        catch (IOException) { rejected = true; }
        Check(rejected && File.ReadAllText(log) == "original evidence", "self-copy cannot replace original log");
    }
}
