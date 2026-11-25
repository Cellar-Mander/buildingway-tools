namespace Buildingway.Tools.Tests;

/// <summary>release prepare and the command dispatch, against a temp checkout and origin with fake dotnet and gh.</summary>
public sealed class ReleasePrepareTests : IDisposable
{
    private const string Branch = "release-prep/v0.2.0.0";
    private readonly ReleaseRepo repo = new();

    public void Dispose() => repo.Dispose();

    private string PrBody() => repo.Commands.Single("gh pr create")[10];

    [Theory]
    [InlineData]
    [InlineData("prepare")]
    [InlineData("prepare", "0.2.0.0", "extra")]
    [InlineData("publish", "0.2.0.0", "--stable")]
    [InlineData("verify")]
    [InlineData("tag", "0.2.0.0")]
    public void UnknownArgumentsPrintTheUsage(params string[] args)
    {
        Assert.Equal(2, repo.Run(args));

        Assert.StartsWith("Usage: dotnet buildingway release prepare <version>", repo.Error.ToString(), StringComparison.Ordinal);
        Assert.Contains("verify: check that the published release", repo.Error.ToString(), StringComparison.Ordinal);
        Assert.Empty(repo.Commands.Ran);
        Assert.Equal("", repo.Output.ToString());
    }

    [Fact]
    public void PrepareMakesTheFirstReleaseBranchAndPr()
    {
        Assert.Equal(0, repo.Run("prepare", "0.2.0.0"));

        Assert.Equal(Branch, repo.Git.Output("branch", "--show-current"));
        Assert.Contains("<Version>0.2.0.0</Version>", repo.OriginShow(Branch, "MyPlugin/MyPlugin.csproj"), StringComparison.Ordinal);
        Assert.Equal("# Changelog\n\n## Unreleased\n\n## 0.2.0.0 - 2026-11-25\n\n### Added\n\n- A window", repo.OriginShow(Branch, "CHANGELOG.md"));
        Assert.Equal("Prepare 0.2.0.0", repo.Git.Output("log", "-1", "--format=%s", $"origin/{Branch}"));
        Assert.Equal($"origin/{Branch}", repo.Git.Output("rev-parse", "--abbrev-ref", $"{Branch}@{{upstream}}"));
        Assert.Equal("", repo.Git.Output("status", "--porcelain", "--untracked-files=no"));
        Assert.False(repo.OriginHas("refs/tags/v0.2.0.0"));

        var csproj = repo.CsprojPath;
        Assert.Equal(
            [
                ["dotnet", "build", csproj, "-c", "Release", "--nologo", "-v", "q"],
                ["dotnet", "test", csproj, "-c", "Release", "--nologo", "-v", "q"],
                ["gh", "pr", "create", "--base", "main", "--head", Branch, "--title", "Prepare 0.2.0.0", "--body", PrBody()],
            ],
            repo.Commands.Ran);
        Assert.Equal("""
            Sets `<Version>` to 0.2.0.0 and dates the CHANGELOG section. Made by `dotnet buildingway release prepare`, which built the plugin and ran the tests.

            Before merging, check that every user-facing change below has a CHANGELOG entry. ⚠ marks a change to plugin code that touched no CHANGELOG entry.

            Changes since the first commit:
            - Add the plugin
            - Initial ⚠ plugin code changed, no CHANGELOG entry

            After merging, publish it with `dotnet buildingway release publish 0.2.0.0 --testing` for a testing build, or `dotnet buildingway release publish 0.2.0.0` for the stable release.
            """.ReplaceLineEndings("\n"), PrBody().ReplaceLineEndings("\n"));
        Assert.Equal("""
            Changes since the first commit:
            - Add the plugin
            - Initial ⚠ plugin code changed, no CHANGELOG entry

            """.ReplaceLineEndings("\n"), repo.Output.ToString().ReplaceLineEndings("\n"));
        Assert.Equal("", repo.Error.ToString());
    }

    [Fact]
    public void PrepareListsOnlyTheChangesSinceTheLastStableRelease()
    {
        repo.Git.Output("tag", "v0.1.0.0");
        repo.Git.Output("push", "-q", "origin", "v0.1.0.0");
        File.AppendAllText(repo.ChangelogPath, "- A button\n");
        repo.Merge("Add a button (#7)");
        repo.Git.Output("tag", "v0.2.0.0-rc1");

        Assert.Equal(0, repo.Run("prepare", "0.2.0.0"));

        Assert.Contains("Changes since v0.1.0.0:\n- #7 Add a button\n\n", PrBody().ReplaceLineEndings("\n"), StringComparison.Ordinal);
    }

