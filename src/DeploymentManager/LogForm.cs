using System.Text;
using GenericWeb.DeploymentManager.Services;

namespace GenericWeb.DeploymentManager;

public sealed class LogForm : Form
{
    private const int MaxUiChars = 500_000;
    private readonly RichTextBox _box = new() { ReadOnly = true, ScrollBars = RichTextBoxScrollBars.Both, WordWrap = false, Dock = DockStyle.Fill, Font = new Font("Consolas", 9.5F), BackColor = Color.White };
    private readonly Font _boldLogFont;
    private readonly string _logFile;
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 750 };
    private readonly Button _pause = new() { Text = "Pause live view", AutoSize = true };
    private readonly Label _status = new() { AutoSize = true, ForeColor = Color.DimGray, Padding = new Padding(8, 6, 0, 0) };
    private readonly CancellationTokenSource _lifetime = new();
    private long _offset;
    private int _generation;
    private bool _initial = true;
    private bool _reading;
    private bool _paused;
    private bool _closing;
    private Task? _copyPending;
    private bool _lifetimeDisposed;

    public LogForm(string logFile)
    {
        _logFile = logFile;
        _boldLogFont = new Font(_box.Font, FontStyle.Bold);
        Text = "Deployment Manager — View Log"; Width = 1100; Height = 720; StartPosition = FormStartPosition.CenterParent;
        KeyPreview = true;
        KeyDown += (_, e) => { if (e.KeyCode == Keys.Escape) { DialogResult = DialogResult.Cancel; Close(); } };
        Controls.Add(_box);
        var top = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 44, Padding = new Padding(8), AutoScroll = true, WrapContents = false };
        var refresh = new Button { Text = "Refresh", AutoSize = true };
        var open = new Button { Text = "Open log folder", AutoSize = true };
        var dump = new Button { Text = "Dump current log to Desktop", AutoSize = true };
        var latest = new Button { Text = "Jump to Latest", AutoSize = true };
        top.Controls.AddRange(new Control[] { refresh, latest, _pause, open, dump, _status });
        Controls.Add(top); top.BringToFront();
        refresh.Click += (_, _) => ResetTail();
        latest.Click += (_, _) => ResetTail();
        _pause.Click += (_, _) =>
        {
            _paused = !_paused;
            _generation++; // Do not publish a read already queued before Pause.
            _pause.Text = _paused ? "Resume live view" : "Pause live view";
            _status.Text = _paused ? "Paused — original log continues on disk" : "Live";
            if (!_paused) _ = ReadNextAsync();
        };
        open.Click += (_, _) => System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(Path.GetDirectoryName(logFile)!) { UseShellExecute = true });
        dump.Click += async (_, _) =>
        {
            dump.Enabled = false;
            try { await DumpCurrentLogAsync(); }
            finally { if (!dump.IsDisposed) dump.Enabled = true; }
        };
        _timer.Tick += async (_, _) => await ReadNextAsync();
        Shown += async (_, _) => { _timer.Start(); await ReadNextAsync(); };
        FormClosed += (_, _) => StopReading();
    }

    private void ResetTail()
    {
        _generation++;
        _initial = true;
        _offset = 0;
        _paused = false;
        _pause.Text = "Pause live view";
        _box.Clear();
        _ = ReadNextAsync();
    }

    private void StopReading()
    {
        if (_closing) return;
        _closing = true;
        _generation++;
        _timer.Stop();
        _lifetime.Cancel();
    }

    private static (Color Color, bool Bold) Classify(string text)
    {
        bool Has(string value) => text.Contains(value, StringComparison.OrdinalIgnoreCase);
        if (Has("[stage-start]")) return (Color.RoyalBlue, true);
        if (Has("[stage-end]")) return (Color.ForestGreen, true);
        if (Has("[stage-separator]")) return (Color.SteelBlue, false);
        if (Has("error") || Has("failed") || Has("unsuccessful")) return (Color.Firebrick, true);
        if (Has("warning") || Has("warn") || Has("[cancel]")) return (Color.DarkOrange, false);
        if (Has("successful") || Has("success") || Has("exit code 0")) return (Color.ForestGreen, false);
        return (Color.DimGray, false);
    }

    // Append one run per adjacent style, not one native RichTextBox call per line.
    private void AppendColorized(string text)
    {
        var run = new StringBuilder();
        var style = (Color: Color.DimGray, Bold: false);
        void Flush()
        {
            if (run.Length == 0) return;
            _box.SelectionStart = _box.TextLength;
            _box.SelectionLength = 0;
            _box.SelectionColor = style.Color;
            _box.SelectionFont = style.Bold ? _boldLogFont : _box.Font;
            _box.AppendText(run.ToString());
            run.Clear();
        }
        for (var start = 0; start < text.Length;)
        {
            var newline = text.IndexOf('\n', start);
            var end = newline < 0 ? text.Length : newline + 1;
            var line = text.Substring(start, end - start);
            var next = Classify(line);
            if (next != style) { Flush(); style = next; }
            run.Append(line);
            start = end;
        }
        Flush();
        _box.SelectionColor = _box.ForeColor;
        _box.SelectionFont = _box.Font;
    }

    private async Task ReadNextAsync()
    {
        if (_reading || _paused || _closing || IsDisposed || Disposing) return;
        _reading = true;
        var generation = _generation;
        var offset = _offset;
        var initial = _initial;
        try
        {
            var batch = await Task.Run(() => LogPreviewReader.ReadBatch(_logFile, offset, initial));
            if (_closing || IsDisposed || Disposing || generation != _generation) return;
            if (!batch.Exists)
            {
                _status.Text = "No log entries yet.";
                _initial = true; _offset = 0; _box.Clear();
                return;
            }
            if (batch.Reset)
            {
                _box.Clear();
                if (batch.OlderBytesHidden)
                    AppendColorized("[... latest log preview; original file contains all earlier bytes ...]\r\n");
            }
            _initial = false;
            _offset = batch.NextOffset;
            if (batch.Text.Length == 0) return;
            AppendColorized(batch.Text);
            if (_box.TextLength > MaxUiChars)
            {
                _box.Select(0, _box.TextLength - MaxUiChars / 2);
                _box.SelectedText = "[... older preview hidden; original log is unchanged ...]\r\n";
            }
            _box.SelectionStart = _box.TextLength;
            _box.ScrollToCaret();
            _status.Text = "Live — bounded preview; full log on disk";
        }
        catch (Exception ex)
        {
            if (!_closing && !IsDisposed && generation == _generation)
                _status.Text = "Read failed: " + SecretRedactor.Redact(ex.Message);
        }
        finally { _reading = false; }
    }

    private async Task DumpCurrentLogAsync()
    {
        try
        {
            if (!File.Exists(_logFile)) { MessageBox.Show(this, "There is no current log file yet.", "Log dump", MessageBoxButtons.OK, MessageBoxIcon.Information); return; }
            var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            var destination = Path.Combine(desktop, $"NSP-Deployment-Log-{DateTime.Now:yyyyMMdd-HHmmss}.txt");
            var token = _lifetime.Token;
            var copy = Task.Run(() => LogPreviewReader.CopySnapshotAsync(_logFile, destination, token));
            _copyPending = copy;
            try { await copy; }
            finally { _copyPending = null; }
            if (!_closing && !IsDisposed)
                MessageBox.Show(this, "Complete log snapshot copied to Desktop:\r\n" + destination, "Log dump created", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (OperationCanceledException) when (_closing) { }
        catch (Exception ex)
        {
            if (!_closing && !IsDisposed) MessageBox.Show(this, SecretRedactor.Redact(ex.Message), "Unable to create log dump", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) StopReading();
        try { base.Dispose(disposing); }
        finally
        {
            if (disposing) { _timer.Dispose(); _boldLogFont?.Dispose(); }
            if (disposing && !_lifetimeDisposed)
            {
                _lifetimeDisposed = true;
                var pending = _copyPending;
                if (pending is null || pending.IsCompleted) _lifetime.Dispose();
                else _ = pending.ContinueWith(_ => _lifetime.Dispose(), TaskScheduler.Default);
            }
        }
    }
}
