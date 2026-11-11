namespace Buildingway.Tools.Tests;

public class ChangelogTests
{
    private const string Heading = "## 1.0.0.3 - 2026-11-05";

    private static string Fixture(string name) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", name));

    // As a CRLF checkout has it (Windows, core.autocrlf, no .gitattributes)
    private static string Crlf(string text) => text.Replace("\n", "\r\n", StringComparison.Ordinal);

    [Theory]
    [InlineData("in1.md", "rev1.md")]
    [InlineData("in2.md", "rev2.md")]
    public void ReversionMatchesTheReleaseScript(string input, string expected) =>
        Assert.Equal(Fixture(expected), Changelog.Reversion(Fixture(input), "1.0.0.2", Heading));

    [Fact]
    public void DateUnreleasedMatchesTheReleaseScript() =>
        Assert.Equal(Fixture("date1.md"), Changelog.DateUnreleased(Fixture("in1.md"), Heading));

    [Fact]
    public void ReversionAtTheEndOfTheFile()
    {
        var text = "## Unreleased\n\n- new\n\n## 1.0.0.2 - 2026-11-01\n\n- old\n";
        Assert.Equal($"## Unreleased\n\n{Heading}\n\n- old\n- new\n", Changelog.Reversion(text, "1.0.0.2", Heading));
    }

    [Fact]
    public void ACrlfChangelogIsDatedAndStaysCrlf() =>
        Assert.Equal(Crlf(Fixture("date1.md")), Changelog.DateUnreleased(Crlf(Fixture("in1.md")), Heading));

    [Fact]
    public void ACrlfChangelogIsReversionedAndStaysCrlf() =>
        Assert.Equal(Crlf(Fixture("rev1.md")), Changelog.Reversion(Crlf(Fixture("in1.md")), "1.0.0.2", Heading));

    [Fact]
    public void ACrlfChangelogsUnreleasedSectionIsFound()
    {
        var text = Crlf("## Unreleased\n\n- entry\n\n## 1.0.0.2 - 2026-11-01\n");
        Assert.True(Changelog.HasUnreleasedEntries(text));
        Assert.True(Changelog.NextSectionIs(text, "1.0.0.2"));
    }

    [Theory]
    [InlineData("## Unreleased\n\n- entry\n\n## 1.0.0.0 - 2026-01-01\n", true)]
    [InlineData("## Unreleased\n\n### Added\n\n## 1.0.0.0 - 2026-01-01\n", true)]
    [InlineData("## Unreleased\n\n\n## 1.0.0.0 - 2026-01-01\n\n- old\n", false)]
    [InlineData("# Changelog\n\n- not under a version\n\n## Unreleased\n", false)]
    [InlineData("## 1.0.0.0 - 2026-01-01\n\n- old\n", false)]
    public void HasUnreleasedEntries(string text, bool expected) =>
        Assert.Equal(expected, Changelog.HasUnreleasedEntries(text));

    [Theory]
    [InlineData("## Unreleased\n\n- x\n\n## 1.0.0.2 - 2026-11-01\n", "1.0.0.2", true)]
    [InlineData("## Unreleased\n\n## 1.0.0.1 - 2026-10-01\n\n## 1.0.0.2 - 2026-11-01\n", "1.0.0.2", false)]
    [InlineData("## Unreleased\n\n## 1.0.0.20 - 2026-11-01\n", "1.0.0.2", false)]
    [InlineData("## 1.0.0.2 - 2026-11-01\n", "1.0.0.2", false)]
    public void NextSectionIs(string text, string version, bool expected) =>
        Assert.Equal(expected, Changelog.NextSectionIs(text, version));
}
