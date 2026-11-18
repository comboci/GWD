using GenericWeb.DeploymentManager.Models;
using GenericWeb.DeploymentManager.Services;
using Microsoft.Data.SqlClient;
using System.Data;

namespace GenericWeb.DeploymentManager;

/// <summary>Local SQL Server database inspection and guarded MDF/LDF relocation.</summary>
internal sealed class DatabaseRelocationForm : Form
{
    private readonly DeploymentProfile _profile;
    private readonly SqlServerService _sql;
    private readonly TextBox _destination = new() { Dock = DockStyle.Top, PlaceholderText = @"D:\SQLData" };
    private readonly TextBox _details = new() { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Dock = DockStyle.Fill };
    private readonly Button _inspect = new() { Text = "Refresh database size", Width = 180 };
    private readonly Button _move = new() { Text = "Detach, Move && Attach", Width = 210, Enabled = false };
    private readonly ProgressBar _relocationProgress = new() { Dock = DockStyle.Top, Height = 18, Minimum = 0, Maximum = 4 };
    private readonly Label _stage = new() { Dock = DockStyle.Top, Height = 32, Text = "Ready", Font = new Font("Segoe UI Semibold", 10F), Padding = new Padding(8, 5, 0, 0) };
    private List<DatabaseFile> _files = new();
    private string _database = "";

    private sealed record DatabaseFile(string LogicalName, string PhysicalPath, string Type, long Bytes);

