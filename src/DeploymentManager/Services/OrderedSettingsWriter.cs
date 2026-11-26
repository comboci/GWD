namespace GenericWeb.DeploymentManager.Services;

// This queue owns no controls or mutable DeploymentProfile. Callers pass
// field mutations closed over values captured on the originating thread.
internal sealed class OrderedSettingsWriter
{
    private readonly object _gate = new();
    private readonly HashSet<string> _failures = new(StringComparer.Ordinal);
    private Task _tail = Task.CompletedTask;

    internal void Enqueue(string setting, Action write)
    {
        lock (_gate)
        {
            _tail = _tail.ContinueWith(_ =>
            {
                try
                {
                    write();
                    lock (_gate) _failures.Remove(setting);
                }
                catch (Exception ex)
                {
                    lock (_gate) _failures.Add(setting);
                    InternalDiagnostics.Record(new IOException("Settings write failed (" + ex.GetType().Name + ")."),
                        "Settings writer: " + setting);
                }
            }, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
        }
    }

    internal bool TryDrain(TimeSpan timeout)
    {
        Task tail;
        lock (_gate) tail = _tail;
        return tail.Wait(timeout);
    }

    internal int FailureCount { get { lock (_gate) return _failures.Count; } }
}
