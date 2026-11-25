using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using GenericWeb.DeploymentManager.Models;

namespace GenericWeb.DeploymentManager.Services;

public enum DiagnosticExportSection
{
    All,
    CurrentLog,
    DeploymentHistory,
    CrashReports,
    ErrorReports,
    BuildPerformance,
    InternalDiagnostics,
    RuntimeResources,
    CodeQuality
}

public sealed class DiagnosticBundleService
{
    public static string GetSuggestedFileName(DiagnosticExportSection section)
    {
        var suffix = section == DiagnosticExportSection.All ? "Diagnostic-Bundle" : "Diagnostic-" + section;
        return $"GWD-{suffix}-{DateTime.Now:yyyyMMdd-HHmmss}.zip";
    }

    public string Export(string destinationFolder, DeploymentProfile profile, DeploymentHistoryService history, string currentLog)
        => ExportSection(destinationFolder, DiagnosticExportSection.All, profile, history, currentLog, null, null);

    public string ExportSection(string destinationFolder, DiagnosticExportSection section, DeploymentProfile profile, DeploymentHistoryService history, string currentLog, BuildPerformanceTrace? performance, RuntimeMetricsService? runtimeMetrics)
    {
        Directory.CreateDirectory(destinationFolder);
        return ExportSectionToFile(Path.Combine(destinationFolder, GetSuggestedFileName(section)), section, profile, history, currentLog, performance, runtimeMetrics);
    }

