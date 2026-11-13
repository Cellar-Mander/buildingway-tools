using System.Text.Json.Nodes;

namespace Buildingway.Tools;

/// <summary>
/// Brings a plugin repo's GitHub settings and local dev-process files to the shared standard.
/// Safe to re-run: it compares first and changes only what differs, so running it after a tool
/// update carries a changed standard to existing repos. GitHub settings that differ are
/// overwritten; local files are created when missing and only reported when they differ,
/// since they may hold personal edits.
/// </summary>
internal sealed class SetupRepo(IGitHub github, string repo, string name, TextWriter output, bool check)
{
    /// <summary>The GitHub Actions app, which reports the required checks.</summary>
    private const int ActionsAppId = 15368;

    public const string RulesetName = "Protect Main";

    /// <summary>Check runs the main branch requires: shared buildingway-ci jobs as 'caller / job'.</summary>
    public static readonly string[] RequiredChecks = ["test / test", "pr-build / build", "lint / actionlint", "lint / zizmor"];

    private bool changed;

    public static int Run(string[] args) => Run(args, Git.FromCurrentDirectory, new GhCli(), Console.Out, Console.Error);

    /// <param name="findGit">The checkout, looked up only once the arguments are valid.</param>
    public static int Run(string[] args, Func<Git> findGit, IGitHub github, TextWriter output, TextWriter error)
    {
        string? repo = null, name = null;
        var check = false;
        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--check":
                    check = true;
                    break;
                case "--repo" when i + 1 < args.Length:
                    repo = args[++i];
                    break;
                case "--name" when i + 1 < args.Length:
                    name = args[++i];
                    break;
                default:
                    error.WriteLine("""
                        Usage: dotnet buildingway setup-repo [--check] [--repo owner/name] [--name PluginName]

                        Sets the GitHub ruleset, merge settings, Actions permissions and labels a plugin repo
                        needs, and writes the local .claude/checks and .worktreeinclude (git-excluded).
                        Re-run it any time: it changes only what differs. --check reports without changing
                        anything and exits 1 if something would change.
                        Repo and name come from the plugin's csproj (<RepoUrl>, file name) unless given.
                        """);
                    return 2;
            }
        }
        var git = findGit();
        if (repo is null || name is null)
        {
            var plugin = PluginProject.Find(git.Top);
            repo ??= plugin.Repo;
            name ??= plugin.Name;
        }
        var setup = new SetupRepo(github, repo, name, output, check);
        setup.ApplyGitHub();
        setup.ApplyLocal(git.Top, git.Output("rev-parse", "--path-format=absolute", "--git-path", "info/exclude"));
        return setup.Finish();
    }

    public void ApplyGitHub()
    {
        var created = ApplyRuleset();
        ApplyRepoSettings();
        ApplyWorkflowPermissions();
        ApplyLabels();
        if (created)
        {
            output.WriteLine("  Not checked: if the Buildingway.Tools package is private, give this repo Read under");
            output.WriteLine("  its \"Manage Actions access\" settings, or CI can't restore the tool.");
        }
    }

    /// <summary>Exit code: in --check mode 1 when something would change.</summary>
    public int Finish()
    {
        if (check && changed)
        {
            output.WriteLine("Run without --check to apply.");
            return 1;
        }
        return 0;
    }

    private void Report(string what, string? change)
    {
        if (change is null)
        {
            output.WriteLine($"✓ {what}");
            return;
        }
        changed = true;
        output.WriteLine(check ? $"✗ {what}: would {change}" : $"→ {what}: {change}");
    }

    private void Send(string method, string path, JsonNode body)
    {
        if (!check)
        {
            github.Send(method, path, body);
        }
    }

    public static JsonObject DesiredRuleset() => new()
    {
        ["name"] = RulesetName,
        ["target"] = "branch",
        ["enforcement"] = "active",
        ["bypass_actors"] = new JsonArray(),
        ["conditions"] = new JsonObject
        {
            ["ref_name"] = new JsonObject { ["include"] = new JsonArray("refs/heads/main"), ["exclude"] = new JsonArray() },
        },
        ["rules"] = new JsonArray(
            new JsonObject { ["type"] = "deletion" },
            new JsonObject { ["type"] = "non_fast_forward" },
            new JsonObject
            {
                ["type"] = "pull_request",
                ["parameters"] = new JsonObject
                {
                    ["required_approving_review_count"] = 0,
                    ["dismiss_stale_reviews_on_push"] = false,
                    ["require_code_owner_review"] = false,
                    ["require_last_push_approval"] = false,
                    ["required_review_thread_resolution"] = false,
                    ["required_reviewers"] = new JsonArray(),
                    ["require_extra_approval_for_unattributed_changes"] = true,
                    ["allowed_merge_methods"] = new JsonArray("merge", "squash", "rebase"),
                },
            },
            new JsonObject
            {
                ["type"] = "required_status_checks",
                ["parameters"] = new JsonObject
                {
                    ["strict_required_status_checks_policy"] = true,
                    ["do_not_enforce_on_create"] = false,
                    ["required_status_checks"] = new JsonArray([.. RequiredChecks.Select(c =>
                        (JsonNode)new JsonObject { ["context"] = c, ["integration_id"] = ActionsAppId })]),
                },
            }),
    };

    /// <summary>Creates or updates the ruleset; true when it had to be created (a new repo).</summary>
    private bool ApplyRuleset()
    {
        var what = $"ruleset \"{RulesetName}\"";
        var desired = DesiredRuleset();
        var listed = github.GetAll($"repos/{repo}/rulesets?per_page=100")
            .FirstOrDefault(r => (string?)r!["name"] == RulesetName && (string?)r["source_type"] != "Organization");
        if (listed is null)
        {
            Report(what, "create");
            Send("POST", $"repos/{repo}/rulesets", desired);
            return true;
        }
        var id = (long)listed["id"]!;
        if (Json.Matches(desired, github.Get($"repos/{repo}/rulesets/{id}")))
        {
            Report(what, null);
            return false;
        }
        Report(what, "update");
        Send("PUT", $"repos/{repo}/rulesets/{id}", desired);
        return false;
    }

    public static JsonObject DesiredRepoSettings() => new()
    {
        ["allow_merge_commit"] = true,
        ["allow_squash_merge"] = true,
        ["allow_rebase_merge"] = true,
        ["allow_auto_merge"] = false,
        ["allow_update_branch"] = false,
        ["delete_branch_on_merge"] = false,
        ["merge_commit_title"] = "MERGE_MESSAGE",
        ["merge_commit_message"] = "PR_TITLE",
        ["squash_merge_commit_title"] = "COMMIT_OR_PR_TITLE",
        ["squash_merge_commit_message"] = "COMMIT_MESSAGES",
    };

    private void ApplyRepoSettings() =>
        Apply("merge settings", $"repos/{repo}", "PATCH", DesiredRepoSettings());

    /// <summary>Read-only token by default; Actions may open the release's repo.json PR.</summary>
    public static JsonObject DesiredWorkflowPermissions() => new()
    {
        ["default_workflow_permissions"] = "read",
        ["can_approve_pull_request_reviews"] = true,
    };

    private void ApplyWorkflowPermissions() =>
        Apply("Actions workflow permissions", $"repos/{repo}/actions/permissions/workflow", "PUT", DesiredWorkflowPermissions());

    private void Apply(string what, string path, string method, JsonObject desired)
    {
        if (Json.Matches(desired, github.Get(path)))
        {
            Report(what, null);
            return;
        }
        Report(what, "update");
        Send(method, path, desired);
    }

    /// <summary>Labels the shared changelog and mutation workflows read; nothing else creates them.</summary>
    public static readonly (string Name, string Color, string Description)[] Labels =
    [
        ("no-changelog", "ededed", "Plugin code change users won't notice; skips the Changelog check"),
        ("mutation", "5319e7", "Run Stryker.NET mutation testing on this PR (advisory)"),
    ];

    private void ApplyLabels()
    {
        var existing = github.GetAll($"repos/{repo}/labels?per_page=100");
        foreach (var (labelName, color, description) in Labels)
        {
            var what = $"label {labelName}";
            var desired = new JsonObject { ["name"] = labelName, ["color"] = color, ["description"] = description };
            var found = existing.FirstOrDefault(l => string.Equals((string?)l!["name"], labelName, StringComparison.OrdinalIgnoreCase));
            if (found is null)
            {
                Report(what, "create");
                Send("POST", $"repos/{repo}/labels", desired);
            }
            else if (Json.Matches(desired, found))
            {
                Report(what, null);
            }
            else
            {
                Report(what, "update");
                desired["new_name"] = labelName;
                desired.Remove("name");
                Send("PATCH", $"repos/{repo}/labels/{Uri.EscapeDataString((string)found["name"]!)}", desired);
            }
        }
    }

    /// <summary>The local dev-process files, kept off the remote.</summary>
    public static IReadOnlyList<(string Path, string Content)> LocalFiles(string name) =>
    [
        // {file}, {trx} and {name} are the checks hooks' placeholders; {{name}} is the plugin
        (".claude/checks", $$"""
            # Commands for ~/.claude/hooks/checks.sh and ~/.local/bin/pre-pr (key=value; {file} = edited file).
            # --include ignores absolute paths silently, hence realpath. Whitespace/charset only: analyzer warnings already fail the build (TreatWarningsAsErrors).
            lint=case {file} in *.cs) dotnet format whitespace {{name}}.slnx --verify-no-changes --include "$(realpath --relative-to=. {file})" ;; esac
            stop=dotnet build {{name}}.slnx --nologo -v q && dotnet test {{name}}.Core.Tests --nologo --no-build
            # Core.Tests only, like CI: the plugin project needs a Dalamud install to build.
            revert_test=dotnet build {{name}}.Core.Tests --nologo -v q && dotnet test {{name}}.Core.Tests --nologo --no-build --logger "trx;LogFileName={trx}"
            # dotnet buildingway mutant-triage: full run, one file's mutants, and the spec triage checks behaviour against.
            # buildingway stryker caps the runners and memory to fit the machine, so a runaway mutant can't freeze it.
            mutation_run=cd {{name}}.Core.Tests && dotnet buildingway stryker
            mutation_rerun=cd {{name}}.Core.Tests && dotnet buildingway stryker --mutate "**/{name}"
            spec=docs/design.md

            """),
        (".worktreeinclude", """
            # Copies these git-excluded files into new Claude Code worktrees.
            .claude/checks

            """),
    ];

    /// <summary>What .git/info/exclude must list so the local files stay off the remote.</summary>
    public static readonly string[] ExcludedPaths = [".claude/", ".worktreeinclude"];

    /// <param name="top">The checkout to write the local files in.</param>
    /// <param name="excludeFile">The repo's info/exclude, shared by its worktrees.</param>
    public void ApplyLocal(string top, string excludeFile)
    {
        foreach (var (path, content) in LocalFiles(name))
        {
            var full = Path.Combine(top, path);
            if (!File.Exists(full))
            {
                Report(path, "create");
                if (!check)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(full)!);
                    File.WriteAllText(full, content);
                }
            }
            else if (File.ReadAllText(full).ReplaceLineEndings("\n") == content)
            {
                Report(path, null);
            }
            else
            {
                output.WriteLine($"⚠ {path}: differs from the standard, left as is (delete it and re-run to replace it)");
            }
        }

        var excluded = File.Exists(excludeFile) ? File.ReadAllLines(excludeFile).Select(l => l.Trim()).ToHashSet() : [];
        var missing = ExcludedPaths.Where(p => !excluded.Contains(p)).ToList();
        if (missing.Count == 0)
        {
            Report("git-excluded local files", null);
        }
        else
        {
            Report("git-excluded local files", $"add {string.Join(", ", missing)} to {excludeFile}");
            if (!check)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(excludeFile)!);
                File.AppendAllText(excludeFile, $"{Separator(excludeFile)}\n# Local dev-process files, kept off the remote (buildingway setup-repo)\n{string.Join("\n", missing)}\n");
            }
        }
    }

    /// <summary>A newline when the file exists and doesn't end in one, so an append starts a new line.</summary>
    private static string Separator(string path) =>
        File.Exists(path) && new FileInfo(path).Length > 0 && !File.ReadAllText(path).EndsWith('\n') ? "\n" : "";

}

internal static class Json
{
    /// <summary>
    /// True when <paramref name="actual"/> has everything <paramref name="desired"/> sets.
    /// Objects may have extra keys (GitHub adds fields and defaults); arrays must have the same
    /// length and match element for element in any order.
    /// </summary>
    public static bool Matches(JsonNode? desired, JsonNode? actual) => (desired, actual) switch
    {
        (null, null) => true,
        (JsonObject d, JsonObject a) => d.All(p => a.TryGetPropertyValue(p.Key, out var v) && Matches(p.Value, v)),
        (JsonArray d, JsonArray a) => d.Count == a.Count && MatchAll(d, a),
        (JsonValue d, JsonValue a) => JsonNode.DeepEquals(d, a),
        _ => false,
    };

    /// <summary>Each desired element matches a different actual element.</summary>
    private static bool MatchAll(JsonArray desired, JsonArray actual)
    {
        var unused = actual.ToList();
        foreach (var d in desired)
        {
            var i = unused.FindIndex(a => Matches(d, a));
            if (i < 0)
            {
                return false;
            }
            unused.RemoveAt(i);
        }
        return true;
    }
}
