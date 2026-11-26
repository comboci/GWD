namespace GenericWeb.DeploymentManager;

public sealed partial class WizardForm
{
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
        _deferredSettingsSaves.Clear();
        foreach (var (key, settings) in pending)
            _settingsWriter.Enqueue(key, settings.Save);
    }

    private bool DrainSettingsSaves()
    {
        FlushDeferredSettingsSaves();
        if (_settingsWriter.TryDrain(TimeSpan.FromSeconds(2)))
        {
            if (_settingsWriter.FailureCount > 0)
                NotifyUi(UiNoticeKind.Warning, "Some settings could not be saved. Check Diagnostics.");
            return true;
        }
        NotifyUi(UiNoticeKind.Warning, "Settings are still being saved. Try again shortly.");
        return false;
    }

    private void PostSettingsSave(string key, Action save)
    {
        FlushDeferredSettingsSaves();
        _settingsWriter.Enqueue(key, save);
    }

    private void SaveIdentitySettings(bool defer)
    {
        var values = (App: _profile.ApplicationName, Site: _profile.SiteName, Database: _profile.DatabaseName);
        void Save() => _appData.SaveDeploymentIdentitySettings(values.App, values.Site, values.Database, throwOnFailure: true);
        if (defer) QueueDeferredSettingsSave("identity", Save); else PostSettingsSave("identity", Save);
    }

    private void SaveHostingSettings(bool defer)
    {
        var values = (Root: _profile.HostingRoot, Layout: _profile.DeploymentLayout,
            Cleanup: _profile.CleanupPreviousDeployments, DefaultSite: _profile.UseDefaultIisWebsite);
        void Save() => _appData.SaveHostingSettings(values.Root, values.Layout, values.Cleanup, values.DefaultSite, throwOnFailure: true);
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
