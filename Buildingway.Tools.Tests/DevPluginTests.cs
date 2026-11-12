namespace Buildingway.Tools.Tests;

public sealed class DevPluginTests : IDisposable
{
    private readonly string root = Directory.CreateTempSubdirectory("buildingway-dev").FullName;
    private readonly StringWriter output = new();
    private readonly StringWriter error = new();

    public DevPluginTests()
    {
        // A build output: dependency, plugin dll, manifest and icon
        Directory.CreateDirectory(Path.Combine(Build, "images"));
        File.WriteAllText(Path.Combine(Build, "Dep.dll"), "dep");
        File.WriteAllText(Path.Combine(Build, "MyPlugin.dll"), "plugin");
        File.WriteAllText(Path.Combine(Build, "MyPlugin.json"), "{}");
        File.WriteAllText(Path.Combine(Build, "images", "icon.png"), "icon");
    }

    public void Dispose() => Directory.Delete(root, recursive: true);

    private string Build => Path.Combine(root, "build");

    private string DevDir => Path.Combine(root, "dev");

    private string Info => Path.Combine(DevDir, ".claim", "info");

    private DevPlugin From(string checkout) => new(DevDir, checkout, output, error);

    private const string Details = "branch=main\ncommit=abc1234\ntask=e9dfe430\ndeployed=2026-11-05T19:00:00+01:00\n";

    [Fact]
    public void DeployClaimsAndCopiesEverything()
    {
        // revert-check: not revert-sensitive (kills a surviving mutant: pins existing behaviour)
        Assert.Equal(0, From("/repo/a").Deploy(Build, "MyPlugin", Details));

        Assert.Equal("plugin", File.ReadAllText(Path.Combine(DevDir, "MyPlugin.dll")));
        Assert.Equal("dep", File.ReadAllText(Path.Combine(DevDir, "Dep.dll")));
        Assert.Equal("icon", File.ReadAllText(Path.Combine(DevDir, "images", "icon.png")));
        Assert.Empty(Directory.EnumerateFiles(DevDir, "*.tmp", SearchOption.AllDirectories));
        // The earlier scripts' format, LF and no BOM, so their checkouts can still read the claim
        Assert.Equal("checkout=/repo/a\n" + Details, File.ReadAllText(Info));
        Assert.Equal((byte)'c', File.ReadAllBytes(Info)[0]);
        Assert.Equal(
            $"Deployed to {DevDir}:\n  checkout=/repo/a\n  branch=main\n  commit=abc1234\n  task=e9dfe430\n  deployed=2026-11-05T19:00:00+01:00\n",
            output.ToString().ReplaceLineEndings("\n"));
    }

    [Fact]
    public void AStaleTempFileFromAnInterruptedCopyIsOverwritten()
    {
        // revert-check: not revert-sensitive (kills a surviving mutant: pins existing behaviour)
        Directory.CreateDirectory(DevDir);
        File.WriteAllText(Path.Combine(DevDir, ".MyPlugin.dll.tmp"), "half a copy");

        Assert.Equal(0, From("/repo/a").Deploy(Build, "MyPlugin", Details));

        Assert.Equal("plugin", File.ReadAllText(Path.Combine(DevDir, "MyPlugin.dll")));
        Assert.Empty(Directory.EnumerateFiles(DevDir, "*.tmp"));
    }

    [Fact]
    public void AnotherCheckoutCantDeployOrRelease()
    {
        // revert-check: not revert-sensitive (kills a surviving mutant: pins existing behaviour)
        From("/repo/a").Deploy(Build, "MyPlugin", Details);
        File.WriteAllText(Path.Combine(Build, "MyPlugin.dll"), "other build");

        Assert.Equal(1, From("/repo/b").Deploy(Build, "MyPlugin", Details));
        Assert.Equal("plugin", File.ReadAllText(Path.Combine(DevDir, "MyPlugin.dll")));
        Assert.Equal(1, From("/repo/b").Release(force: false));
        Assert.True(File.Exists(Info));

        var claim = "  checkout=/repo/a\n  branch=main\n  commit=abc1234\n  task=e9dfe430\n  deployed=2026-11-05T19:00:00+01:00\n";
        Assert.Equal(
            "Refusing to deploy: another checkout is testing in the dev folder.\n" + claim +
            "Ask whoever holds it to run 'dotnet buildingway dev-plugin release' when they're done.\n" +
            "Refusing to release: the claim belongs to another checkout.\n" + claim,
            error.ToString().ReplaceLineEndings("\n"));
    }

