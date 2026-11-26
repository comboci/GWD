using GenericWeb.DeploymentManager.Models;
using System.Diagnostics;
using System.Net.Http;

namespace GenericWeb.DeploymentManager.Services;

public sealed record HealthSnapshot(DateTime CheckedAt, bool IisInstalled, int SiteCount, int AppPoolCount, bool? SqlOk, string SqlDetail, int? HttpStatus, string HttpDetail)
{
    public DateTime? IisCheckedAt { get; init; }
    public DateTime? SqlCheckedAt { get; init; }
    public DateTime? HttpCheckedAt { get; init; }
    internal long Generation { get; init; }
    internal long Sequence { get; init; }
    internal long? SqlSuccessStamp { get; init; }
}

public sealed class BackgroundHealthMonitor : IDisposable
{
    private sealed record ProbeScope(string TargetMode, string TargetHost, string SiteName, int Port, string HealthPath, string SqlFingerprint);
    private readonly EnvironmentService _environment;
    private readonly SqlServerService _sql;
    private readonly CancellationTokenSource _stop = new();
    private readonly HttpClient _http = new(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(8) };
    private readonly object _gate = new();
    private ProbeScope _scope;
    private DeploymentProfile _input;
    private long _generation;
    private long _sequence;
    private Task? _loop;
    private Task? _flight;
    private CancellationTokenSource? _inFlight;
    private bool _pending;
    private bool _disposed;
    private HealthSnapshot? _latest;

    public HealthSnapshot? Latest { get { lock (_gate) return _latest; } }
    public event Action<HealthSnapshot>? Updated;

    public BackgroundHealthMonitor(EnvironmentService environment, SqlServerService sql, DeploymentProfile profile)
    {
        _environment = environment;
        _sql = sql;
        _input = profile.SnapshotForHealth();
        _scope = Key(_input);
    }

    // Called by the owner thread. Background work never reads the live profile.
    public void SubmitProfile(DeploymentProfile profile)
    {
        var input = profile.SnapshotForHealth();
        var key = Key(input);
        CancellationTokenSource? cancel = null;
        HealthSnapshot? invalidated = null;
        lock (_gate)
        {
            if (_disposed || key == _scope) return;
            _scope = key;
            _input = input;
            _generation++;
            invalidated = Empty(_generation, ++_sequence, "Configuration changed; not checked.");
            _latest = invalidated;
            if (_loop is not null)
            {
                _pending = true;
                cancel = _inFlight;
                StartFlightLocked();
            }
        }
        TryCancel(cancel);
        Updated?.Invoke(invalidated!);
    }

    private ProbeScope Key(DeploymentProfile profile) => new(profile.TargetMode, profile.TargetHost, profile.SiteName,
        profile.HttpPort, profile.HealthPath, _sql.Connections.Fingerprint(profile));

    public bool IsCurrent(HealthSnapshot snapshot)
    {
        lock (_gate) return !_disposed && _latest?.Sequence == snapshot.Sequence && _generation == snapshot.Generation;
    }

