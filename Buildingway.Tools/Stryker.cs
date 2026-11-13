using System.Globalization;

namespace Buildingway.Tools;

/// <summary>
/// Sizes a Stryker run to the machine: how many test runners at once, and the memory ceiling the
/// whole run (Stryker and its test hosts) gets. A mutant that loops while allocating, such as one
/// that removes a loop's increment, otherwise allocates in every runner until the machine stops
/// responding. Pure, so the sizing is tested without systemd or a Windows job; StrykerHost reads
/// the machine and launches the run.
/// </summary>
internal static class Stryker
{
    /// <summary>Overrides <see cref="DefaultRunnerMiB"/>, in MiB.</summary>
    public const string RunnerVariable = "BUILDINGWAY_STRYKER_RUNNER_MB";

    /// <summary>
    /// Memory to allow per test runner. Measured on Jaunt.Core.Tests (Stryker 5.0.0, 408 tests,
    /// 2026-11-13): one runner peaked at 606 MiB for the whole run, four at 1562 MiB, so about
    /// 320 MiB per extra runner (vstest.console about 300 MiB and the test host about 130 MiB at
    /// their peaks). 768 MiB leaves room for bigger test projects.
    /// </summary>
    public const long DefaultRunnerMiB = 768;

    private const long MiB = 1024 * 1024;

    /// <param name="Args">The arguments to pass to dotnet stryker.</param>
    /// <param name="Concurrency">The concurrency added to the arguments, or null when they gave one.</param>
    /// <param name="Ceiling">The memory ceiling in bytes: half the memory available at the start.</param>
    internal sealed record Plan(string[] Args, int? Concurrency, long Ceiling);

    /// <summary>
    /// Adds --concurrency N unless the arguments give one, where N = max(1, min(cores / 2,
    /// ceiling / per-runner)) and the ceiling is half the available memory.
    /// </summary>
    public static Plan Size(string[] args, int cores, long availableBytes, long runnerBytes)
    {
        var ceiling = availableBytes / 2;
        if (GivesConcurrency(args))
        {
            return new Plan(args, null, ceiling);
        }
        var concurrency = (int)Math.Max(1, Math.Min(cores / 2, ceiling / runnerBytes));
        return new Plan([.. args, "--concurrency", concurrency.ToString(CultureInfo.InvariantCulture)], concurrency, ceiling);
    }

    /// <summary>True when the arguments set Stryker's concurrency (-c, --concurrency, with or without a value attached).</summary>
    public static bool GivesConcurrency(string[] args) => args.Any(a =>
        a is "-c" or "--concurrency"
        || a.StartsWith("--concurrency=", StringComparison.Ordinal)
        || a.StartsWith("--concurrency:", StringComparison.Ordinal)
        || a.StartsWith("-c=", StringComparison.Ordinal)
        || a.StartsWith("-c:", StringComparison.Ordinal));

    /// <summary>Bytes per runner: <see cref="RunnerVariable"/> in MiB when set, else the default.</summary>
    public static long RunnerBytes(string? variable)
    {
        if (string.IsNullOrEmpty(variable))
        {
            return DefaultRunnerMiB * MiB;
        }
        return long.TryParse(variable, NumberStyles.None, CultureInfo.InvariantCulture, out var mib) && mib is > 0 and <= long.MaxValue / MiB
            ? mib * MiB
            : throw new ToolException($"{RunnerVariable} must be a whole number of MiB above 0, not \"{variable}\".", 2);
    }

    /// <summary>CI providers set CI=true; there the runner is disposable, so only the concurrency is capped.</summary>
    public static bool IsCi(string? variable) => string.Equals(variable, "true", StringComparison.OrdinalIgnoreCase);

