using GenericWeb.DeploymentManager.Models;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace GenericWeb.DeploymentManager.Services;

// Disposable, derived cache only. Offset records refer to bytes from the
// verified JSONL prefix, never to another file or to exported artifacts.
internal static class DeploymentHistoryIndex
{
    private const int Version = 2;
    private const int BufferSize = 64 * 1024;
    private const int MaximumIndexedLine = 4 * 1024 * 1024;
    private static readonly UTF8Encoding Utf8 = new(false, true);

    public sealed record Row(long Offset, int Length, long FirstOffset, string AttemptId, DateTime EventAtUtc,
        DateTime StartedAtUtc, DateTime? EndedAtUtc, string ApplicationName, string SiteName, string DatabaseName,
        string ApplicationVersion, string BuildMode, string Result, double ElapsedSeconds, double? IntervalSincePreviousSeconds)
    {
        internal static Row From(long offset, int length, long firstOffset, DeploymentHistoryRecord r) =>
            new(offset, length, firstOffset, r.AttemptId, r.EventAtUtc, r.StartedAtUtc, r.EndedAtUtc,
                SecretRedactor.Redact(r.ApplicationName ?? ""), SecretRedactor.Redact(r.SiteName ?? ""),
                SecretRedactor.Redact(r.DatabaseName ?? ""), SecretRedactor.Redact(r.ApplicationVersion ?? ""),
                r.BuildMode, r.Result, r.ElapsedSeconds, r.IntervalSincePreviousSeconds);

        internal DeploymentHistoryRecord ToSummaryRecord() => new()
        {
            AttemptId = AttemptId, EventAtUtc = EventAtUtc, StartedAtUtc = StartedAtUtc, EndedAtUtc = EndedAtUtc,
            ApplicationName = ApplicationName, SiteName = SiteName, DatabaseName = DatabaseName,
            ApplicationVersion = ApplicationVersion, BuildMode = BuildMode, Result = Result, ElapsedSeconds = ElapsedSeconds,
            IntervalSincePreviousSeconds = IntervalSincePreviousSeconds
        };
    }

    public sealed record Snapshot(int Version, long Processed, int Lines, string PrefixHash, Row[] Rows);
    public sealed record Envelope(string ContentHash, string Payload);

    internal static Snapshot Read(FileStream source, string path, Action<int, string, JsonException> reportDamage, CancellationToken ct)
    {
        var length = source.Length;
        var modified = File.GetLastWriteTimeUtc(path);
        var sidecar = Path.Combine(Path.GetDirectoryName(path)!, ".index-v2", Path.GetFileName(path) + ".index.json");
        var cached = ReadSidecar(sidecar, length);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[BufferSize];
        if (cached is not null)
        {
            long left = cached.Processed;
            while (left > 0)
            {
                ct.ThrowIfCancellationRequested();
                var count = source.Read(buffer, 0, (int)Math.Min(left, buffer.Length));
                if (count == 0) throw new IOException("Deployment history changed while being read.");
                hash.AppendData(buffer, 0, count);
                left -= count;
            }
            if (!string.Equals(Convert.ToHexString(hash.GetCurrentHash()), cached.PrefixHash, StringComparison.Ordinal))
            {
                cached = null;
                source.Position = 0;
                hash.GetHashAndReset();
            }
        }

        if (cached is not null && cached.Processed == length)
        {
            VerifyStable(source, path, length, modified);
            return cached;
        }

        var latest = cached is null
            ? new Dictionary<string, Row>(StringComparer.OrdinalIgnoreCase)
            : cached.Rows.ToDictionary(r => r.AttemptId, StringComparer.OrdinalIgnoreCase);
        var lineNo = cached?.Lines ?? 0;
        var offset = cached?.Processed ?? 0;
        var remaining = length - offset;

        using var line = new MemoryStream();
        while (remaining > 0)
        {
            ct.ThrowIfCancellationRequested();
            var count = source.Read(buffer, 0, (int)Math.Min(remaining, buffer.Length));
            if (count == 0) throw new IOException("Deployment history changed while being read.");
            hash.AppendData(buffer, 0, count);
            remaining -= count;
            var start = 0;
            while (start < count)
            {
                var nl = Array.IndexOf(buffer, (byte)'\n', start, count - start);
                var end = nl < 0 ? count : nl + 1;
                var take = end - start;
                if (line.Length + take > MaximumIndexedLine) throw new NotSupportedException("History line cannot be indexed.");
                line.Write(buffer, start, take);
                start = end;
                if (nl < 0) continue;
                var bytes = line.GetBuffer().AsSpan(0, (int)line.Length);
                var value = Decode(bytes, offset);
                lineNo++;
                if (!string.IsNullOrWhiteSpace(value))
                {
                    try
                    {
                        var record = DeploymentHistoryService.ReadHistoryRecord(value);
                        if (!latest.TryGetValue(record.AttemptId, out var prior) || record.EventAtUtc > prior.EventAtUtc)
                            latest[record.AttemptId] = Row.From(offset, bytes.Length, prior?.FirstOffset ?? offset, record);
                    }
                    catch (JsonException ex) { reportDamage(lineNo, value, ex); }
                }
                offset += bytes.Length;
                line.SetLength(0);
                line.Position = 0;
            }
        }

        // The baseline can read a complete legacy row without a newline. Use it
        // for this generation instead of persisting offsets into a mutable tail.
        if (line.Length > 0) throw new NotSupportedException("History has a partial or non-terminated final line.");
        VerifyStable(source, path, length, modified);
        var snapshot = new Snapshot(Version, length, lineNo, Convert.ToHexString(hash.GetHashAndReset()), latest.Values.ToArray());
        if (length > 0) WriteSidecar(sidecar, snapshot);
        return snapshot;
    }

