using System.Diagnostics;

namespace Buildingway.Tools;

/// <summary>An error to report to the user and exit with, without a stack trace.</summary>
internal sealed class ToolException(string message, int exitCode = 1) : Exception(message)
{
    public int ExitCode { get; } = exitCode;
}

internal static class Shell
{
    /// <summary>Runs a command with the console's output, failing when it exits non-zero.</summary>
    public static void Run(string file, params string[] args)
    {
        var code = RunCode(file, args);
        if (code != 0)
        {
            throw new ToolException($"{file} {string.Join(' ', args)} exited with {code}", code);
        }
    }

    /// <summary>Runs a command with the console's output and returns its exit code.</summary>
    public static int RunCode(string file, params string[] args)
    {
        using var process = Start(file, args, capture: false);
        process.WaitForExit();
        return process.ExitCode;
    }

    /// <summary>Starts a command with the console's output, for a caller that watches it while it runs.</summary>
    public static Process StartChild(string file, params string[] args) => Start(file, args, capture: false);

    /// <summary>Runs a command and returns its trimmed output, failing when it exits non-zero.</summary>
    public static string Output(string file, params string[] args)
    {
        var (code, output) = TryOutput(file, args);
        if (code != 0)
        {
            throw new ToolException($"{file} {string.Join(' ', args)} exited with {code}", code);
        }
        return output;
    }

    /// <summary>Runs a command and returns its exit code and trimmed output; stderr is discarded.</summary>
    public static (int Code, string Output) TryOutput(string file, params string[] args)
    {
        using var process = Start(file, args, capture: true);
        var stderr = process.StandardError.ReadToEndAsync();
        var output = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        _ = stderr.Result;
        return (process.ExitCode, output.TrimEnd('\r', '\n'));
    }

    private static Process Start(string file, string[] args, bool capture)
    {
        var info = new ProcessStartInfo(file)
        {
            UseShellExecute = false,
            RedirectStandardOutput = capture,
            RedirectStandardError = capture,
        };
        foreach (var arg in args)
        {
            info.ArgumentList.Add(arg);
        }
        try
        {
            return Process.Start(info) ?? throw new ToolException($"Couldn't start {file}");
        }
        catch (System.ComponentModel.Win32Exception e)
        {
            throw new ToolException($"Couldn't start {file}: {e.Message}");
        }
    }
}

/// <summary>Git commands run in one checkout.</summary>
internal sealed class Git(string top)
{
    public string Top { get; } = top;

    public static Git FromCurrentDirectory()
    {
        var (code, top) = Shell.TryOutput("git", "rev-parse", "--show-toplevel");
        return code == 0 ? new Git(top) : throw new ToolException("Run this inside the plugin's git checkout.");
    }

    public string Output(params string[] args) => Shell.Output("git", ["-C", Top, .. args]);

    public (int Code, string Output) TryOutput(params string[] args) => Shell.TryOutput("git", ["-C", Top, .. args]);

    /// <summary>True when the command exits 0.</summary>
    public bool Succeeds(params string[] args) => TryOutput(args).Code == 0;

    public void Run(params string[] args) => Shell.Run("git", ["-C", Top, .. args]);

    public bool HasRef(string reference) => Succeeds("rev-parse", "-q", "--verify", reference);

    public bool IsDirty() => Output("status", "--porcelain").Length > 0;
}
