using GenericWeb.DeploymentManager.Models;
using System.Net.Http;

namespace GenericWeb.DeploymentManager.Services;

public sealed record HealthSnapshot(DateTime CheckedAt, bool IisInstalled, int SiteCount, int AppPoolCount, bool? SqlOk, string SqlDetail, int? HttpStatus, string HttpDetail);

public sealed class BackgroundHealthMonitor : IDisposable
{
    private readonly EnvironmentService _environment;
    private readonly SqlServerService _sql;
    private readonly DeploymentProfile _profile;
    private readonly CancellationTokenSource _cts = new();
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(8) };
    private Task? _loop;
    private readonly object _lifecycleLock = new();
    private bool _disposed;
    public HealthSnapshot? Latest { get; private set; }
    public event Action<HealthSnapshot>? Updated;
    public BackgroundHealthMonitor(EnvironmentService environment, SqlServerService sql, DeploymentProfile profile) { _environment=environment; _sql=sql; _profile=profile; }
    public void Start()
    {
        lock (_lifecycleLock)
        {
            if (_disposed) return;
            _loop ??= Task.Run(LoopAsync);
        }
    }
    public async Task RefreshNowAsync(CancellationToken ct=default) => await CheckAsync(ct);
    public bool HasRecentSuccessfulSql(TimeSpan maxAge) => _sql.Connections.HasRecentSuccessfulSql(_profile, maxAge);
    public void ReportSqlSuccess(string detail)
    {
        var previous = Latest;
        var snap = previous is null
            ? new HealthSnapshot(DateTime.Now, false, 0, 0, _sql.Connections.State(_profile).Verified, SecretRedactor.Redact(detail), null, "Not checked")
            : previous with { CheckedAt = DateTime.Now, SqlOk = _sql.Connections.State(_profile).Verified, SqlDetail = SecretRedactor.Redact(detail) };
        Latest = snap;
        Updated?.Invoke(snap);
    }
    public void ReportSqlFailure(string detail)
    {
        _sql.Connections.Invalidate(_profile, detail);
        detail = SecretRedactor.Redact(detail);
        var previous = Latest;
        var snap = previous is null
            ? new HealthSnapshot(DateTime.Now, false, 0, 0, false, detail, null, "Not checked")
            : previous with { CheckedAt = DateTime.Now, SqlOk = false, SqlDetail = detail };
        Latest = snap;
        Updated?.Invoke(snap);
    }
    private async Task LoopAsync()
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(5), _cts.Token);
            while (!_cts.IsCancellationRequested)
            {
                try
                {
                    await CheckAsync(_cts.Token);
                }
                catch (OperationCanceledException) when (_cts.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    InternalDiagnostics.Record(ex, "Background health monitor cycle");
                }

                await Task.Delay(TimeSpan.FromMinutes(10), _cts.Token);
            }
        }
        catch (OperationCanceledException) when (_cts.IsCancellationRequested) { }
    }
    private async Task CheckAsync(CancellationToken ct)
    {
        bool iis=false; int sites=0,pools=0; bool? sqlOk=null; string sqlDetail="Not configured"; int? httpStatus=null; string httpDetail="Not checked";
        try { iis=(await _environment.CheckIisAsync()).Installed; if(iis){ sites=(await _environment.ListIisSitesAsync(ct)).Count; pools=(await _environment.ListIisAppPoolsAsync(ct)).Count; } } catch(OperationCanceledException) when (ct.IsCancellationRequested) { throw; } catch(Exception ex){ InternalDiagnostics.Record(ex,"Background IIS health"); }
        if(!string.IsNullOrWhiteSpace(_profile.SqlInstance)) { try { sqlDetail=await _sql.TestConnectionWithSafeFallbackAsync(_profile, ct); sqlOk=_sql.Connections.State(_profile).Verified; } catch(OperationCanceledException) when (ct.IsCancellationRequested) { throw; } catch(Exception ex){ sqlOk=false; sqlDetail=SecretRedactor.Redact(ex.Message); } }
        if(iis && !string.IsNullOrWhiteSpace(_profile.SiteName)) { try { var uri=new Uri($"http://localhost:{_profile.HttpPort}{(_profile.HealthPath.StartsWith('/')?_profile.HealthPath:"/"+_profile.HealthPath)}"); using var r=await _http.GetAsync(uri,ct); httpStatus=(int)r.StatusCode; httpDetail=$"HTTP {httpStatus}"; } catch(OperationCanceledException) when (ct.IsCancellationRequested) { throw; } catch(Exception ex){ httpDetail=ex.Message; } }
        var snap=new HealthSnapshot(DateTime.Now,iis,sites,pools,sqlOk,sqlDetail,httpStatus,httpDetail); Latest=snap; Updated?.Invoke(snap);
    }
    public void Dispose()
    {
        Task? running;
        lock (_lifecycleLock)
        {
            if (_disposed) return;
            _disposed = true;
            _cts.Cancel();
            running = _loop;
        }

        // Do not release the cancellation source or HTTP client while the loop uses them.
        if (running is null)
        {
            _http.Dispose();
            _cts.Dispose();
            return;
        }

        _ = running.ContinueWith(
            completed =>
            {
                if (completed.IsFaulted)
                    InternalDiagnostics.Record(completed.Exception!, "Background health monitor shutdown");
                _http.Dispose();
                _cts.Dispose();
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }
}
