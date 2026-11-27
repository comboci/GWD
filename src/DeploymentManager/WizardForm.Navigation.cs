namespace GenericWeb.DeploymentManager;

public sealed partial class WizardForm
{
    private sealed record WizardStage(string Caption, Action<WizardForm> Render);

    // One ordered registry for the visible stage names and their presenters.
    // Manual and automatic validation keep the same stable index contract.
    private static readonly WizardStage[] Stages =
    {
        new("Project Source & Dependencies", static wizard => wizard.BuildProjectSourcePage()),
        new("Target & Identity", static wizard => wizard.BuildTargetPage()),
        new("Environment Scan", static wizard => wizard.BuildScanPage()),
        new("IIS", static wizard => wizard.BuildIisPage()),
        new("Installers & Runtimes", static wizard => wizard.BuildInstallersPage()),
        new("SQL Server & Database", static wizard => wizard.BuildSqlPage()),
        new("Deployment Location", static wizard => wizard.BuildDeploymentLocationPage()),
        new("Review & Deploy", static wizard => wizard.BuildReviewPage()),
    };

    private readonly string[] _steps = Stages.Select(stage => stage.Caption).ToArray();

    private bool CanNavigateInteractively() =>
        !IsDisposed && !Disposing && !_operationRunning && !_autoDeployRunning &&
        !_dependencySyncRunning && !_navigationInProgress && !_pageActionRunning;

    // Keep the original one-argument entry point for existing smoke tests.
    private void ShowPage(int index) => NavigateToPage(index, forceRebuild: false);

    private void RebuildCurrentPage() => NavigateToPage(_pageIndex, forceRebuild: true);

    private void NavigateToPage(int index, bool forceRebuild)
    {
        if (IsDisposed || Disposing) return;
        var requested = Math.Clamp(index, 0, _steps.Length - 1);
        if (!forceRebuild && _renderedPageIndex == requested && _pageIndex == requested &&
            _content.Controls.Count > 0)
        {
            // Re-selecting the active step is not a request to reset its controls.
            UpdateStepIndicator();
            UpdateNavigationState();
            return;
        }

        _content.SuspendLayout();
        try
        {
            // Flush the outgoing view while its controls and page identity still exist.
            DisposeContentControls();
            _pageIndex = requested;
            if (_lastLoggedPageIndex != _pageIndex)
            {
                if (_lastLoggedPageIndex >= 0)
                    AppendLog($"[STAGE-END] {_steps[_lastLoggedPageIndex]}");
                AppendLog("[STAGE-SEPARATOR] ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━");
                AppendLog($"[STAGE-START] Step {_pageIndex + 1}/{_steps.Length} — {_steps[_pageIndex]}");
                _lastLoggedPageIndex = _pageIndex;
            }
            _activity.Visible = _pageIndex != _steps.Length - 1;
            UpdateDeploymentIndicator();
            UpdateStepIndicator();
            _stepIndicator.Text = $"Step {_pageIndex + 1} of {_steps.Length}";
            _back.Enabled = _pageIndex > 0;
            _next.Text = _pageIndex == _steps.Length - 1 ? ((_persistentState.LastResult == "Failed" || _persistentState.LastResult == "RecoveryFailed") ? "Retry Deploy" : (_persistentState.LastResult == "Successful" ? "Deploy Again" : "Deploy")) : "Next >";
            _progress.Value = (int)Math.Round((_pageIndex / (double)(_steps.Length - 1)) * 100);

            Stages[_pageIndex].Render(this);
            _renderedPageIndex = _pageIndex;
            GwdUi.StyleTree(_content);
            ApplyInteractiveCursors(_content);
        }
        finally { _content.ResumeLayout(true); }
        UpdateNavigationState();
    }

    private void DisposeContentControls()
    {
        _renderedPageIndex = -1;
        _scanView?.Deactivate();
        _scanView = null;
        _reviewView?.Deactivate();
        _reviewView = null;
        _locationView?.Deactivate();
        _locationView = null;
        if (_pageIndex == 0)
        {
            CancelBackgroundVerification();
            InvalidateNavigationSourceReadiness();
        }
        // Source/target controls may own the latest unsaved edit. Their queued
        // actions must execute while controls are still alive.
        FlushDeferredSettingsSaves();
        _saveProfileAction = null;
        _loadProfileAction = null;
        _lockSourceAction = null;
        _exportSourceAction = null;
        if (_content.Controls.Count == 0) return;

        // The live log textbox is reused across wizard pages and is hosted inside
        // the review page's split container. Detach it before disposing the page
        // tree; otherwise disposing the split panel also disposes _log and the
        // next deployment attempts to access a disposed TextBox.
        if (!_log.IsDisposed && _log.Parent is not null)
            _log.Parent.Controls.Remove(_log);

        var controls = _content.Controls.Cast<Control>().ToArray();
        _content.Controls.Clear();
        foreach (var control in controls) control.Dispose();
    }
}
