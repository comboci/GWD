using System.Text;

namespace GenericWeb.DeploymentManager.Services;

/// <summary>
/// Reads the bounded tail of an active deployment log without denying access
/// to the process that currently owns the append writer. A diagnostic reader
/// must never take exclusive ownership of the deployment evidence.
/// </summary>
public static class DeploymentLogTailReader
{
    public static IReadOnlyList<string> ReadLastLines(string path, int maxLines)
    {
        if (maxLines <= 0 || !File.Exists(path)) return Array.Empty<string>();

        // LogService opens its append writer with FileAccess.Write / FileShare.Read.
        // File.ReadLines uses sharing that does not permit an existing writer;
        // this reader must allow both reading AND the existing write handle.
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete, 16 * 1024, FileOptions.SequentialScan);
        using var reader = new StreamReader(stream, Encoding.UTF8,
            detectEncodingFromByteOrderMarks: true, bufferSize: 16 * 1024);
        var tail = new Queue<string>();
        while (reader.ReadLine() is { } line)
        {
            if (tail.Count == maxLines) tail.Dequeue();
            tail.Enqueue(line);
        }
        return tail.ToArray();
    }
}
