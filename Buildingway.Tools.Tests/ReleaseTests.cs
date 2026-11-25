using System.IO.Compression;
using System.Text;
using System.Text.Json;

namespace Buildingway.Tools.Tests;

public class ReleaseTests
{
    [Theory]
    [InlineData("1.0.0.10", "1.0.0.9", 1)]
    [InlineData("1.0.0.9", "1.0.0.10", -1)]
    [InlineData("2.0.0.0", "1.9.9.9", 1)]
    [InlineData("1.0.0.1", "1.0.0.1", 0)]
    public void ComparesVersionsNumerically(string a, string b, int sign) =>
        Assert.Equal(sign, Math.Sign(Release.CompareVersions(a, b)));

    [Theory]
    [InlineData("1.0.0.1", true)]
    [InlineData("1.0.0", false)]
    [InlineData("1.0.0.1.2", false)]
    [InlineData("v1.0.0.1", false)]
    [InlineData("1.0.0.1-rc1", false)]
    public void IsVersion(string version, bool expected) => Assert.Equal(expected, Release.IsVersion(version));

    [Fact]
    public void HighestRcComparesRcNumbersNumerically()
    {
        var lsRemote = "aaa\trefs/tags/v1.0.0.2-rc9\nbbb\trefs/tags/v1.0.0.2-rc10\nccc\trefs/tags/v1.0.0.2-rc2\n";
        Assert.Equal("v1.0.0.2-rc10", Release.HighestRc(lsRemote, "1.0.0.2"));
    }

    [Fact]
    public void HighestRcIgnoresOtherVersionsAndTags()
    {
        // ls-remote's glob also matches v1.0.0.2-rc1-test, and another version's tags aren't this one's
        var lsRemote = "aaa\trefs/tags/v1.0.0.2-rc1-test\nbbb\trefs/tags/v1.0.0.20-rc1\n";
        Assert.Null(Release.HighestRc(lsRemote, "1.0.0.2"));
        Assert.Null(Release.HighestRc("", "1.0.0.2"));
    }

    [Theory]
    [InlineData("Merge pull request #113 from Cellar-Mander/claude/shared-release", "Use the shared release\nmore", "113", "Use the shared release")]
    [InlineData("Fix the thing (#42)", "", "42", "Fix the thing")]
    [InlineData("Direct commit", "", null, "Direct commit")]
    public void ParsesMergeSubjects(string subject, string body, string? number, string title) =>
        Assert.Equal((number, title), Release.ParseMergeSubject(subject, () => body));

    [Fact]
    public void ReadsRepoJsonFields()
    {
        var entry = Release.RepoEntry(Encoding.UTF8.GetBytes("""[{"AssemblyVersion":"1.0.0.1","DalamudApiLevel":15}]"""));
        Assert.NotNull(entry);
        Assert.Equal("1.0.0.1", Release.JsonString(entry.Value, "AssemblyVersion"));
        Assert.Equal("15", Release.JsonString(entry.Value, "DalamudApiLevel"));
        Assert.Equal("", Release.JsonString(entry.Value, "TestingAssemblyVersion"));
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("{}")]
    [InlineData("<html>")]
    public void RepoEntryIsNullForAnythingButAFilledArray(string json) =>
        Assert.Null(Release.RepoEntry(Encoding.UTF8.GetBytes(json)));

    [Fact]
    public void ReadsTheManifestVersionFromTheZip()
    {
        Assert.Equal("1.2.3.4", Release.ManifestVersion(Zip("MyPlugin.json", """{"AssemblyVersion": "1.2.3.4"}"""), "MyPlugin.json"));
        Assert.Equal("", Release.ManifestVersion(Zip("Other.json", """{"AssemblyVersion": "1.2.3.4"}"""), "MyPlugin.json"));
        Assert.Equal("", Release.ManifestVersion("not a zip"u8.ToArray(), "MyPlugin.json"));
    }

    [Fact]
    public void ChecksReportFailuresWithNothingForEmptyValues()
    {
        var output = new StringWriter();
        var checks = new Checks(output);
        checks.Check("same", "a", "a");
        Assert.False(checks.Failed);
        checks.Check("missing", "", "x");
        Assert.True(checks.Failed);
        Assert.Equal("✓ same\n✗ missing: expected x, got nothing\n", output.ToString().ReplaceLineEndings("\n"));
    }

