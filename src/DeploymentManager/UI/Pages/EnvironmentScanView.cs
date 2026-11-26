using static GenericWeb.DeploymentManager.UI.Controls.StatusCardUi;

namespace GenericWeb.DeploymentManager.UI.Pages;

internal readonly record struct ScanCardState(string Title, string Detail, bool Ready);

// The view owns controls and subscriptions only; the shell owns the profile,
// preflight, and navigation. No scan or deployment service is resolved here.
internal sealed class EnvironmentScanView : UserControl
{
    private readonly Button _scan = new() { Text = "Re-scan environment", AutoSize = true };
    private readonly TableLayoutPanel _cards = new()
    {
        Dock = DockStyle.Fill, AutoScroll = true, ColumnCount = 2, RowCount = 3, Padding = new Padding(0, 12, 0, 0)
    };
    private readonly GroupBox[] _cardViews;
    private Func<Button, Task>? _scanRequested;

    internal EnvironmentScanView(IReadOnlyList<ScanCardState> initial)
    {
        Dock = DockStyle.Fill;
        TabStop = false;
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 2 };
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.Controls.Add(_scan, 0, 0);
        _cards.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        _cards.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        for (var row = 0; row < 3; row++) _cards.RowStyles.Add(new RowStyle(SizeType.Percent, 33.333F));
        _cardViews = new GroupBox[initial.Count];
        for (var i = 0; i < initial.Count; i++)
        {
            var model = initial[i];
            _cardViews[i] = StatusCard(model.Title, model.Detail, model.Ready);
            _cards.Controls.Add(_cardViews[i], i % 2, i / 2);
        }
        root.Controls.Add(_cards, 0, 1);
        Controls.Add(root);
    }

    internal void Activate(Func<Button, Task> scanRequested)
    {
        Deactivate();
        if (IsDisposed || Disposing) return;
        _scanRequested = scanRequested;
        _scan.Click += OnScanClick;
    }

    internal void Deactivate()
    {
        _scan.Click -= OnScanClick;
        _scanRequested = null;
    }

    internal void RequestScan()
    {
        if (_scanRequested is not null && !IsDisposed && !Disposing) _scan.PerformClick();
    }

    internal void RefreshCards(IReadOnlyList<ScanCardState> state)
    {
        if (_scanRequested is null || IsDisposed || Disposing || state.Count != _cardViews.Length) return;
        _cards.SuspendLayout();
        try
        {
            for (var i = 0; i < state.Count; i++)
            {
                var model = state[i];
                UpdateStatusCard(_cardViews[i], model.Title, model.Detail, model.Ready);
            }
        }
        finally { _cards.ResumeLayout(true); }
    }

    private async void OnScanClick(object? sender, EventArgs e)
    {
        if (_scanRequested is { } request) await request(_scan);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) Deactivate();
        base.Dispose(disposing);
    }
}
