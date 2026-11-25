using System.IO.Compression;
using System.Text;

namespace Buildingway.Tools.Tests;

/// <summary>release publish and verify, against a temp checkout and origin with fake gh and HTTP.</summary>
public sealed class ReleasePublishVerifyTests : IDisposable
{
    private const string Raw = $"https://raw.githubusercontent.com/{ReleaseRepo.RepoName}";
    private const string Download = $"https://github.com/{ReleaseRepo.RepoName}/releases/download";
    private const string StableZip = $"{Download}/v0.1.0.0/MyPlugin.zip";
    private const string StableIcon = $"{Raw}/v0.1.0.0/MyPlugin/images/icon.png";
    private const string RepoJson = $"{Raw}/main/repo.json";

    private const string NoVersion = $"<Project Sdk=\"Dalamud.NET.Sdk/15.0.0\"><PropertyGroup><RepoUrl>https://github.com/{ReleaseRepo.RepoName}</RepoUrl></PropertyGroup></Project>";

    private readonly ReleaseRepo repo = new();

    public void Dispose() => repo.Dispose();

    private string Out => repo.Output.ToString().ReplaceLineEndings("\n");

    [Fact]
    public void PublishRunsTheStableReleaseWorkflow()
    {
        Assert.Equal(0, repo.Run("publish", "0.1.0.0"));

        Assert.Equal([["gh", "workflow", "run", "release.yml", "-R", ReleaseRepo.RepoName, "--ref", "main", "-f", "channel=stable"]], repo.Commands.Ran);
        Assert.Equal($"""
            Started the Release workflow for the stable channel. Its summary names the tag; follow it with:
              gh run watch -R {ReleaseRepo.RepoName} $(gh run list -R {ReleaseRepo.RepoName} --workflow release.yml -L 1 --json databaseId --jq '.[].databaseId')

            """.ReplaceLineEndings("\n"), Out);
    }

    [Fact]
    public void PublishRunsTheTestingWorkflowWhenTheVersionHasNoTestingBuild()
    {
        PushTag("v0.0.9.0-rc1");
        PushTag("v0.1.0.0-test1");

        Assert.Equal(0, repo.Run("publish", "0.1.0.0", "--testing"));

        Assert.Equal("channel=testing", repo.Commands.Single("gh workflow run")[^1]);
        Assert.StartsWith("Started the Release workflow for the testing channel.", Out, StringComparison.Ordinal);
    }

    [Fact]
    public void PublishRefusesASecondTestingBuildOfAVersion()
    {
        repo.WriteCsproj("1.2.3.9");
        repo.Merge("Set the version");
        PushTag("v1.2.3.9-rc1");
        PushTag("v1.2.3.9-rc2");

        var e = Assert.Throws<ToolException>(() => repo.Run("publish", "1.2.3.9", "--testing"));

        Assert.Equal("v1.2.3.9-rc2 is already out for testing, and installs that have it won't update to another build of 1.2.3.9. Run prepare 1.2.3.10 and merge its PR first.", e.Message);
        Assert.Empty(repo.Commands.Ran);
    }

    [Fact]
    public void PublishSeesTestingBuildsOnlyOnOrigin()
    {
        PushTag("v0.1.0.0-rc1");
        repo.Git.Output("tag", "-d", "v0.1.0.0-rc1");

        Assert.Throws<ToolException>(() => repo.Run("publish", "0.1.0.0", "--testing"));
    }

    [Fact]
    public void PublishAllowsTheStableReleaseAfterATestingBuild()
    {
        PushTag("v0.1.0.0-rc1");

        Assert.Equal(0, repo.Run("publish", "0.1.0.0"));

        Assert.Equal("channel=stable", repo.Commands.Single("gh workflow run")[^1]);
    }

    [Fact]
    public void PublishRefusesAVersionThatIsNotMains()
    {
        var e = Assert.Throws<ToolException>(() => repo.Run("publish", "0.2.0.0"));

        Assert.Equal("<Version> on main is 0.1.0.0, not 0.2.0.0. Run prepare 0.2.0.0 and merge its PR first.", e.Message);
        Assert.Empty(repo.Commands.Ran);
    }

    [Fact]
    public void PublishFetchesMainFirst()
    {
        repo.WriteCsproj("0.2.0.0");
        repo.Merge("Prepare 0.2.0.0");
        repo.Git.Output("update-ref", "refs/remotes/origin/main", "HEAD~1");

        Assert.Equal(0, repo.Run("publish", "0.2.0.0"));
    }

