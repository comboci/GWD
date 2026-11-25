using System.IO.Compression;
using System.Text.Json;
using GenericWeb.DeploymentManager.Models;
using GenericWeb.DeploymentManager.Services;

namespace GenericWeb.DeploymentManager.SmokeTests;

internal static class DiagnosticHistoryTests
{
    public static void Run(string root)
    {
        SecretRedactor.Register("1");
        SecretRedactor.Register("SyntheticDiagnosticSecret");
        var history = new DeploymentHistoryService();
        var record = new DeploymentHistoryRecord
        {
            AttemptId = "11111111111111111111111111111111",
            ProjectId = "11111111111111111111",
            ApplicationName = "test SyntheticDiagnosticSecret",
            StartedAtUtc = new DateTime(2026, 11, 25, 11, 11, 11, DateTimeKind.Utc),
            EventAtUtc = new DateTime(2026, 11, 25, 11, 11, 11, DateTimeKind.Utc),
            ElapsedSeconds = 111.1,
            BuildPerformance = new BuildPerformanceHistory
            {
                StartedAt = new DateTime(2026, 11, 25, 11, 11, 11, DateTimeKind.Utc),
                TotalMilliseconds = 111.1,
                Stages = new() { new() { Detail = "password=SyntheticDiagnosticSecret" } }
            }
        };
        var json = DeploymentHistoryService.SerializeRedactedRecord(record);
        var read = DeploymentHistoryService.ReadHistoryRecord(json);
        if (read.StartedAtUtc != record.StartedAtUtc || read.AttemptId != record.AttemptId ||
            read.ElapsedSeconds != record.ElapsedSeconds || read.BuildPerformance!.TotalMilliseconds != 111.1 ||
            json.Contains("SyntheticDiagnosticSecret", StringComparison.Ordinal))
            throw new InvalidOperationException("History structured redaction regression.");

        var path = Path.Combine(history.Root, record.ProjectId + ".jsonl");
        var original = json + Environment.NewLine + "{\"StartedAtUtc\":\"[REDACTED]\"}" + Environment.NewLine;
        File.WriteAllText(path, original);
        if (history.GetAttempts(record.ProjectId).Count != 1)
            throw new InvalidOperationException("Malformed row must not hide valid history.");

        var appData = new AppDataService();
        var log = new LogService(appData);
        try
        {
            log.Write("Active log SyntheticDiagnosticSecret");
            var service = new DiagnosticBundleService();
            var zip = Path.Combine(root, "diagnostics.zip");
            service.ExportSectionToFile(zip, DiagnosticExportSection.CurrentLog,
                new DeploymentProfile(), history, log.LogFile, null, null);
            using (var archive = ZipFile.OpenRead(zip))
            {
                var entry = archive.GetEntry("logs/current-session.log") ?? throw new InvalidOperationException("Open log not exported.");
                using var reader = new StreamReader(entry.Open());
                var text = reader.ReadToEnd();
                if (!text.Contains("Active log") || text.Contains("SyntheticDiagnosticSecret", StringComparison.Ordinal))
                    throw new InvalidOperationException("Open log export or redaction regression.");
            }
            var historyZip = Path.Combine(root, "history.zip");
            service.ExportSectionToFile(historyZip, DiagnosticExportSection.DeploymentHistory,
                new DeploymentProfile(), history, log.LogFile, null, null);
            using (var archive = ZipFile.OpenRead(historyZip))
            {
                var name = "history/" + record.ProjectId + ".jsonl";
                var entry = archive.GetEntry(name) ?? throw new InvalidOperationException("History not exported.");
                using var reader = new StreamReader(entry.Open());
                var exported = DeploymentHistoryService.ReadHistoryRecord(reader.ReadLine()!);
                if (exported.StartedAtUtc != record.StartedAtUtc || exported.ElapsedSeconds != record.ElapsedSeconds ||
                    reader.ReadLine() is not null || archive.GetEntry(name + ".rejected.txt") is null)
                    throw new InvalidOperationException("History export structure or rejected evidence regression.");
            }
            if (File.ReadAllText(path) != original) throw new InvalidOperationException("Export changed original history.");
        }
        finally { log.CloseSession(); }
        Console.WriteLine("PASS: history JSON, damaged row isolation, active log export and redaction.");
    }
}
