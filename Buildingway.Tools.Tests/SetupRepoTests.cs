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
        var github = new FakeGitHub(SayMyName());
        var setup = Setup(github, check: true);

        setup.ApplyGitHub();

        Assert.Empty(github.Sent);
        Assert.Equal(0, setup.Finish());
        Assert.DoesNotContain("✗", output.ToString());
    }

    [Fact]
    public void TheStandardRulesetSetsEverySayMyNameRuleSetting()
    {
        // The other direction to SayMyNameAlreadyMatchesTheStandard, so a setting can't drop out
        Assert.True(Json.Matches(Fixture("ruleset.json")["rules"], SetupRepo.DesiredRuleset()["rules"]));
    }

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
        Directory.CreateDirectory(Path.GetDirectoryName(Exclude)!);
        File.WriteAllText(Exclude, "# git's default comment\n.claude/worktrees/");

        Local();
        var first = File.ReadAllText(Exclude);
        output.GetStringBuilder().Clear();
        Assert.Equal(0, Local(check: true));

        Assert.Equal(first, File.ReadAllText(Exclude));
        Assert.DoesNotContain("✗", output.ToString());
        var checks = File.ReadAllText(Path.Combine(Checkout, ".claude", "checks"));
        Assert.Contains("stop=dotnet build SayMyName.slnx", checks);
        Assert.Contains("--mutate \"**/{name}\"", checks);
        Assert.Equal(["# git's default comment", ".claude/worktrees/", "", "# Local dev-process files, kept off the remote (buildingway setup-repo)", ".claude/", ".worktreeinclude"],
            File.ReadAllLines(Exclude));
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
    public void JsonMatching(string desired, string actual, bool matches) =>
        Assert.Equal(matches, Json.Matches(JsonNode.Parse(desired), JsonNode.Parse(actual)));
}