    [Theory]
    [InlineData("0.1.0")]
    [InlineData("0.1.0.0-rc1")]
    public void PublishRefusesAVersionThatIsNotFourNumbers(string version)
    {
        var e = Assert.Throws<ToolException>(() => repo.Run("publish", version, "--testing"));

        Assert.Equal("Version must be four numbers, e.g. 0.1.2.0", e.Message);
        Assert.Empty(repo.Commands.Ran);
    }

    [Fact]
    public void VerifyPassesAStableReleaseThatInstallsGet()
    {
        Released();

        Assert.Equal(0, repo.Run("verify", "0.1.0.0"));

        Assert.Equal("""
            ✓ v0.1.0.0 is on main
            ✓ <Version> at v0.1.0.0
            ✓ GitHub release v0.1.0.0
            ✓ version in the zip's manifest
            ✓ repo.json AssemblyVersion
            ✓ repo.json DownloadLinkInstall
            ✓ repo.json DownloadLinkUpdate
            ✓ repo.json DownloadLinkTesting
            ✓ repo.json has no TestingAssemblyVersion
            ✓ repo.json IconUrl
            ✓ icon downloads

            """, Out);
        Assert.Equal(
            [["gh", "release", "view", "v0.1.0.0", "-R", ReleaseRepo.RepoName, "--json", "isPrerelease,isDraft,assets",
              "--jq", """if .isDraft then "draft" else (if .isPrerelease then "prerelease " else "" end) + ([.assets[].name] | join(",")) end"""]],
            repo.Commands.Ran);
        Assert.Equal(
            [(HttpMethod.Get, StableZip, "buildingway-tools"), (HttpMethod.Get, RepoJson, "buildingway-tools"), (HttpMethod.Head, StableIcon, "buildingway-tools")],
            repo.Http.Requested);
    }

    [Fact]
    public void VerifyFailsDownloadsFromAnUnreachableHost()
    {
        Released();
        repo.Http.Unreachable.UnionWith([StableZip, StableIcon]);

        Assert.Equal(1, repo.Run("verify", "0.1.0.0"));

        Assert.Contains($"✗ download {StableZip}: expected the zip, got nothing\n", Out, StringComparison.Ordinal);
        Assert.EndsWith("✗ icon downloads: expected 200, got nothing\n", Out, StringComparison.Ordinal);
    }

    [Fact]
    public void VerifyPassesATestingBuild()
    {
        repo.WriteCsproj("0.2.0.0");
        repo.Merge("Prepare 0.2.0.0");
        PushTag("v0.2.0.0-rc1");
        repo.Commands.Responses["gh release view"] = (0, "prerelease MyPlugin.zip");
        var zip = $"{Download}/v0.2.0.0-rc1/MyPlugin.zip";
        repo.Http.Responses[zip] = Zip("0.2.0.0");
        repo.Http.Responses[StableIcon] = [];
        repo.Http.Responses[RepoJson] = Encoding.UTF8.GetBytes($$"""
            [{"AssemblyVersion":"0.1.0.0","TestingAssemblyVersion":"0.2.0.0","DalamudApiLevel":14,"TestingDalamudApiLevel":14,
              "DownloadLinkInstall":"{{StableZip}}","DownloadLinkUpdate":"{{StableZip}}","DownloadLinkTesting":"{{zip}}","IconUrl":"{{StableIcon}}"}]
            """);

        Assert.Equal(0, repo.Run("verify", "0.2.0.0-rc1"));

        Assert.StartsWith("""
            ✓ v0.2.0.0-rc1 is on main
            ✓ <Version> at v0.2.0.0-rc1
            ✓ GitHub release v0.2.0.0-rc1
            ✓ version in the zip's manifest
            ✓ repo.json TestingAssemblyVersion

            """, Out, StringComparison.Ordinal);
        Assert.EndsWith("✓ icon downloads\n", Out, StringComparison.Ordinal);
    }

