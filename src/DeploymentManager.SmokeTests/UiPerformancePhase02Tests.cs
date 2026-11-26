using System.Reflection;
using GenericWeb.DeploymentManager;
using GenericWeb.DeploymentManager.Services;

namespace GenericWeb.DeploymentManager.SmokeTests;

internal static class UiPerformancePhase02Tests
{
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
        Console.WriteLine("PASS: " + message);
    }

    private static void Reject(Action action, string name)
    {
        try { action(); }
        catch (Exception ex) when (ex is FormatException or System.Xml.XmlException)
        {
            Console.WriteLine("PASS: " + name);
            return;
        }
        throw new InvalidOperationException("Expected malformed inventory rejection: " + name);
    }

    public static void Run(string root)
    {
        const string sites = """<appcmd><SITE SITE.NAME="Alpha &amp; Co" bindings="http/*:80:" state="Started"/><SITE SITE.NAME="Beta" bindings="https/*:443:" state="Stopped"/></appcmd>""";
        const string apps = """<appcmd><APP APP.NAME="Beta/" APPPOOL.NAME="PoolB"/><APP APP.NAME="Alpha &amp; Co/sub" APPPOOL.NAME="WrongPool"/><APP APP.NAME="alpha &amp; co/" APPPOOL.NAME="PoolA"/></appcmd>""";
        const string vdirs = """<appcmd><VDIR VDIR.NAME="Beta/" physicalPath="C:\Sites\Beta"/><VDIR VDIR.NAME="Alpha &amp; Co/sub" physicalPath="C:\Wrong"/><VDIR VDIR.NAME="Alpha &amp; Co/" physicalPath="%SystemDrive%\Sites\A &amp; Co"/></appcmd>""";
        var rows = IisInventoryParser.ParseSites(sites, apps, vdirs);
        var alpha = rows.Single(x => x.Name == "Alpha & Co");
        var beta = rows.Single(x => x.Name == "Beta");
        Check(rows.Count == 2 && alpha.AppPool == "PoolA" &&
            alpha.PhysicalPath == @"%SystemDrive%\Sites\A & Co" && beta.State == "Stopped",
            "IIS inventory exact root join, case, XML escaping and state");
        var pools = IisInventoryParser.ParsePools("""<appcmd><APPPOOL APPPOOL.NAME="Pool A" state="Started"/><APPPOOL APPPOOL.NAME="Pool B" state="Stopped"/></appcmd>""");
        Check(pools.Count == 2 && pools.Single(x => x.Name == "Pool B").State == "Stopped", "IIS pool inventory");
        Check(IisInventoryParser.ParseSites("<appcmd/>", "<appcmd/>", "<appcmd/>").Count == 0, "IIS empty inventory");
        Reject(() => IisInventoryParser.ParseSites(sites, "<appcmd/>", vdirs), "missing root app rejected");
        Reject(() => IisInventoryParser.ParsePools("""<appcmd><APPPOOL APPPOOL.NAME="a" state="Started"/><APPPOOL APPPOOL.NAME="A" state="Stopped"/></appcmd>"""), "duplicate IIS identities rejected");
        Reject(() => IisInventoryParser.ParsePools("<appcmd><APPPOOL"), "truncated XML rejected");
        Reject(() => IisInventoryParser.ParsePools("""<!DOCTYPE appcmd [<!ENTITY x "data">]><appcmd/>"""), "DTD rejected");

        var runner = new CommandRunner();
        var service = new SqlServerService(runner, new PrerequisiteService(runner));
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        var cancellationObserved = false;
        try { service.DetectInstancesAsync(cancelled.Token).GetAwaiter().GetResult(); }
        catch (OperationCanceledException) { cancellationObserved = true; }
        Check(cancellationObserved, "SQL discovery honours pre-cancellation");

        var source = Path.Combine(root, "phase02-source");
        Directory.CreateDirectory(source);
        var input = Path.Combine(source, "input.txt");
        File.WriteAllText(input, "synthetic source");
        var destination = Path.Combine(root, "phase02-existing.zip");
        var old = new byte[] { 1, 2, 3, 4 };
        File.WriteAllBytes(destination, old);
        var export = typeof(WizardForm).GetMethod("ExportCleanSourceZip", BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new MissingMethodException("ExportCleanSourceZip");
        // An exclusive source handle makes compression fail on Windows.
        using (var held = new FileStream(input, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            var rejected = false;
            try { export.Invoke(null, [source, destination, Array.Empty<string>()]); }
            catch (TargetInvocationException ex) when (ex.InnerException is IOException or UnauthorizedAccessException) { rejected = true; }
            Check(rejected && File.ReadAllBytes(destination).SequenceEqual(old), "failed compression preserves existing archive");
        }
        export.Invoke(null, [source, destination, Array.Empty<string>()]);
        using (var zip = System.IO.Compression.ZipFile.OpenRead(destination))
            Check(zip.Entries.Count == 1 && zip.Entries[0].FullName == "input.txt", "successful compression replaces archive with complete ZIP");
        Check(!Directory.EnumerateFiles(root, ".*.tmp").Any(), "temporary archive removed");
    }
}
