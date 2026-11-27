using System.Collections.Concurrent;
using System.Reflection;
using System.Text.Json;
using GenericWeb.DeploymentManager.Models;
using GenericWeb.DeploymentManager.Services;

namespace GenericWeb.DeploymentManager.SmokeTests;

// This suite is for an isolated Windows owner-run test account, not deployment.
internal static class FinalRemediation08Tests
{
    private const BindingFlags Member = BindingFlags.NonPublic | BindingFlags.Instance;
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(5);

    private static void Check(bool yes, string description)
    {
        if (!yes) throw new InvalidOperationException(description);
        Console.WriteLine("PASS: " + description);
    }

    private static void Throws<T>(Action run, string description) where T : Exception
    {
        try { run(); }
        catch (TargetInvocationException e) when (e.InnerException is T)
        { Check(true, description); return; }
        catch (T) { Check(true, description); return; }
        throw new InvalidOperationException(description);
    }

    private static MethodInfo Method(Type type, string name) =>
        type.GetMethod(name, Member | BindingFlags.Public) ?? throw new MissingMethodException(type.FullName, name);

    private static FieldInfo Field(Type type, string name) =>
        type.GetField(name, Member) ?? throw new MissingFieldException(type.FullName, name);

    private static WizardForm OpenIsolatedWizard() => (WizardForm)Activator.CreateInstance(
        typeof(WizardForm), Member, binder: null, args: [false], culture: null)!;

    public static void Run(string root)
    {
        var expected = Path.GetFullPath(root);
        Check(string.Equals(Path.GetFullPath(Environment.GetEnvironmentVariable("GWDM_APPDATA_ROOT") ?? "."), expected,
            StringComparison.OrdinalIgnoreCase) && Path.GetFileName(expected).StartsWith("GWDM-Smoke-", StringComparison.Ordinal),
            "guard dedicated synthetic settings and history root");

        OrderedWriter();
        Settings();
        History();
        Health();
        SourcePreview(root);
    }

    private static void OrderedWriter()
    {
        var type = typeof(AppDataService).Assembly.GetType("GenericWeb.DeploymentManager.Services.OrderedSettingsWriter", true)!;
        var writer = Activator.CreateInstance(type, nonPublic: true)!;
        var enqueue = Method(type, "Enqueue");
        var drain = Method(type, "TryDrain");
        bool Add(string key, Action action) => (bool)enqueue.Invoke(writer, [key, action])!;
        bool Drain(TimeSpan time, bool close = false) => (bool)drain.Invoke(writer, [time, close])!;
        using var started = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var rows = new ConcurrentQueue<string>();
        Check(Add("first", () => { started.Set(); if (!release.Wait(Bound)) throw new TimeoutException(); rows.Enqueue("first"); }) &&
            started.Wait(Bound), "first writer held in a deterministic gate");
        try
        {
            Check(Add("same", () => rows.Enqueue("stale")) &&
                Add("next", () => rows.Enqueue("next")) &&
                Add("same", () => rows.Enqueue("latest")), "concurrent queue receives replacement");
            for (var n = 0; n < 30; n++) Check(Add("cap" + n, () => { }), "queue accepts capacity slot " + n);
            Check(!Add("overflow", () => throw new InvalidOperationException()), "queue rejects excess rather than silently overrunning");
            Check(!Drain(TimeSpan.Zero), "flush reports pending work at timeout");
        }
        finally { release.Set(); }
        Check(Drain(Bound) && rows.SequenceEqual(["first", "next", "latest"]), "bounded ordered writes coalesce by newest edit order");
        Check(Add("fault", () => throw new IOException("synthetic")) && Drain(Bound) &&
            (int)type.GetProperty("FailureCount", Member)!.GetValue(writer)! == 1, "writer exposes isolated failure after flush");
        Check(Add("fault", () => { }) && Drain(Bound, true) && !Add("after-close", () => { }) &&
            (int)type.GetProperty("FailureCount", Member)!.GetValue(writer)! == 0, "successful retry clears failure and closing prevents late writes");
    }