    [Fact]
    public void PrepareStartsFromOriginMainNotTheLocalBranch()
    {
        File.AppendAllText(repo.ChangelogPath, "- A button\n");
        repo.Merge("Add a button");
        repo.Git.Output("reset", "-q", "--hard", "HEAD~1");
        repo.Git.Output("update-ref", "refs/remotes/origin/main", "HEAD");

        Assert.Equal(0, repo.Run("prepare", "0.2.0.0"));

        Assert.EndsWith("- A window\n- A button", repo.OriginShow(Branch, "CHANGELOG.md"), StringComparison.Ordinal);
    }

    [Fact]
    public void PrepareMovesASectionNeverReleasedAsStableToTheNewVersion()
    {
        File.WriteAllText(repo.ChangelogPath, "# Changelog\n\n## Unreleased\n\n## 0.1.0.0 - 2026-01-01\n\n### Added\n\n- A window\n");
        repo.Merge("Date 0.1.0.0");
        repo.Git.Output("tag", "v0.1.0.0-rc1");
        repo.Git.Output("push", "-q", "origin", "v0.1.0.0-rc1");

        Assert.Equal(0, repo.Run("prepare", "0.2.0.0"));

        Assert.Equal("# Changelog\n\n## Unreleased\n\n## 0.2.0.0 - 2026-11-25\n\n### Added\n\n- A window", repo.OriginShow(Branch, "CHANGELOG.md"));
        Assert.StartsWith("Sets `<Version>` to 0.2.0.0 and moves the CHANGELOG section of 0.1.0.0, never released as stable, to 0.2.0.0 with the Unreleased entries. Made by", PrBody(), StringComparison.Ordinal);
    }

    [Fact]
    public void PrepareDatesUnreleasedWhenMainsVersionWasReleased()
    {
        File.WriteAllText(repo.ChangelogPath, "# Changelog\n\n## Unreleased\n\n- A button\n\n## 0.1.0.0 - 2026-01-01\n\n- A window\n");
        repo.Merge("Add a button");
        repo.Git.Output("tag", "v0.1.0.0");
        repo.Git.Output("push", "-q", "origin", "v0.1.0.0");

        Assert.Equal(0, repo.Run("prepare", "0.2.0.0"));

        Assert.Equal("# Changelog\n\n## Unreleased\n\n## 0.2.0.0 - 2026-11-25\n\n- A button\n\n## 0.1.0.0 - 2026-01-01\n\n- A window", repo.OriginShow(Branch, "CHANGELOG.md"));
    }

    [Fact]
    public void PrepareRefusesWhenMainsReleasedVersionHasNoUnreleasedEntries()
    {
        File.WriteAllText(repo.ChangelogPath, "# Changelog\n\n## Unreleased\n\n## 0.1.0.0 - 2026-01-01\n\n- A window\n");
        repo.Merge("Date 0.1.0.0");
        repo.Git.Output("tag", "v0.1.0.0");
        repo.Git.Output("push", "-q", "origin", "v0.1.0.0");

        AssertRefused("CHANGELOG.md has no Unreleased entries to release.");
    }

    [Theory]
    [InlineData("0.2.0")]
    [InlineData("v0.2.0.0")]
    [InlineData("0.2.0.0-rc1")]
    public void PrepareRefusesAVersionThatIsNotFourNumbers(string version)
    {
        var e = Assert.Throws<ToolException>(() => repo.Run("prepare", version));

        Assert.Equal("Version must be four numbers, e.g. 0.1.2.0", e.Message);
        Assert.Equal("main", repo.Git.Output("branch", "--show-current"));
    }

    [Fact]
    public void PrepareRefusesADirtyCheckout()
    {
        File.WriteAllText(Path.Combine(repo.Top, "notes.txt"), "wip\n");

        AssertRefused("Commit or set aside your changes first.");
    }

    [Theory]
    [InlineData("0.1.0.0")]
    [InlineData("0.0.9.0")]
    public void PrepareRefusesAVersionNotAboveMain(string version)
    {
        var e = Assert.Throws<ToolException>(() => repo.Run("prepare", version));

        Assert.Equal($"{version} isn't above the version on main, 0.1.0.0.", e.Message);
        Assert.Empty(repo.Commands.Ran);
    }

    [Fact]
    public void PrepareRefusesATagThatExistsOnOrigin()
    {
        repo.Git.Output("tag", "v0.2.0.0");
        repo.Git.Output("push", "-q", "origin", "v0.2.0.0");
        repo.Git.Output("tag", "-d", "v0.2.0.0");

        AssertRefused("Tag v0.2.0.0 already exists.");
    }

    [Fact]
    public void PrepareRefusesAChangelogWithNoUnreleasedEntries()
    {
        File.WriteAllText(repo.ChangelogPath, "# Changelog\n\n## Unreleased\n\n");
        repo.Merge("Empty the changelog");

        AssertRefused("CHANGELOG.md has no Unreleased entries to release.");
    }

