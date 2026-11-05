using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace Buildingway.Tools;

/// <summary>
/// The checkout's Dalamud plugin: the one project in a top-level folder whose csproj uses
/// Dalamud.NET.Sdk. Its file name is the plugin's name.
/// </summary>
internal sealed partial class PluginProject
{
    private PluginProject(string top, string csprojPath, XDocument csproj)
    {
        Top = top;
        Name = Path.GetFileNameWithoutExtension(csprojPath);
        Dir = Path.GetFileName(Path.GetDirectoryName(csprojPath)!);
        var props = csproj.Root!.Elements("PropertyGroup").Elements().ToList();
        RepoUrl = props.FirstOrDefault(e => e.Name.LocalName == "RepoUrl")?.Value.Trim();

        // The plugin's folder and the folders of the projects it references, whose code also
        // ships in the plugin. Trailing slashes keep e.g. Plugin.Core.Tests out of Plugin.Core/.
        var paths = new List<string> { Dir + "/" };
        foreach (var reference in csproj.Root.Elements("ItemGroup").Elements("ProjectReference"))
        {
            var include = reference.Attribute("Include")?.Value.Replace('\\', '/');
            if (include is null)
            {
                continue;
            }
            var full = Path.GetFullPath(Path.Combine(top, Dir, Path.GetDirectoryName(include)!));
            var relative = Path.GetRelativePath(top, full).Replace('\\', '/');
            if (!relative.StartsWith("..", StringComparison.Ordinal))
            {
                paths.Add(relative + "/");
            }
        }
        CodePaths = paths;
    }

    public string Top { get; }

    /// <summary>The csproj's file name, e.g. SayMyName: the assembly, dll and manifest name.</summary>
    public string Name { get; }

    /// <summary>The project's folder, relative to the checkout.</summary>
    public string Dir { get; }

    /// <summary>The csproj, relative to the checkout, with forward slashes.</summary>
    public string Csproj => $"{Dir}/{Name}.csproj";

    public string? RepoUrl { get; }

    /// <summary>Folders of code that ships in the plugin, relative and with trailing slashes.</summary>
    public IReadOnlyList<string> CodePaths { get; }

    /// <summary>owner/name from a https://github.com/ RepoUrl.</summary>
    public string Repo => RepoUrl is not null && GitHubRepo().Match(RepoUrl) is { Success: true } m
        ? m.Groups[1].Value
        : throw new ToolException($"{Csproj} needs <RepoUrl>https://github.com/owner/name</RepoUrl>.");

    /// <summary>The environment variable that overrides the dev folder, e.g. SAYMYNAME_DEV_DIR.</summary>
    public string DevDirVariable => Name.ToUpperInvariant() + "_DEV_DIR";

    public static PluginProject Find(string top)
    {
        var found = new List<(string Path, XDocument Doc)>();
        foreach (var dir in Directory.EnumerateDirectories(top))
        {
            if (Path.GetFileName(dir).StartsWith('.'))
            {
                continue;
            }
            foreach (var path in Directory.EnumerateFiles(dir, "*.csproj"))
            {
                XDocument doc;
                try
                {
                    doc = XDocument.Load(path);
                }
                catch (System.Xml.XmlException)
                {
                    continue;
                }
                var sdk = doc.Root?.Attribute("Sdk")?.Value ?? "";
                if (sdk.StartsWith("Dalamud.NET.Sdk", StringComparison.OrdinalIgnoreCase))
                {
                    found.Add((path, doc));
                }
            }
        }
        return found.Count switch
        {
            1 => new PluginProject(top, found[0].Path, found[0].Doc),
            0 => throw new ToolException($"No <folder>/<name>.csproj using Dalamud.NET.Sdk in {top}."),
            _ => throw new ToolException(
                "More than one project uses Dalamud.NET.Sdk: " +
                string.Join(", ", found.Select(f => Path.GetRelativePath(top, f.Path)))),
        };
    }

    /// <summary>The &lt;Version&gt; in a csproj's text, or null.</summary>
    public static string? VersionIn(string csprojText) =>
        CsprojVersion().Match(csprojText) is { Success: true } m ? m.Groups[1].Value : null;

    [GeneratedRegex(@"^https://github\.com/([^/]+/[^/]+?)/?$")]
    private static partial Regex GitHubRepo();

    [GeneratedRegex("<Version>(.*?)</Version>")]
    private static partial Regex CsprojVersion();
}
