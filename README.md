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

### Python tools

`revert-check` and `mutant-triage` are copies of the tools in `Cellar-Mander/ebd`'s `bin/`, at
the commits in [`Buildingway.Tools/Scripts/SOURCES`](Buildingway.Tools/Scripts/SOURCES); change
them there and copy them over. They run with `python3` (`python` on Windows); set
`BUILDINGWAY_PYTHON` to use another interpreter.

## Releasing this tool

Push a tag `v<semver>` (e.g. `v0.1.0`); the Release workflow tests, packs that version, pushes
it to GitHub Packages with `github.token` and attaches the `.nupkg` to a GitHub release.