    private static void Settings()
    {
        var a = new AppDataService();
        var b = new AppDataService();
        var original = a.GetSettings();
        original.DependencyResolutionMode = "Offline Only";
        a.SaveSettings(original);
        var left = a.GetSettings();
        var right = b.GetSettings();
        left.DefaultProjectFolder = @"C:\synthetic-left";
        right.DefaultProjectFolder = @"C:\synthetic-right";
        using var start = new ManualResetEventSlim();
        int saved = 0, stale = 0;
        Task Race(AppDataService svc, DeploymentManagerSettings model) => Task.Run(() =>
        {
            if (!start.Wait(Bound)) throw new TimeoutException();
            try { svc.SaveSettings(model); Interlocked.Increment(ref saved); }
            catch (InvalidOperationException) { Interlocked.Increment(ref stale); }
        });
        var first = Race(a, left);
        var second = Race(b, right);
        start.Set();
        Check(Task.WaitAll([first, second], Bound) && saved == 1 && stale == 1, "competing revisions cannot lose an update or partially publish");
        using (var json = JsonDocument.Parse(File.ReadAllText(a.SettingsFile)))
            Check(json.RootElement.GetProperty("Revision").GetGuid() != Guid.Empty, "atomic settings readback is complete JSON");

        var credentialHistory = a.GetSettings();
        var token = a.SetRemoteCredentialPreference("synthetic.invalid", "synthetic-account", "SyntheticSettingsPhrase08", true);
        _ = b.SetRemoteCredentialPreference("", "", "", false);
        Check(!a.SaveRememberedRemoteCredential(token, "synthetic.invalid", "synthetic-account", "SyntheticSettingsPhrase08") &&
            string.IsNullOrEmpty(b.GetRemoteCredential().Password) && !b.GetRemoteCredential().Remember &&
            !File.ReadAllText(a.SettingsFile).Contains("SyntheticSettingsPhrase08", StringComparison.Ordinal),
            "credential opt-out fences stale queued passwords and plaintext is absent");
        Throws<InvalidOperationException>(() => a.SaveSettings(credentialHistory), "stale settings snapshot cannot reverse credential opt-out");

        var bad = System.Text.Encoding.UTF8.GetBytes("{\"Revision\":");
        File.WriteAllBytes(a.SettingsFile, bad);
        using (var wizard = OpenIsolatedWizard()) Check(!wizard.IsDisposed, "corrupt startup remains available for recovery");
        Check(a.GetSettingsAccessStatus().CanReset && File.ReadAllBytes(a.SettingsFile).SequenceEqual(bad), "startup diagnoses without replacing corrupt bytes");
        Throws<IOException>(() => a.SaveSettings(a.GetSettings()), "ordinary save is blocked while corrupt file remains");
        var archive = a.PreserveAndResetCorruptSettings();
        Check(File.ReadAllBytes(archive).SequenceEqual(bad) && !a.GetSettingsAccessStatus().CanReset &&
            a.GetSettings().Revision != Guid.Empty && !a.GetRemoteCredential().Remember, "explicit recovery preserves exact bytes and creates credential-free defaults");
        Throws<InvalidOperationException>(() => a.PreserveAndResetCorruptSettings(), "valid settings cannot be reset through corruption recovery");
    }

    private static void History()
    {
        var service = new DeploymentHistoryService();
        var p = new DeploymentProfile { ApplicationName = "Audit08", SiteName = "Synthetic History", BuildMode = "Full Build" };
        var id = service.GetProjectId(p);
        var path = Path.Combine(service.Root, id + ".jsonl");
        var at = DateTime.UtcNow;
        var rows = new[] { ("11111111111111111111111111111111","Prepared IIS Publish","Successful"), ("22222222222222222222222222222222","Fast Local Test","Successful"),
            ("33333333333333333333333333333333","Fast IIS Update","Failed"), ("44444444444444444444444444444444","Fast Local Test","Cancelled"),
            ("55555555555555555555555555555555","not-a-verified-build-mode","Successful") };
        SecretRedactor.Register("Successful");
        SecretRedactor.Register("Fast Local Test");
        for (var i = 0; i < rows.Length; i++)
        {
            var (attempt, mode, result) = rows[i];
            var item = new DeploymentHistoryRecord { AttemptId = attempt, ProjectId = id, EventType = "Completed", BuildMode = mode,
                Result = result, StartedAtUtc = at.AddSeconds(i), EventAtUtc = at.AddSeconds(i + 1),
                EndedAtUtc = at.AddSeconds(i + 1), ElapsedSeconds = i + 1, ApplicationName = "Synthetic History",
                ApplicationVersion = "100." + i, SessionLog = "token=synthetic-history-redaction" };
            File.AppendAllText(path, JsonSerializer.Serialize(item) + Environment.NewLine);
        }
        File.AppendAllText(path, "not-a-valid-JSON-row" + Environment.NewLine);
        var baseline = File.ReadAllBytes(path);
        var p1 = service.GetHistoryPage(id, 0, 2);
        var p2 = service.GetHistoryPage(id, 1, 2);
        Check(p1.TotalAttempts == rows.Length && p1.Rows.Count == 2 && p2.Rows.Count == 2 &&
            !p1.Rows.Select(x => x.AttemptId).Intersect(p2.Rows.Select(x => x.AttemptId)).Any(),
            "paged index keeps stable attempt IDs and survives a malformed legacy row");
        var older = service.GetHistoryPage(id, 2, 2).Rows.Single();
        Check(older.AttemptId == "11111111111111111111111111111111" && older.Result == "Successful" && older.BuildMode == "Prepared IIS Publish" &&
            !older.SessionLog.Contains("synthetic-history-redaction", StringComparison.Ordinal),
            "free-text redaction does not erase known semantic metadata");
        var evidence = Method(typeof(DeploymentHistoryService), "ReadDashboardHistory").Invoke(service, [id, CancellationToken.None])!;
        var hosted = (DeploymentHistoryRecord?)evidence.GetType().GetProperty("LatestRecordedRelease")!.GetValue(evidence);
        Check(hosted?.AttemptId == "11111111111111111111111111111111", "dashboard does not count a later successful local or unknown-mode run as hosted evidence");
        var sidecar = Directory.GetFiles(Path.Combine(service.Root, ".index-v2"), "*.index.json").Single();
        File.WriteAllText(sidecar, "{damaged-index");
        Check(service.GetHistoryPage(id, 0, 2).TotalAttempts == rows.Length &&
            File.ReadAllBytes(path).SequenceEqual(baseline) && File.ReadAllText(sidecar).Contains("ContentHash", StringComparison.Ordinal),
            "corrupt index rebuilt without rewriting source JSONL");
        using var stop = new CancellationTokenSource();
        stop.Cancel();
        Throws<OperationCanceledException>(() => service.GetHistoryPage(id, 0, 2, stop.Token), "paged load observes caller cancellation");
    }

