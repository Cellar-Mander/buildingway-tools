using System.Text.Json.Nodes;

namespace Buildingway.Tools.Tests;

/// <summary>GitHub with canned GET responses, recording what would be sent.</summary>
internal sealed class FakeGitHub(Dictionary<string, JsonNode> responses) : IGitHub
{
    public List<(string Method, string Path, JsonNode Body)> Sent { get; } = [];

    public JsonNode Get(string path) => responses[path].DeepClone();

    public JsonArray GetAll(string path) => responses[path].DeepClone().AsArray();

    public void Send(string method, string path, JsonNode body) => Sent.Add((method, path, body));
}

public sealed class SetupRepoTests : IDisposable
{
    private const string Repo = "Cellar-Mander/say-my-name";
    private readonly string root = Directory.CreateTempSubdirectory("buildingway-setup").FullName;
    private readonly StringWriter output = new();

    public void Dispose() => Directory.Delete(root, recursive: true);

    private static JsonNode Fixture(string name) =>
        JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "setup-repo", name)))!;

    /// <summary>say-my-name's live settings on 2026-11-06, which the standard was taken from.</summary>
    private static Dictionary<string, JsonNode> SayMyName() => new()
    {
        [$"repos/{Repo}/rulesets?per_page=100"] = Fixture("rulesets.json"),
        [$"repos/{Repo}/rulesets/26000382"] = Fixture("ruleset.json"),
        [$"repos/{Repo}"] = Fixture("repo.json"),
        [$"repos/{Repo}/actions/permissions/workflow"] = Fixture("workflow.json"),
        // gh --slurp gives pages; GetAll flattens them
        [$"repos/{Repo}/labels?per_page=100"] = Fixture("labels.json")[0]!,
    };

    /// <summary>A new repo: no ruleset, GitHub's default labels and settings.</summary>
    private static Dictionary<string, JsonNode> NewRepo() => new()
    {
        [$"repos/{Repo}/rulesets?per_page=100"] = new JsonArray(),
        [$"repos/{Repo}"] = JsonNode.Parse("""
            {"allow_merge_commit":true,"allow_squash_merge":true,"allow_rebase_merge":true,"allow_auto_merge":false,
             "allow_update_branch":false,"delete_branch_on_merge":false,"merge_commit_title":"MERGE_MESSAGE",
             "merge_commit_message":"PR_TITLE","squash_merge_commit_title":"COMMIT_OR_PR_TITLE",
             "squash_merge_commit_message":"COMMIT_MESSAGES"}
            """)!,
        [$"repos/{Repo}/actions/permissions/workflow"] = JsonNode.Parse("""{"default_workflow_permissions":"read","can_approve_pull_request_reviews":false}""")!,
        [$"repos/{Repo}/labels?per_page=100"] = JsonNode.Parse("""[{"name":"bug","color":"d73a4a","description":"Something isn't working"}]""")!,
    };

    private SetupRepo Setup(FakeGitHub github, bool check = false) => new(github, Repo, "SayMyName", output, check);

    [Fact]
    public void SayMyNameAlreadyMatchesTheStandard()
    {
        // revert-check: not revert-sensitive (kills a surviving mutant: pins existing behaviour)
        var github = new FakeGitHub(SayMyName());
        var setup = Setup(github, check: true);

        setup.ApplyGitHub();

        Assert.Empty(github.Sent);
        Assert.Equal(0, setup.Finish());
        Assert.Equal(
            """
            ✓ ruleset "Protect Main"
            ✓ merge settings
            ✓ Actions workflow permissions
            ✓ label no-changelog
            ✓ label mutation

            """, output.ToString().ReplaceLineEndings("\n"));
    }

    [Fact]
    public void TheStandardRulesetSetsEverySayMyNameRuleSetting()
    {
        // The other direction to SayMyNameAlreadyMatchesTheStandard, so a setting can't drop out
        Assert.True(Json.Matches(Fixture("ruleset.json")["rules"], SetupRepo.DesiredRuleset()["rules"]));
    }

    [Fact]
    public void TheStandardRulesetProtectsMainLikeSayMyName() =>
        // revert-check: not revert-sensitive (kills a surviving mutant: pins existing behaviour)
        Assert.True(Json.Matches(Fixture("ruleset.json")["conditions"], SetupRepo.DesiredRuleset()["conditions"]));

    [Fact]
    public void ANewRepoGetsTheRulesetLabelsAndActionsPermission()
    {
        var github = new FakeGitHub(NewRepo());

        Setup(github).ApplyGitHub();

        Assert.Equal(
            [("POST", $"repos/{Repo}/rulesets"), ("PUT", $"repos/{Repo}/actions/permissions/workflow"),
             ("POST", $"repos/{Repo}/labels"), ("POST", $"repos/{Repo}/labels")],
            github.Sent.Select(s => (s.Method, s.Path)));
        var ruleset = github.Sent[0].Body;
        Assert.True(Json.Matches(ruleset, Fixture("ruleset.json")), "the created ruleset is say-my-name's");
        Assert.Equal(["no-changelog", "mutation"], github.Sent.Skip(2).Select(s => (string)s.Body["name"]!));
        Assert.Contains("Manage Actions access", output.ToString());
    }

    [Fact]
    public void ANewRepoReportsEachChange()
    {
        // revert-check: not revert-sensitive (kills a surviving mutant: pins existing behaviour)
        var setup = Setup(new FakeGitHub(NewRepo()));

        setup.ApplyGitHub();

        Assert.Equal(0, setup.Finish());
        Assert.Equal(
            """
            → ruleset "Protect Main": create
            ✓ merge settings
            → Actions workflow permissions: update
            → label no-changelog: create
            → label mutation: create
              Not checked: if the Buildingway.Tools package is private, give this repo Read under
              its "Manage Actions access" settings, or CI can't restore the tool.

            """, output.ToString().ReplaceLineEndings("\n"));
    }

    [Fact]
    public void CheckModeReportsWhatWouldBeUpdated()
    {
        // revert-check: not revert-sensitive (kills a surviving mutant: pins existing behaviour)
        var responses = SayMyName();
        responses[$"repos/{Repo}/rulesets/26000382"]["enforcement"] = "disabled";
        responses[$"repos/{Repo}"]["delete_branch_on_merge"] = true;
        responses[$"repos/{Repo}/labels?per_page=100"].AsArray().Single(l => (string?)l!["name"] == "mutation")!["color"] = "000000";
        var setup = Setup(new FakeGitHub(responses), check: true);

        setup.ApplyGitHub();

        Assert.Equal(1, setup.Finish());
        Assert.Equal(
            """
            ✗ ruleset "Protect Main": would update
            ✗ merge settings: would update
            ✓ Actions workflow permissions
            ✓ label no-changelog
            ✗ label mutation: would update
            Run without --check to apply.

            """, output.ToString().ReplaceLineEndings("\n"));
    }

    // The package-access reminder is for a new repo only
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AnExistingRulesetGetsNoPackageAccessReminder(bool rulesetDiffers)
    {
        // revert-check: not revert-sensitive (kills a surviving mutant: pins existing behaviour)
        var responses = SayMyName();
        if (rulesetDiffers)
        {
            responses[$"repos/{Repo}/rulesets/26000382"]["enforcement"] = "disabled";
        }

        Setup(new FakeGitHub(responses)).ApplyGitHub();

        Assert.DoesNotContain("Manage Actions access", output.ToString());
    }

    [Fact]
    public void CheckModeSendsNothingAndFails()
    {
        var github = new FakeGitHub(NewRepo());
        var setup = Setup(github, check: true);

        setup.ApplyGitHub();

        Assert.Empty(github.Sent);
        Assert.Equal(1, setup.Finish());
        Assert.Contains("✗ ruleset \"Protect Main\": would create", output.ToString());
    }

    [Fact]
    public void AMissingRequiredCheckUpdatesTheRulesetInPlace()
    {
        var responses = SayMyName();
        var checks = responses[$"repos/{Repo}/rulesets/26000382"]["rules"]!.AsArray()
            .Single(r => (string?)r!["type"] == "required_status_checks")!["parameters"]!["required_status_checks"]!.AsArray();
        checks.RemoveAt(checks.Count - 1);
        var github = new FakeGitHub(responses);

        Setup(github).ApplyGitHub();

        var (method, path, body) = Assert.Single(github.Sent);
        Assert.Equal(("PUT", $"repos/{Repo}/rulesets/26000382"), (method, path));
        Assert.True(Json.Matches(SetupRepo.DesiredRuleset(), body));
    }

    [Fact]
    public void FieldsGitHubAddsDontCountAsDifferences()
    {
        var responses = SayMyName();
        responses[$"repos/{Repo}/rulesets/26000382"]["rules"]![2]!["parameters"]!["some_new_github_option"] = true;
        var github = new FakeGitHub(responses);

        Setup(github).ApplyGitHub();

        Assert.Empty(github.Sent);
    }

    [Fact]
    public void AnExtraRuleCountsAsADifference()
    {
        var responses = SayMyName();
        responses[$"repos/{Repo}/rulesets/26000382"]["rules"]!.AsArray().Add(new JsonObject { ["type"] = "required_signatures" });
        var github = new FakeGitHub(responses);

        Setup(github).ApplyGitHub();

        Assert.Equal("PUT", Assert.Single(github.Sent).Method);
    }

    [Fact]
    public void AnOrganizationRulesetOfTheSameNameIsNotThisRepos()
    {
        var responses = NewRepo();
        responses[$"repos/{Repo}/rulesets?per_page=100"] = JsonNode.Parse("""[{"id":1,"name":"Protect Main","source_type":"Organization"}]""")!;
        var github = new FakeGitHub(responses);

        Setup(github).ApplyGitHub();

        Assert.Equal(("POST", $"repos/{Repo}/rulesets"), (github.Sent[0].Method, github.Sent[0].Path));
    }

    [Fact]
    public void ALabelWithTheWrongColourIsUpdatedByItsOwnName()
    {
        // revert-check: not revert-sensitive (kills a surviving mutant: pins existing behaviour)
        var responses = SayMyName();
        var label = responses[$"repos/{Repo}/labels?per_page=100"].AsArray().Single(l => (string?)l!["name"] == "no-changelog")!;
        label["name"] = "No-Changelog";
        label["color"] = "000000";
        var github = new FakeGitHub(responses);

        Setup(github).ApplyGitHub();

        var (method, path, body) = Assert.Single(github.Sent);
        Assert.Equal(("PATCH", $"repos/{Repo}/labels/No-Changelog"), (method, path));
        Assert.Equal("no-changelog", (string?)body["new_name"]);
        Assert.Equal("ededed", (string?)body["color"]);
        // GitHub's label PATCH renames by new_name; name isn't one of its fields
        Assert.False(body.AsObject().ContainsKey("name"));
    }

    [Fact]
    public void ADifferentMergeSettingIsPatched()
    {
        var responses = SayMyName();
        responses[$"repos/{Repo}"]["delete_branch_on_merge"] = true;
        var github = new FakeGitHub(responses);

        Setup(github).ApplyGitHub();

        var (method, path, body) = Assert.Single(github.Sent);
        Assert.Equal(("PATCH", $"repos/{Repo}"), (method, path));
        Assert.False((bool)body["delete_branch_on_merge"]!);
    }

    private string Checkout => Path.Combine(root, "checkout");

    private string Exclude => Path.Combine(Checkout, ".git", "info", "exclude");

    private int Local(bool check = false)
    {
        var setup = Setup(new FakeGitHub([]), check);
        setup.ApplyLocal(Checkout, Exclude);
        return setup.Finish();
    }

    [Fact]
    public void LocalFilesAreCreatedOnceAndExcluded()
    {
        // revert-check: not revert-sensitive (kills a surviving mutant: pins existing behaviour)
        Directory.CreateDirectory(Path.GetDirectoryName(Exclude)!);
        File.WriteAllText(Exclude, "# git's default comment\n.claude/worktrees/");

        Local();
        Assert.Equal(
            $"""
            → .claude/checks: create
            → .worktreeinclude: create
            → git-excluded local files: add .claude/, .worktreeinclude to {Exclude}

            """, output.ToString().ReplaceLineEndings("\n"));
        var first = File.ReadAllText(Exclude);
        output.GetStringBuilder().Clear();
        Assert.Equal(0, Local(check: true));

        Assert.Equal(first, File.ReadAllText(Exclude));
        Assert.Equal("✓ .claude/checks\n✓ .worktreeinclude\n✓ git-excluded local files\n", output.ToString().ReplaceLineEndings("\n"));
        Assert.Contains(".claude/checks", File.ReadAllText(Path.Combine(Checkout, ".worktreeinclude")));
        var checks = File.ReadAllText(Path.Combine(Checkout, ".claude", "checks"));
        Assert.Contains("stop=dotnet build SayMyName.slnx", checks);
        Assert.Contains("--mutate \"**/{name}\"", checks);
        Assert.Equal(["# git's default comment", ".claude/worktrees/", "", "# Local dev-process files, kept off the remote (buildingway setup-repo)", ".claude/", ".worktreeinclude"],
            File.ReadAllLines(Exclude));
    }

    // One blank line before the added block, whatever the file ended in (null: no file yet)
    [Theory]
    [InlineData(".claude/worktrees/\n", ".claude/worktrees/\n\n")]
    [InlineData("", "\n")]
    [InlineData(null, "\n")]
    public void TheExcludedPathsAreAppendedAfterOneBlankLine(string? existing, string before)
    {
        // revert-check: not revert-sensitive (kills a surviving mutant: pins existing behaviour)
        Directory.CreateDirectory(Path.GetDirectoryName(Exclude)!);
        if (existing is not null)
        {
            File.WriteAllText(Exclude, existing);
        }

        Local();

        Assert.Equal(before + "# Local dev-process files, kept off the remote (buildingway setup-repo)\n.claude/\n.worktreeinclude\n", File.ReadAllText(Exclude));
    }

    [Fact]
    public void ALocalFileWithEditsIsLeftAlone()
    {
        Directory.CreateDirectory(Path.Combine(Checkout, ".claude"));
        File.WriteAllText(Path.Combine(Checkout, ".claude", "checks"), "stop=make\n");

        Local();

        Assert.Equal("stop=make\n", File.ReadAllText(Path.Combine(Checkout, ".claude", "checks")));
        Assert.Contains("⚠ .claude/checks: differs", output.ToString());
    }

    [Fact]
    public void CheckModeWritesNoLocalFiles()
    {
        Directory.CreateDirectory(Checkout);

        Assert.Equal(1, Local(check: true));

        Assert.False(Directory.Exists(Path.Combine(Checkout, ".claude")));
        Assert.False(File.Exists(Exclude));
    }

    [Theory]
    [InlineData("""[1,2]""", """[2,1]""", true)]
    [InlineData("""[1,2]""", """[1,2,2]""", false)]
    [InlineData("""[1,1]""", """[1,2]""", false)]
    [InlineData("""{"a":1}""", """{"a":1,"b":2}""", true)]
    [InlineData("""{"a":1,"b":2}""", """{"a":1}""", false)]
    [InlineData("""{"a":0}""", """{"a":false}""", false)]
    [InlineData("""{"a":"x"}""", """{"a":"X"}""", false)]
    [InlineData("""{"a":null}""", """{"a":null}""", true)]
    [InlineData("""{"a":[1]}""", """{"a":{"0":1}}""", false)]
    public void JsonMatching(string desired, string actual, bool matches) =>
        // revert-check: not revert-sensitive (kills a surviving mutant: pins existing behaviour)
        Assert.Equal(matches, Json.Matches(JsonNode.Parse(desired), JsonNode.Parse(actual)));

    private readonly StringWriter error = new();

    [Theory]
    [InlineData("--force")]
    [InlineData("--check", "extra")]
    [InlineData("--repo")]
    [InlineData("--check", "--name")]
    [InlineData("--name", "SayMyName", "--repo")]
    public void BadArgumentsPrintUsageBeforeLookingAtTheCheckout(params string[] args)
    {
        // revert-check: not revert-sensitive (pins existing behaviour; the product change only adds the seams it calls)
        var github = new FakeGitHub([]);

        Assert.Equal(2, SetupRepo.Run(args, () => throw new InvalidOperationException("looked up the checkout"), github, output, error));

        Assert.Empty(github.Sent);
        Assert.Equal("", output.ToString());
        Assert.StartsWith(
            "Usage: dotnet buildingway setup-repo [--check] [--repo owner/name] [--name PluginName]\n\n",
            error.ToString().ReplaceLineEndings("\n"));
        Assert.EndsWith("Repo and name come from the plugin's csproj (<RepoUrl>, file name) unless given.\n", error.ToString().ReplaceLineEndings("\n"));
    }

    [Fact]
    public void RepoAndNameComeFromThePluginProject()
    {
        // revert-check: not revert-sensitive (pins existing behaviour; the product change only adds the seams it calls)
        using var repo = new PluginRepo("SayMyName", $"https://github.com/{Repo}");
        var github = new FakeGitHub(SayMyName());

        Assert.Equal(0, SetupRepo.Run([], () => repo.Git, github, output, error));

        // say-my-name's settings already match, so only the local files change
        Assert.Empty(github.Sent);
        foreach (var (path, content) in SetupRepo.LocalFiles("SayMyName"))
        {
            Assert.Equal(content, File.ReadAllText(Path.Combine(repo.Top, path)));
        }
        // The exclude file is git's own, found through the checkout
        Assert.Subset(File.ReadAllLines(Path.Combine(repo.Top, ".git", "info", "exclude")).ToHashSet(), SetupRepo.ExcludedPaths.ToHashSet());
        Assert.StartsWith("✓ ruleset \"Protect Main\"\n", output.ToString().ReplaceLineEndings("\n"));
        Assert.Contains("→ .claude/checks: create\n", output.ToString().ReplaceLineEndings("\n"));
        Assert.Equal("", error.ToString());
    }

    [Fact]
    public void AGivenRepoWinsAndTheNameComesFromThePluginProject()
    {
        // revert-check: not revert-sensitive (pins existing behaviour; the product change only adds the seams it calls)
        // FakeGitHub knows only say-my-name, so using the csproj's repo would throw
        using var repo = new PluginRepo("SayMyName", "https://github.com/Owner/other");

        Assert.Equal(0, SetupRepo.Run(["--repo", Repo], () => repo.Git, new FakeGitHub(SayMyName()), output, error));

        Assert.Equal(SetupRepo.LocalFiles("SayMyName")[0].Content, File.ReadAllText(Path.Combine(repo.Top, ".claude", "checks")));
    }

    [Fact]
    public void AGivenNameWinsAndTheRepoComesFromThePluginProject()
    {
        // revert-check: not revert-sensitive (pins existing behaviour; the product change only adds the seams it calls)
        using var repo = new PluginRepo("Other", $"https://github.com/{Repo}");

        Assert.Equal(0, SetupRepo.Run(["--name", "SayMyName"], () => repo.Git, new FakeGitHub(SayMyName()), output, error));

        Assert.Equal(SetupRepo.LocalFiles("SayMyName")[0].Content, File.ReadAllText(Path.Combine(repo.Top, ".claude", "checks")));
    }

    [Fact]
    public void GivenRepoAndNameNeedNoPluginProjectAndCheckChangesNothing()
    {
        // revert-check: not revert-sensitive (pins existing behaviour; the product change only adds the seams it calls)
        using var repo = new PluginRepo("Other", "https://github.com/Owner/other");
        File.Delete(Path.Combine(repo.Top, "Other", "Other.csproj"));

        Assert.Equal(1, SetupRepo.Run(["--check", "--repo", Repo, "--name", "SayMyName"], () => repo.Git, new FakeGitHub(SayMyName()), output, error));

        Assert.False(File.Exists(Path.Combine(repo.Top, ".claude", "checks")));
        Assert.Contains("✗ .claude/checks: would create\n", output.ToString().ReplaceLineEndings("\n"));
        Assert.EndsWith("Run without --check to apply.\n", output.ToString().ReplaceLineEndings("\n"));
    }
}
