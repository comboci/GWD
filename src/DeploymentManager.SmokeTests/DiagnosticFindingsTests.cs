using System.Net.Mail;
using GenericWeb.DeploymentManager.Services;

namespace GenericWeb.DeploymentManager.SmokeTests;

internal static class DiagnosticFindingsTests
{
    public static async Task RunAsync(string root)
    {
        var trace = new BuildPerformanceTrace("Full IIS Deploy", "Hybrid");
        var error = new CommandResult(1, "TS2741", "");
        var returned = await trace.MeasureAsync("Frontend build", () => Task.FromResult(error));
        if (!ReferenceEquals(returned, error) || trace.Stages.Single().Status != "FAILED" ||
            !trace.Stages.Single().Detail.Contains("exit code: 1", StringComparison.Ordinal))
            throw new InvalidOperationException("Nonzero command must remain available for retry and must not be DONE.");
        await trace.MeasureAsync("Frontend build retry", () => Task.FromResult(new CommandResult(0, "", "")));
        trace.Measure("Sync command", () => new CommandResult(2, "", ""));
        trace.AddSkipped("Unneeded build", "unchanged");
        if (!trace.Stages.Select(x => x.Status).SequenceEqual(new[] { "FAILED", "DONE", "FAILED", "SKIPPED" }))
            throw new InvalidOperationException("Process outcome classification regression.");
        try { await trace.MeasureAsync("Thrown command", () => Task.FromException(new IOException("test"))); }
        catch (IOException) { }
        if (trace.Stages.Last().Status != "FAILED") throw new InvalidOperationException("Exception outcome regression.");

        const string secret = "SyntheticCredentialNeverLog";
        var category = EmailNotificationService.FailureCategory(new SmtpException(SmtpStatusCode.MailboxUnavailable, secret));
        if (category != "SMTP_STATUS_550" || category.Contains(secret, StringComparison.Ordinal))
            throw new InvalidOperationException("SMTP diagnostics must contain only a safe status code.");
        if (EmailNotificationService.FailureCategory(new TimeoutException(secret)) != "TIMEOUT_OR_CANCELLED" ||
            EmailNotificationService.FailureCategory(new ArgumentException(secret)) != "INVALID_SETTINGS")
            throw new InvalidOperationException("Email failure category regression.");

        Directory.CreateDirectory(root);
        var payload = Path.Combine(root, "payload");
        Directory.CreateDirectory(payload);
        File.WriteAllBytes(Path.Combine(payload, "a.bin"), new byte[1234]);
        var estimate = DeploymentCapacityGuard.EstimateCopyBytes(payload, CancellationToken.None);
        if (estimate < 1234) throw new InvalidOperationException("Capacity estimate smaller than payload.");
        DeploymentCapacityGuard.RequireAvailable(estimate, ulong.MaxValue, "test");
        var rejected = false;
        try { DeploymentCapacityGuard.RequireAvailable(estimate, 0, "test"); }
        catch (IOException) { rejected = true; }
        if (!rejected || !File.Exists(Path.Combine(payload, "a.bin")))
            throw new InvalidOperationException("Low space must reject without deleting files.");

        Console.WriteLine("PASS: process status, SMTP diagnostic safety, capacity estimate and rejection.");
    }
}
