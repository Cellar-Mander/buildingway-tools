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

    // Keep a Changelog's order whatever order the sections came in; others after, as they came
    [Fact]
    public void ReversionPutsTheKindsInKeepAChangelogsOrder()
    {
        // revert-check: not revert-sensitive (kills a surviving mutant: pins existing behaviour)
        var text = "## Unreleased\n\n### Notes\n\n- n\n\n### Security\n\n- s\n\n### Fixed\n\n- f\n\n### Removed\n\n- r\n\n" +
            "## 1.0.0.2 - 2026-11-01\n\n### Deprecated\n\n- d\n\n### Changed\n\n- c\n\n### Added\n\n- a\n";
        Assert.Equal(
            $"## Unreleased\n\n{Heading}\n\n### Added\n\n- a\n\n### Changed\n\n- c\n\n### Deprecated\n\n- d\n\n" +
            "### Removed\n\n- r\n\n### Fixed\n\n- f\n\n### Security\n\n- s\n\n### Notes\n\n- n\n",
            Changelog.Reversion(text, "1.0.0.2", Heading));
    }

    // Only the section right after Unreleased moves, even if a later heading has the same version
    [Fact]
    public void ReversionLeavesALaterSectionOfTheSameVersionAlone()
    {
        // revert-check: not revert-sensitive (kills a surviving mutant: pins existing behaviour)
        var text = "## Unreleased\n\n- new\n\n## 1.0.0.2 - 2026-11-01\n\n- old\n\n## 1.0.0.1 - 2026-10-01\n\n## 1.0.0.2 - 2026-09-01\n\n- older\n";
        Assert.Equal(
            $"## Unreleased\n\n{Heading}\n\n- old\n- new\n\n## 1.0.0.1 - 2026-10-01\n\n## 1.0.0.2 - 2026-09-01\n\n- older\n",
            Changelog.Reversion(text, "1.0.0.2", Heading));
    }

    [Fact]
    public void AChangelogWithoutAFinalNewlineKeepsItsLastCharacterAndGainsNoNewline()
    {
        // revert-check: not revert-sensitive (kills a surviving mutant: pins existing behaviour)
        Assert.Equal(
            $"## Unreleased\n\n{Heading}\n\n- old\n- new",
            Changelog.Reversion("## Unreleased\n\n- new\n\n## 1.0.0.2 - 2026-11-01\n\n- old", "1.0.0.2", Heading));
        Assert.Equal($"## Unreleased\n\n{Heading}\n\n- new", Changelog.DateUnreleased("## Unreleased\n\n- new", Heading));
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
