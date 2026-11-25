namespace Buildingway.Tools.Tests;

/// <summary>A temporary git checkout holding a Dalamud plugin project, with one commit on main.</summary>
internal sealed class PluginRepo : IDisposable
{
    public PluginRepo(string name, string repoUrl = "https://github.com/Owner/my-plugin")
    {
        // These point git at another repo whatever -C says, e.g. when the tests run in a git hook
        foreach (var variable in new[] { "GIT_DIR", "GIT_WORK_TREE", "GIT_INDEX_FILE", "GIT_OBJECT_DIRECTORY", "GIT_COMMON_DIR" })
        {
            if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable(variable)))
            {
                throw new InvalidOperationException($"Unset {variable}: tests that make git repos would act on the repo it names.");
            }
        }
        Top = Directory.CreateTempSubdirectory("buildingway-repo").FullName;
        Directory.CreateDirectory(Path.Combine(Top, name));
        File.WriteAllText(Path.Combine(Top, name, name + ".csproj"), $"""
            <Project Sdk="Dalamud.NET.Sdk/15.0.0">
              <PropertyGroup>
                <RepoUrl>{repoUrl}</RepoUrl>
              </PropertyGroup>
            </Project>
            """);
        Git = new Git(Top);
        Git.Output("init", "-q", "-b", "main");
        Commit("Initial");
    }

    /// <summary>Commits every change in the checkout.</summary>
    public void Commit(string message)
    {
        Git.Output("add", ".");
        Git.Output("-c", "user.name=Test", "-c", "user.email=test@example.com", "-c", "commit.gpgsign=false", "commit", "-q", "--allow-empty", "-m", message);
    }

    public string Top { get; }

    public Git Git { get; }

    public PluginProject Plugin => PluginProject.Find(Top);

    public void Dispose()
    {
        // git makes its object files read-only, which Directory.Delete refuses on Windows
        foreach (var file in Directory.EnumerateFiles(Top, "*", SearchOption.AllDirectories))
        {
            File.SetAttributes(file, FileAttributes.Normal);
        }
        Directory.Delete(Top, recursive: true);
    }
}