    [Fact]
    public void VerifyFailsATestingBuildThatIsNotAPrerelease()
    {
        repo.WriteCsproj("0.2.0.0");
        repo.Merge("Prepare 0.2.0.0");
        PushTag("v0.2.0.0-rc1");
        repo.Commands.Responses["gh release view"] = (0, "MyPlugin.zip");

        Assert.Equal(1, repo.Run("verify", "0.2.0.0-rc1"));

        Assert.Contains("✗ GitHub release v0.2.0.0-rc1: expected prerelease MyPlugin.zip, got MyPlugin.zip\n", Out, StringComparison.Ordinal);
    }

    [Fact]
    public void VerifyFetchesTagsAndMainFromOrigin()
    {
        Released();
        repo.Git.Output("tag", "-d", "v0.1.0.0");
        repo.Git.Output("update-ref", "refs/remotes/origin/main", "HEAD~1");

        Assert.Equal(0, repo.Run("verify", "0.1.0.0"));
    }

    [Fact]
    public void VerifyFailsAMissingTag()
    {
        Released();
        repo.Git.Output("push", "-q", "origin", ":refs/tags/v0.1.0.0");
        repo.Git.Output("tag", "-d", "v0.1.0.0");

        Assert.Equal(1, repo.Run("verify", "0.1.0.0"));

        Assert.StartsWith("✗ tag v0.1.0.0: expected pushed to origin, got nothing\n✓ GitHub release v0.1.0.0\n", Out, StringComparison.Ordinal);
    }

    [Fact]
    public void VerifyFailsATagOffMainOrAtAnotherVersion()
    {
        Released(tagged: false);
        repo.Git.Output("switch", "-q", "-c", "side");
        repo.WriteCsproj("0.0.9.0");
        repo.Commit("Side");
        PushTag("v0.1.0.0");
        // Only fetch --tags brings a tag that isn't on main
        repo.Git.Output("tag", "-d", "v0.1.0.0");

        Assert.Equal(1, repo.Run("verify", "0.1.0.0"));

        Assert.StartsWith("✗ v0.1.0.0 is on main: expected yes, got nothing\n✗ <Version> at v0.1.0.0: expected 0.1.0.0, got 0.0.9.0\n", Out, StringComparison.Ordinal);
    }

    [Fact]
    public void VerifyFailsATagWhoseCsprojHasNoVersion()
    {
        Released(tagged: false);
        File.WriteAllText(repo.CsprojPath, NoVersion);
        repo.Merge("Drop the version");
        PushTag("v0.1.0.0");

        Assert.Equal(1, repo.Run("verify", "0.1.0.0"));

        Assert.Contains("✗ <Version> at v0.1.0.0: expected 0.1.0.0, got nothing\n", Out, StringComparison.Ordinal);
    }

    [Fact]
    public void PublishRefusesAMainWithoutAVersion()
    {
        File.WriteAllText(repo.CsprojPath, NoVersion);
        repo.Merge("Drop the version");

        var e = Assert.Throws<ToolException>(() => repo.Run("publish", "0.1.0.0"));

        Assert.Equal("No <Version> in MyPlugin/MyPlugin.csproj on main.", e.Message);
    }

    [Theory]
    [InlineData(1, "MyPlugin.zip", "nothing")]
    [InlineData(0, "draft", "draft")]
    [InlineData(0, "prerelease MyPlugin.zip", "prerelease MyPlugin.zip")]
    [InlineData(0, "", "nothing")]
    public void VerifyFailsAGitHubReleaseWithoutTheZip(int code, string release, string got)
    {
        Released();
        repo.Commands.Responses["gh release view"] = (code, release);

        Assert.Equal(1, repo.Run("verify", "0.1.0.0"));

        Assert.Contains($"✗ GitHub release v0.1.0.0: expected MyPlugin.zip, got {got}\n", Out, StringComparison.Ordinal);
    }

    [Fact]
    public void VerifyFailsAZipThatDoesNotDownload()
    {
        Released();
        repo.Http.Responses.Remove(StableZip);

        Assert.Equal(1, repo.Run("verify", "0.1.0.0"));

        Assert.Contains($"✓ GitHub release v0.1.0.0\n✗ download {StableZip}: expected the zip, got nothing\n✓ repo.json AssemblyVersion\n", Out, StringComparison.Ordinal);
    }

    [Fact]
    public void VerifyFailsAZipWithAnotherVersion()
    {
        Released();
        repo.Http.Responses[StableZip] = Zip("0.0.9.0");

        Assert.Equal(1, repo.Run("verify", "0.1.0.0"));

        Assert.Contains("✗ version in the zip's manifest: expected 0.1.0.0, got 0.0.9.0\n", Out, StringComparison.Ordinal);
    }