    // The checkout= line is found wherever it is; a claim without one belongs to nobody here
    [Theory]
    [InlineData("branch=main\ncheckout=/repo/a\n", 0)]
    [InlineData("branch=main\ncommit=abc1234\n", 1)]
    public void TheClaimOwnerIsItsCheckoutLine(string info, int exitCode)
    {
        // revert-check: not revert-sensitive (kills a surviving mutant: pins existing behaviour)
        Directory.CreateDirectory(Path.GetDirectoryName(Info)!);
        File.WriteAllText(Info, info);

        Assert.Equal(exitCode, From("/repo/a").Deploy(Build, "MyPlugin", Details));
    }

    [Fact]
    public void TheHolderRedeploysAndReleases()
    {
        // revert-check: not revert-sensitive (kills a surviving mutant: pins existing behaviour)
        From("/repo/a").Deploy(Build, "MyPlugin", Details);
        File.WriteAllText(Path.Combine(Build, "MyPlugin.dll"), "new build");

        Assert.Equal(0, From("/repo/a").Deploy(Build, "MyPlugin", Details.Replace("abc1234", "def5678")));
        Assert.Equal("new build", File.ReadAllText(Path.Combine(DevDir, "MyPlugin.dll")));
        Assert.Contains("def5678", File.ReadAllText(Info));

        output.GetStringBuilder().Clear();
        From("/repo/a").Status();
        Assert.StartsWith("Claimed:\n  checkout=/repo/a\n", output.ToString().ReplaceLineEndings("\n"));

        output.GetStringBuilder().Clear();
        Assert.Equal(0, From("/repo/a").Release(force: false));
        Assert.Equal($"Released {DevDir}\n", output.ToString().ReplaceLineEndings("\n"));
        Assert.False(Directory.Exists(Path.Combine(DevDir, ".claim")));
        Assert.Equal(0, From("/repo/b").Deploy(Build, "MyPlugin", Details));
    }

    [Fact]
    public void ForceReleasesAnotherCheckoutsClaim()
    {
        From("/repo/a").Deploy(Build, "MyPlugin", Details);
        Assert.Equal(0, From("/repo/b").Release(force: true));
        Assert.False(Directory.Exists(Path.Combine(DevDir, ".claim")));
    }

    [Fact]
    public void AClaimWithoutDetailsBlocksEveryone()
    {
        // Left over from a deploy interrupted between claiming and writing the info file
        Directory.CreateDirectory(Path.Combine(DevDir, ".claim"));
        Assert.Equal(1, From("/repo/a").Deploy(Build, "MyPlugin", Details));
        Assert.Contains("claim has no details", error.ToString());

        From("/repo/a").Status();
        Assert.Contains("Claimed:", output.ToString());
    }

    [Fact]
    public void StatusAndReleaseWhenUnclaimed()
    {
        From("/repo/a").Status();
        Assert.Equal(0, From("/repo/a").Release(force: false));
        Assert.Equal($"Unclaimed: {DevDir}\nAlready unclaimed.\n", output.ToString().ReplaceLineEndings("\n"));
    }

    private int Run(PluginRepo repo, params string[] args) =>
        DevPlugin.Run(args, repo.Git, repo.Plugin, DevDir, plugin => { built.Add(plugin.Name); return Build; }, output, error);

    private readonly List<string> built = [];

    [Theory]
    [InlineData]
    [InlineData("deploy", "e9dfe430", "extra")]
    [InlineData("status", "now")]
    [InlineData("release", "-f")]
    [InlineData("release", "--force", "now")]
    [InlineData("deploy-now")]
    public void BadArgumentsPrintUsageWithTheDevFolder(params string[] args)
    {
        using var repo = new PluginRepo("MyPlugin");

        Assert.Equal(2, Run(repo, args));

        Assert.Empty(built);
        Assert.False(Directory.Exists(DevDir));
        Assert.Equal("", output.ToString());
        Assert.Equal(
            $"""
            Usage: dotnet buildingway dev-plugin deploy [task-ref]   build, claim the dev folder and copy the plugin in
                   dotnet buildingway dev-plugin status              show who holds the dev folder
                   dotnet buildingway dev-plugin release [--force]   free the dev folder (--force: even if another checkout holds it)

            Dev folder: {DevDir} (set MYPLUGIN_DEV_DIR to change)

            """,
            error.ToString().ReplaceLineEndings("\n"));
    }

    [Theory]
    [InlineData("e9dfe430")]
    [InlineData(null)]
    public void DeployBuildsAndClaimsForThisCheckout(string? task)
    {
        using var repo = new PluginRepo("MyPlugin");
        var commit = repo.Git.Output("rev-parse", "--short", "HEAD");

        Assert.Equal(0, task is null ? Run(repo, "deploy") : Run(repo, "deploy", task));

        Assert.Equal(["MyPlugin"], built);
        Assert.Equal("plugin", File.ReadAllText(Path.Combine(DevDir, "MyPlugin.dll")));
        var info = File.ReadAllLines(Info);
        Assert.Equal([$"checkout={repo.Top}", "branch=main", $"commit={commit}", $"task={task}"], info[..4]);
        Assert.Matches(@"^deployed=\d{4}-\d\d-\d\dT\d\d:\d\d:\d\d[+-]\d\d:\d\d$", info[4]);
        Assert.Equal(5, info.Length);
    }