    internal DatabaseRelocationForm(DeploymentProfile profile, SqlServerService sql)
    {
        _profile = profile;
        _sql = sql;
        Text = "Database size and relocation";
        Width = 850; Height = 560; StartPosition = FormStartPosition.CenterParent;
        var toolbar = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 48, FlowDirection = FlowDirection.LeftToRight };
        var browse = new Button { Text = "Destination...", Width = 130 };
        browse.Click += (_, _) =>
        {
            using var picker = new FolderBrowserDialog { Description = "Select an existing destination directory" };
            if (picker.ShowDialog(this) == DialogResult.OK) _destination.Text = picker.SelectedPath;
        };
        toolbar.Controls.Add(_inspect); toolbar.Controls.Add(browse); toolbar.Controls.Add(_move);
        _details.Font = new Font("Segoe UI", 11F);
        _details.WordWrap = true;
        Controls.Add(_details); Controls.Add(_relocationProgress); Controls.Add(_stage); Controls.Add(_destination); Controls.Add(toolbar);
        _inspect.Click += async (_, _) => await InspectAsync();
        _move.Click += async (_, _) => await RelocateAsync();
        Shown += async (_, _) => await InspectAsync();
    }

    private async Task<SqlConnection> OpenMasterAsync() => await _sql.Connections.OpenAsync(_profile, "master", CancellationToken.None);

    private static async Task ExecuteAsync(SqlConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandTimeout = 300;
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    private static string Identifier(string name) => "[" + name.Replace("]", "]]") + "]";
    private static string Literal(string name) => "N'" + name.Replace("'", "''") + "'";

    private async Task InspectAsync()
    {
        _move.Enabled = false;
        _inspect.Enabled = false;
        try
        {
            _database = _profile.DatabaseName?.Trim() ?? "";
            if (string.IsNullOrWhiteSpace(_database)) throw new InvalidOperationException("Select a database in the wizard first.");
            await using var connection = await OpenMasterAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = @"SELECT mf.name, mf.physical_name, mf.type_desc, mf.size * CAST(8192 AS bigint)
FROM sys.master_files mf INNER JOIN sys.databases d ON mf.database_id=d.database_id
WHERE d.name=@db AND d.state_desc='ONLINE' ORDER BY mf.file_id";
            command.Parameters.AddWithValue("@db", _database);
            await using var reader = await command.ExecuteReaderAsync();
            var files = new List<DatabaseFile>();
            while (await reader.ReadAsync())
                files.Add(new DatabaseFile(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetInt64(3)));
            if (files.Count == 0) throw new InvalidOperationException("Database is missing, offline or inaccessible.");
            _files = files;
            _details.Text = "Database: " + _database + Environment.NewLine +
                "Total allocated: " + (files.Sum(f => f.Bytes) / 1048576d).ToString("N1") + " MB" + Environment.NewLine +
                string.Join(Environment.NewLine, files.Select(f => $"{f.Type}: {f.Bytes / 1048576d:N1} MB | {f.PhysicalPath}")) +
                Environment.NewLine + Environment.NewLine +
                "Relocation requires exclusive database access and local SQL Server file permissions. No new backup is created. Original files are retained for recovery.";
            _move.Enabled = true;
        }
        catch (Exception ex) { _details.Text = "Inspection failed: " + ex.Message; }
        finally { _inspect.Enabled = true; }
    }

    private async Task RelocateAsync()
    {
        if (_files.Count == 0) return;
        var destination = _destination.Text.Trim();
        if (string.IsNullOrWhiteSpace(destination)) { MessageBox.Show(this, "Choose a destination directory."); return; }
        string target;
        try
        {
            target = Path.GetFullPath(destination).TrimEnd(Path.DirectorySeparatorChar);
            if (!Path.IsPathFullyQualified(target) || !Directory.Exists(target))
                throw new IOException("Destination must be an existing local directory.");
            if (new DirectoryInfo(target).Attributes.HasFlag(FileAttributes.ReparsePoint))
                throw new IOException("Destination cannot be a reparse point.");
            if (_files.Any(f => string.Equals(Path.GetDirectoryName(f.PhysicalPath)?.TrimEnd(Path.DirectorySeparatorChar), target, StringComparison.OrdinalIgnoreCase)))
                throw new IOException("Destination must differ from the current file directories.");
            if (_files.Select(f => Path.GetFileName(f.PhysicalPath)).Distinct(StringComparer.OrdinalIgnoreCase).Count() != _files.Count)
                throw new IOException("Database files have duplicate names.");
            if (_files.Any(f => !File.Exists(f.PhysicalPath)))
                throw new IOException("Database files are not accessible from this machine. Remote SQL relocation is not supported.");
            if (_files.Any(f => File.Exists(Path.Combine(target, Path.GetFileName(f.PhysicalPath)))))
                throw new IOException("A destination file already exists.");
            var root = Path.GetPathRoot(target)!;
            var drive = new DriveInfo(root);
            if (drive.DriveType != DriveType.Fixed || drive.AvailableFreeSpace < _files.Sum(f => f.Bytes) * 1.15 + 256L * 1048576)
                throw new IOException("Destination must be a fixed drive with sufficient free space.");
        }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "Preflight failed", MessageBoxButtons.OK, MessageBoxIcon.Error); return; }

        var confirmation = MessageBox.Show(this,
            $"Database: {_database}\nDestination: {target}\n\nThis will disconnect database users, detach the database, copy its files and attach the copies WITHOUT creating a backup. Ensure an independent verified backup exists before proceeding. The application will be unavailable during relocation. Continue?",
            "Confirm database relocation", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2);
        if (confirmation != DialogResult.Yes) return;

        _inspect.Enabled = false; _move.Enabled = false;
        _relocationProgress.Value = 0;
        _stage.Text = "Checking SQL permissions...";
        var detached = false;
        var attachedAtDestination = false;
        var copies = _files.Select(f => Path.Combine(target, Path.GetFileName(f.PhysicalPath))).ToList();
        try
        {
            await using var connection = await OpenMasterAsync();
            await using (var admin = connection.CreateCommand())
            {
                admin.CommandText = "SELECT IS_SRVROLEMEMBER('sysadmin')";
                if (Convert.ToInt32(await admin.ExecuteScalarAsync()) != 1)
                    throw new UnauthorizedAccessException("SQL sysadmin permission is required.");
            }
            _stage.Text = "Detaching database...";
            _details.AppendText(Environment.NewLine + "Detaching database (no backup created)...");
            await ExecuteAsync(connection, $"ALTER DATABASE {Identifier(_database)} SET SINGLE_USER WITH ROLLBACK IMMEDIATE");
            try
            {
                await ExecuteAsync(connection, $"EXEC master.dbo.sp_detach_db @dbname={Literal(_database)}, @skipchecks=N'false'");
                detached = true;
                _relocationProgress.Value = 1;
                _stage.Text = "Copying database files...";
                _details.AppendText(Environment.NewLine + "Detached. Copying files...");
            }
            catch
            {
                // A timeout can occur after SQL Server has detached the database.
                // Probe the actual catalog using a fresh master connection rather
                // than trusting the local flag or the failed command connection.
                try
                {
                    await using var probe = await OpenMasterAsync();
                    await using var check = probe.CreateCommand();
                    check.CommandText = "SELECT COUNT(*) FROM sys.databases WHERE name = @db";
                    check.Parameters.AddWithValue("@db", _database);
                    detached = Convert.ToInt32(await check.ExecuteScalarAsync()) == 0;
                    if (!detached)
                        await ExecuteAsync(probe, $"ALTER DATABASE {Identifier(_database)} SET MULTI_USER");
                }
                catch (Exception probeError)
                {
                    // Unknown catalog state: do not assume a safe reattach.
                    detached = false;
                    _details.AppendText(Environment.NewLine + "CRITICAL: DETACH outcome could not be verified: " + probeError.Message + ". Inspect sys.databases and original MDF/LDF files manually.");
                }
                throw;
            }
            for (var i = 0; i < _files.Count; i++)
            {
                File.Copy(_files[i].PhysicalPath, copies[i], overwrite: false);
                if (new FileInfo(copies[i]).Length != new FileInfo(_files[i].PhysicalPath).Length)
                    throw new IOException("Copied file length mismatch.");
            }
            _relocationProgress.Value = 2;
            _stage.Text = "Attaching database at destination...";
            _details.AppendText(Environment.NewLine + "Files copied. Attaching...");
            var attachFiles = string.Join(", ", copies.Select(p => "(FILENAME = " + Literal(p) + ")"));
            await ExecuteAsync(connection, $"CREATE DATABASE {Identifier(_database)} ON {attachFiles} FOR ATTACH");
            detached = false;
            attachedAtDestination = true;
            _relocationProgress.Value = 3;
            _stage.Text = "Verifying database is online...";
            await ExecuteAsync(connection, $"ALTER DATABASE {Identifier(_database)} SET MULTI_USER");
            await using (var verify = connection.CreateCommand())
            {
                verify.CommandText = "SELECT state_desc FROM sys.databases WHERE name = @db";
                verify.Parameters.AddWithValue("@db", _database);
                if (!string.Equals(Convert.ToString(await verify.ExecuteScalarAsync()), "ONLINE", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("Database did not return to ONLINE state.");
            }
            _relocationProgress.Value = 4;
            _stage.Text = "Completed — database online";
            _details.AppendText(Environment.NewLine + "Relocation completed. Original database files retained. No backup was created.");
            MessageBox.Show(this, "Relocation completed. Keep the original database files until runtime review.", "Success");
            await InspectAsync();
        }
        catch (Exception ex)
        {
            _stage.Text = "Failed — inspect recovery log";
            _details.AppendText(Environment.NewLine + "ERROR: " + ex.Message);
            if (detached && !attachedAtDestination)
            {
                try
                {
                    await using var recovery = await OpenMasterAsync();
                    // ATTACH can succeed server-side even if the client times out.
                    // Resolve the actual file ownership before any compensating ATTACH.
                    var registered = new List<string>();
                    await using (var check = recovery.CreateCommand())
                    {
                        check.CommandText = @"SELECT mf.physical_name
FROM sys.master_files mf JOIN sys.databases d ON d.database_id = mf.database_id
WHERE d.name = @db ORDER BY mf.file_id";
                        check.Parameters.AddWithValue("@db", _database);
                        await using var reader = await check.ExecuteReaderAsync();
                        while (await reader.ReadAsync()) registered.Add(reader.GetString(0));
                    }
                    if (registered.Count == 0)
                    {
                        var originals = string.Join(", ", _files.Select(f => "(FILENAME = " + Literal(f.PhysicalPath) + ")"));
                        await ExecuteAsync(recovery, $"CREATE DATABASE {Identifier(_database)} ON {originals} FOR ATTACH");
                        _details.AppendText(Environment.NewLine + "Rollback: original files reattached.");
                    }
                    else
                    {
                        var actual = registered.Select(Path.GetFullPath).ToHashSet(StringComparer.OrdinalIgnoreCase);
                        var originalPaths = _files.Select(f => Path.GetFullPath(f.PhysicalPath)).ToHashSet(StringComparer.OrdinalIgnoreCase);
                        var destinationPaths = copies.Select(Path.GetFullPath).ToHashSet(StringComparer.OrdinalIgnoreCase);
                        if (!actual.SetEquals(originalPaths) && !actual.SetEquals(destinationPaths))
                            throw new IOException("Database is attached to unexpected files; automatic recovery blocked.");
                        _details.AppendText(Environment.NewLine + (actual.SetEquals(destinationPaths)
                            ? "Recovery: destination files were already attached; duplicate ATTACH skipped."
                            : "Recovery: original files were already attached; duplicate ATTACH skipped."));
                    }
                    await ExecuteAsync(recovery, $"ALTER DATABASE {Identifier(_database)} SET MULTI_USER");
                }
                catch (Exception rollback)
                {
                    _details.AppendText(Environment.NewLine + "CRITICAL: automatic recovery failed or catalog ownership is uncertain: " + rollback.Message + ". Original files were retained. Manual SQL recovery is required.");
                }
            }
            else if (attachedAtDestination)
            {
                // The destination database is already attached. A failed MULTI_USER
                // transition or ONLINE probe must not leave it in SINGLE_USER mode
                // without a compensating attempt. Never detach or delete either copy.
                try
                {
                    await using var recovery = await OpenMasterAsync();
                    await ExecuteAsync(recovery, $"ALTER DATABASE {Identifier(_database)} SET MULTI_USER");
                    _details.AppendText(Environment.NewLine + "Recovery: destination database returned to MULTI_USER; verify ONLINE state before use.");
                }
                catch (Exception recoveryError)
                {
                    _details.AppendText(Environment.NewLine + "CRITICAL: destination database may remain SINGLE_USER: " + recoveryError.Message + ". Manual SQL recovery is required; original files retained.");
                }
            }
            MessageBox.Show(this, "Relocation failed. Inspect the log and database status before retrying.", "Relocation error", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally { _inspect.Enabled = true; }
    }
}
