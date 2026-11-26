using GenericWeb.DeploymentManager.Services;

namespace GenericWeb.DeploymentManager;

public sealed partial class WizardForm
{
    private sealed record SourcePreview(bool Ready, string Detail, string[] Lines, int Failures, string? ProjectFile = null,
        string? SourceRoot = null, string? SuggestedIdentity = null, int FrameworkMajor = 0, bool RequiresNode = false, bool CanWatch = false);

    // Preview never substitutes for the deployment engine's full validation.
    private static readonly HashSet<string> PreviewIgnoredDirectories = new(StringComparer.OrdinalIgnoreCase)
    { ".git", ".vs", "node_modules", "bin", "obj", "dist", "build", "out", ".vite", "iis-publish", "publish" };

    private static bool ShouldIgnorePreviewChange(string root, string path)
    {
        var relative = Path.GetRelativePath(root, path);
        var segments = relative.Split(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar }, StringSplitOptions.RemoveEmptyEntries);
        return segments.Take(Math.Max(0, segments.Length - 1)).Any(PreviewIgnoredDirectories.Contains);
    }

    private static SourcePreview InspectSourcePreview(EnvironmentService environment, bool prepared, string folder, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (prepared)
        {
            var validation = DeploymentService.ValidatePreparedPublish(folder);
            ct.ThrowIfCancellationRequested();
            var app = string.IsNullOrWhiteSpace(validation.ApplicationBinary) ? null : Path.GetFileNameWithoutExtension(validation.ApplicationBinary);
            if (app is not null && app.EndsWith(".Api", StringComparison.OrdinalIgnoreCase)) app = app[..^4];
            return new(validation.Ok, validation.Detail,
                [validation.Ok ? "PASS — " + validation.Detail : "FAIL — " + validation.Detail], validation.Ok ? 0 : 1,
                SuggestedIdentity: app, CanWatch: Directory.Exists(folder));
        }

        if (!Directory.Exists(folder)) return new(false, "Select an existing source folder first.", ["FAIL — Source folder is unavailable."], 1);
        string? api = null;
        var packages = new List<string>();
        var pending = new Queue<string>();
        pending.Enqueue(folder);
        while (pending.TryDequeue(out var current))
        {
            ct.ThrowIfCancellationRequested();
            foreach (var file in Directory.EnumerateFiles(current))
            {
                ct.ThrowIfCancellationRequested();
                if (api is null && file.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase) &&
                    File.ReadAllText(file).Contains("Microsoft.NET.Sdk.Web", StringComparison.OrdinalIgnoreCase))
                    api = file;
                if (packages.Count < 10 && string.Equals(Path.GetFileName(file), "package.json", StringComparison.OrdinalIgnoreCase))
                    packages.Add(file);
            }
            if (api is not null && packages.Count == 10) break;
            foreach (var directory in Directory.EnumerateDirectories(current))
            {
                ct.ThrowIfCancellationRequested();
                if (!PreviewIgnoredDirectories.Contains(Path.GetFileName(directory)) &&
                    (File.GetAttributes(directory) & FileAttributes.ReparsePoint) == 0)
                    pending.Enqueue(directory);
            }
        }

        if (api is null)
        {
            ct.ThrowIfCancellationRequested();
            api = environment.DetectApiProject(folder);
            ct.ThrowIfCancellationRequested();
            if (api is null)
                return new(false, "No ASP.NET Core Web project detected in the source preview.",
                    ["FAIL — ASP.NET Core Web .csproj not found."], 1, CanWatch: true);
        }
        ct.ThrowIfCancellationRequested();
        var content = File.ReadAllText(api);
        var major = environment.DetectTargetFrameworkMajor(api);
        var requiresNode = packages.Count > 0 || content.Contains("npm ", StringComparison.OrdinalIgnoreCase) ||
            content.Contains("ClientApp", StringComparison.OrdinalIgnoreCase) || content.Contains("package.json", StringComparison.OrdinalIgnoreCase);
        string? frontendRoot = null;
        foreach (var candidate in packages)
        {
            ct.ThrowIfCancellationRequested();
            var dir = Path.GetDirectoryName(candidate)!;
            if (File.Exists(Path.Combine(dir, "index.html")) && Directory.EnumerateFiles(dir, "vite.config.*", SearchOption.TopDirectoryOnly).Any())
            { frontendRoot = dir; break; }
        }
        var sourceRoot = DeploymentService.ResolveCanonicalSourceRoot(folder, api);
        var name = InferApplicationName(folder, api);
        ct.ThrowIfCancellationRequested();
        var lines = new List<string> { "PASS — Web project structure: " + api, $"PASS — Target framework: .NET {major}",
            $"PASS — Frontend / Node assets: {(requiresNode ? "detected" : "not required")} ({packages.Count} source package.json candidate(s))",
            "PASS — Source access/readability" };
        if (requiresNode) lines.Add(frontendRoot is null ? "FAIL — Frontend detected but no complete source frontend root was found in the preview." : "PASS — Frontend root: " + frontendRoot);
        return new(true, "Source preview ready.", lines.ToArray(), requiresNode && frontendRoot is null ? 1 : 0,
            api, sourceRoot, name, major, requiresNode, CanWatch: true);
    }

    private readonly SemaphoreSlim _sourcePreviewSlots = new(2, 2);
    private readonly SemaphoreSlim _browseFolderLookupSlot = new(1, 1);

    private async Task<SourcePreview> InspectSourcePreviewAsync(bool prepared, string folder, CancellationToken token)
    {
        await _sourcePreviewSlots.WaitAsync(token);
        try { return await Task.Run(() => InspectSourcePreview(_environment, prepared, folder, token), token); }
        finally { _sourcePreviewSlots.Release(); }
    }

    private async Task<string> ResolveBrowseStartAsync(string preferred, BrowseContext context)
    {
        // A disconnected network share must not hold up the UI before the
        // Windows folder picker opens. Late advisory results have no effect.
        if (!_browseFolderLookupSlot.Wait(0)) return string.Empty;
        var lookup = Task.Run(() =>
        {
            try { return BrowseStartFolder(preferred, context); }
            catch (Exception ex) { InternalDiagnostics.Record(ex, "Folder picker starting location"); return string.Empty; }
            finally { _browseFolderLookupSlot.Release(); }
        });
        return await Task.WhenAny(lookup, Task.Delay(350)) == lookup ? await lookup : string.Empty;
    }
}
