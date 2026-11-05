using System.IO.Compression;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Buildingway.Tools;

/// <summary>
/// Release steps that need no judgement. Buildingway-ci's release.yml does what happens once the
/// release-prep PR is merged: tag, build, publish and the repo.json PR.
/// </summary>
internal sealed partial class Release(Git git, PluginProject plugin)
{
    private const string PinFile = ".github/dalamud.env";

    private const string Usage = """
        Usage: dotnet buildingway release prepare <version>   e.g. 0.1.2.0
               dotnet buildingway release publish <version> [--testing]
               dotnet buildingway release verify <version>|<version>-rc<n>

        prepare: from origin/main, make branch release-prep/v<version> that sets <Version> and moves the
        CHANGELOG's Unreleased entries under a dated heading; build, test, push and open the PR. When
        main's <Version> was never released as stable (it went out only as a testing build, which
        Dalamud won't update to another build of), its dated section moves to <version> instead, taking
        any Unreleased entries with it. The PR lists the changes merged since the last release, flagging
        plugin code changes that touched no CHANGELOG entry.

        publish: run the Release workflow on main, which tags and publishes main's <Version> (it must be
        <version>): v<version> as a stable release, or with --testing v<version>-rc1 as a
        testing build, offered only to installs that receive testing versions. A testing build is
        refused when <version> already has one: prepare the next version first.

        verify: check that the published release is what installs get: the tag is on main at that
        version, the GitHub release has the zip, the zip's manifest has the version, and the live
        repo.json on main points at it. For a testing build (<version>-rc<n>), the release is a prerelease
        and repo.json's testing fields point at it while its stable fields don't. Exits 1 if any check
        fails.
        """;

    public static int Run(string[] args)
    {
        var git = Git.FromCurrentDirectory();
        var release = new Release(git, PluginProject.Find(git.Top));
        switch (args)
        {
            case ["prepare", var version]:
                release.Prepare(version);
                return 0;
            case ["publish", var version]:
                release.Publish(version, testing: false);
                return 0;
            case ["publish", var version, "--testing"]:
                release.Publish(version, testing: true);
                return 0;
            case ["verify", var version]:
                return release.Verify(version);
            default:
                Console.Error.WriteLine(Usage);
                return 2;
        }
    }

    public static bool IsVersion(string version) => FourNumbers().IsMatch(version);

    /// <summary>Compares four-number versions numerically, as sort -V does.</summary>
    public static int CompareVersions(string a, string b) => Version.Parse(a).CompareTo(Version.Parse(b));

