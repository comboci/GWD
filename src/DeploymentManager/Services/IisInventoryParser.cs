using System.Xml;
using System.Xml.Linq;

namespace GenericWeb.DeploymentManager.Services;

/// <summary>
/// Read-only default AppCmd XML inventory. Unknown/incomplete schemas trigger
/// legacy queries. Root objects are joined by exact case-insensitive identity.
/// </summary>
public static class IisInventoryParser
{
    private static XElement[] Rows(string xml, string rowName)
    {
        using var input = new StringReader(xml.TrimStart('\uFEFF'));
        using var reader = XmlReader.Create(input, new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null
        });
        var root = XDocument.Load(reader).Root;
        if (root is null || root.Name.LocalName != "appcmd" || root.Elements().Any(x => x.Name.LocalName != rowName))
            throw new FormatException("Unrecognized AppCmd inventory schema.");
        return root.Elements().ToArray();
    }

    private static string Attribute(XElement row, string name)
    {
        var matches = row.Attributes().Where(x => x.Name.LocalName.Equals(name, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (matches.Length != 1) throw new FormatException("Missing or ambiguous inventory attribute: " + name);
        return matches[0].Value;
    }

    private static Dictionary<string, XElement> Index(IEnumerable<XElement> rows, string keyName)
    {
        var index = new Dictionary<string, XElement>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in rows)
        {
            var name = Attribute(row, keyName);
            if (string.IsNullOrWhiteSpace(name) || !index.TryAdd(name, row))
                throw new FormatException("Missing or duplicate IIS object identity.");
        }
        return index;
    }

    private static string State(XElement row)
    {
        var value = Attribute(row, "state");
        return string.IsNullOrWhiteSpace(value) ? "Unknown" : value;
    }

    public static IReadOnlyList<IisSiteInfo> ParseSites(string siteXml, string appXml, string vdirXml)
    {
        var sites = Index(Rows(siteXml, "SITE"), "SITE.NAME");
        var apps = Index(Rows(appXml, "APP"), "APP.NAME");
        var vdirs = Index(Rows(vdirXml, "VDIR"), "VDIR.NAME");
        var result = new List<IisSiteInfo>(sites.Count);
        foreach (var (name, row) in sites)
        {
            var rootName = name + "/";
            if (!apps.TryGetValue(rootName, out var app) || !vdirs.TryGetValue(rootName, out var vdir))
                throw new FormatException("Root application or virtual directory missing from inventory.");
            var path = Attribute(vdir, "physicalPath").Trim('"');
            var pool = Attribute(app, "APPPOOL.NAME").Trim('"');
            if (string.IsNullOrWhiteSpace(path) || string.IsNullOrWhiteSpace(pool))
                throw new FormatException("Incomplete root application inventory.");
            result.Add(new IisSiteInfo(name, State(row), Attribute(row, "bindings"), path, pool));
        }
        return result;
    }

    public static IReadOnlyList<IisAppPoolInfo> ParsePools(string xml)
        => Index(Rows(xml, "APPPOOL"), "APPPOOL.NAME")
            .Select(pair => new IisAppPoolInfo(pair.Key, State(pair.Value))).ToArray();
}
