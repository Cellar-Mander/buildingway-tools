namespace Buildingway.Tools.Tests;

public sealed class PluginProjectTests : IDisposable
{
    private readonly string top = Directory.CreateTempSubdirectory("buildingway-tests").FullName;

    public void Dispose() => Directory.Delete(top, recursive: true);

    private void Write(string path, string content)
    {
        var full = Path.Combine(top, path);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
    }

    private const string Plugin = """
        <Project Sdk="Dalamud.NET.Sdk/15.0.0">
          <PropertyGroup>
            <Version>1.0.0.1</Version>
            <RepoUrl>https://github.com/Owner/my-plugin</RepoUrl>
          </PropertyGroup>
          <ItemGroup>
            <ProjectReference Include="..\MyPlugin.Core\MyPlugin.Core.csproj" />
          </ItemGroup>
        </Project>
        """;

    [Fact]
    public void FindsTheDalamudProjectAndReadsItsName()
    {
        Write("MyPlugin/MyPlugin.csproj", Plugin);
        Write("MyPlugin.Core/MyPlugin.Core.csproj", """<Project Sdk="Microsoft.NET.Sdk" />""");
        Write("MyPlugin.Core.Tests/MyPlugin.Core.Tests.csproj", """<Project Sdk="Microsoft.NET.Sdk" />""");

        var plugin = PluginProject.Find(top);

        Assert.Equal("MyPlugin", plugin.Name);
        Assert.Equal("MyPlugin/MyPlugin.csproj", plugin.Csproj);
        Assert.Equal("Owner/my-plugin", plugin.Repo);
        Assert.Equal("MYPLUGIN_DEV_DIR", plugin.DevDirVariable);
        Assert.Equal(["MyPlugin/", "MyPlugin.Core/"], plugin.CodePaths);
    }

    [Fact]
    public void NameComesFromTheCsprojNotTheFolder()
    {
        Write("src/Other.csproj", """<Project Sdk="Dalamud.NET.Sdk/15.0.0" />""");
        var plugin = PluginProject.Find(top);
        Assert.Equal("Other", plugin.Name);
        Assert.Equal("src/Other.csproj", plugin.Csproj);
    }

    [Fact]
    public void RefusesNoneOrSeveral()
    {
        Write("Lib/Lib.csproj", """<Project Sdk="Microsoft.NET.Sdk" />""");
        Assert.Throws<ToolException>(() => PluginProject.Find(top));
        Write("A/A.csproj", """<Project Sdk="Dalamud.NET.Sdk/15.0.0" />""");
        Write("B/B.csproj", """<Project Sdk="Dalamud.NET.Sdk/15.0.0" />""");
        Assert.Contains("More than one", Assert.Throws<ToolException>(() => PluginProject.Find(top)).Message);
    }

    [Fact]
    public void IgnoresDotFoldersSuchAsWorktrees()
    {
        Write("MyPlugin/MyPlugin.csproj", Plugin);
        Write(".claude/worktrees/x/MyPlugin/MyPlugin.csproj", Plugin);
        Write(".hidden/Copy.csproj", Plugin);
        Assert.Equal("MyPlugin", PluginProject.Find(top).Name);
    }

    [Theory]
    [InlineData("https://gitlab.com/Owner/my-plugin")]
    [InlineData("https://github.com/Owner")]
    public void RepoNeedsAGitHubRepoUrl(string url)
    {
        Write("MyPlugin/MyPlugin.csproj", Plugin.Replace("https://github.com/Owner/my-plugin", url));
        Assert.Throws<ToolException>(() => PluginProject.Find(top).Repo);
    }

    [Theory]
    [InlineData("<Version>1.2.3.4</Version>", "1.2.3.4")]
    [InlineData("<Foo/>", null)]
    public void VersionIn(string text, string? expected) => Assert.Equal(expected, PluginProject.VersionIn(text));
}