    [Theory]
    [InlineData("0.1.0.0", "The built manifest says 0.1.0.0, not 0.2.0.0.")]
    [InlineData(null, "The built manifest says , not 0.2.0.0.")]
    public void PrepareStopsWhenTheBuiltManifestHasAnotherVersion(string? built, string message)
    {
        repo.Commands.BuiltVersion = () => built;

        var e = Assert.Throws<ToolException>(() => repo.Run("prepare", "0.2.0.0"));

        Assert.Equal(message, e.Message);
        AssertNothingPushed();
    }

    [Fact]
    public void PrepareStopsWhenTheTestsFail()
    {
        repo.Commands.Responses["dotnet test"] = (1, "");

        var e = Assert.Throws<ToolException>(() => repo.Run("prepare", "0.2.0.0"));

        Assert.Equal(1, e.ExitCode);
        AssertNothingPushed();
    }

    [Fact]
    public void PrepareBuildsTheCheckoutsOneSolution()
    {
        var solution = Path.Combine(repo.Top, "MyPlugin.slnx");
        File.WriteAllText(solution, "<Solution />\n");
        repo.Merge("Add a solution");

        repo.Run("prepare", "0.2.0.0");

        Assert.Equal(solution, repo.Commands.Single("dotnet build")[2]);
        Assert.Equal(solution, repo.Commands.Single("dotnet test")[2]);
    }

    [Fact]
    public void PrepareBuildsTheCsprojWhenThereIsMoreThanOneSolution()
    {
        File.WriteAllText(Path.Combine(repo.Top, "MyPlugin.slnx"), "<Solution />\n");
        File.WriteAllText(Path.Combine(repo.Top, "Other.sln"), "\n");
        repo.Merge("Add solutions");

        repo.Run("prepare", "0.2.0.0");

        Assert.Equal(repo.CsprojPath, repo.Commands.Single("dotnet build")[2]);
    }

    [Fact]
    public void PrepareNotesALocalDalamudPastThePin()
    {
        PinDalamud("DALAMUD_VERSION=14.0.0.0");
        File.WriteAllText(Path.Combine(repo.DalamudDir, "Dalamud.deps.json"), """{"libraries":{"Dalamud/14.0.1.0":{}}}""");

        repo.Run("prepare", "0.2.0.0");

        const string note = "⚠ Local Dalamud is 14.0.1.0 but CI builds against 14.0.0.0. If you've tested with 14.0.1.0, move the pin in .github/dalamud.env.";
        Assert.Equal(note + Environment.NewLine, repo.Error.ToString());
        Assert.Contains($"- Initial ⚠ plugin code changed, no CHANGELOG entry\n\n{note}\n\nAfter merging", PrBody().ReplaceLineEndings("\n"), StringComparison.Ordinal);
    }

    [Fact]
    public void PrepareNotesALocalDalamudItCannotRead()
    {
        PinDalamud("DALAMUD_VERSION=14.0.0.0");

        repo.Run("prepare", "0.2.0.0");

        Assert.Equal(
            $"⚠ Couldn't read the local Dalamud version from {Path.Combine(repo.DalamudDir, "Dalamud.deps.json")}; CI builds against 14.0.0.0." + Environment.NewLine,
            repo.Error.ToString());
    }

    [Theory]
    [InlineData("OTHER=1\nDALAMUD_VERSION=14.0.0.0")]
    [InlineData("DALAMUD_VERSION=")]
    [InlineData("OTHER=14.0.1.0")]
    public void PrepareHasNoDalamudNoteWhenThePinMatchesOrIsUnset(string pin)
    {
        PinDalamud(pin);
        File.WriteAllText(Path.Combine(repo.DalamudDir, "Dalamud.deps.json"), """{"libraries":{"Dalamud/14.0.0.0":{}}}""");

        repo.Run("prepare", "0.2.0.0");

        Assert.Equal("", repo.Error.ToString());
        Assert.DoesNotContain("⚠ Local", PrBody(), StringComparison.Ordinal);
    }

    private void PinDalamud(string content)
    {
        Directory.CreateDirectory(Path.Combine(repo.Top, ".github"));
        File.WriteAllText(Path.Combine(repo.Top, ".github", "dalamud.env"), content + "\n");
        repo.Merge("Pin Dalamud");
    }

    private void AssertRefused(string message)
    {
        var e = Assert.Throws<ToolException>(() => repo.Run("prepare", "0.2.0.0"));

        Assert.Equal(message, e.Message);
        Assert.Equal("main", repo.Git.Output("branch", "--show-current"));
        Assert.Empty(repo.Commands.Ran);
    }

    private void AssertNothingPushed()
    {
        Assert.False(repo.OriginHas($"refs/heads/{Branch}"));
        Assert.DoesNotContain(repo.Commands.Ran, r => r[0] == "gh");
    }
}
