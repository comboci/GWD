namespace GenericWeb.DeploymentManager.Services;

// Active write is never replaced. A newer pending write for the same logical
// setting moves to the end, so pending order reflects the latest actual edits.
internal sealed class OrderedSettingsWriter
{
    private const int MaxPending = 32;
    private readonly object _gate = new();
    private readonly LinkedList<(string Setting, Action Write)> _pending = new();
    private readonly HashSet<string> _failures = new(StringComparer.Ordinal);
    private Task _worker = Task.CompletedTask;
    private bool _running;
    private bool _accepting = true;

    internal bool Enqueue(string setting, Action write)
    {
        lock (_gate)
        {
            if (!_accepting) return false;
            for (var node = _pending.First; node is not null; node = node.Next)
                if (node.Value.Setting == setting) { _pending.Remove(node); break; }
            if (_pending.Count >= MaxPending) return false;
            _pending.AddLast((setting, write));
            if (!_running)
            {
                _running = true;
                _worker = Task.Run(Work);
            }
            return true;
        }
    }

    private void Work()
    {
        for (;;)
        {
            (string Setting, Action Write) job;
            lock (_gate)
            {
                if (_pending.First is null) { _running = false; return; }
                job = _pending.First.Value;
                _pending.RemoveFirst();
            }
            try
            {
                job.Write();
                lock (_gate) _failures.Remove(job.Setting);
            }
            catch (Exception ex)
            {
                lock (_gate) _failures.Add(job.Setting);
                InternalDiagnostics.Record(new IOException("Settings write failed (" + ex.GetType().Name + ")."),
                    "Settings writer: " + job.Setting);
            }
        }
    }

    internal bool TryDrain(TimeSpan timeout, bool stopAccepting = false)
    {
        Task worker;
        lock (_gate)
        {
            if (stopAccepting) _accepting = false;
            worker = _worker;
        }
        return worker.Wait(timeout);
    }

    internal void ResumeAccepting() { lock (_gate) _accepting = true; }
    internal int FailureCount { get { lock (_gate) return _failures.Count; } }
}
