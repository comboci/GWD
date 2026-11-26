using System.IO.Compression;
using System.Text;
using System.Text.Json;
using GenericWeb.DeploymentManager.Models;
using GenericWeb.DeploymentManager.Services;

namespace GenericWeb.DeploymentManager.SmokeTests;

internal static class UiPerformancePhase04Tests
{
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
        Console.WriteLine("PASS: " + message);
    }

    private static void Cancelled(Action action, string message)
    {
        var cancelled = false;
        try { action(); }
        catch (OperationCanceledException) { cancelled = true; }
        Check(cancelled, message);
    }

    public static void Run(string root)
    {
        Directory.CreateDirectory(root);
        CheckLogPreview(root);
        CheckHistory(root);
        CheckMetrics();
    }

    private static void CheckLogPreview(string root)
    {
        var path = Path.Combine(root, "phase04-utf8.log");
        var expected = new string('a', LogPreviewReader.MaxBatchBytes - 1) + "€ فارسی 🙂\r\nlast";
        File.WriteAllText(path, expected, new UTF8Encoding(false));
        var first = LogPreviewReader.ReadBatch(path, 0, initial: false);
        var second = LogPreviewReader.ReadBatch(path, first.NextOffset, initial: false);
        Check(first.NextOffset == LogPreviewReader.MaxBatchBytes - 1 &&
            first.Text + second.Text == expected, "UTF-8 batch boundary preserves all complete codepoints");
        Check(second.NextOffset == new FileInfo(path).Length, "incremental offset reaches EOF");

        var bytes = Encoding.UTF8.GetBytes("🙂");
        File.WriteAllBytes(path, bytes[..2]);
        var partial = LogPreviewReader.ReadBatch(path, 0, initial: false);
        Check(partial.NextOffset == 0 && partial.Text.Length == 0, "incomplete UTF-8 at EOF is not consumed");
        using (var append = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read))
            append.Write(bytes, 2, bytes.Length - 2);
        var resumed = LogPreviewReader.ReadBatch(path, partial.NextOffset, initial: false);
        Check(resumed.Text == "🙂" && resumed.NextOffset == bytes.Length, "UTF-8 append completes pending codepoint");

        var large = new string('x', LogPreviewReader.InitialTailBytes * 2) + "latest";
        File.WriteAllText(path, large, new UTF8Encoding(false));
        var tail = LogPreviewReader.ReadBatch(path, 0, initial: true);
        Check(tail.Reset && tail.OlderBytesHidden && tail.Text.Length <= LogPreviewReader.MaxBatchBytes &&
            tail.NextOffset > LogPreviewReader.InitialTailBytes, "initial large-file preview is bounded and marked");
        File.WriteAllText(path, "short", new UTF8Encoding(false));
        var reset = LogPreviewReader.ReadBatch(path, tail.NextOffset, initial: false);
        Check(reset.Reset && reset.Text == "short", "truncation resets read offset");

        var output = Path.Combine(root, "phase04-log-copy.txt");
        var activeBytes = Encoding.UTF8.GetBytes("Active log evidence فارسی\r\n");
        using (var active = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read))
        {
            active.Write(activeBytes);
            active.Flush(true);
            LogPreviewReader.CopySnapshotAsync(path, output).GetAwaiter().GetResult();
            Check(File.ReadAllBytes(output).SequenceEqual(activeBytes), "complete snapshot of active writer");
            using var cts = new CancellationTokenSource();
            cts.Cancel();
            Cancelled(() => LogPreviewReader.CopySnapshotAsync(path, output, cts.Token).GetAwaiter().GetResult(),
                "cancelled snapshot is rejected");
            Check(File.ReadAllBytes(output).SequenceEqual(activeBytes), "cancelled snapshot preserves previous export");
        }
        Check(File.ReadAllBytes(path).SequenceEqual(activeBytes), "original log remains byte-identical");
    }

    private static void CheckHistory(string root)
    {
        var history = new DeploymentHistoryService();
        var projectId = "abcdef0123456789abcd";
        var firstId = Guid.NewGuid().ToString("N");
        var secondId = Guid.NewGuid().ToString("N");
        var start = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var secret = "SyntheticPhase04Credential";
        SecretRedactor.Register(secret);
        DeploymentHistoryRecord Row(string id, DateTime started, DateTime eventAt, string result, bool completed) => new()
        {
            ProjectId = projectId, AttemptId = id, ApplicationName = "Synthetic " + secret, SiteName = "SyntheticSite",
            StartedAtUtc = started, EventAtUtc = eventAt, EndedAtUtc = completed ? eventAt : null,
            EventType = completed ? "Completed" : "Started", Result = result, ElapsedSeconds = completed ? 20 : 0
        };
        var rows = new[]
        {
            Row(firstId, start, start.AddSeconds(20), "Successful", true),
            Row(firstId, start, start, "InProgress", false),
            Row(firstId, start, start.AddSeconds(20), "Failed", true), // stable tie keeps first
            Row(secondId, start.AddMinutes(1), start.AddMinutes(1), "InProgress", false)
        };
        var path = Path.Combine(history.Root, projectId + ".jsonl");
        var malformed = "{\"broken\":\"" + secret + "\"}";
        File.WriteAllLines(path, rows.Select(x => JsonSerializer.Serialize(x)).Append(malformed), new UTF8Encoding(false));
        var original = File.ReadAllBytes(path);
        var attempts = history.GetAttempts(projectId);
        Check(attempts.Count == 2 && attempts[0].AttemptId == firstId &&
            attempts[0].Result == "Successful" && attempts[1].AttemptId == secondId,
            "stream reduction keeps latest event, stable tie and chronological order");
        var summary = history.GetSummary(projectId) ?? throw new InvalidOperationException("Summary missing");
        var catalog = history.GetProjectCatalog().Single(x => x.ProjectId == projectId);
        Check(summary.TotalAttempts == attempts.Count && catalog.ApplicationName == summary.ApplicationName &&
            catalog.SiteName == summary.SiteName && catalog.LatestDeploymentUtc == summary.LatestDeploymentUtc,
            "lightweight catalog matches detailed history");
        using (var cts = new CancellationTokenSource())
        {
            cts.Cancel();
            Cancelled(() => { _ = history.GetAttempts(projectId, cts.Token); }, "cancelled history read");
            Cancelled(() => { _ = history.GetProjectCatalog(cts.Token); }, "cancelled project catalog");
        }

        var zip = Path.Combine(root, "phase04-history.zip");
        new DiagnosticBundleService().ExportSectionToFile(zip, DiagnosticExportSection.DeploymentHistory,
            new DeploymentProfile(), history, "", null, null);
        using (var archive = ZipFile.OpenRead(zip))
        {
            var name = "history/" + projectId + ".jsonl";
            using var valid = new StreamReader((archive.GetEntry(name) ?? throw new InvalidOperationException("Missing valid entry")).Open());
            var validText = valid.ReadToEnd();
            var exported = validText.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Select(DeploymentHistoryService.ReadHistoryRecord).ToArray();
            Check(exported.Length == rows.Length && exported[0].AttemptId == firstId &&
                exported[0].StartedAtUtc == start && !validText.Contains(secret, StringComparison.Ordinal),
                "streamed history export preserves all valid events, timestamps and structured redaction");
            using var rejected = new StreamReader((archive.GetEntry(name + ".rejected.txt") ??
                throw new InvalidOperationException("Missing rejected evidence")).Open());
            var rejectedText = rejected.ReadToEnd();
            Check(rejectedText.Contains("Invalid history line 5") && !rejectedText.Contains(secret, StringComparison.Ordinal),
                "malformed evidence sidecar retained and redacted");
        }
        Check(File.ReadAllBytes(path).SequenceEqual(original), "history source remains byte-identical");
    }

    private static void CheckMetrics()
    {
        using var metrics = new RuntimeMetricsService();
        for (var i = 0; i < 3; i++) metrics.Sample();
        var history = metrics.GetHistory();
        Check(history.Length == 3 && metrics.GetRecentHistory(2).SequenceEqual(history.TakeLast(2)) &&
            metrics.GetRecentHistory(0).Length == 0, "metrics bounded observer preserves full history");
        Check(history.All(x => x.CpuPercent >= 0 && x.CpuPercent <= 100), "CPU percentage remains in range");
        metrics.Dispose();
        Check(metrics.GetHistory().Length == 3, "metrics history remains readable after shutdown");
        var disposed = false;
        try { metrics.Sample(); }
        catch (ObjectDisposedException) { disposed = true; }
        Check(disposed, "disposed resource sampler cannot reuse process handle");
    }
}
