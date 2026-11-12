namespace Buildingway.Tools.Tests;

/// <summary>A temporary git checkout holding a Dalamud plugin project, with one commit on main.</summary>
internal sealed class PluginRepo : IDisposable
{
    public PluginRepo(string name, string repoUrl = "https://github.com/Owner/my-plugin")
    {
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
        Git.Output("add", ".");
        Git.Output("-c", "user.name=Test", "-c", "user.email=test@example.com", "-c", "commit.gpgsign=false", "commit", "-q", "-m", "Initial");
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