    /// <summary>MemAvailable from /proc/meminfo, in bytes, or null when it isn't there.</summary>
    public static long? MemAvailable(string meminfo)
    {
        foreach (var line in meminfo.Split('\n'))
        {
            // "MemAvailable:    4921244 kB"
            var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts is ["MemAvailable:", var kb, "kB"] && long.TryParse(kb, NumberStyles.None, CultureInfo.InvariantCulture, out var value))
            {
                return value * 1024;
            }
        }
        return null;
    }

    /// <summary>
    /// The systemd-run arguments that run dotnet stryker in a transient user scope: a cgroup with
    /// the ceiling and no swap, which the test hosts Stryker starts inherit. The kernel's OOM
    /// killer then ends the biggest process in it (the runaway test host) instead of the machine
    /// thrashing. <paramref name="oomPolicy"/> keeps the scope running when that happens; systemd
    /// 253 and later otherwise stop the whole scope, and earlier ones don't accept it on a scope.
    /// </summary>
    /// <param name="unit">The scope's name, so the run's processes can be found in its cgroup; systemd picks one when null.</param>
    public static string[] SystemdRunArgs(long ceiling, bool oomPolicy, string? unit, string[] strykerArgs) =>
    [
        "--user", "--scope", "--quiet",
        .. unit is null ? [] : (string[])["--unit", unit],
        "-p", $"MemoryMax={ceiling.ToString(CultureInfo.InvariantCulture)}",
        "-p", "MemorySwapMax=0",
        .. oomPolicy ? (string[])["-p", "OOMPolicy=continue"] : [],
        "--", "dotnet", "stryker", .. strykerArgs,
    ];

    /// <summary>The scope a run started by process <paramref name="pid"/> runs in.</summary>
    public static string ScopeUnit(int pid) => $"buildingway-stryker-{pid.ToString(CultureInfo.InvariantCulture)}";

    /// <summary>
    /// The cgroup directory under /sys/fs/cgroup from a /proc/PID/cgroup file, when the process is
    /// in <paramref name="unit"/>'s scope yet (systemd moves it there just after it starts); else null.
    /// </summary>
    public static string? ScopeCgroup(string procCgroup, string unit)
    {
        // cgroup v2: "0::/user.slice/.../app.slice/buildingway-stryker-123.scope"
        var line = procCgroup.Split('\n').FirstOrDefault(l => l.StartsWith("0::/", StringComparison.Ordinal));
        return line is not null && line.EndsWith($"/{unit}.scope", StringComparison.Ordinal) ? line[3..] : null;
    }

    /// <summary>
    /// The OOM killer's first pick in the scope: every process but the dotnet stryker launcher
    /// (<paramref name="root"/>) and the Stryker host it starts. Left alone, the killer picks the
    /// biggest process, and when two runaway test hosts overflow the ceiling at once that can be
    /// Stryker itself, ending the run without a report (seen on jaunt, 2026-11-13).
    /// </summary>
    public static bool IsExpendable(int pid, int root, string cmdline) =>
        pid != root && !cmdline.Contains("Stryker.CLI", StringComparison.Ordinal);

    /// <summary>The oom_score_adj given to expendable processes: always killed before one at 0.</summary>
    public const string ExpendableOomScore = "1000";

    /// <summary>The one line saying what the run gets.</summary>
    /// <param name="ceilingBy">What enforces the ceiling, or null when nothing does.</param>
    public static string Summary(Plan plan, int cores, long availableBytes, long runnerBytes, string? ceilingBy)
    {
        var concurrency = plan.Concurrency is { } n
            ? $"concurrency {n} ({cores} cores, {MiBs(availableBytes)} available, {MiBs(runnerBytes)} per runner)"
            : "concurrency as given";
        var ceiling = ceilingBy is null ? "no memory ceiling" : $"memory ceiling {MiBs(plan.Ceiling)} ({ceilingBy})";
        return $"Stryker: {concurrency}; {ceiling}";
    }

    private static string MiBs(long bytes) => $"{(bytes / MiB).ToString(CultureInfo.InvariantCulture)} MiB";
}