    public void Start()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _loop ??= Task.Run(LoopAsync);
        }
    }

    public Task RefreshNowAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        Task flight;
        lock (_gate)
        {
            if (_disposed) return Task.CompletedTask;
            if (_flight is null)
            {
                _pending = true;
                StartFlightLocked();
            }
            flight = _flight!;
        }
        // The caller's cancellation only detaches its own await; the single
        // owner task can still satisfy other callers and the periodic loop.
        return ct.CanBeCanceled ? flight.WaitAsync(ct) : flight;
    }

    private void StartFlightLocked()
    {
        if (_flight is null) _flight = Task.Run(DrainAsync);
    }

    public bool HasRecentSuccessfulSql(TimeSpan maxAge)
    {
        lock (_gate)
        {
            return !_disposed && maxAge > TimeSpan.Zero && _latest?.SqlOk == true &&
                _latest.SqlSuccessStamp is { } stamp && Stopwatch.GetElapsedTime(stamp) <= maxAge;
        }
    }

    public void ReportSqlSuccess(string detail, DeploymentProfile ownerProfile) =>
        ReportSqlEvidence(detail, ownerProfile, _sql.Connections.State(ownerProfile).Verified);

    public void ReportSqlFailure(string detail, DeploymentProfile ownerProfile)
    {
        _sql.Connections.Invalidate(ownerProfile, detail);
        ReportSqlEvidence(detail, ownerProfile, false);
    }

    private void ReportSqlEvidence(string detail, DeploymentProfile ownerProfile, bool verified)
    {
        SubmitProfile(ownerProfile);
        HealthSnapshot? result;
        lock (_gate)
        {
            if (_disposed) return;
            var current = _latest ?? Empty(_generation, _sequence, "Not checked.");
            result = current with { Sequence = ++_sequence, SqlCheckedAt = DateTime.Now, SqlOk = verified,
                SqlDetail = SecretRedactor.Redact(detail), SqlSuccessStamp = verified ? Stopwatch.GetTimestamp() : null };
            _latest = result;
        }
        Updated?.Invoke(result);
    }

    private static HealthSnapshot Empty(long generation, long sequence, string detail) =>
        new(default, false, 0, 0, null, detail, null, detail) { Generation = generation, Sequence = sequence };

    private async Task LoopAsync()
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(5), _stop.Token);
            while (!_stop.IsCancellationRequested)
            {
                await RefreshNowAsync(_stop.Token);
                await Task.Delay(TimeSpan.FromMinutes(10), _stop.Token);
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
    }

    private async Task DrainAsync()
    {
        while (true)
        {
            DeploymentProfile source;
            long generation;
            long sqlSequence;
            CancellationTokenSource request;
            lock (_gate)
            {
                if (_disposed || !_pending)
                {
                    _flight = null;
                    return;
                }
                _pending = false;
                source = _input;
                generation = _generation;
                sqlSequence = _sequence;
                request = _inFlight = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
            }
            try
            {
                var result = await CheckAsync(source, request.Token);
                HealthSnapshot? published = null;
                lock (_gate)
                {
                    if (!_disposed && !request.IsCancellationRequested && generation == _generation)
                    {
                        // Keep any newer manual SQL result; an older poll is not
                        // allowed to erase operator verification or failure.
                        if (_latest is { } last && last.Generation == generation && last.Sequence > sqlSequence && last.SqlCheckedAt.HasValue)
                            result = result with { SqlCheckedAt = last.SqlCheckedAt, SqlOk = last.SqlOk, SqlDetail = last.SqlDetail, SqlSuccessStamp = last.SqlSuccessStamp };
                        published = result with { Generation = generation, Sequence = ++_sequence };
                        _latest = published;
                    }
                }
                if (published is not null) Updated?.Invoke(published);
            }
            catch (OperationCanceledException) when (request.IsCancellationRequested) { }
            catch (Exception ex) { InternalDiagnostics.Record(ex, "Background health monitor cycle"); }
            finally
            {
                lock (_gate) { if (ReferenceEquals(_inFlight, request)) _inFlight = null; }
                request.Dispose();
            }
        }
    }

    private async Task<HealthSnapshot> CheckAsync(DeploymentProfile source, CancellationToken ct)
    {
        var local = !string.Equals(source.TargetMode, "Remote IIS Server", StringComparison.OrdinalIgnoreCase);
        bool iis = false;
        int sites = 0, pools = 0;
        bool? sqlOk = null;
        string sqlDetail = "Not configured";
        int? httpStatus = null;
        string httpDetail = local ? "Not checked" : "Remote target is not checked by the local background monitor.";
        DateTime? iisTime = null, sqlTime = null, httpTime = null;
        long? sqlStamp = null;
        if (local)
        {
            try
            {
                iis = (await _environment.CheckIisAsync()).Installed;
                ct.ThrowIfCancellationRequested();
                if (iis) (sites, pools) = await _environment.CountIisObjectsAsync(ct);
                iisTime = DateTime.Now;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex) { iis = false; InternalDiagnostics.Record(ex, "Background IIS health"); }
        }
        ct.ThrowIfCancellationRequested();
        if (!string.IsNullOrWhiteSpace(source.SqlInstance))
        {
            // This session is independent of the live profile: health reports
            // administrative reachability, not database readiness.
            var probe = source.SnapshotForHealth();
            try
            {
                sqlDetail = await _sql.TestConnectionWithSafeFallbackAsync(probe, ct);
                sqlOk = _sql.Connections.State(probe).Verified;
                if (sqlOk == true) sqlStamp = Stopwatch.GetTimestamp();
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex) { sqlOk = false; sqlDetail = SecretRedactor.Redact(ex.Message); }
            sqlTime = DateTime.Now;
        }
        ct.ThrowIfCancellationRequested();
        if (local && iis && !string.IsNullOrWhiteSpace(source.SiteName))
        {
            try
            {
                var parts = source.HealthPath.Split('?', 2);
                var probe = new UriBuilder(Uri.UriSchemeHttp, "localhost", source.HttpPort)
                { Path = parts[0], Query = parts.Length == 2 ? parts[1] : string.Empty };
                using var response = await _http.GetAsync(probe.Uri, HttpCompletionOption.ResponseHeadersRead, ct);
                httpStatus = (int)response.StatusCode;
                httpDetail = "HTTP " + httpStatus;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex) { httpDetail = SecretRedactor.Redact(ex.Message); }
            httpTime = DateTime.Now;
        }
        ct.ThrowIfCancellationRequested();
        return new(DateTime.Now, iis, sites, pools, sqlOk, sqlDetail, httpStatus, httpDetail)
        { IisCheckedAt = iisTime, SqlCheckedAt = sqlTime, HttpCheckedAt = httpTime, SqlSuccessStamp = sqlStamp };
    }

    private static void TryCancel(CancellationTokenSource? cancellation)
    {
        try { cancellation?.Cancel(); }
        catch (ObjectDisposedException) { }
    }

    public void Dispose()
    {
        Task running;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _pending = false;
            running = Task.WhenAll(_flight ?? Task.CompletedTask, _loop ?? Task.CompletedTask);
        }
        TryCancel(_stop);
        _ = running.ContinueWith(completed =>
        {
            if (completed.IsFaulted) InternalDiagnostics.Record(completed.Exception!, "Background health monitor shutdown");
            _http.Dispose();
            _stop.Dispose();
        }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }
}