    private static void Health()
    {
        var runner = new CommandRunner();
        var profile = new DeploymentProfile { SiteName = "first" };
        using var monitor = new BackgroundHealthMonitor(new EnvironmentService(runner),
            new SqlServerService(runner, new PrerequisiteService(runner)), profile);
        // Inject a held task belonging to the current generation. Real SQL/HTTP/IIS probes are never started in this fixture.
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var type = typeof(BackgroundHealthMonitor);
        Field(type, "_flight").SetValue(monitor, completion.Task);
        Field(type, "_inFlightGeneration").SetValue(monitor, Field(type, "_generation").GetValue(monitor));
        Check(ReferenceEquals(monitor.RefreshNowAsync(), monitor.RefreshNowAsync()), "same-generation subscribers share one owner flight");
        using var caller = new CancellationTokenSource();
        var detached = monitor.RefreshNowAsync(caller.Token);
        caller.Cancel();
        Throws<OperationCanceledException>(() => detached.GetAwaiter().GetResult(), "caller cancellation detaches without canceling monitor flight");
        Check(!completion.Task.IsCompleted, "owner flight survives caller cancellation");
        profile.SiteName = "second";
        monitor.SubmitProfile(profile);
        var older = monitor.Latest!;
        profile.SiteName = "third";
        monitor.SubmitProfile(profile);
        Check(!monitor.IsCurrent(older) && monitor.IsCurrent(monitor.Latest!), "new profile invalidates stale health evidence");
        completion.SetResult(true);
    }

    private static void SourcePreview(string root)
    {
        using var form = OpenIsolatedWizard();
        var inspect = Method(typeof(WizardForm), "InspectSourcePreviewAsync");
        var gone = Path.Combine(root, "missing-source");
        async Task<object> InspectOffUiAsync(CancellationToken token)
        {
            var task = (Task)inspect.Invoke(form, [false, gone, token])!;
            await task.WaitAsync(Bound);
            return task.GetType().GetProperty("Result")!.GetValue(task)!;
        }

        var result = Task.Run(() => InspectOffUiAsync(CancellationToken.None)).GetAwaiter().GetResult();
        Check(!(bool)result.GetType().GetProperty("Ready")!.GetValue(result)!, "missing source cannot be marked ready");
        using var stop = new CancellationTokenSource();
        stop.Cancel();
        Throws<OperationCanceledException>(() => Task.Run(() => InspectOffUiAsync(stop.Token)).GetAwaiter().GetResult(),
            "async preview stops on cancellation");

        var type = typeof(WizardForm);
        var profile = (DeploymentProfile)Field(type, "_profile").GetValue(form)!;
        profile.BuildMode = "Fast Local Test";
        profile.SourceFolder = gone;
        Method(type, "RememberNavigationSourceReadiness").Invoke(form, [false, gone, true]);
        var presenter = Field(type, "_sourcePresenterFresh");
        var ready = Method(type, "EnsureNavigationSourceReadiness");
        var previous = presenter.GetValue(form);
        bool isClean = true;
        try
        {
            presenter.SetValue(form, (Func<bool>)(() => isClean));
            Check((bool)ready.Invoke(form, null)!, "advisory ready value is used only with a clean active presenter");
            isClean = false;
            Check(!(bool)ready.Invoke(form, null)!, "dirty presenter immediately invalidates cached same-path readiness");
        }
        finally { presenter.SetValue(form, previous); }

        // A directory at the synthetic state-file path forces an ordinary write failure.
        var state = new AppDataService().StateFile;
        Directory.CreateDirectory(state);
        try
        {
            Method(type, "RecordClosingState").Invoke(form, null);
            Check(Directory.Exists(state), "state-write failure does not overwrite synthetic conflicting data or abort teardown");
        }
        finally { Directory.Delete(state); }
        form.Dispose();
        var resourceFlag = Field(type, "_ownedResourcesReleased");
        var monitor = (BackgroundHealthMonitor)Field(type, "_healthMonitor").GetValue(form)!;
        Check((bool)resourceFlag.GetValue(form)! && (bool)Field(typeof(BackgroundHealthMonitor), "_disposed").GetValue(monitor)! &&
            presenter.GetValue(form) is null, "direct disposal releases health monitor and source presenter without saving wizard state");
    }
}
