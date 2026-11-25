using System.Net;
using System.Text.Json;

namespace Buildingway.Tools.Tests;

/// <summary>
/// Stands in for dotnet and gh: records each command and never starts a process. dotnet build
/// writes the manifest DalamudPackager would, with the csproj's version. A command with no
/// response set fails the test.
/// </summary>
internal sealed class FakeCommands(Func<string> csprojVersion, string manifestPath) : ICommands
{
    public List<string[]> Ran { get; } = [];

    /// <summary>Exit codes and outputs by the command's start, e.g. "gh release view".</summary>
    public Dictionary<string, (int Code, string Output)> Responses { get; } = new()
    {
        ["dotnet test"] = (0, ""),
        ["gh pr create"] = (0, ""),
        ["gh workflow run"] = (0, ""),
        ["gh pr list"] = (0, ""),
    };

    /// <summary>What dotnet build writes as the manifest's AssemblyVersion; null writes no manifest.</summary>
    public Func<string?> BuiltVersion { get; set; } = csprojVersion;

    public void Run(string file, params string[] args)
    {
        var (code, _) = TryOutput(file, args);
        if (code != 0)
        {
            throw new ToolException($"{file} {string.Join(' ', args)} exited with {code}", code);
        }
    }

    public (int Code, string Output) TryOutput(string file, params string[] args)
    {
        Ran.Add([file, .. args]);
        if (file == "dotnet" && args is ["build", ..])
        {
            if (BuiltVersion() is { } version)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(manifestPath)!);
                File.WriteAllText(manifestPath, JsonSerializer.Serialize(new { AssemblyVersion = version }));
            }
            return (0, "");
        }
        var key = Responses.Keys.Where(k => $"{file} {string.Join(' ', args)}".StartsWith(k, StringComparison.Ordinal)).MaxBy(k => k.Length);
        return key is not null ? Responses[key] : throw new InvalidOperationException($"No fake response for {file} {string.Join(' ', args)}");
    }

    public string[] Single(string start) =>
        Assert.Single(Ran, r => string.Join(' ', r).StartsWith(start, StringComparison.Ordinal));
}

/// <summary>HTTP with canned responses by URL; anything else is a 404, so nothing reaches the network.</summary>
internal sealed class FakeHttp : HttpMessageHandler
{
    public Dictionary<string, byte[]> Responses { get; } = [];

    /// <summary>URLs whose request fails as an unreachable host would.</summary>
    public HashSet<string> Unreachable { get; } = [];

    public List<(HttpMethod Method, string Url, string UserAgent)> Requested { get; } = [];

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
        Task.FromResult(Send(request, cancellationToken));

    protected override HttpResponseMessage Send(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var url = request.RequestUri!.ToString();
        Requested.Add((request.Method, url, request.Headers.UserAgent.ToString()));
        if (Unreachable.Contains(url))
        {
            throw new HttpRequestException($"Can't reach {url}");
        }
        return Responses.TryGetValue(url, out var body)
            ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(body) }
            : new HttpResponseMessage(HttpStatusCode.NotFound);
    }
}

/// <summary>
/// A plugin checkout whose origin is a bare repo in a temp folder, with fake dotnet, gh and HTTP.
/// Nothing a test runs can reach a real repo: the checkout and origin are checked to be the
/// temp ones before any test uses them, hooks are off, and other commands never start a process.
/// </summary>
internal sealed class ReleaseRepo : IDisposable
{
    public const string Name = "MyPlugin";
    public const string RepoName = "Owner/my-plugin";

    /// <summary>A host for tests that run only git: any other command fails them.</summary>
    public static ReleaseHost Offline => new(new NoCommands(), () => new FakeHttp(), TextWriter.Null, TextWriter.Null, () => DateTime.MinValue, "");

    private sealed class NoCommands : ICommands
    {
        public void Run(string file, params string[] args) => TryOutput(file, args);

        public (int Code, string Output) TryOutput(string file, params string[] args) =>
            throw new InvalidOperationException($"Unexpected command: {file} {string.Join(' ', args)}");
    }

    private readonly PluginRepo repo;
    private readonly string outside = Directory.CreateTempSubdirectory("buildingway-origin").FullName;

