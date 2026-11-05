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
        Assert.Equal((number, title), Release.ParseMergeSubject(subject, body));

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

    [Fact]
    public void JsonStringIsEmptyForNonObjects() =>
        Assert.Equal("", Release.JsonString(JsonDocument.Parse("[1]").RootElement, "x"));
}
