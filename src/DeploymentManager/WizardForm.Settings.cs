namespace GenericWeb.DeploymentManager;

public sealed partial class WizardForm
{
    // The constructor initializes these; only the ordered writer advances them.
    private (string ApplicationName, string SiteName, string DatabaseName) _identitySettingsBaseline;
    private (string HostingRoot, string DeploymentLayout, bool CleanupPreviousDeployments, bool UseDefaultIisWebsite) _hostingSettingsBaseline;

    private void QueueDeferredSettingsSave(string key, Action save)
    {
        _deferredSettingsSaves[key] = (++_settingsWriteOrder, save);
        _settingsSaveTimer.Stop();
        _settingsSaveTimer.Start();
    }

    private void CancelDeferredSettingsSave(string key)
    {
        _deferredSettingsSaves.Remove(key);
        if (_deferredSettingsSaves.Count == 0) _settingsSaveTimer.Stop();
    }

    // This drains the debounce buffer to the worker, not to disk.
    private void FlushDeferredSettingsSaves()
    {
        _settingsSaveTimer.Stop();
        if (_deferredSettingsSaves.Count == 0) return;
        var pending = _deferredSettingsSaves.OrderBy(entry => entry.Value.Order).ToArray();
        foreach (var (key, settings) in pending)
        {
            if (_settingsWriter.Enqueue(key, settings.Save))
                _deferredSettingsSaves.Remove(key);
        }
        // Retain the bounded set of UI keys until the worker has room.
        if (_deferredSettingsSaves.Count > 0) _settingsSaveTimer.Start();
    }

    private bool DrainSettingsSaves(bool closing = false)
    {
        FlushDeferredSettingsSaves();
        if (_deferredSettingsSaves.Count == 0 && _settingsWriter.TryDrain(TimeSpan.FromSeconds(2), stopAccepting: closing))
        {
            if (_settingsWriter.FailureCount > 0)
                NotifyUi(UiNoticeKind.Warning, "Some settings could not be saved. Check Diagnostics.");
            return true;
        }
        if (closing) _settingsWriter.ResumeAccepting();
        NotifyUi(UiNoticeKind.Warning, "Settings are still being saved. Try again shortly.");
        return false;
    }

    private void PostSettingsSave(string key, Action save)
    {
        FlushDeferredSettingsSaves();
        if (!_settingsWriter.Enqueue(key, save))
        {
            QueueDeferredSettingsSave(key, save);
            NotifyUi(UiNoticeKind.Warning, "Settings write has been deferred. Your edit has not been saved yet.");
        }
    }

    private void SaveIdentitySettings(bool defer)
    {
        var values = (App: _profile.ApplicationName, Site: _profile.SiteName, Database: _profile.DatabaseName);
        void Save()
        {
            if (values == _identitySettingsBaseline) return;
            _appData.SaveDeploymentIdentitySettings(values.App, values.Site, values.Database,
                throwOnFailure: true, expected: _identitySettingsBaseline);
            _identitySettingsBaseline = values;
        }
        if (defer) QueueDeferredSettingsSave("identity", Save); else PostSettingsSave("identity", Save);
    }

    private void SaveHostingSettings(bool defer)
    {
        var values = (Root: _profile.HostingRoot, Layout: _profile.DeploymentLayout,
            Cleanup: _profile.CleanupPreviousDeployments, DefaultSite: _profile.UseDefaultIisWebsite);
        void Save()
        {
            if (values == _hostingSettingsBaseline || string.IsNullOrWhiteSpace(values.Root)) return;
            _appData.SaveHostingSettings(values.Root, values.Layout, values.Cleanup, values.DefaultSite,
                throwOnFailure: true, expected: _hostingSettingsBaseline);
            _hostingSettingsBaseline = values;
        }
        if (defer) QueueDeferredSettingsSave("hosting", Save); else PostSettingsSave("hosting", Save);
    }

    private void SaveFolderSetting(string key, string value, Action<string> write, bool defer)
    {
        void Save() => write(value);
        if (defer) QueueDeferredSettingsSave(key, Save); else PostSettingsSave(key, Save);
    }

    private void SaveDependencyMode(string mode) =>
        PostSettingsSave("dependency-mode", () => _appData.SaveDependencyResolutionMode(mode));
}
