using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using GenericWeb.DeploymentManager.Models;

namespace GenericWeb.DeploymentManager.Services;

public sealed record DeploymentProjectCatalogEntry(string ProjectId, string ApplicationName, string SiteName, DateTime LatestDeploymentUtc);

public sealed record DeploymentHistoryPage(DeploymentProjectSummary? Summary, IReadOnlyList<DeploymentHistoryRecord> Rows,
    int PageIndex, int PageSize, int TotalAttempts);

public sealed class DeploymentHistoryService
{
    private readonly object _sync = new();
    private static readonly ConcurrentDictionary<string, byte> ReportedDamagedRows = new(StringComparer.Ordinal);

    public string Root { get; } = string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("GWDM_APPDATA_ROOT")) ? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "GenericWebDeploymentManager",
        "History") : Path.Combine(Path.GetFullPath(Environment.GetEnvironmentVariable("GWDM_APPDATA_ROOT")!), "History");

    public DeploymentHistoryService()
    {
        Directory.CreateDirectory(Root);
    }

    public string GetProjectId(DeploymentProfile profile)
    {
        var application = NormalizeIdentity(profile.ApplicationName);
        var site = NormalizeIdentity(profile.SiteName);
        var database = NormalizeIdentity(profile.DatabaseName);

        if (string.IsNullOrWhiteSpace(application) && string.IsNullOrWhiteSpace(site) && string.IsNullOrWhiteSpace(database))
        {
            application = NormalizeIdentity(new DirectoryInfo(profile.SourceFolder).Name);
        }

        var canonical = $"app={application}|site={site}|db={database}";
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(canonical));
        return Convert.ToHexString(hash)[..20].ToLowerInvariant();
    }

    public DeploymentHistoryRecord BeginAttempt(DeploymentProfile profile, string sessionLog)
    {
        var started = DateTime.UtcNow;
        var projectId = GetProjectId(profile);
        var previous = GetAttempts(projectId)
            .Where(x => x.EndedAtUtc.HasValue)
            .OrderByDescending(x => x.EndedAtUtc)
            .FirstOrDefault();

        var record = new DeploymentHistoryRecord
        {
            EventType = "Started",
            AttemptId = Guid.NewGuid().ToString("N"),
            ProjectId = projectId,
            ApplicationName = profile.ApplicationName.Trim(),
            SiteName = profile.SiteName.Trim(),
            DatabaseName = profile.DatabaseName.Trim(),
            SourceFolder = profile.SourceFolder,
            DeploymentTargetPath = profile.HostingRoot,
            ApplicationVersion = DetectApplicationVersion(profile),
            BuildMode = profile.BuildMode,
            OfflineBuild = profile.OfflineBuild,
            Result = "InProgress",
            SessionLog = sessionLog,
            StartedAtUtc = started,
            IntervalSincePreviousSeconds = previous?.EndedAtUtc is DateTime previousEnd
                ? Math.Max(0, (started - previousEnd).TotalSeconds)
                : null,
            EventAtUtc = started
        };

        Append(record);
        return record;
    }

    public void CompleteAttempt(DeploymentHistoryRecord started, DeploymentProfile profile, string result, TimeSpan elapsed, string? deploymentTargetPath, BuildPerformanceTrace? performance)
    {
        var completed = new DeploymentHistoryRecord
        {
            SchemaVersion = started.SchemaVersion,
            EventType = "Completed",
            AttemptId = started.AttemptId,
            ProjectId = started.ProjectId,
            ApplicationName = profile.ApplicationName.Trim(),
            SiteName = profile.SiteName.Trim(),
            DatabaseName = profile.DatabaseName.Trim(),
            SourceFolder = profile.SourceFolder,
            DeploymentTargetPath = string.IsNullOrWhiteSpace(deploymentTargetPath) ? profile.HostingRoot : deploymentTargetPath,
            ApplicationVersion = string.IsNullOrWhiteSpace(started.ApplicationVersion) ? DetectApplicationVersion(profile) : started.ApplicationVersion,
            BuildMode = profile.BuildMode,
            OfflineBuild = profile.OfflineBuild,
            Result = NormalizeResult(result),
            SessionLog = started.SessionLog,
            StartedAtUtc = started.StartedAtUtc,
            EndedAtUtc = DateTime.UtcNow,
            ElapsedSeconds = Math.Max(0, elapsed.TotalSeconds),
            IntervalSincePreviousSeconds = started.IntervalSincePreviousSeconds,
            EventAtUtc = DateTime.UtcNow
        };

        if (performance is not null)
        {
            completed.BuildPerformance = new BuildPerformanceHistory
            {
                StartedAt = performance.StartedAt,
                CompletedAt = performance.CompletedAt,
                BuildMode = performance.BuildMode,
                DependencyMode = performance.DependencyMode,
                TotalMilliseconds = performance.TotalElapsed.TotalMilliseconds,
                Stages = performance.Stages.Select(x => new BuildPerformanceStageHistory
                {
                    Name = x.Name,
                    ElapsedMilliseconds = x.Elapsed.TotalMilliseconds,
                    Status = x.Status,
                    Detail = x.Detail
                }).ToList()
            };
        }

        Append(completed);
    }

    public IReadOnlyList<DeploymentHistoryRecord> GetAttempts(string projectId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var path = GetProjectFile(projectId);
        if (!File.Exists(path)) return Array.Empty<DeploymentHistoryRecord>();

        // Keep only the latest event per attempt while reading. Equal timestamps
        // retain the first event, matching the previous stable OrderBy behavior.
        var latest = new Dictionary<string, DeploymentHistoryRecord>(StringComparer.OrdinalIgnoreCase);
        lock (_sync)
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            int lineNumber = 0;
            while (reader.ReadLine() is string line)
            {
                ct.ThrowIfCancellationRequested();
                lineNumber++;
                if (string.IsNullOrWhiteSpace(line)) continue;
                try
                {
                    var record = ReadHistoryRecord(line);
                    if (!latest.TryGetValue(record.AttemptId, out var existing) || record.EventAtUtc > existing.EventAtUtc)
                        latest[record.AttemptId] = record;
                }
                catch (JsonException ex)
                {
                    // Preserve the original record; never invent dates or successful outcomes.
                    // Report the same damaged row once per process rather than on every refresh.
                    ReportDamagedRow(path, lineNumber, line, ex);
                }
            }
        }

        ct.ThrowIfCancellationRequested();
        return latest.Values.OrderBy(x => x.StartedAtUtc).ToArray();
    }

    private static void ReportDamagedRow(string path, int lineNumber, string line, JsonException ex)
    {
        var key = path + ":" + lineNumber + ":" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(line.TrimEnd('\r', '\n'))));
        if (ReportedDamagedRows.TryAdd(key, 0))
            InternalDiagnostics.Record(ex, "Read deployment history JSONL record (line " + lineNumber + ")");
    }

    private T? ReadIndexed<T>(string projectId, Func<FileStream, DeploymentHistoryIndex.Snapshot, T> query, CancellationToken ct)
        where T : class
    {
        var path = GetProjectFile(projectId);
        try
        {
            lock (_sync)
            {
                ct.ThrowIfCancellationRequested();
                using var source = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                var length = source.Length;
                var modified = File.GetLastWriteTimeUtc(path);
                var snapshot = DeploymentHistoryIndex.Read(source, path, (line, text, error) => ReportDamagedRow(path, line, text, error), ct);
                var result = query(source, snapshot);
                ct.ThrowIfCancellationRequested();
                if (!File.Exists(path) || source.Length != length || File.GetLastWriteTimeUtc(path) != modified)
                    return null;
                return result;
            }
        }
        catch (Exception ex) when (ex is IOException or NotSupportedException or UnauthorizedAccessException or JsonException)
        {
            // The legacy reader remains authoritative when an old encoding,
            // incomplete tail, or a concurrent replacement prevents indexing.
            return null;
        }
    }

    public DeploymentHistoryPage GetHistoryPage(string projectId, int requestedPage, int pageSize, CancellationToken ct = default)
    {
        pageSize = Math.Clamp(pageSize, 1, 250);
        int ClampPage(int count) => Math.Clamp(requestedPage, 0, count == 0 ? 0 : (count - 1) / pageSize);
        var indexed = ReadIndexed(projectId, (source, snapshot) =>
        {
            var sorted = snapshot.Rows.OrderBy(r => r.StartedAtUtc).ThenBy(r => r.FirstOffset).ToArray();
            var page = ClampPage(sorted.Length);
            var rows = sorted.OrderByDescending(r => r.StartedAtUtc).ThenBy(r => r.FirstOffset)
                .Skip(page * pageSize).Take(pageSize).Select(row =>
                {
                    ct.ThrowIfCancellationRequested();
                    return DeploymentHistoryIndex.ReadRow(source, row);
                }).ToArray();
            var summary = sorted.Length == 0 ? null : BuildSummary(projectId, sorted.Select(r => r.ToSummaryRecord()).ToArray());
            return new DeploymentHistoryPage(summary, rows, page, pageSize, sorted.Length);
        }, ct);
        if (indexed is not null) return indexed;

        var attempts = GetAttempts(projectId, ct);
        var fallbackPage = ClampPage(attempts.Count);
        var fallbackRows = attempts.OrderByDescending(r => r.StartedAtUtc).Skip(fallbackPage * pageSize).Take(pageSize).ToArray();
        return new(attempts.Count == 0 ? null : BuildSummary(projectId, attempts), fallbackRows,
            fallbackPage, pageSize, attempts.Count);
    }

    public IReadOnlyList<DeploymentProjectSummary> GetProjectSummaries()
    {
        Directory.CreateDirectory(Root);
        var summaries = new List<DeploymentProjectSummary>();
        foreach (var path in Directory.EnumerateFiles(Root, "*.jsonl", SearchOption.TopDirectoryOnly))
        {
            var projectId = Path.GetFileNameWithoutExtension(path);
            var attempts = GetAttempts(projectId);
            if (attempts.Count == 0) continue;
            summaries.Add(BuildSummary(projectId, attempts));
        }

        return summaries
            .OrderByDescending(x => x.LatestDeploymentUtc ?? DateTime.MinValue)
            .ThenBy(x => x.ApplicationName, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    // The project selector needs only identity and recency; do not hold all
    // projects' detailed attempts and build stages in memory together.
    public IReadOnlyList<DeploymentProjectCatalogEntry> GetProjectCatalog(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        Directory.CreateDirectory(Root);
        var catalog = new List<DeploymentProjectCatalogEntry>();
        foreach (var path in Directory.EnumerateFiles(Root, "*.jsonl", SearchOption.TopDirectoryOnly))
        {
            ct.ThrowIfCancellationRequested();
            var projectId = Path.GetFileNameWithoutExtension(path);
            var indexed = ReadIndexed(projectId, (_, snapshot) =>
            {
                var row = snapshot.Rows.OrderBy(x => x.StartedAtUtc).ThenBy(x => x.FirstOffset).LastOrDefault();
                return new CatalogLookup(row is null ? null : new(projectId, row.ApplicationName, row.SiteName,
                    row.EndedAtUtc ?? row.StartedAtUtc));
            }, ct);
            if (indexed is not null)
            {
                if (indexed.Entry is not null) catalog.Add(indexed.Entry);
                continue;
            }
            var attempts = GetAttempts(projectId, ct);
            if (attempts.Count == 0) continue;
            var latest = attempts[^1];
            catalog.Add(new(projectId, latest.ApplicationName, latest.SiteName,
                latest.EndedAtUtc ?? latest.StartedAtUtc));
        }
        return catalog.OrderByDescending(x => x.LatestDeploymentUtc)
            .ThenBy(x => x.ApplicationName, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private sealed record CatalogLookup(DeploymentProjectCatalogEntry? Entry);

    public DeploymentProjectSummary? GetSummary(string projectId, CancellationToken ct = default)
    {
        var attempts = GetAttempts(projectId, ct);
        ct.ThrowIfCancellationRequested();
        return attempts.Count == 0 ? null : BuildSummary(projectId, attempts);
    }

    private DeploymentProjectSummary BuildSummary(string projectId, IReadOnlyList<DeploymentHistoryRecord> attempts)
    {
        var ordered = attempts.OrderBy(x => x.StartedAtUtc).ToArray();
        var completed = ordered.Where(x => x.EndedAtUtc.HasValue).ToArray();
        var durations = completed.Select(x => TimeSpan.FromSeconds(Math.Max(0, x.ElapsedSeconds))).ToArray();
        var successfulDurations = completed.Where(x => IsResult(x, "Successful")).Select(x => TimeSpan.FromSeconds(Math.Max(0, x.ElapsedSeconds))).ToArray();
        var intervals = completed
            .Where(x => x.IntervalSincePreviousSeconds.HasValue)
            .Select(x => TimeSpan.FromSeconds(Math.Max(0, x.IntervalSincePreviousSeconds!.Value)))
            .ToArray();
        var successful = completed.Where(x => IsResult(x, "Successful")).ToArray();
        var first = ordered.FirstOrDefault();
        var latest = ordered.LastOrDefault();

        return new DeploymentProjectSummary
        {
            ProjectId = projectId,
            ApplicationName = latest?.ApplicationName ?? first?.ApplicationName ?? "Unknown application",
            SiteName = latest?.SiteName ?? first?.SiteName ?? string.Empty,
            DatabaseName = latest?.DatabaseName ?? first?.DatabaseName ?? string.Empty,
            TotalAttempts = ordered.Length,
            SuccessfulAttempts = completed.Count(x => IsResult(x, "Successful")),
            FailedAttempts = completed.Count(x => IsResult(x, "Failed") || IsResult(x, "RecoveryFailed")),
            CancelledAttempts = completed.Count(x => IsResult(x, "Cancelled")),
            IncompleteAttempts = ordered.Count(x => !x.EndedAtUtc.HasValue),
            DistinctVersions = successful
                .Select(x => x.ApplicationVersion)
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Count(),
            FirstDeploymentUtc = first?.StartedAtUtc,
            LatestDeploymentUtc = latest?.EndedAtUtc ?? latest?.StartedAtUtc,
            CalendarLifetime = first is null || latest is null
                ? TimeSpan.Zero
                : (latest.EndedAtUtc ?? latest.StartedAtUtc) - first.StartedAtUtc,
            CumulativeRuntime = Sum(durations),
            AverageRuntime = Average(durations),
            AverageSuccessfulRuntime = Average(successfulDurations),
            FastestRuntime = durations.Length == 0 ? TimeSpan.Zero : durations.Min(),
            SlowestRuntime = durations.Length == 0 ? TimeSpan.Zero : durations.Max(),
            AverageInterval = Average(intervals),
            SmartDeployCount = completed.Count(x => string.Equals(x.BuildMode, "Fast Local Test", StringComparison.OrdinalIgnoreCase) || string.Equals(x.BuildMode, "Fast IIS Update", StringComparison.OrdinalIgnoreCase) || string.Equals(x.BuildMode, "Fast Build", StringComparison.OrdinalIgnoreCase) || string.Equals(x.BuildMode, "Smart Deploy", StringComparison.OrdinalIgnoreCase)),
            FullDeployCount = completed.Count(x => string.Equals(x.BuildMode, "Full IIS Deploy", StringComparison.OrdinalIgnoreCase) || string.Equals(x.BuildMode, "Full Deploy", StringComparison.OrdinalIgnoreCase)),
            PreparedDeployCount = completed.Count(x => x.BuildMode.Contains("prepared", StringComparison.OrdinalIgnoreCase) || x.BuildMode.Contains("publish folder", StringComparison.OrdinalIgnoreCase)),
            Attempts = ordered
        };
    }

    private void Append(DeploymentHistoryRecord record)
    {
        Directory.CreateDirectory(Root);
        var path = GetProjectFile(record.ProjectId);
        var json = SerializeRedactedRecord(record) + Environment.NewLine;
        lock (_sync)
        {
            using var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read);
            using var writer = new StreamWriter(stream, new UTF8Encoding(false));
            writer.Write(json);
            writer.Flush();
            stream.Flush(flushToDisk: true);
        }
    }

    public static DeploymentHistoryRecord ReadHistoryRecord(string line)
    {
        using var document = JsonDocument.Parse(line);
        if (document.RootElement.ValueKind != JsonValueKind.Object ||
            !document.RootElement.TryGetProperty("AttemptId", out var id) || id.ValueKind != JsonValueKind.String ||
            !Guid.TryParseExact(id.GetString(), "N", out _) ||
            !document.RootElement.TryGetProperty("StartedAtUtc", out var started) ||
            started.ValueKind != JsonValueKind.String || !started.TryGetDateTime(out var date) || date == default)
            throw new JsonException("Required deployment history identity or timestamp is invalid.");
        var record = document.RootElement.Deserialize<DeploymentHistoryRecord>()
            ?? throw new JsonException("Null deployment history record.");
        // Explicit nulls/invalid numeric data must not crash the entire history
        // screen or be silently converted into a successful attempt. Missing
        // optional legacy fields continue to use model defaults.
        static bool ValidDuration(double seconds) => double.IsFinite(seconds) &&
            seconds >= 0 && seconds < TimeSpan.MaxValue.TotalSeconds;
        if (record.BuildMode is null || record.Result is null ||
            !ValidDuration(record.ElapsedSeconds) ||
            (record.IntervalSincePreviousSeconds is double interval && !ValidDuration(interval)))
            throw new JsonException("Invalid history mode, result or duration.");
        if (record.BuildPerformance is { } performance &&
            (performance.Stages is null || !ValidDuration(performance.TotalMilliseconds / 1000d) ||
             performance.Stages.Any(stage => stage is null || stage.Status is null ||
                 !ValidDuration(stage.ElapsedMilliseconds / 1000d))))
            throw new JsonException("Invalid build performance history.");
        return record;
    }

    // Redact text BEFORE writing JSON. Replacing arbitrary substrings in the
    // serialized document destroys timestamps, IDs and numeric measurements
    // when a registered credential is short or numeric.
    public static string SerializeRedactedRecord(DeploymentHistoryRecord record)
    {
        var root = JsonSerializer.SerializeToNode(record)!.AsObject();
        static void Clean(JsonObject obj, params string[] names)
        {
            foreach (var name in names)
                if (obj[name] is JsonValue value && value.TryGetValue<string>(out var text))
                    obj[name] = SecretRedactor.Redact(text);
        }
        Clean(root, "ApplicationName", "SiteName", "DatabaseName", "SourceFolder",
            "DeploymentTargetPath", "ApplicationVersion", "SessionLog", "BuildMode");
        if (root["BuildPerformance"] is JsonObject performance)
        {
            Clean(performance, "BuildMode", "DependencyMode");
            if (performance["Stages"] is JsonArray stages)
                foreach (var stage in stages.OfType<JsonObject>())
                    Clean(stage, "Name", "Status", "Detail");
        }
        // IDs, result/status event types, times and measurements are generated
        // metadata, not user-supplied credential fields.
        return root.ToJsonString();
    }

    private string GetProjectFile(string projectId) => Path.Combine(Root, projectId + ".jsonl");

    private static string NormalizeIdentity(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        return Regex.Replace(value.Trim().ToLowerInvariant(), @"\s+", " ");
    }

    private static string NormalizeResult(string result)
    {
        if (result.Equals("Successful", StringComparison.OrdinalIgnoreCase)) return "Successful";
        if (result.Equals("Cancelled", StringComparison.OrdinalIgnoreCase)) return "Cancelled";
        if (result.Equals("RecoveryFailed", StringComparison.OrdinalIgnoreCase)) return "RecoveryFailed";
        return "Failed";
    }

    private static bool IsResult(DeploymentHistoryRecord record, string result) =>
        string.Equals(record.Result, result, StringComparison.OrdinalIgnoreCase);

    private static TimeSpan Sum(IEnumerable<TimeSpan> values) => TimeSpan.FromTicks(values.Sum(x => x.Ticks));
    private static TimeSpan Average(IReadOnlyCollection<TimeSpan> values) => values.Count == 0 ? TimeSpan.Zero : TimeSpan.FromTicks((long)values.Average(x => x.Ticks));

    private static string DetectApplicationVersion(DeploymentProfile profile)
    {
        try
        {
            var versionPath = profile.BuildMode == "Prepared IIS Publish" && !string.IsNullOrWhiteSpace(profile.ReadyPublishFolder)
                ? profile.ReadyPublishFolder
                : profile.SourceFolder;
            var sourceName = new DirectoryInfo(versionPath).Name;
            var match = Regex.Match(sourceName, @"(?i)(?:^|[-_\s])v?(\d+\.\d+(?:\.\d+){0,2})(?:$|[-_\s])");
            if (match.Success) return match.Groups[1].Value;
        }
        catch (Exception ex) { InternalDiagnostics.Record(ex, "Detect application version from source folder"); }

        try
        {
            if (File.Exists(profile.ApiProjectPath))
            {
                var document = XDocument.Load(profile.ApiProjectPath);
                var candidates = new[] { "Version", "FileVersion", "AssemblyVersion" };
                foreach (var name in candidates)
                {
                    var value = document.Descendants().FirstOrDefault(x => x.Name.LocalName == name)?.Value?.Trim();
                    if (!string.IsNullOrWhiteSpace(value)) return value;
                }
            }
        }
        catch (Exception ex) { InternalDiagnostics.Record(ex, "Detect application version from project file"); }

        return string.Empty;
    }
}
