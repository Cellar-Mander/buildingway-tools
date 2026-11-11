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
}