    [Fact]
    public void StatusAndReleaseGoToThisCheckoutsClaim()
    {
        using var repo = new PluginRepo("MyPlugin");
        Run(repo, "deploy");
        output.GetStringBuilder().Clear();

        Assert.Equal(0, Run(repo, "status"));
        Assert.StartsWith($"Claimed:\n  checkout={repo.Top}\n", output.ToString().ReplaceLineEndings("\n"));

        Assert.Equal(0, Run(repo, "release"));
        Assert.False(Directory.Exists(Path.Combine(DevDir, ".claim")));
        Assert.Equal("", error.ToString());
    }

    [Fact]
    public void ReleaseNeedsForceForAnotherCheckoutsClaim()
    {
        using var repo = new PluginRepo("MyPlugin");
        From("/repo/other").Deploy(Build, "MyPlugin", Details);

        Assert.Equal(1, Run(repo, "release"));
        Assert.True(File.Exists(Info));
        Assert.Equal(0, Run(repo, "release", "--force"));
        Assert.False(Directory.Exists(Path.Combine(DevDir, ".claim")));
    }

    [Fact]
    public void ClaimDetailsNameTheBranchCommitTaskAndTime()
    {
        using var repo = new PluginRepo("MyPlugin");
        var commit = repo.Git.Output("rev-parse", "--short", "HEAD");
        var now = new DateTimeOffset(2026, 11, 5, 19, 0, 7, TimeSpan.FromHours(1));

        Assert.Equal(
            $"branch=main\ncommit={commit}\ntask=e9dfe430\ndeployed=2026-11-05T19:00:07+01:00\n",
            DevPlugin.ClaimDetails(repo.Git, "e9dfe430", now));

        repo.Git.Output("switch", "-q", "-c", "feature");
        File.WriteAllText(Path.Combine(repo.Top, "MyPlugin", "Plugin.cs"), "// edit");
        Assert.Equal(
            $"branch=feature\ncommit={commit} (uncommitted changes)\ntask=\ndeployed=2026-11-05T19:00:07+01:00\n",
            DevPlugin.ClaimDetails(repo.Git, "", now));
    }

    [Fact]
    public void BuildIsAnX64DebugBuildOfThePluginProject()
    {
        using var repo = new PluginRepo("MyPlugin");

        var (args, output) = DevPlugin.BuildCommand(repo.Plugin);

        Assert.Equal(
            ["build", Path.Combine(repo.Top, "MyPlugin/MyPlugin.csproj"), "-c", "Debug", "-p:Platform=x64", "--nologo", "-v", "q"],
            args);
        Assert.Equal(Path.Combine(repo.Top, "MyPlugin", "bin", "x64", "Debug"), output);
    }

    [Theory]
    [InlineData("/set/dir", false, "/set/dir")]
    [InlineData("/set/dir", true, "/set/dir")]
    [InlineData("", false, "home/.xlcore/devPlugins/MyPlugin")]
    [InlineData(null, false, "home/.xlcore/devPlugins/MyPlugin")]
    [InlineData(null, true, "appdata/XIVLauncher/devPlugins/MyPlugin")]
    public void TheDevFolderIsTheVariableOrTheLaunchersFolder(string? set, bool windows, string expected)
    {
        Assert.Equal(
            expected.Replace('/', Path.DirectorySeparatorChar),
            DevPlugin.DefaultDevDir("MyPlugin", set, windows, "appdata", "home").Replace('/', Path.DirectorySeparatorChar));
    }

    [Fact]
    public void TheDevFolderComesFromThePluginsVariable()
    {
        // A plugin name no other test uses, since the variable is process-wide
        using var repo = new PluginRepo("DevDirProbe");
        var launcher = OperatingSystem.IsWindows()
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "XIVLauncher")
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".xlcore");
        Assert.Equal(Path.Combine(launcher, "devPlugins", "DevDirProbe"), DevPlugin.DefaultDevDir(repo.Plugin));

        Environment.SetEnvironmentVariable("DEVDIRPROBE_DEV_DIR", DevDir);
        try
        {
            Assert.Equal(DevDir, DevPlugin.DefaultDevDir(repo.Plugin));
        }
        finally
        {
            Environment.SetEnvironmentVariable("DEVDIRPROBE_DEV_DIR", null);
        }
    }
}