    /// <summary>The highest vX-rcN tag among ls-remote lines, or null.</summary>
    public static string? HighestRc(string lsRemote, string version)
    {
        var prefix = $"refs/tags/v{version}-rc";
        return lsRemote.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.Split('\t').Last().Trim())
            .Where(r => r.StartsWith(prefix, StringComparison.Ordinal) && int.TryParse(r[prefix.Length..], out _))
            .OrderBy(r => int.Parse(r[prefix.Length..]))
            .Select(r => r["refs/tags/".Length..])
            .LastOrDefault();
    }

    /// <summary>The PR number and title of a first-parent commit on main from its subject and body.</summary>
    public static (string? Number, string Title) ParseMergeSubject(string subject, string body)
    {
        if (MergeCommit().Match(subject) is { Success: true } merge)
        {
            return (merge.Groups[1].Value, body.Split('\n')[0]);
        }
        if (SquashCommit().Match(subject) is { Success: true } squash)
        {
            return (squash.Groups[2].Value, squash.Groups[1].Value);
        }
        return (null, subject);
    }

    private string CsprojPath => Path.Combine(git.Top, plugin.Csproj);

    private string ChangelogPath => Path.Combine(git.Top, "CHANGELOG.md");

    private string MainVersion() =>
        PluginProject.VersionIn(git.Output("show", $"origin/main:{plugin.Csproj}"))
        ?? throw new ToolException($"No <Version> in {plugin.Csproj} on main.");

    private void Prepare(string version)
    {
        if (!IsVersion(version))
        {
            throw new ToolException("Version must be four numbers, e.g. 0.1.2.0");
        }
        var tag = $"v{version}";
        var branch = $"release-prep/v{version}";

        if (git.IsDirty())
        {
            throw new ToolException("Commit or set aside your changes first.");
        }
        git.Run("fetch", "-q", "--tags", "origin", "main");

        var current = MainVersion();
        if (CompareVersions(version, current) <= 0)
        {
            throw new ToolException($"{version} isn't above the version on main, {current}.");
        }
        if (git.HasRef($"refs/tags/{tag}"))
        {
            throw new ToolException($"Tag {tag} already exists.");
        }
        // Test tags (v…-test1) are prereleases, not the last release
        var last = git.Output("describe", "--tags", "--abbrev=0", "--match", "v*", "--exclude", "v*-*", "origin/main");

        // A dated section for a <Version> never released as stable (only as testing builds, or not
        // at all) moves to the new version, as each testing build needs a higher version than the last
        var heading = $"## {version} - {DateTime.Now:yyyy-MM-dd}";
        var mainChangelog = git.Output("show", "origin/main:CHANGELOG.md");
        var reversion = !git.HasRef($"refs/tags/v{current}") && Changelog.NextSectionIs(mainChangelog, current);
        if (!reversion && !Changelog.HasUnreleasedEntries(mainChangelog))
        {
            throw new ToolException("CHANGELOG.md has no Unreleased entries to release.");
        }

        git.Run("switch", "-q", "-c", branch, "origin/main");
        var csproj = File.ReadAllText(CsprojPath);
        File.WriteAllText(CsprojPath, csproj.Replace($"<Version>{current}</Version>", $"<Version>{version}</Version>"));
        var changelog = File.ReadAllText(ChangelogPath);
        File.WriteAllText(ChangelogPath, reversion
            ? Changelog.Reversion(changelog, current, heading)
            : Changelog.DateUnreleased(changelog, heading));

        var note = DalamudNote();
        if (note is not null)
        {
            Console.Error.WriteLine(note);
        }

        // The release workflow refuses a tag that doesn't match the version DalamudPackager writes
        var solution = Solution();
        Shell.Run("dotnet", "build", solution, "-c", "Release", "--nologo", "-v", "q");
        var manifest = Path.Combine(git.Top, plugin.Dir, "bin", "x64", "Release", plugin.Name, plugin.Name + ".json");
        var built = File.Exists(manifest) ? JsonString(JsonDocument.Parse(File.ReadAllText(manifest)).RootElement, "AssemblyVersion") : "";
        if (built != version)
        {
            throw new ToolException($"The built manifest says {built}, not {version}.");
        }
        Shell.Run("dotnet", "test", solution, "-c", "Release", "--nologo", "-v", "q");

        var changes = ChangesSince(last);
        Console.WriteLine($"Changes since {last}:");
        Console.WriteLine(changes);

        var dated = reversion
            ? $"moves the CHANGELOG section of {current}, never released as stable, to {version} with the Unreleased entries"
            : "dates the CHANGELOG section";
        git.Run("commit", "-q", "-m", $"Prepare {version}", "--", plugin.Csproj, "CHANGELOG.md");
        git.Run("push", "-q", "-u", "origin", branch);
        var body = $"""
            Sets `<Version>` to {version} and {dated}. Made by `dotnet buildingway release prepare`, which built the plugin and ran the tests.

            Before merging, check that every user-facing change below has a CHANGELOG entry. ⚠ marks a change to plugin code that touched no CHANGELOG entry.

            Changes since {last}:
            {changes}
            {(note is null ? "" : $"\n{note}\n")}
            After merging, publish it with `dotnet buildingway release publish {version} --testing` for a testing build, or `dotnet buildingway release publish {version}` for the stable release.
            """;
        Shell.Run("gh", "pr", "create", "--base", "main", "--head", branch, "--title", $"Prepare {version}", "--body", body);
    }

    /// <summary>The checkout's one .slnx or .sln, else the plugin's csproj.</summary>
    private string Solution()
    {
        var solutions = Directory.EnumerateFiles(git.Top, "*.slnx").Concat(Directory.EnumerateFiles(git.Top, "*.sln")).ToList();
        return solutions.Count == 1 ? solutions[0] : CsprojPath;
    }

    /// <summary>
    /// A note when the launcher has updated the local Dalamud past the one CI is pinned to, so
    /// releases still build against an older one than you test with in-game.
    /// </summary>
    private string? DalamudNote()
    {
        var pinFile = Path.Combine(git.Top, PinFile);
        if (!File.Exists(pinFile))
        {
            return null;
        }
        var pinned = File.ReadLines(pinFile).FirstOrDefault(l => l.StartsWith("DALAMUD_VERSION=", StringComparison.Ordinal))?["DALAMUD_VERSION=".Length..];
        if (string.IsNullOrEmpty(pinned))
        {
            return null;
        }
        var deps = Path.Combine(LocalDalamudDir(), "Dalamud.deps.json");
        var local = File.Exists(deps) && DalamudDep().Match(File.ReadAllText(deps)) is { Success: true } m ? m.Groups[1].Value : null;
        if (local is null)
        {
            return $"⚠ Couldn't read the local Dalamud version from {deps}; CI builds against {pinned}.";
        }
        return local == pinned
            ? null
            : $"⚠ Local Dalamud is {local} but CI builds against {pinned}. If you've tested with {local}, move the pin in {PinFile}.";
    }

    /// <summary>The Dalamud local builds use, as Dalamud.NET.Sdk finds it.</summary>
    private static string LocalDalamudDir()
    {
        var set = Environment.GetEnvironmentVariable("DALAMUD_HOME");
        if (!string.IsNullOrEmpty(set))
        {
            return set;
        }
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (OperatingSystem.IsWindows())
        {
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "XIVLauncher", "addon", "Hooks", "dev");
        }
        return OperatingSystem.IsMacOS()
            ? Path.Combine(home, "Library", "Application Support", "XIV on Mac", "dalamud", "Hooks", "dev")
            : Path.Combine(home, ".xlcore", "dalamud", "Hooks", "dev");
    }

    /// <summary>
    /// One line per change on main since last: number, title and a flag when it changed plugin
    /// code without touching the CHANGELOG.
    /// </summary>
    private string ChangesSince(string last)
    {
        var lines = new List<string>();
        foreach (var entry in git.Output("log", "--first-parent", "--format=%H%x09%s", $"{last}..origin/main").Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var (commit, subject) = (entry[..entry.IndexOf('\t')], entry[(entry.IndexOf('\t') + 1)..]);
            var body = MergeCommit().IsMatch(subject) ? git.Output("log", "-1", "--format=%b", commit) : "";
            var (number, title) = ParseMergeSubject(subject, body);
            var codeChanged = !git.Succeeds(["diff", "--quiet", $"{commit}^", commit, "--", .. plugin.CodePaths]);
            var flag = codeChanged && git.Succeeds("diff", "--quiet", $"{commit}^", commit, "--", "CHANGELOG.md")
                ? " ⚠ plugin code changed, no CHANGELOG entry"
                : "";
            lines.Add($"- {(number is null ? "" : $"#{number} ")}{title}{flag}");
        }
        return string.Join('\n', lines);
    }

    private void Publish(string version, bool testing)
    {
        if (!IsVersion(version))
        {
            throw new ToolException("Version must be four numbers, e.g. 0.1.2.0");
        }
        var repo = plugin.Repo;
        // The workflow publishes main's <Version>; naming it here catches publishing the wrong one
        git.Run("fetch", "-q", "origin", "main");
        var current = MainVersion();
        if (current != version)
        {
            throw new ToolException($"<Version> on main is {current}, not {version}. Run prepare {version} and merge its PR first.");
        }
        // As the workflow's tag job: installs that have a testing build only update to a higher version
        if (testing && HighestRc(git.Output("ls-remote", "--tags", "--refs", "origin", $"refs/tags/v{version}-rc*"), version) is { } rc)
        {
            var parts = version.Split('.');
            var next = $"{string.Join('.', parts[..3])}.{int.Parse(parts[3]) + 1}";
            throw new ToolException($"{rc} is already out for testing, and installs that have it won't update to another build of {version}. Run prepare {next} and merge its PR first.");
        }

        var channel = testing ? "testing" : "stable";
        Shell.Run("gh", "workflow", "run", "release.yml", "-R", repo, "--ref", "main", "-f", $"channel={channel}");
        Console.WriteLine($"Started the Release workflow for the {channel} channel. Its summary names the tag; follow it with:");
        Console.WriteLine($"  gh run watch -R {repo} $(gh run list -R {repo} --workflow release.yml -L 1 --json databaseId --jq '.[].databaseId')");
    }

    private int Verify(string given)
    {
        if (!VerifyVersion().IsMatch(given))
        {
            throw new ToolException("Give a version, e.g. 0.1.2.0, or a testing build, e.g. 0.1.2.0-rc1");
        }
        var version = given.Split('-')[0];
        var tag = $"v{given}";
        var testing = given != version;
        var repo = plugin.Repo;
        var zipName = plugin.Name + ".zip";
        var checks = new Checks(Console.Out);

        git.Run("fetch", "-q", "--tags", "origin", "main");

        if (git.HasRef($"refs/tags/{tag}"))
        {
            checks.Check($"{tag} is on main", git.Succeeds("merge-base", "--is-ancestor", tag, "origin/main") ? "yes" : "", "yes");
            checks.Check($"<Version> at {tag}", PluginProject.VersionIn(git.Output("show", $"{tag}:{plugin.Csproj}")) ?? "", version);
        }
        else
        {
            checks.Check($"tag {tag}", "", "pushed to origin");
        }

        var (code, release) = Shell.TryOutput("gh", "release", "view", tag, "-R", repo, "--json", "isPrerelease,isDraft,assets",
            "--jq", """if .isDraft then "draft" else (if .isPrerelease then "prerelease " else "" end) + ([.assets[].name] | join(",")) end""");
        checks.Check($"GitHub release {tag}", code == 0 ? release : "", (testing ? "prerelease " : "") + zipName);

        using var http = new HttpClient();
        http.DefaultRequestHeaders.UserAgent.ParseAdd("buildingway-tools");
        var url = $"https://github.com/{repo}/releases/download/{tag}/{zipName}";
        var zip = Download(http, url);
        if (zip is not null)
        {
            checks.Check("version in the zip's manifest", ManifestVersion(zip, plugin.Name + ".json"), version);
        }
        else
        {
            checks.Check($"download {url}", "", "the zip");
        }

        // What /xlsettings fetches. raw.githubusercontent.com caches for a few minutes, so a repo.json
        // PR merged just now can still show the old version.
        var repoJson = Download(http, $"https://raw.githubusercontent.com/{repo}/main/repo.json");
        var entry = RepoEntry(repoJson);
        string Field(string name) => entry is { } e ? JsonString(e, name) : "";
        if (testing)
        {
            // Dalamud offers the testing build only above the stable version and at the game's API level
            checks.Check("repo.json TestingAssemblyVersion", Field("TestingAssemblyVersion"), version);
            checks.Check("repo.json DownloadLinkTesting", Field("DownloadLinkTesting"), url);
            checks.Check("repo.json TestingDalamudApiLevel matches DalamudApiLevel", Field("TestingDalamudApiLevel"), Field("DalamudApiLevel"));
            var stable = Field("AssemblyVersion");
            // No stable version yet counts as below
            var below = stable != version && (!IsVersion(stable) || CompareVersions(stable, version) < 0);
            checks.Check($"repo.json AssemblyVersion ({stable}) is below {version}", below ? "yes" : "", "yes");
            foreach (var link in new[] { "DownloadLinkInstall", "DownloadLinkUpdate" })
            {
                checks.Check($"repo.json {link} doesn't point at {tag}", Field(link) != url ? "yes" : "", "yes");
            }
        }
        else
        {
            checks.Check("repo.json AssemblyVersion", Field("AssemblyVersion"), version);
            foreach (var link in new[] { "DownloadLinkInstall", "DownloadLinkUpdate", "DownloadLinkTesting" })
            {
                checks.Check($"repo.json {link}", Field(link), url);
            }
            // A testing version left behind would keep opted-in installs on the build being released
            checks.Check("repo.json has no TestingAssemblyVersion", Field("TestingAssemblyVersion"), "");
            checks.Check("repo.json IconUrl", Field("IconUrl"),
                $"https://raw.githubusercontent.com/{repo}/{tag}/{plugin.Dir}/images/icon.png");
        }
        checks.Check("icon downloads", StatusCode(http, Field("IconUrl")), "200");

        if (!checks.Failed)
        {
            return 0;
        }
        var (_, pr) = Shell.TryOutput("gh", "pr", "list", "-R", repo, "--head", "release/repo-json", "--state", "open", "--json", "url", "--jq", ".[].url");
        if (pr.Length > 0)
        {
            Console.WriteLine($"The repo.json PR isn't merged yet: {pr}");
        }
        return 1;
    }

    /// <summary>The first entry of a repo.json array, or null when it isn't one.</summary>
    public static JsonElement? RepoEntry(byte[]? repoJson)
    {
        if (repoJson is null)
        {
            return null;
        }
        try
        {
            var root = JsonDocument.Parse(repoJson).RootElement;
            return root.ValueKind == JsonValueKind.Array && root.GetArrayLength() > 0 ? root[0] : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>A string or number property as text, or "" when it's missing.</summary>
    public static string JsonString(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value)
            ? value.ValueKind switch
            {
                JsonValueKind.String => value.GetString() ?? "",
                JsonValueKind.Number => value.GetRawText(),
                _ => "",
            }
            : "";

    /// <summary>AssemblyVersion from the manifest at the zip's root, or "".</summary>
    public static string ManifestVersion(byte[] zip, string manifestName)
    {
        try
        {
            using var archive = new ZipArchive(new MemoryStream(zip));
            var manifest = archive.GetEntry(manifestName);
            if (manifest is null)
            {
                return "";
            }
            using var stream = manifest.Open();
            return JsonString(JsonDocument.Parse(stream).RootElement, "AssemblyVersion");
        }
        catch (Exception e) when (e is InvalidDataException or JsonException)
        {
            return "";
        }
    }

    private static byte[]? Download(HttpClient http, string url)
    {
        try
        {
            using var response = http.GetAsync(url).GetAwaiter().GetResult();
            return response.IsSuccessStatusCode ? response.Content.ReadAsByteArrayAsync().GetAwaiter().GetResult() : null;
        }
        catch (HttpRequestException)
        {
            return null;
        }
    }

    private static string StatusCode(HttpClient http, string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return "";
        }
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Head, uri);
            using var response = http.Send(request);
            return ((int)response.StatusCode).ToString(System.Globalization.CultureInfo.InvariantCulture);
        }
        catch (HttpRequestException)
        {
            return "";
        }
    }

    [GeneratedRegex(@"^[0-9]+(\.[0-9]+){3}$")]
    private static partial Regex FourNumbers();

    [GeneratedRegex(@"^[0-9]+(\.[0-9]+){3}(-rc[0-9]+)?$")]
    private static partial Regex VerifyVersion();

    [GeneratedRegex(@"^Merge pull request #([0-9]+)")]
    private static partial Regex MergeCommit();

    [GeneratedRegex(@"^(.*) \(#([0-9]+)\)$")]
    private static partial Regex SquashCommit();

    [GeneratedRegex("\"Dalamud/([^\"]*)\"")]
    private static partial Regex DalamudDep();
}

/// <summary>Prints ✓ or ✗ per check and remembers whether any failed.</summary>
internal sealed class Checks(TextWriter output)
{
    public bool Failed { get; private set; }

    public void Check(string name, string got, string expected)
    {
        if (got == expected)
        {
            output.WriteLine($"✓ {name}");
            return;
        }
        output.WriteLine($"✗ {name}: expected {Nothing(expected)}, got {Nothing(got)}");
        Failed = true;
    }

    private static string Nothing(string value) => value.Length == 0 ? "nothing" : value;
}
