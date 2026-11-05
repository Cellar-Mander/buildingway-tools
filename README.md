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

## Using it in a plugin repo

Add it to the repo's tool manifest at `.config/dotnet-tools.json`, where Dependabot's `nuget`
updater looks for tools to update:

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

Then, from anywhere in the checkout:

```bash
dotnet tool restore
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

### Python tools

`revert-check` and `mutant-triage` are copies of the tools in `Cellar-Mander/ebd`'s `bin/`, at
the commits in [`Buildingway.Tools/Scripts/SOURCES`](Buildingway.Tools/Scripts/SOURCES); change
them there and copy them over. They run with `python3` (`python` on Windows); set
`BUILDINGWAY_PYTHON` to use another interpreter.

## Releasing this tool

Push a tag `v<semver>` (e.g. `v0.1.0`); the Release workflow tests, packs that version and pushes
it to nuget.org.