    internal static DeploymentHistoryRecord ReadRow(FileStream source, Row row)
    {
        source.Position = row.Offset;
        var bytes = new byte[row.Length];
        source.ReadExactly(bytes);
        var result = DeploymentHistoryService.ReadHistoryRecord(Decode(bytes, row.Offset));
        if (result.EventAtUtc != row.EventAtUtc || !string.Equals(result.AttemptId, row.AttemptId, StringComparison.OrdinalIgnoreCase))
            throw new IOException("Deployment history changed while the page was read.");
        return result;
    }

    private static string Decode(ReadOnlySpan<byte> bytes, long offset)
    {
        if (offset == 0 && bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
            bytes = bytes[3..];
        var carriageReturn = bytes.IndexOf((byte)'\r');
        if (carriageReturn >= 0 && (carriageReturn != bytes.Length - 2 || bytes[^1] != (byte)'\n'))
            throw new NotSupportedException("Legacy history line endings require the original reader.");
        if (bytes.IndexOf((byte)0) >= 0)
            throw new NotSupportedException("Legacy history encoding is not UTF-8.");
        try { return Utf8.GetString(bytes); }
        catch (DecoderFallbackException) { throw new NotSupportedException("Legacy history encoding is not UTF-8."); }
    }

    private static void VerifyStable(FileStream source, string path, long length, DateTime modified)
    {
        if (!File.Exists(path) || source.Length != length || File.GetLastWriteTimeUtc(path) != modified)
            throw new IOException("Deployment history changed while being read.");
    }

    private static string Digest(string payload) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload)));

    private static Snapshot? ReadSidecar(string sidecar, long length)
    {
        try
        {
            using var file = new FileStream(sidecar, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
            var envelope = JsonSerializer.Deserialize<Envelope>(file);
            if (envelope is null || envelope.Payload is null || envelope.ContentHash != Digest(envelope.Payload)) return null;
            var saved = JsonSerializer.Deserialize<Snapshot>(envelope.Payload);
            if (saved is null || saved.Version != Version || saved.Processed < 0 || saved.Processed > length || saved.Lines < 0 ||
                saved.PrefixHash is null || saved.PrefixHash.Length != 64 || saved.Rows is null ||
                saved.Rows.Any(r => r is null || !Guid.TryParseExact(r.AttemptId, "N", out _) || r.Offset < 0 || r.Length <= 0 ||
                    r.Length > MaximumIndexedLine || r.StartedAtUtc == default || r.BuildMode is null ||
                    r.Result is null || !DeploymentHistoryService.IsValidDuration(r.ElapsedSeconds) ||
                    (r.IntervalSincePreviousSeconds is double interval && !DeploymentHistoryService.IsValidDuration(interval)) || r.Offset > saved.Processed - r.Length || r.FirstOffset < 0 || r.FirstOffset > r.Offset) ||
                saved.Rows.Select(r => r.AttemptId).Distinct(StringComparer.OrdinalIgnoreCase).Count() != saved.Rows.Length) return null;
            return saved;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return null; }
    }

    private static void WriteSidecar(string sidecar, Snapshot snapshot)
    {
        var temp = sidecar + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(sidecar)!);
            var data = JsonSerializer.Serialize(snapshot);
            File.WriteAllText(temp, JsonSerializer.Serialize(new Envelope(Digest(data), data)));
            File.Move(temp, sidecar, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Caching is optional, and no history or export is ever rewritten.
            InternalDiagnostics.Record(ex, "Write disposable deployment history index");
        }
        finally
        {
            try { if (File.Exists(temp)) File.Delete(temp); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }
}
