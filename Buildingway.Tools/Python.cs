using System.Reflection;

namespace Buildingway.Tools;

/// <summary>Runs one of ebd's Python tools, embedded in this assembly, with python3.</summary>
internal static class Python
{
    public static int Run(string script, string[] args)
    {
        var path = Extract(script);
        // BUILDINGWAY_PYTHON overrides, e.g. "py" on Windows where python3 may be the Store stub
        var python = Environment.GetEnvironmentVariable("BUILDINGWAY_PYTHON");
        if (string.IsNullOrEmpty(python))
        {
            python = OperatingSystem.IsWindows() ? "python" : "python3";
        }
        return Shell.RunCode(python, [path, .. args]);
    }

    /// <summary>
    /// Writes the script to a per-version folder in the temp directory, once, and returns its path.
    /// Written to a unique name and renamed into place, so parallel runs never read a partial copy.
    /// </summary>
    private static string Extract(string script)
    {
        var assembly = Assembly.GetExecutingAssembly();
        using var resource = assembly.GetManifestResourceStream(script)
            ?? throw new ToolException($"{script} is missing from this build of buildingway-tools.");
        var content = new byte[resource.Length];
        resource.ReadExactly(content);

        var version = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "dev";
        var dir = Path.Combine(Path.GetTempPath(), "buildingway-tools", version);
        var path = Path.Combine(dir, script);
        if (File.Exists(path) && File.ReadAllBytes(path).AsSpan().SequenceEqual(content))
        {
            return path;
        }
        Directory.CreateDirectory(dir);
        var tmp = $"{path}.{Environment.ProcessId}.tmp";
        File.WriteAllBytes(tmp, content);
        File.Move(tmp, path, overwrite: true);
        return path;
    }
}
