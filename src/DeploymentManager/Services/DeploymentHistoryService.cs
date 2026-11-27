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

internal sealed record DashboardHistoryEvidence(DeploymentHistoryRecord? Latest, DeploymentHistoryRecord? LatestRecordedRelease);

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
        record.BuildMode = PreserveBuildMode(record.BuildMode);
        record.Result = PreserveResult(record.Result);
        if (record.BuildPerformance is { } build) build.BuildMode = PreserveBuildMode(build.BuildMode);
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
            var rows = NewestFirst(sorted, r => r.StartedAtUtc, ct)
                .Skip(page * pageSize).Take(pageSize).Select(row =>
                {
                    ct.ThrowIfCancellationRequested();
                    return DeploymentHistoryIndex.ReadRow(source, row);
                }).ToArray();
            var summary = sorted.Length == 0 ? null : BuildSummary(projectId, sorted.Select(r => r.ToSummaryRecord()).ToArray(), ct);
            return new DeploymentHistoryPage(summary, rows, page, pageSize, sorted.Length);
        }, ct);
        if (indexed is not null) return indexed;

        var attempts = GetAttempts(projectId, ct);
        var fallbackPage = ClampPage(attempts.Count);
        var fallbackRows = NewestFirst(attempts, r => r.StartedAtUtc, ct).Skip(fallbackPage * pageSize).Take(pageSize).ToArray();
        return new(attempts.Count == 0 ? null : BuildSummary(projectId, attempts, ct), fallbackRows,
            fallbackPage, pageSize, attempts.Count);
    }

    internal DashboardHistoryEvidence ReadDashboardHistory(string projectId, CancellationToken ct)
    {
        static DashboardHistoryEvidence Select(IReadOnlyList<DeploymentHistoryRecord> ordered)
        {
            var completed = ordered.Where(IsRecordedHostedDeployment).OrderBy(r => r.EndedAtUtc).LastOrDefault();
            return new(ordered.LastOrDefault(), completed);
        }

        return ReadIndexed(projectId, (_, snapshot) =>
        {
            DeploymentHistoryIndex.Row? latest = null, release = null;
            foreach (var row in snapshot.Rows)
            {
                ct.ThrowIfCancellationRequested();
                if (latest is null || (row.StartedAtUtc, row.FirstOffset).CompareTo((latest.StartedAtUtc, latest.FirstOffset)) > 0)
                    latest = row;
                if (IsRecordedHostedDeployment(row.EndedAtUtc, row.Result, row.BuildMode) &&
                    (release is null || (row.EndedAtUtc, row.StartedAtUtc, row.FirstOffset)
                        .CompareTo((release.EndedAtUtc, release.StartedAtUtc, release.FirstOffset)) > 0))
                    release = row;
            }
            return new DashboardHistoryEvidence(latest?.ToSummaryRecord(), release?.ToSummaryRecord());
        }, ct) ?? Select(GetAttempts(projectId, ct));
    }

    // Reverse the chronological groups, not the equal-time peers within each
    // group. This retains source order for equal timestamps while avoiding a
    // second sort and sorted copy of the full index on every page.
    private static IEnumerable<T> NewestFirst<T>(IReadOnlyList<T> ordered, Func<T, DateTime> started, CancellationToken ct)
    {
        var end = ordered.Count;
        while (end > 0)
        {
            var begin = end - 1;
            var time = started(ordered[begin]);
            while (begin > 0 && started(ordered[begin - 1]) == time)
            {
                ct.ThrowIfCancellationRequested();
                begin--;
            }
            for (var i = begin; i < end; i++)
            {
                ct.ThrowIfCancellationRequested();
                yield return ordered[i];
            }
            end = begin;
        }
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
                DeploymentHistoryIndex.Row? row = null;
                foreach (var candidate in snapshot.Rows)
                {
                    ct.ThrowIfCancellationRequested();
                    if (row is null || (candidate.StartedAtUtc, candidate.FirstOffset)
                        .CompareTo((row.StartedAtUtc, row.FirstOffset)) > 0)
                        row = candidate;
                }
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
        return attempts.Count == 0 ? null : BuildSummary(projectId, attempts, ct);
    }

    private DeploymentProjectSummary BuildSummary(string projectId, IReadOnlyList<DeploymentHistoryRecord> attempts, CancellationToken ct = default)
    {
        // Both JSONL readers pass chronological records. Aggregate without
        // allocating separate arrays for outcomes, durations or intervals.
        var first = attempts.Count == 0 ? null : attempts[0];
        var latest = attempts.Count == 0 ? null : attempts[^1];
        int completed = 0, success = 0, hosted = 0, local = 0, failed = 0, cancelled = 0;
        int unknown = 0, smart = 0, full = 0, prepared = 0, intervalCount = 0;
        decimal runtimeTicks = 0, successfulTicks = 0, intervalTicks = 0;
        long fastest = long.MaxValue, slowest = 0;
        var versions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in attempts)
        {
            ct.ThrowIfCancellationRequested();
            if (!row.EndedAtUtc.HasValue) continue;
            completed++;
            var ticks = TimeSpan.FromSeconds(row.ElapsedSeconds).Ticks;
            runtimeTicks += ticks;
            fastest = Math.Min(fastest, ticks);
            slowest = Math.Max(slowest, ticks);
            if (row.IntervalSincePreviousSeconds is double interval)
            {
                intervalCount++;
                intervalTicks += TimeSpan.FromSeconds(interval).Ticks;
            }
            switch (row.Result)
            {
                case "Successful":
                    success++;
                    successfulTicks += ticks;
                    if (IsRecordedHostedDeployment(row))
                    {
                        hosted++;
                        if (!string.IsNullOrWhiteSpace(row.ApplicationVersion)) versions.Add(row.ApplicationVersion);
                    }
                    else if (IsLocalTest(row.BuildMode)) local++;
                    break;
                case "Failed":
                case "RecoveryFailed": failed++; break;
                case "Cancelled": cancelled++; break;
                default: unknown++; break;
            }
            switch (row.BuildMode)
            {
                case "Fast Local Test":
                case "Fast IIS Update":
                case "Fast Build":
                case "Smart Deploy": smart++; break;
                case "Full IIS Deploy":
                case "Full Deploy": full++; break;
                case "Prepared IIS Publish":
                case "Visual Studio prepared":
                case "Publish folder": prepared++; break;
            }
        }
        static TimeSpan Duration(decimal ticks) => TimeSpan.FromTicks((long)Math.Min(ticks, long.MaxValue));
        static TimeSpan AverageDuration(decimal ticks, int count) => Duration(count == 0 ? 0 : ticks / count);

        return new DeploymentProjectSummary
        {
            ProjectId = projectId,
            ApplicationName = latest?.ApplicationName ?? first?.ApplicationName ?? "Unknown application",
            SiteName = latest?.SiteName ?? first?.SiteName ?? string.Empty,
            DatabaseName = latest?.DatabaseName ?? first?.DatabaseName ?? string.Empty,
            TotalAttempts = attempts.Count,
            SuccessfulAttempts = success,
            FailedAttempts = failed,
            RecordedHostedSuccesses = hosted,
            SuccessfulLocalTests = local,
            SuccessfulUnknownMode = success - hosted - local,
            UnknownResultAttempts = unknown,
            CancelledAttempts = cancelled,
            IncompleteAttempts = attempts.Count - completed,
            DistinctVersions = versions.Count,
            FirstDeploymentUtc = first?.StartedAtUtc,
            LatestDeploymentUtc = latest?.EndedAtUtc ?? latest?.StartedAtUtc,
            CalendarLifetime = first is null || latest is null
                ? TimeSpan.Zero
                : (latest.EndedAtUtc ?? latest.StartedAtUtc) - first.StartedAtUtc,
            CumulativeRuntime = Duration(runtimeTicks),
            AverageRuntime = AverageDuration(runtimeTicks, completed),
            AverageSuccessfulRuntime = AverageDuration(successfulTicks, success),
            FastestRuntime = TimeSpan.FromTicks(completed == 0 ? 0 : fastest),
            SlowestRuntime = TimeSpan.FromTicks(slowest),
            AverageInterval = AverageDuration(intervalTicks, intervalCount),
            SmartDeployCount = smart,
            FullDeployCount = full,
            PreparedDeployCount = prepared,
            Attempts = attempts
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
        if (record.BuildMode is null || record.Result is null ||
            !IsValidDuration(record.ElapsedSeconds) ||
            (record.IntervalSincePreviousSeconds is double interval && !IsValidDuration(interval)))
            throw new JsonException("Invalid history mode, result or duration.");
        if (record.BuildPerformance is { } performance &&
            (performance.Stages is null || !IsValidDuration(performance.TotalMilliseconds / 1000d) ||
             performance.Stages.Any(stage => stage is null || stage.Status is null ||
                 !IsValidDuration(stage.ElapsedMilliseconds / 1000d))))
            throw new JsonException("Invalid build performance history.");
        record.BuildMode = PreserveBuildMode(record.BuildMode);
        record.Result = PreserveResult(record.Result);
        // Legacy raw text is projected into a redacted read model only.
        record.ApplicationName = SecretRedactor.Redact(record.ApplicationName);
        record.SiteName = SecretRedactor.Redact(record.SiteName);
        record.DatabaseName = SecretRedactor.Redact(record.DatabaseName);
        record.SourceFolder = SecretRedactor.Redact(record.SourceFolder);
        record.DeploymentTargetPath = SecretRedactor.Redact(record.DeploymentTargetPath);
        record.ApplicationVersion = SecretRedactor.Redact(record.ApplicationVersion);
        record.SessionLog = SecretRedactor.Redact(record.SessionLog);
        if (record.BuildPerformance is { } build)
        {
            build.BuildMode = PreserveBuildMode(build.BuildMode);
            build.DependencyMode = SecretRedactor.Redact(build.DependencyMode);
            foreach (var stage in build.Stages)
            {
                stage.Name = SecretRedactor.Redact(stage.Name);
                stage.Status = SecretRedactor.Redact(stage.Status);
                stage.Detail = SecretRedactor.Redact(stage.Detail);
            }
        }
        return record;
    }

    internal static bool IsValidDuration(double seconds) => double.IsFinite(seconds) &&
        seconds >= 0 && seconds < TimeSpan.MaxValue.TotalSeconds;

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
        root["BuildMode"] = PreserveBuildMode(record.BuildMode);
        root["Result"] = PreserveResult(record.Result);
        Clean(root, "ApplicationName", "SiteName", "DatabaseName", "SourceFolder",
            "DeploymentTargetPath", "ApplicationVersion", "SessionLog");
        if (root["BuildPerformance"] is JsonObject performance)
        {
            performance["BuildMode"] = PreserveBuildMode(record.BuildPerformance?.BuildMode);
            Clean(performance, "DependencyMode");
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

    private static readonly string[] HistoryResults =
        ["InProgress", "Successful", "Failed", "Cancelled", "RecoveryFailed"];
    private static readonly string[] HostedModes =
        ["Fast IIS Update", "Full IIS Deploy", "Prepared IIS Publish", "Visual Studio prepared", "Full Deploy"];
    private static readonly string[] OtherModes = ["Fast Local Test", "Fast Build", "Smart Deploy", "Publish folder"];

    // These finite vocabularies are generated metadata. Unknown and partly
    // redacted historical values must never be promoted to a known outcome.
    private static string PreserveMetadata(string? value, params string[][] groups)
    {
        if (string.IsNullOrWhiteSpace(value)) return "";
        foreach (var group in groups)
            foreach (var option in group)
                if (option.Equals(value, StringComparison.OrdinalIgnoreCase)) return option;
        const string unknown = "Unrecognized: ";
        if (value.StartsWith(unknown, StringComparison.Ordinal)) value = value[unknown.Length..];
        return unknown + SecretRedactor.Redact(value);
    }

    internal static string PreserveBuildMode(string? value) => PreserveMetadata(value, HostedModes, OtherModes);
    internal static string PreserveResult(string? value) => PreserveMetadata(value, HistoryResults);

    private static bool IsResult(DeploymentHistoryRecord record, string result) =>
        string.Equals(record.Result, result, StringComparison.OrdinalIgnoreCase);

    internal static bool IsLocalTest(string mode) => string.Equals(mode, "Fast Local Test", StringComparison.OrdinalIgnoreCase);

    internal static bool IsRecordedHostedDeployment(DeploymentHistoryRecord row) =>
        IsRecordedHostedDeployment(row.EndedAtUtc, row.Result, row.BuildMode);

    private static bool IsRecordedHostedDeployment(DateTime? ended, string result, string mode) =>
        ended.HasValue && string.Equals(result, "Successful", StringComparison.OrdinalIgnoreCase) &&
        HostedModes.Contains(mode, StringComparer.OrdinalIgnoreCase);

    internal static string DescribeAttemptResult(DeploymentHistoryRecord row)
    {
        if (!row.EndedAtUtc.HasValue) return "In progress / interrupted";
        if (!IsResult(row, "Successful"))
            return string.IsNullOrWhiteSpace(row.Result) ? "Unrecognized result" : row.Result;
        if (IsLocalTest(row.BuildMode)) return "Successful local test — IIS unchanged";
        if (IsRecordedHostedDeployment(row)) return "Recorded hosted success";
        return "Successful attempt — hosting not established";
    }

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
