using System.Runtime.InteropServices;

namespace GenericWeb.DeploymentManager.Services;

/// <summary>Read-only conservative capacity estimate. It cannot reserve disk space
/// or guarantee against concurrent writers, quotas changing, or later disk failure.</summary>
public static class DeploymentCapacityGuard
{
    private const long ReserveBytes = 64L * 1024 * 1024;
    private const long EntryOverheadBytes = 64L * 1024;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetDiskFreeSpaceEx(string directory, out ulong available, out ulong total, out ulong free);

    public static long EstimateCopyBytes(string path, CancellationToken ct)
    {
        DeploymentPathSafety.RequirePlainTree(path);
        if (!Directory.Exists(path)) return 0;
        long total = EntryOverheadBytes;
        var directories = new Stack<string>();
        directories.Push(path);
        while (directories.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            foreach (var entry in Directory.EnumerateFileSystemEntries(directories.Pop()))
            {
                ct.ThrowIfCancellationRequested();
                var attributes = File.GetAttributes(entry);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("Reparse point blocks capacity check: " + entry);
                total = checked(total + EntryOverheadBytes);
                if ((attributes & FileAttributes.Directory) != 0) directories.Push(entry);
                else total = checked(total + new FileInfo(entry).Length);
            }
        }
        return total;
    }

    public static void RequireAvailable(long copyBytes, ulong available, string label)
    {
        if (copyBytes < 0) throw new ArgumentOutOfRangeException(nameof(copyBytes));
        var required = checked(copyBytes + ReserveBytes);
        if (available < (ulong)required)
            throw new IOException($"Insufficient disk space for {label}: estimated required {required:N0} bytes including safety margin, available {available:N0} bytes. Free space manually; no backup or hosted files will be deleted automatically.");
    }

    public static void Check(string target, long targetBytes, string? backup, long backupBytes)
    {
        var targetRoot = Path.GetPathRoot(DeploymentPathSafety.Normalize(target)) ?? throw new IOException("Cannot identify target volume.");
        var backupRoot = backup is null ? null : Path.GetPathRoot(DeploymentPathSafety.Normalize(backup));
        if (backup is not null && backupRoot is null) throw new IOException("Cannot identify backup volume.");
        if (backupRoot is not null && string.Equals(targetRoot, backupRoot, StringComparison.OrdinalIgnoreCase))
            RequireAvailable(checked(targetBytes + backupBytes), Available(target), "deployment and backup on " + targetRoot);
        else
        {
            RequireAvailable(targetBytes, Available(target), "deployment target " + targetRoot);
            if (backupRoot is not null && backupBytes > 0)
                RequireAvailable(backupBytes, Available(backup!), "backup destination " + backupRoot);
        }
    }

    private static ulong Available(string path)
    {
        var directory = DeploymentPathSafety.Normalize(path);
        while (!Directory.Exists(directory))
            directory = Path.GetDirectoryName(directory) ?? throw new IOException("Cannot find existing directory for disk-space check.");
        if (!GetDiskFreeSpaceEx(directory, out var available, out _, out _))
            throw new IOException("Cannot verify available disk space for " + directory,
                new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error()));
        return available;
    }
}