    public ReleaseRepo(string version = "0.1.0.0", string changelog = "# Changelog\n\n## Unreleased\n\n### Added\n\n- A window\n")
    {
        repo = new PluginRepo(Name, $"https://github.com/{RepoName}");
        Origin = Path.Combine(outside, "origin.git");
        Directory.CreateDirectory(Path.Combine(outside, "hooks"));
        Shell.Output("git", "init", "-q", "--bare", "-b", "main", Origin);
        foreach (var (key, value) in new[]
        {
            ("user.name", "Test"), ("user.email", "test@example.com"), ("commit.gpgsign", "false"),
            ("tag.gpgsign", "false"), ("core.hooksPath", Path.Combine(outside, "hooks")), ("push.autoSetupRemote", "false"),
        })
        {
            Git.Output("config", key, value);
        }
        Git.Output("remote", "add", "origin", Origin);
        GuardIsolated();

        WriteCsproj(version);
        File.WriteAllText(ChangelogPath, changelog);
        Commit("Add the plugin");
        Git.Output("push", "-q", "origin", "main");

        DalamudDir = Path.Combine(outside, "dalamud");
        Directory.CreateDirectory(DalamudDir);
        Commands = new FakeCommands(() => CsprojVersion(), Path.Combine(Top, Name, "bin", "x64", "Release", Name, Name + ".json"));
        Host = new ReleaseHost(Commands, () => Http, Output, Error, () => new DateTime(2026, 11, 25), DalamudDir);
    }

    public string Top => repo.Top;

    public Git Git => repo.Git;

    public string Origin { get; }

    public string DalamudDir { get; }

    public FakeCommands Commands { get; }

    public FakeHttp Http { get; } = new();

    public StringWriter Output { get; } = new();

    public StringWriter Error { get; } = new();

    public ReleaseHost Host { get; }

    public string CsprojPath => Path.Combine(Top, Name, Name + ".csproj");

    /// <summary>The csproj as Release passes it to dotnet: the checkout joined to the forward-slash relative path.</summary>
    public string CsprojArgument => Path.Combine(Top, $"{Name}/{Name}.csproj");

    public string ChangelogPath => Path.Combine(Top, "CHANGELOG.md");

    public Release Release => new(Git, repo.Plugin, Host);

    public int Run(params string[] args) => Release.Run(args, () => Git, Host);

    public void Commit(string message) => repo.Commit(message);

    /// <summary>Commits on main and pushes it, as merging a PR on GitHub would.</summary>
    public void Merge(string message)
    {
        Commit(message);
        Git.Output("push", "-q", "origin", "main");
    }

    public void WriteCsproj(string version) => File.WriteAllText(CsprojPath, $"""
        <Project Sdk="Dalamud.NET.Sdk/15.0.0">
          <PropertyGroup>
            <Version>{version}</Version>
            <RepoUrl>https://github.com/{RepoName}</RepoUrl>
          </PropertyGroup>
        </Project>
        """);

    public string CsprojVersion() => PluginProject.VersionIn(File.ReadAllText(CsprojPath)) ?? "";

    /// <summary>Origin's content of a file on a branch.</summary>
    public string OriginShow(string branch, string path) => Shell.Output("git", "-C", Origin, "show", $"{branch}:{path}");

    public bool OriginHas(string reference) => Shell.TryOutput("git", "-C", Origin, "rev-parse", "-q", "--verify", reference).Code == 0;

    /// <summary>Fails unless the checkout's origin, and its push URL, are the temp bare repo.</summary>
    private void GuardIsolated()
    {
        var fetch = Git.Output("remote", "get-url", "origin");
        var push = Git.Output("remote", "get-url", "--push", "origin");
        if (fetch != Origin || push != Origin)
        {
            throw new InvalidOperationException($"Release tests need the temp origin {Origin}, but git has {fetch} (push {push}).");
        }
    }

    public void Dispose()
    {
        repo.Dispose();
        foreach (var file in Directory.EnumerateFiles(outside, "*", SearchOption.AllDirectories))
        {
            File.SetAttributes(file, FileAttributes.Normal);
        }
        Directory.Delete(outside, recursive: true);
    }
}