    public string ExportSectionToFile(string destinationFile, DiagnosticExportSection section, DeploymentProfile profile, DeploymentHistoryService history, string currentLog, BuildPerformanceTrace? performance, RuntimeMetricsService? runtimeMetrics, CancellationToken ct = default, IProgress<string>? progress = null)
    {
        ct.ThrowIfCancellationRequested();
        progress?.Report("Preparing diagnostics ZIP...");
        if (string.IsNullOrWhiteSpace(destinationFile)) throw new ArgumentException("A destination file is required.", nameof(destinationFile));
        if (!destinationFile.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) destinationFile += ".zip";
        var destinationFolder = Path.GetDirectoryName(destinationFile);
        if (string.IsNullOrWhiteSpace(destinationFolder)) throw new InvalidOperationException("The diagnostic destination folder could not be resolved.");
        Directory.CreateDirectory(destinationFolder);
        // Write beside the destination so a failed export cannot destroy an existing bundle.
        var temporaryFile = Path.Combine(destinationFolder, ".gwd-diagnostic-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            using (var archive = ZipFile.Open(temporaryFile, ZipArchiveMode.Create))
            {
                ct.ThrowIfCancellationRequested();
                var appData = new AppDataService();

        if (section is DiagnosticExportSection.All)
        {
            progress?.Report("Collecting runtime information...");
            ct.ThrowIfCancellationRequested();
            AddText(archive, "runtime.json", JsonSerializer.Serialize(new
            {
                Product = "Generic Web Deployment Manager",
                Version = GenericWeb.DeploymentManager.ProductInfo.Version,
                ExportedAt = DateTimeOffset.Now,
                OS = Environment.OSVersion.VersionString,
                Runtime = Environment.Version.ToString(),
                Machine = Environment.MachineName,
                ProcessArchitecture = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString(),
                profile.BuildMode,
                profile.DependencyResolutionMode,
                profile.OfflineBuild,
                profile.TargetMode,
                profile.WorkspaceRoot,
                profile.TargetFrameworkMajor,
                profile.RequiresNode
            }, new JsonSerializerOptions { WriteIndented = true }));
        }

        if (section is DiagnosticExportSection.All or DiagnosticExportSection.DeploymentHistory)
        {
            progress?.Report("Collecting IIS attempt journals...");
            AddDirectoryTextFiles(archive, Path.Combine(appData.Root, "IisAttempts"), "iis-attempts", "*.json", ct);
        }

        if (section is DiagnosticExportSection.All or DiagnosticExportSection.CodeQuality)
        {
            progress?.Report("Collecting Code Quality reports...");
            ct.ThrowIfCancellationRequested();
            var qualityRoot = CodeQualitySession.Store(appData).Root;
            foreach (var build in CodeQualitySession.Store(appData).Read())
            {
                ct.ThrowIfCancellationRequested();
                AddText(archive, "code-quality/" + build.BuildId + ".json", GenericWeb.DeploymentManager.CodeQuality.DiagnosticExport.Json(build));
            }
            if (Directory.Exists(qualityRoot))
                foreach (var file in Directory.EnumerateFiles(qualityRoot, "*.log", SearchOption.AllDirectories))
                {
                    ct.ThrowIfCancellationRequested();
                    var entry = archive.CreateEntry("code-quality/" + Path.GetRelativePath(qualityRoot, file).Replace('\\', '/'), CompressionLevel.Optimal);
                    using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
                    using var source = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                    using var reader = new StreamReader(source);
                    string? line;
                    while ((line = reader.ReadLine()) is not null)
                    {
                        ct.ThrowIfCancellationRequested();
                        writer.WriteLine(SecretRedactor.Redact(line));
                    }
                }
        }

        if (section is DiagnosticExportSection.All or DiagnosticExportSection.DeploymentHistory)
        {
            progress?.Report("Collecting deployment history...");
            AddDirectoryTextFiles(archive, history.Root, "history", "*.jsonl", ct);
        }

        if (section is DiagnosticExportSection.All or DiagnosticExportSection.CurrentLog)
        {
            progress?.Report("Collecting current session log...");
            ct.ThrowIfCancellationRequested();
            if (File.Exists(currentLog)) AddSanitizedFile(archive, currentLog, "logs/current-session.log", ct);
        }

        if (section is DiagnosticExportSection.All or DiagnosticExportSection.CrashReports)
        {
            progress?.Report("Collecting crash reports...");
            AddDirectoryTextFiles(archive, appData.CrashReports, "crash-reports", "*.txt", ct);
        }

        if (section is DiagnosticExportSection.All or DiagnosticExportSection.ErrorReports)
        {
            progress?.Report("Collecting error reports...");
            AddDirectoryTextFiles(archive, appData.ErrorReports, "error-reports", "*.txt", ct);
        }

        if (section is DiagnosticExportSection.All or DiagnosticExportSection.InternalDiagnostics)
        {
            progress?.Report("Collecting internal diagnostics...");
            AddDirectoryTextFiles(archive, appData.InternalDiagnosticsFolder, "internal-diagnostics", "*.log", ct);
        }

        if (section is DiagnosticExportSection.All or DiagnosticExportSection.BuildPerformance)
        {
            progress?.Report("Collecting build performance...");
            ct.ThrowIfCancellationRequested();
            if (performance is not null)
                AddText(archive, "performance/latest-build-performance.json", JsonSerializer.Serialize(performance, new JsonSerializerOptions { WriteIndented = true }));
            else
                AddText(archive, "performance/README.txt", "No in-memory build performance snapshot is available in this session.");
        }

        if (section is DiagnosticExportSection.All or DiagnosticExportSection.RuntimeResources)
        {
            progress?.Report("Collecting CPU and memory metrics...");
            ct.ThrowIfCancellationRequested();
            if (runtimeMetrics is not null)
            {
                AddText(archive, "runtime-resources/history.json", runtimeMetrics.ToJson());
                AddText(archive, "runtime-resources/history.txt", runtimeMetrics.ToPlainText());
            }
            else AddText(archive, "runtime-resources/README.txt", "No in-memory CPU/memory history is available in this session.");
        }

        if (section is DiagnosticExportSection.All)
        {
            progress?.Report("Collecting dependency and workspace state...");
            ct.ThrowIfCancellationRequested();
            var repository = string.IsNullOrWhiteSpace(profile.DeploymentRepositoryFolder) ? profile.InstallerCacheFolder : profile.DeploymentRepositoryFolder;
            var buildState = Path.Combine(repository, "BuildState");
            AddDirectoryTextFiles(archive, buildState, "fingerprints", "*.json", ct);
            if (Directory.Exists(profile.WorkspaceRoot))
                AddDirectoryTextFiles(archive, profile.WorkspaceRoot, "workspace", ".gwd-workspace-manifest.json", ct);
            AddText(archive, "dependency-state.txt", BuildDependencyState(repository));
        }

        ct.ThrowIfCancellationRequested();
        AddText(archive, "README.txt", $"Diagnostic export generated by {GenericWeb.DeploymentManager.ProductInfo.DisplayVersion}. Scope: {section}. Passwords, tokens, connection strings, and credentials are redacted.");
            }
            // Publish only a completed and still-authorized archive; cancellation
            // never replaces an existing destination or leaves a partial ZIP there.
            progress?.Report("Finalizing diagnostics ZIP...");
            ct.ThrowIfCancellationRequested();
            File.Move(temporaryFile, destinationFile, overwrite: true);
            return destinationFile;
        }
        finally
        {
            try { if (File.Exists(temporaryFile)) File.Delete(temporaryFile); }
            catch (IOException) { /* Retain a failed temporary export for manual cleanup. */ }
            catch (UnauthorizedAccessException) { /* Preserve the original export exception. */ }
        }
    }

