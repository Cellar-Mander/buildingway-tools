using System.Reflection;
using System.Text;

namespace Buildingway.Tools;

internal static class Program
{
    private const string Usage = """
        Usage: dotnet buildingway <command> [args]

          dev-plugin     deploy this checkout's build to the in-game dev folder, or show or free its claim
          release        prepare, publish and verify plugin releases
          revert-check   do the branch's new or changed tests fail with its product changes reverted?
          mutant-triage  work through a mutation run's surviving mutants
          setup-repo     set the repo's GitHub ruleset, merge settings and labels, and write local dev files

        Run a command without arguments, or revert-check and mutant-triage with --help, for its usage.
        The plugin is the project in a top-level folder whose csproj uses Dalamud.NET.Sdk.
        """;

    public static int Main(string[] args)
    {
        // ✓, ✗ and ⚠ on Windows consoles
        Console.OutputEncoding = new UTF8Encoding(false);
        try
        {
            return args switch
            {
                ["dev-plugin", .. var rest] => DevPlugin.Run(rest),
                ["release", .. var rest] => Release.Run(rest),
                ["revert-check", .. var rest] => Python.Run("revert-check", rest),
                ["mutant-triage", .. var rest] => Python.Run("mutant-triage", rest),
                ["setup-repo", .. var rest] => SetupRepo.Run(rest),
                ["--version"] => PrintVersion(),
                _ => PrintUsage(),
            };
        }
        catch (ToolException e)
        {
            Console.Error.WriteLine(e.Message);
            return e.ExitCode;
        }
    }

    private static int PrintVersion()
    {
        Console.WriteLine(Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion);
        return 0;
    }

    private static int PrintUsage()
    {
        Console.Error.WriteLine(Usage);
        return 2;
    }
}
