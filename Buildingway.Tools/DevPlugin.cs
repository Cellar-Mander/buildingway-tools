using System.Runtime.InteropServices;

namespace Buildingway.Tools;

/// <summary>
/// Deploys this checkout's plugin build to the one dev folder Dalamud loads from. Only one
/// checkout may hold the folder at a time, so a build under test is never overwritten by
/// another checkout or worktree. The claim is a .claim directory (creating it is atomic) with
/// an info file, in the format the earlier scripts/dev-plugin used.
/// </summary>
internal sealed class DevPlugin(string devDir, string checkout, TextWriter output, TextWriter error)
{
    private string Claim => Path.Combine(devDir, ".claim");

    private string ClaimInfo => Path.Combine(Claim, "info");

    public static string DefaultDevDir(PluginProject plugin) => DefaultDevDir(
        plugin.Name,
        Environment.GetEnvironmentVariable(plugin.DevDirVariable),
        OperatingSystem.IsWindows(),
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));

    /// <param name="set">The plugin's dev folder variable, which wins when set.</param>
    public static string DefaultDevDir(string pluginName, string? set, bool windows, string appData, string home)
    {
        if (!string.IsNullOrEmpty(set))
        {
            return set;
        }
        // XIVLauncher's folder on Windows, XIVLauncher.Core's elsewhere
        var root = windows ? Path.Combine(appData, "XIVLauncher") : Path.Combine(home, ".xlcore");
        return Path.Combine(root, "devPlugins", pluginName);
    }

    public static int Run(string[] args)
    {
        var git = Git.FromCurrentDirectory();
        var plugin = PluginProject.Find(git.Top);
        return Run(args, git, plugin, DefaultDevDir(plugin), Build, Console.Out, Console.Error);
    }

    /// <param name="build">Builds the plugin and returns its output folder.</param>
    public static int Run(string[] args, Git git, PluginProject plugin, string devDir, Func<PluginProject, string> build, TextWriter output, TextWriter error)
    {
        var dev = new DevPlugin(devDir, git.Top, output, error);
        switch (args)
        {
            case ["deploy"]:
            case ["deploy", _]:
                var built = build(plugin);
                return dev.Deploy(built, plugin.Name, ClaimDetails(git, args.ElementAtOrDefault(1) ?? "", DateTimeOffset.Now));
            case ["status"]:
                dev.Status();
                return 0;
            case ["release"]:
                return dev.Release(force: false);
            case ["release", "--force"]:
                return dev.Release(force: true);
            default:
                error.WriteLine($"""
                    Usage: dotnet buildingway dev-plugin deploy [task-ref]   build, claim the dev folder and copy the plugin in
                           dotnet buildingway dev-plugin status              show who holds the dev folder
                           dotnet buildingway dev-plugin release [--force]   free the dev folder (--force: even if another checkout holds it)

                    Dev folder: {dev.DevDir} (set {plugin.DevDirVariable} to change)
                    """);
                return 2;
        }
    }

    public string DevDir => devDir;

    /// <summary>Builds the plugin and returns its output folder.</summary>
    private static string Build(PluginProject plugin)
    {
        var (args, output) = BuildCommand(plugin);
        Shell.Run("dotnet", args);
        return output;
    }

    /// <summary>The dotnet arguments that build the plugin, and the folder the build lands in.</summary>
    public static (string[] Args, string Output) BuildCommand(PluginProject plugin) =>
        // x64 so the build lands in bin/x64/Debug, where the copy reads it; a bare project
        // build is AnyCPU and goes to bin/Debug
        (["build", Path.Combine(plugin.Top, plugin.Csproj), "-c", "Debug", "-p:Platform=x64", "--nologo", "-v", "q"],
         Path.Combine(plugin.Top, plugin.Dir, "bin", "x64", "Debug"));

    public static string ClaimDetails(Git git, string task, DateTimeOffset now)
    {
        var commit = git.Output("rev-parse", "--short", "HEAD");
        if (git.IsDirty())
        {
            commit += " (uncommitted changes)";
        }
        var branch = git.Output("branch", "--show-current");
        return $"branch={branch}\ncommit={commit}\ntask={task}\ndeployed={now:yyyy-MM-ddTHH:mm:sszzz}\n";
    }

    /// <summary>Claims the dev folder and copies the build in; refuses if another checkout holds it.</summary>
    /// <param name="details">Claim lines after checkout=, each ending in \n.</param>
    public int Deploy(string buildOutput, string pluginName, string details)
    {
        Directory.CreateDirectory(devDir);
        if (!TryCreateClaim() && ClaimOwner() != checkout)
        {
            error.WriteLine("Refusing to deploy: another checkout is testing in the dev folder.");
            PrintClaim(error);
            error.WriteLine("Ask whoever holds it to run 'dotnet buildingway dev-plugin release' when they're done.");
            return 1;
        }
        // LF and no BOM, so the earlier bash and PowerShell scripts can read it too
        File.WriteAllText(ClaimInfo, $"checkout={checkout}\n{details}");

        // Plugin dll last, and each file renamed into place, so Dalamud's reload on change
        // never sees a half-copied set or a new dll next to old dependencies
        var dll = pluginName + ".dll";
        foreach (var file in Directory.EnumerateFiles(buildOutput).Where(f => Path.GetFileName(f) != dll))
        {
            CopyInto(file, devDir);
        }
        // The plugin icon, which Dalamud reads from images/icon.png beside the dll
        var images = Path.Combine(buildOutput, "images");
        if (Directory.Exists(images))
        {
            var target = Path.Combine(devDir, "images");
            Directory.CreateDirectory(target);
            foreach (var file in Directory.EnumerateFiles(images))
            {
                CopyInto(file, target);
            }
        }
        CopyInto(Path.Combine(buildOutput, dll), devDir);

        output.WriteLine($"Deployed to {devDir}:");
        PrintClaim(output);
        return 0;
    }

    public void Status()
    {
        if (Directory.Exists(Claim))
        {
            output.WriteLine("Claimed:");
            PrintClaim(output);
        }
        else
        {
            output.WriteLine($"Unclaimed: {devDir}");
        }
    }

    public int Release(bool force)
    {
        if (!Directory.Exists(Claim))
        {
            output.WriteLine("Already unclaimed.");
            return 0;
        }
        if (ClaimOwner() != checkout && !force)
        {
            error.WriteLine("Refusing to release: the claim belongs to another checkout.");
            PrintClaim(error);
            return 1;
        }
        Directory.Delete(Claim, recursive: true);
        output.WriteLine($"Released {devDir}");
        return 0;
    }

    private bool TryCreateClaim()
    {
        // Directory.CreateDirectory succeeds on an existing folder, so it can't tell two deploys
        // apart; mkdir (Unix) and CreateDirectory (Win32) fail when it exists, so of two deploys
        // racing for a free folder exactly one gets the claim.
        if (Directory.Exists(Claim))
        {
            return false;
        }
        try
        {
            // Stryker disable once Conditional : the other platform's call throws DllNotFoundException and falls back to Directory.CreateDirectory, which differs only when two deploys race for the claim
            return OperatingSystem.IsWindows() ? CreateDirectoryWin32(Claim) : Mkdir(Claim, Convert.ToInt32("755", 8)) == 0;
        }
        // Stryker disable Equality,Statement,Boolean,Block : only reached where neither libc's mkdir nor kernel32's CreateDirectoryW exists, and the tests run on Linux and Windows
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException)
        {
            // Not atomic, but only where neither call exists
            Directory.CreateDirectory(Claim);
            return true;
        }
        // Stryker restore Equality,Statement,Boolean,Block
    }

    [DllImport("libc", EntryPoint = "mkdir", SetLastError = true)]
    private static extern int Mkdir([MarshalAs(UnmanagedType.LPUTF8Str)] string path, int mode);

    [DllImport("kernel32", EntryPoint = "CreateDirectoryW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateDirectoryWin32(string path, IntPtr securityAttributes = default);

    private string ClaimOwner()
    {
        if (!File.Exists(ClaimInfo))
        {
            // Stryker disable once String : the owner is only compared with checkout, a full path, which is never "Stryker was here!" or another made-up text
            return "";
        }
        var line = File.ReadLines(ClaimInfo).FirstOrDefault(l => l.StartsWith("checkout=", StringComparison.Ordinal));
        // Stryker disable once String : as above, for a claim without a checkout= line
        return line?[9..] ?? "";
    }

    private void PrintClaim(TextWriter writer)
    {
        if (File.Exists(ClaimInfo))
        {
            foreach (var line in File.ReadLines(ClaimInfo))
            {
                writer.WriteLine("  " + line);
            }
        }
        else
        {
            writer.WriteLine("  (claim has no details; it may be left over from an interrupted deploy)");
        }
    }

    private static void CopyInto(string file, string dir)
    {
        var name = Path.GetFileName(file);
        var tmp = Path.Combine(dir, $".{name}.tmp");
        File.Copy(file, tmp, overwrite: true);
        File.Move(tmp, Path.Combine(dir, name), overwrite: true);
    }
}