    private static string BuildDependencyState(string repository)
    {
        var paths = new[]
        {
            ("Repository", repository),
            ("NuGet packages", Path.Combine(repository, "NuGet", "packages")),
            ("NuGet feed", Path.Combine(repository, "NuGet", "feed")),
            ("npm cache", Path.Combine(repository, "Node", "npm-cache")),
            ("Build state", Path.Combine(repository, "BuildState"))
        };
        return string.Join(Environment.NewLine, paths.Select(x => $"{x.Item1}: {(Directory.Exists(x.Item2) ? "PRESENT" : "MISSING")} | {x.Item2}"));
    }

    private static void AddDirectoryTextFiles(ZipArchive archive, string root, string targetRoot, string pattern, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (!Directory.Exists(root)) return;
        foreach (var file in Directory.EnumerateFiles(root, pattern, SearchOption.AllDirectories))
        {
            ct.ThrowIfCancellationRequested();
            var relative = Path.GetRelativePath(root, file).Replace('\\', '/');
            AddSanitizedFile(archive, file, targetRoot + "/" + relative, ct);
        }
    }

    private static void AddSanitizedFile(ZipArchive archive, string source, string name, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        try
        {
            using var stream = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            var contents = reader.ReadToEnd();
            ct.ThrowIfCancellationRequested();
            if (name.StartsWith("history/", StringComparison.Ordinal) && name.EndsWith(".jsonl", StringComparison.OrdinalIgnoreCase))
            {
                var valid = new StringBuilder();
                var rejected = new StringBuilder();
                int lineNumber = 0;
                using var lines = new StringReader(contents);
                while (lines.ReadLine() is string line)
                {
                    ct.ThrowIfCancellationRequested();
                    lineNumber++;
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    try
                    {
                        var record = DeploymentHistoryService.ReadHistoryRecord(line);
                        valid.AppendLine(DeploymentHistoryService.SerializeRedactedRecord(record));
                    }
                    catch (JsonException)
                    {
                        rejected.AppendLine("Invalid history line " + lineNumber + ": " + SecretRedactor.Redact(line));
                    }
                }
                AddAlreadyRedactedText(archive, name, valid.ToString());
                if (rejected.Length > 0) AddAlreadyRedactedText(archive, name + ".rejected.txt", rejected.ToString());
            }
            else AddText(archive, name, contents);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            InternalDiagnostics.Record(ex, "Diagnostic export file: " + source);
            AddText(archive, name + ".unavailable.txt", ex.Message);
        }
    }

    private static void AddText(ZipArchive archive, string name, string text)
    {
        AddAlreadyRedactedText(archive, name, SecretRedactor.Redact(text));
    }

    private static void AddAlreadyRedactedText(ZipArchive archive, string name, string text)
    {
        var entry = archive.CreateEntry(name, CompressionLevel.Optimal);
        using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
        writer.Write(text);
    }

}
