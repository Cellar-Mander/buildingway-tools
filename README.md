# buildingway-tools

Dev and release commands for Dalamud plugin projects, as a .NET local tool. The plugin is the one
project in a top-level folder whose csproj uses `Dalamud.NET.Sdk`; its file name is the plugin's
name, so nothing in the tool is specific to one plugin.

| Command | Does |
|---|---|
| `dev-plugin deploy [task-ref]` | Builds the plugin (Debug, x64), claims the one in-game dev folder and copies the build in |
| `dev-plugin status` | Shows which checkout, branch and commit hold the dev folder |
| `dev-plugin release [--force]` | Frees the dev folder (`--force`: even if another checkout holds it) |
| `release prepare <version>` | Makes the release-prep PR: sets `<Version>`, dates the CHANGELOG, builds, tests |
| `release publish <version> [--testing]` | Runs the plugin repo's Release workflow on main |
| `release verify <version>[-rc<n>]` | Checks the tag, GitHub release, zip manifest and live `repo.json` |
| `revert-check` | Do the branch's new or changed tests fail with its product changes reverted? (needs `python3`) |
| `mutant-triage` | Works through a Stryker run's surviving mutants (needs `python3`) |
| `stryker [args]` | Runs `dotnet stryker` with its concurrency and memory capped to fit the machine |
| `setup-repo [--check]` | Sets the repo's ruleset, merge settings, Actions permissions and labels, and writes local dev files (needs `gh`) |

## Using it in a plugin repo

The package is on this owner's GitHub Packages feed. GitHub asks for a token to read NuGet
packages, even public ones, so a plugin repo needs three things.

1. A `nuget.config` at the root that adds the feed for `Buildingway.*` only, so every other
   package still comes from nuget.org and a restore without a token only fails for this tool:

   ```xml
   <?xml version="1.0" encoding="utf-8"?>
   <configuration>
     <packageSources>
       <add key="nuget.org" value="https://api.nuget.org/v3/index.json" />
       <add key="buildingway" value="https://nuget.pkg.github.com/Cellar-Mander/index.json" />
     </packageSources>
     <packageSourceMapping>
       <packageSource key="nuget.org">
         <package pattern="*" />
       </packageSource>
       <packageSource key="buildingway">
         <package pattern="Buildingway.*" />
       </packageSource>
     </packageSourceMapping>
   </configuration>
   ```

2. The tool in `.config/dotnet-tools.json`, where Dependabot's `nuget` updater looks for tools:

   ```json
   {
     "version": 1,
     "isRoot": true,
     "tools": {
       "buildingway.tools": {
         "version": "0.1.0",
         "commands": [ "buildingway" ],
         "rollForward": false
       }
     }
   }
   ```