    private static byte[] Zip(string name, string content)
    {
        var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            using var writer = new StreamWriter(archive.CreateEntry(name).Open());
            writer.Write(content);
        }
        return stream.ToArray();
    }

    [Theory]
    [InlineData("""[{"IsTestingExclusive":true}]""", true)]
    [InlineData("""[{"IsTestingExclusive":false}]""", false)]
    [InlineData("""[{"IsTestingExclusive":"true"}]""", false)]
    [InlineData("""[{"AssemblyVersion":"1.0.0.1"}]""", false)]
    public void IsTestingExclusiveReadsTheBoolean(string json, bool expected) =>
        Assert.Equal(expected, Release.IsTestingExclusive(Release.RepoEntry(Encoding.UTF8.GetBytes(json))));

    [Fact]
    public void IsTestingExclusiveIsFalseWithoutAnEntry() => Assert.False(Release.IsTestingExclusive(null));

    private const string RcUrl = "https://github.com/o/r/releases/download/v1.0.0.1-rc1/P.zip";

    private static (bool Failed, string Output) CheckTestingEntry(string fields)
    {
        var json = $$"""[{"TestingAssemblyVersion":"1.0.0.1","DownloadLinkTesting":"{{RcUrl}}","TestingDalamudApiLevel":15,"DalamudApiLevel":15{{fields}}}]""";
        var output = new StringWriter();
        var checks = new Checks(output);
        Release.CheckTestingEntry(checks, Release.RepoEntry(Encoding.UTF8.GetBytes(json)), "1.0.0.1", RcUrl, "v1.0.0.1-rc1");
        return (checks.Failed, output.ToString().ReplaceLineEndings("\n"));
    }

    [Theory]
    [InlineData("", false)]
    [InlineData(""","AssemblyVersion":"1.0.0.0" """, false)]
    [InlineData(""","AssemblyVersion":"1.0.0.0","DownloadLinkInstall":"https://x/v1.0.0.0/P.zip" """, false)]
    [InlineData(""","AssemblyVersion":"1.0.0.2" """, true)]
    [InlineData(""","AssemblyVersion":"1.0.0.01" """, true)]
    [InlineData(""","AssemblyVersion":"1.0.0.1","IsTestingExclusive":true""", false)]
    [InlineData(""","AssemblyVersion":"1.0.0.1","IsTestingExclusive":false""", true)]
    [InlineData(""","DownloadLinkInstall":"https://github.com/o/r/releases/download/v1.0.0.1-rc1/P.zip" """, true)]
    [InlineData(""","DownloadLinkUpdate":"https://github.com/o/r/releases/download/v1.0.0.1-rc1/P.zip" """, true)]
    [InlineData(""","DalamudApiLevel":14""", true)]
    public void TestingEntryNeedsTheStableVersionBelowUnlessTestingOnly(string fields, bool failed) =>
        Assert.Equal(failed, CheckTestingEntry(fields).Failed);

    [Theory]
    [InlineData(""","TestingAssemblyVersion":"1.0.0.0" """)]
    [InlineData(""","DownloadLinkTesting":"https://x/P.zip" """)]
    public void TestingEntryNeedsItsTestingFieldsOnThisBuild(string fields)
    {
        // A JSON object's last duplicate key wins, so these replace the defaults
        Assert.True(CheckTestingEntry(fields).Failed);
    }

    [Fact]
    public void TestingEntryNamesEachCheck() =>
        Assert.Equal(
            """
            ✓ repo.json TestingAssemblyVersion
            ✓ repo.json DownloadLinkTesting
            ✓ repo.json TestingDalamudApiLevel matches DalamudApiLevel
            ✓ repo.json AssemblyVersion (1.0.0.0) is below 1.0.0.1
            ✓ repo.json DownloadLinkInstall doesn't point at v1.0.0.1-rc1
            ✓ repo.json DownloadLinkUpdate doesn't point at v1.0.0.1-rc1

            """.ReplaceLineEndings("\n"),
            CheckTestingEntry(""","AssemblyVersion":"1.0.0.0" """).Output);

    [Fact]
    public void TestingEntryShowsALinkThatPointsAtTheBuild() =>
        Assert.Contains(
            "✗ repo.json DownloadLinkInstall doesn't point at v1.0.0.1-rc1: expected yes, got nothing\n",
            CheckTestingEntry($$""","DownloadLinkInstall":"{{RcUrl}}" """).Output);

    [Fact]
    public void TestingEntryFieldsAreMissingWithoutAnEntry()
    {
        var output = new StringWriter();
        var checks = new Checks(output);
        Release.CheckTestingEntry(checks, null, "1.0.0.1", RcUrl, "v1.0.0.1-rc1");
        Assert.Contains("✗ repo.json TestingAssemblyVersion: expected 1.0.0.1, got nothing\n", output.ToString().ReplaceLineEndings("\n"));
    }

    [Fact]
    public void TestingEntryNamesTheStableVersionThatIsNotBelow() =>
        Assert.Contains(
            "✗ repo.json AssemblyVersion (1.0.0.1) is below 1.0.0.1: expected yes, got nothing\n",
            CheckTestingEntry(""","AssemblyVersion":"1.0.0.1" """).Output);

    [Fact]
    public void JsonStringIsEmptyForNonObjects() =>
        Assert.Equal("", Release.JsonString(JsonDocument.Parse("[1]").RootElement, "x"));

    [Theory]
    [InlineData("true")]
    [InlineData("null")]
    [InlineData("""{"a":1}""")]
    public void JsonStringIsEmptyForValuesThatAreNotStringsOrNumbers(string value) =>
        Assert.Equal("", Release.JsonString(JsonDocument.Parse($$"""{"x":{{value}}}""").RootElement, "x"));

    [Fact]
    public void LastReleaseIsNullBeforeTheFirstStableTag()
    {
        using var repo = new PluginRepo("MyPlugin");
        repo.Git.Output("tag", "v0.1.0.0-rc1");
        repo.Git.Output("update-ref", "refs/remotes/origin/main", "HEAD");

        Assert.Null(new Release(repo.Git, repo.Plugin, ReleaseRepo.Offline).LastRelease());
    }

    [Fact]
    public void LastReleaseIsTheLatestStableTagOnMain()
    {
        using var repo = new PluginRepo("MyPlugin");
        repo.Git.Output("tag", "v0.1.0.0");
        repo.Commit("Second");
        repo.Git.Output("tag", "v0.2.0.0-rc1");
        repo.Git.Output("update-ref", "refs/remotes/origin/main", "HEAD");

        Assert.Equal("v0.1.0.0", new Release(repo.Git, repo.Plugin, ReleaseRepo.Offline).LastRelease());
    }

    [Fact]
    public void ChangesSinceNoReleaseListsEveryChangeFromTheFirstCommit()
    {
        using var repo = new PluginRepo("MyPlugin");
        File.WriteAllText(Path.Combine(repo.Top, "CHANGELOG.md"), "# Changelog\n");
        repo.Commit("Add a CHANGELOG");
        File.WriteAllText(Path.Combine(repo.Top, "MyPlugin", "Plugin.cs"), "class Plugin;\n");
        repo.Commit("Add the plugin");
        repo.Git.Output("update-ref", "refs/remotes/origin/main", "HEAD");

        Assert.Equal("""
            - Add the plugin ⚠ plugin code changed, no CHANGELOG entry
            - Add a CHANGELOG
            - Initial ⚠ plugin code changed, no CHANGELOG entry
            """.ReplaceLineEndings("\n"), new Release(repo.Git, repo.Plugin, ReleaseRepo.Offline).ChangesSince(null));
    }

    [Fact]
    public void ChangesSinceAReleaseListsOnlyLaterChanges()
    {
        using var repo = new PluginRepo("MyPlugin");
        repo.Git.Output("tag", "v0.1.0.0");
        File.WriteAllText(Path.Combine(repo.Top, "MyPlugin", "Plugin.cs"), "class Plugin;\n");
        File.WriteAllText(Path.Combine(repo.Top, "CHANGELOG.md"), "# Changelog\n");
        repo.Commit("Add the plugin");
        repo.Git.Output("update-ref", "refs/remotes/origin/main", "HEAD");

        Assert.Equal("- Add the plugin", new Release(repo.Git, repo.Plugin, ReleaseRepo.Offline).ChangesSince("v0.1.0.0"));
    }

    [Fact]
    public void ChangesSinceDiffsAMergeAgainstMain()
    {
        using var repo = new PluginRepo("MyPlugin");
        repo.Git.Output("tag", "v0.1.0.0");
        repo.Git.Output("switch", "-q", "-c", "feature");
        File.WriteAllText(Path.Combine(repo.Top, "MyPlugin", "Plugin.cs"), "class Plugin;\n");
        repo.Commit("Add the plugin");
        repo.Git.Output("switch", "-q", "main");
        repo.Git.Output("-c", "user.name=Test", "-c", "user.email=test@example.com", "-c", "commit.gpgsign=false",
            "merge", "-q", "--no-ff", "-m", "Merge pull request #5 from owner/feature", "-m", "Add the plugin window", "feature");
        repo.Git.Output("update-ref", "refs/remotes/origin/main", "HEAD");

        Assert.Equal("- #5 Add the plugin window ⚠ plugin code changed, no CHANGELOG entry", new Release(repo.Git, repo.Plugin, ReleaseRepo.Offline).ChangesSince("v0.1.0.0"));
    }

    [Theory]
    [InlineData("v0.1.0.0", "Changes since v0.1.0.0:")]
    [InlineData(null, "Changes since the first commit:")]
    public void ChangesHeadingNamesTheLastReleaseOrTheFirstCommit(string? last, string expected) =>
        Assert.Equal(expected, Release.ChangesHeading(last));
}
