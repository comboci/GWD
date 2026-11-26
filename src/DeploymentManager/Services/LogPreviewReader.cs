using System.Text;

namespace GenericWeb.DeploymentManager.Services;

public sealed record LogPreviewBatch(long NextOffset, string Text, bool Reset, bool Exists, bool OlderBytesHidden);

/// <summary>Bounded, read-only UTF-8 view of an append-only session log.</summary>
public static class LogPreviewReader
{
    public const int MaxBatchBytes = 64 * 1024;
    public const int InitialTailBytes = 200_000;

    public static LogPreviewBatch ReadBatch(string path, long offset, bool initial)
    {
        if (!File.Exists(path)) return new(0, "", true, false, false);
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete, 16 * 1024, FileOptions.SequentialScan);
        var length = stream.Length;
        var reset = initial || offset < 0 || offset > length;
        var start = reset ? Math.Max(0, length - InitialTailBytes) : offset;
        stream.Position = start;
        var buffer = new byte[(int)Math.Min(MaxBatchBytes, length - start)];
        var count = 0;
        while (count < buffer.Length)
        {
            var read = stream.Read(buffer, count, buffer.Length - count);
            if (read == 0) break;
            count += read;
        }

        // Initial tail may start inside a UTF-8 codepoint. Skip only its orphan
        // continuation bytes, not a full line or a valid leading character.
        var first = 0;
        if (reset && start > 0)
            while (first < count && (buffer[first] & 0xC0) == 0x80) first++;
        if (start == 0 && count >= 3 && buffer[0] == 0xEF && buffer[1] == 0xBB && buffer[2] == 0xBF)
            first = 3;

        // Do not consume an incomplete trailing UTF-8 codepoint. The next poll
        // rereads those bytes together with their continuation, even across EOF.
        var complete = CompleteUtf8Prefix(buffer, first, count);
        return new(start + complete, Encoding.UTF8.GetString(buffer, first, complete - first),
            reset, true, reset && start > 0);
    }

    private static int CompleteUtf8Prefix(byte[] bytes, int first, int count)
    {
        if (count <= first) return count;
        var lead = count - 1;
        while (lead > first && (bytes[lead] & 0xC0) == 0x80 && count - lead < 4) lead--;
        var value = bytes[lead];
        var expected = value is >= 0xC2 and <= 0xDF ? 2 :
            value is >= 0xE0 and <= 0xEF ? 3 :
            value is >= 0xF0 and <= 0xF4 ? 4 : 1;
        return expected > count - lead ? lead : count;
    }

    // A full, exact-length snapshot of the original file at open time. Appends
    // after that point remain in the original log. Never replace a good export
    // until its complete successor has been written.
    public static async Task CopySnapshotAsync(string source, string destination, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (string.Equals(Path.GetFullPath(source), Path.GetFullPath(destination), StringComparison.OrdinalIgnoreCase))
            throw new IOException("Log snapshot destination must differ from the original evidence file.");
        var temporary = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(destination))!, "." + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            await using (var input = new FileStream(source, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete, MaxBatchBytes, FileOptions.Asynchronous | FileOptions.SequentialScan))
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write,
                FileShare.None, MaxBatchBytes, FileOptions.Asynchronous))
            {
                var remaining = input.Length;
                var buffer = new byte[MaxBatchBytes];
                while (remaining > 0)
                {
                    var read = await input.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, remaining)), ct).ConfigureAwait(false);
                    if (read == 0) throw new IOException("The original log was shortened during export.");
                    await output.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                    remaining -= read;
                }
                await output.FlushAsync(ct).ConfigureAwait(false);
            }
            ct.ThrowIfCancellationRequested();
            File.Move(temporary, destination, overwrite: true);
        }
        finally
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); }
            catch (Exception ex) { InternalDiagnostics.Record(ex, "Log snapshot temporary cleanup"); }
        }
    }
}