3. A token with `read:packages` for the `buildingway` source, passed in an environment
   variable so it never lands in a file:

   ```bash
   gh auth refresh -s read:packages   # once
   export NuGetPackageSourceCredentials_buildingway="Username=token;Password=$(gh auth token)"
   dotnet tool restore
   ```

   For GitHub Actions and Dependabot, give the plugin repo Read access in the package's
   settings, under "Manage Actions access". Then a job with `packages: read` sets the same
   variable from `github.token` (buildingway-ci's workflows do), and Dependabot authenticates on
   its own: no token secret or `registries:` entry.

Then, from anywhere in the checkout:

```bash
dotnet buildingway dev-plugin deploy 1a2b3c4d
```

### The dev folder

Only one checkout may hold the folder Dalamud loads dev plugins from, so parallel worktrees never
overwrite a build under test. The claim is a `.claim` folder there (created atomically) with an
`info` file. The folder is `%APPDATA%\XIVLauncher\devPlugins\<Name>` on Windows and
`~/.xlcore/devPlugins/<Name>` elsewhere; set `<NAME>_DEV_DIR` (the plugin name upper-cased, e.g.
`SAYMYNAME_DEV_DIR`) to use another.

### Releases

`release` expects what buildingway-ci's `release.yml` expects: `<RepoUrl>` on github.com in the
csproj, a Keep a Changelog `CHANGELOG.md` with `## Unreleased`, `repo.json` on main, and
optionally `.github/dalamud.env` (`prepare` warns when the local Dalamud is past its pin). It
needs `git` and `gh`. `prepare` builds the one `.slnx` or `.sln` at the root and runs its tests.

### Repo setup

`setup-repo` brings a plugin repo to the shared standard, changing only what differs, so re-run
it after updating the tool to carry a changed standard to existing repos. `--check` only reports,
and exits 1 if anything would change. It needs `gh` logged in with admin rights on the repo, and
takes the repo and name from the csproj (`--repo owner/name` and `--name` for a repo without one
yet).

- GitHub: the "Protect Main" ruleset on `main` (PRs only, no deletion or force push, buildingway-ci's
  checks required), merge settings, a read-only Actions token that may open PRs (for the release's
  `repo.json` PR), and the `no-changelog` and `mutation` labels the shared workflows read. Settings
  that differ are overwritten.
- Local, kept off the remote through `.git/info/exclude`: `.claude/checks` and `.worktreeinclude`,
  created when missing and only reported when they differ.

### Stryker limits

`stryker` runs `dotnet stryker [args]` (the repo's own Stryker tool) so that a mutant that
allocates in a loop, such as one that removes the loop's increment, can't take the machine down.
`setup-repo`'s `mutation_run` and `mutation_rerun` use it; in a repo set up before, `setup-repo`
reports `.claude/checks` as differing, so change those two lines or delete the file and re-run.

- **Concurrency**: unless the args give `--concurrency`/`-c`, it passes
  `--concurrency N`, N = max(1, min(logical cores / 2, ceiling / 768 MiB)). This overrides a
  `concurrency` in `stryker-config.json`; pass `-c` to choose. Set
  `BUILDINGWAY_STRYKER_RUNNER_MB` to change the 768 MiB per runner (measured on
  Jaunt.Core.Tests: about 320 MiB per extra runner, vstest.console plus test host).
- **Memory ceiling**: half the memory available at the start (`MemAvailable` on Linux,
  available physical memory on Windows), covering Stryker and every test host it starts. On
  Linux the run is in a `systemd-run --user --scope` with `MemoryMax` and `MemorySwapMax=0`;
  on Windows the tool joins a Job Object with a job memory limit before starting Stryker.
  Without a systemd user session it warns and caps the concurrency only. While the run goes,
  the tool sets `oom_score_adj` to 1000 on every process in the scope except Stryker itself
  (vstest.console, test hosts, builds), so the kernel's OOM killer always picks a test process.
  Left to pick by size, it once killed Stryker after two runaway test hosts overflowed the
  ceiling together, ending the run with no report.
- **CI** (`CI=true`): concurrency only; the runner is disposable.
- **One run at a time**: a second `buildingway stryker` run (per user, any repo) waits, naming the
  one that holds the lock, then sizes itself to the memory left, since two runs sized to half the
  free memory each could use it all. Bare `dotnet stryker` runs don't take the lock.

It prints one line with the concurrency and ceiling it chose.

**How capped mutants are recorded** (Stryker.NET 5.0.0, tested 2026-11-13 with a mutant that
allocates 1 MiB per loop pass forever): never as survived, so triage isn't misled.

- Usually **Killed**: .NET sizes its GC heap limit to 75% of the cgroup's or job's memory
  limit, so the runaway test host throws `OutOfMemoryException` and the test fails.
- **Timeout** when the kernel's OOM killer ends the test host first (several runners share the
  ceiling, so the runaway one reaches it before its own heap limit). Stryker carries on with the
  other mutants. A timeout on a mutant in a loop may be this; it's caught either way. On jaunt's
  tokeniser (2 runners, 2500 MiB ceiling) the OOM killer fired 4 times and the 18 loop-progress
  mutants (`i++` removed, `i--`, negated conditions) all came out Timeout; the run finished.

### Python tools

`revert-check` and `mutant-triage` are copies of the tools in `Cellar-Mander/ebd`'s `bin/`, at
the commits in [`Buildingway.Tools/Scripts/SOURCES`](Buildingway.Tools/Scripts/SOURCES); change
them there, not here. They run with `python3` (`python` on Windows); set
`BUILDINGWAY_PYTHON` to use another interpreter.

ebd is private, so it publishes these scripts to the public mirror
[`Cellar-Mander/ebd-scripts`](https://github.com/Cellar-Mander/ebd-scripts) on each push, with a
`MANIFEST` of the ebd commit and each script's SHA-256. `SOURCES` pins each copy's commit and
SHA-256, and nothing here needs a credential for ebd.

- CI fails when a copy's SHA-256 differs from its pin.
- The daily ebd sync workflow fails when a pin differs from the mirror's or a script has left
  ebd, and opens or updates a PR from the `ebd-sync` branch that recopies them, pins them at
  ebd's latest and deletes copies of scripts ebd no longer has (and their `EmbeddedResource`;
  the PR says what else to remove). It starts CI on that branch itself, since PRs opened with
  `github.token` don't trigger it. Run it by hand from the Actions tab after changing a script
  in ebd. It needs Settings → Actions → General → "Allow GitHub Actions to create and approve
  pull requests".
- Before merging a sync PR, diff the copies against ebd at the commit it names: CI here can only
  check them against the mirror.

Locally: `.github/scripts/ebd-scripts check [--latest <mirror-clone>]` or
`.github/scripts/ebd-scripts sync <mirror-clone>`.

## Releasing this tool

Push a tag `v<semver>` (e.g. `v0.1.0`); the Release workflow tests, packs that version, pushes
it to GitHub Packages with `github.token` and attaches the `.nupkg` to a GitHub release.
