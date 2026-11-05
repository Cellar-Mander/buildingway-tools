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
        Assert.Equal(0, From("/repo/a").Deploy(Build, "MyPlugin", Details));

        Assert.Equal("plugin", File.ReadAllText(Path.Combine(DevDir, "MyPlugin.dll")));
        Assert.Equal("dep", File.ReadAllText(Path.Combine(DevDir, "Dep.dll")));
        Assert.Equal("icon", File.ReadAllText(Path.Combine(DevDir, "images", "icon.png")));
        Assert.Empty(Directory.EnumerateFiles(DevDir, "*.tmp", SearchOption.AllDirectories));
        // The earlier scripts' format, LF and no BOM, so their checkouts can still read the claim
        Assert.Equal("checkout=/repo/a\n" + Details, File.ReadAllText(Info));
        Assert.Equal((byte)'c', File.ReadAllBytes(Info)[0]);
    }

    [Fact]
    public void AnotherCheckoutCantDeployOrRelease()
    {
        From("/repo/a").Deploy(Build, "MyPlugin", Details);
        File.WriteAllText(Path.Combine(Build, "MyPlugin.dll"), "other build");

        Assert.Equal(1, From("/repo/b").Deploy(Build, "MyPlugin", Details));
        Assert.Equal("plugin", File.ReadAllText(Path.Combine(DevDir, "MyPlugin.dll")));
        Assert.Contains("checkout=/repo/a", error.ToString());

        Assert.Equal(1, From("/repo/b").Release(force: false));
        Assert.True(File.Exists(Info));
    }

    [Fact]
    public void TheHolderRedeploysAndReleases()
    {
        From("/repo/a").Deploy(Build, "MyPlugin", Details);
        File.WriteAllText(Path.Combine(Build, "MyPlugin.dll"), "new build");

        Assert.Equal(0, From("/repo/a").Deploy(Build, "MyPlugin", Details.Replace("abc1234", "def5678")));
        Assert.Equal("new build", File.ReadAllText(Path.Combine(DevDir, "MyPlugin.dll")));
        Assert.Contains("def5678", File.ReadAllText(Info));

        Assert.Equal(0, From("/repo/a").Release(force: false));
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