    [Fact]
    public void VerifyFailsARepoJsonThatStillOffersATestingVersion()
    {
        Released(testingLeft: "0.1.0.0");

        Assert.Equal(1, repo.Run("verify", "0.1.0.0"));

        Assert.Contains("✗ repo.json has no TestingAssemblyVersion: expected nothing, got 0.1.0.0\n", Out, StringComparison.Ordinal);
    }

    [Fact]
    public void VerifyFailsWithoutARepoJsonAndNamesTheOpenPr()
    {
        Released();
        repo.Http.Responses.Remove(RepoJson);
        repo.Commands.Responses["gh pr list"] = (0, "https://github.com/Owner/my-plugin/pull/9");

        Assert.Equal(1, repo.Run("verify", "0.1.0.0"));

        Assert.EndsWith($"""
            ✗ repo.json AssemblyVersion: expected 0.1.0.0, got nothing
            ✗ repo.json DownloadLinkInstall: expected {StableZip}, got nothing
            ✗ repo.json DownloadLinkUpdate: expected {StableZip}, got nothing
            ✗ repo.json DownloadLinkTesting: expected {StableZip}, got nothing
            ✓ repo.json has no TestingAssemblyVersion
            ✗ repo.json IconUrl: expected {StableIcon}, got nothing
            ✗ icon downloads: expected 200, got nothing
            The repo.json PR isn't merged yet: https://github.com/Owner/my-plugin/pull/9

            """, Out, StringComparison.Ordinal);
        Assert.Equal(
            ["gh", "pr", "list", "-R", ReleaseRepo.RepoName, "--head", "release/repo-json", "--state", "open", "--json", "url", "--jq", ".[].url"],
            repo.Commands.Single("gh pr list"));
        Assert.DoesNotContain(repo.Http.Requested, r => r.Method == HttpMethod.Head);
    }

    [Fact]
    public void VerifyFailsAnIconThatDoesNotDownload()
    {
        Released();
        repo.Http.Responses.Remove(StableIcon);

        Assert.Equal(1, repo.Run("verify", "0.1.0.0"));

        Assert.EndsWith("✗ icon downloads: expected 200, got 404\n", Out, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("0.1.0")]
    [InlineData("v0.1.0.0")]
    [InlineData("0.1.0.0-test1")]
    [InlineData("0.1.0.0-rc")]
    public void VerifyRefusesAnythingButAVersionOrTestingBuild(string version)
    {
        var e = Assert.Throws<ToolException>(() => repo.Run("verify", version));

        Assert.Equal("Give a version, e.g. 0.1.2.0, or a testing build, e.g. 0.1.2.0-rc1", e.Message);
        Assert.Empty(repo.Commands.Ran);
        Assert.Empty(repo.Http.Requested);
    }

    /// <summary>Main's 0.1.0.0 released as stable: tagged on origin, on GitHub, and in repo.json.</summary>
    private void Released(bool tagged = true, string? testingLeft = null)
    {
        if (tagged)
        {
            PushTag("v0.1.0.0");
        }
        repo.Commands.Responses["gh release view"] = (0, "MyPlugin.zip");
        repo.Http.Responses[StableZip] = Zip("0.1.0.0");
        repo.Http.Responses[StableIcon] = [];
        var testing = testingLeft is null ? "" : $"\"TestingAssemblyVersion\":\"{testingLeft}\",";
        repo.Http.Responses[RepoJson] = Encoding.UTF8.GetBytes($$"""
            [{"AssemblyVersion":"0.1.0.0",{{testing}}"DownloadLinkInstall":"{{StableZip}}","DownloadLinkUpdate":"{{StableZip}}",
              "DownloadLinkTesting":"{{StableZip}}","IconUrl":"{{StableIcon}}"}]
            """);
    }

    private void PushTag(string tag)
    {
        repo.Git.Output("tag", tag);
        repo.Git.Output("push", "-q", "origin", tag);
    }

    private static byte[] Zip(string version)
    {
        using var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create))
        {
            using var writer = new StreamWriter(archive.CreateEntry("MyPlugin.json").Open());
            writer.Write($$"""{"AssemblyVersion":"{{version}}"}""");
        }
        return stream.ToArray();
    }
}
