using System.Runtime.InteropServices;

namespace Buildingway.Tools;

/// <summary>
/// Runs dotnet stryker with the limits <see cref="Stryker"/> sizes: reads the machine, then puts
/// the run under a memory ceiling covering its test hosts, a systemd user scope on Linux and a
/// Job Object on Windows. Without either, or on CI, it caps the concurrency only.
/// </summary>
internal static class StrykerHost
{
    public static int Run(string[] args)
    {
        var runner = Stryker.RunnerBytes(Environment.GetEnvironmentVariable(Stryker.RunnerVariable));
        // Before reading the available memory, so a run that waited sizes itself to what the other left
        var lockPath = StrykerLock.DefaultPath(OperatingSystem.IsLinux() ? Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR") : null, Path.GetTempPath());
        using var held = StrykerLock.Acquire(
            lockPath,
            $"pid {Environment.ProcessId} in {Environment.CurrentDirectory}, since {DateTimeOffset.Now:HH:mm:ss}",
            Console.Error.WriteLine,
            TimeSpan.FromSeconds(2),
            CancellationToken.None);
        var cores = Environment.ProcessorCount;
        var available = AvailableMemory();
        if (available is null)
        {
            Console.Error.WriteLine("⚠ Couldn't read the available memory; sizing for one runner's worth.");
        }
        var plan = Stryker.Size(args, cores, available ?? 2 * runner, runner);

        string? ceilingBy = null;
        Func<int>? run = null;
        if (Stryker.IsCi(Environment.GetEnvironmentVariable("CI")) || available is null)
        {
            // CI runners are disposable, and without the available memory there's no ceiling to set
        }
        else if (OperatingSystem.IsLinux())
        {
            if (SystemdScope(plan.Ceiling) is { } systemdArgs)
            {
                ceilingBy = "systemd scope";
                var unit = Stryker.ScopeUnit(Environment.ProcessId);
                run = () => RunInScope(systemdArgs(unit, plan.Args), unit);
            }
            else
            {
                Console.Error.WriteLine("⚠ No systemd user session to set a memory ceiling in (systemd-run --user --scope failed); capping the concurrency only.");
            }
        }
        else if (OperatingSystem.IsWindows())
        {
            if (TryJoinMemoryJob(plan.Ceiling, out var failure))
            {
                ceilingBy = "Job Object";
            }
            else
            {
                Console.Error.WriteLine($"⚠ Couldn't set a Job Object memory limit ({failure}); capping the concurrency only.");
            }
        }
        else
        {
            Console.Error.WriteLine("⚠ Memory ceilings need systemd (Linux) or Windows; capping the concurrency only.");
        }

        Console.WriteLine(Stryker.Summary(plan, cores, available ?? 0, runner, ceilingBy));
        if (available is not null && plan.Ceiling < runner)
        {
            Console.Error.WriteLine($"⚠ The ceiling is below one runner's {runner / (1024 * 1024)} MiB; the run may fail out of memory. Free some memory or lower {Stryker.RunnerVariable}.");
        }
        // On Windows this process is in the job, so the dotnet stryker it starts is too
        return run?.Invoke() ?? Shell.RunCode("dotnet", ["stryker", .. plan.Args]);
    }

    /// <summary>
    /// Runs systemd-run and, until it exits, marks the test processes in its scope as the OOM
    /// killer's first pick. Raising a process's oom_score_adj needs no privilege. Test hosts start
    /// once per runner (and again after a kill), so polling marks each long before a mutant runs.
    /// </summary>
    private static int RunInScope(string[] systemdArgs, string unit)
    {
        using var process = Shell.StartChild("systemd-run", systemdArgs);
        var marked = new HashSet<int>();
        while (!process.WaitForExit(200))
        {
            MarkExpendable(process.Id, unit, marked);
        }
        process.WaitForExit();
        return process.ExitCode;
    }

    private static void MarkExpendable(int root, string unit, HashSet<int> marked)
    {
        try
        {
            // systemd-run execs dotnet stryker in its own process, so root's cgroup is the scope
            if (Stryker.ScopeCgroup(File.ReadAllText($"/proc/{root}/cgroup"), unit) is not { } cgroup)
            {
                return;
            }
            foreach (var line in File.ReadAllLines($"/sys/fs/cgroup{cgroup}/cgroup.procs"))
            {
                if (!int.TryParse(line, out var pid) || marked.Contains(pid))
                {
                    continue;
                }
                try
                {
                    var cmdline = File.ReadAllText($"/proc/{pid}/cmdline").Replace('\0', ' ');
                    // Not remembered when kept: between fork and exec a child still shows its parent's command line
                    if (Stryker.IsExpendable(pid, root, cmdline))
                    {
                        File.WriteAllText($"/proc/{pid}/oom_score_adj", Stryker.ExpendableOomScore);
                        marked.Add(pid);
                    }
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    // Gone already
                }
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // The scope is gone or not there yet
        }
    }

    private static long? AvailableMemory()
    {
        if (OperatingSystem.IsLinux())
        {
            try
            {
                return Stryker.MemAvailable(File.ReadAllText("/proc/meminfo"));
            }
            catch (IOException)
            {
                return null;
            }
        }
        if (OperatingSystem.IsWindows())
        {
            var status = new MemoryStatusEx { Length = (uint)Marshal.SizeOf<MemoryStatusEx>() };
            return GlobalMemoryStatusEx(ref status) ? (long)status.AvailPhys : null;
        }
        return null;
    }

    /// <summary>
    /// Checks that a user scope with the ceiling can be made, by making one around `true`, and
    /// returns how to build the real command; null when there's no systemd user session. Tries
    /// OOMPolicy=continue first, which systemd 253 and later need and earlier ones reject.
    /// </summary>
    private static Func<string, string[], string[]>? SystemdScope(long ceiling)
    {
        foreach (var oomPolicy in new[] { true, false })
        {
            var probe = Stryker.SystemdRunArgs(ceiling, oomPolicy, null, []);
            // ... -- dotnet stryker  →  ... -- true
            string[] check = [.. probe[..^2], "true"];
            try
            {
                if (Shell.TryOutput("systemd-run", check).Code == 0)
                {
                    return (unit, strykerArgs) => Stryker.SystemdRunArgs(ceiling, oomPolicy, unit, strykerArgs);
                }
            }
            catch (ToolException)
            {
                // systemd-run isn't installed
                return null;
            }
        }
        return null;
    }

    /// <summary>
    /// Puts this process in a new Job Object whose processes may commit <paramref name="ceiling"/>
    /// bytes between them. Processes it starts afterwards are in the job too, so dotnet stryker and
    /// its test hosts are covered from their start.
    /// </summary>
    private static bool TryJoinMemoryJob(long ceiling, out string failure)
    {
        try
        {
            var job = CreateJobObjectW(IntPtr.Zero, null);
            if (job == IntPtr.Zero)
            {
                failure = $"CreateJobObject error {Marshal.GetLastPInvokeError()}";
                return false;
            }
            var limits = new ExtendedLimitInformation
            {
                BasicLimitInformation = new BasicLimitInformation { LimitFlags = JobObjectLimitJobMemory },
                JobMemoryLimit = (UIntPtr)(ulong)ceiling,
            };
            if (!SetInformationJobObject(job, JobObjectExtendedLimitInformation, ref limits, (uint)Marshal.SizeOf<ExtendedLimitInformation>()))
            {
                failure = $"SetInformationJobObject error {Marshal.GetLastPInvokeError()}";
                return false;
            }
            // The handle stays open for this process's life, which is the job's
            if (!AssignProcessToJobObject(job, GetCurrentProcess()))
            {
                failure = $"AssignProcessToJobObject error {Marshal.GetLastPInvokeError()}";
                return false;
            }
            failure = "";
            return true;
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException)
        {
            failure = e.Message;
            return false;
        }
    }

    private const int JobObjectExtendedLimitInformation = 9;

    private const uint JobObjectLimitJobMemory = 0x200;

    [StructLayout(LayoutKind.Sequential)]
    internal struct BasicLimitInformation
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize;
        public UIntPtr MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct IoCounters
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    /// <summary>JOBOBJECT_EXTENDED_LIMIT_INFORMATION.</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct ExtendedLimitInformation
    {
        public BasicLimitInformation BasicLimitInformation;
        public IoCounters IoInfo;
        public UIntPtr ProcessMemoryLimit;
        public UIntPtr JobMemoryLimit;
        public UIntPtr PeakProcessMemoryUsed;
        public UIntPtr PeakJobMemoryUsed;
    }

    /// <summary>MEMORYSTATUSEX.</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct MemoryStatusEx
    {
        public uint Length;
        public uint MemoryLoad;
        public ulong TotalPhys;
        public ulong AvailPhys;
        public ulong TotalPageFile;
        public ulong AvailPageFile;
        public ulong TotalVirtual;
        public ulong AvailVirtual;
        public ulong AvailExtendedVirtual;
    }

    [DllImport("kernel32", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx buffer);

    [DllImport("kernel32", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateJobObjectW(IntPtr attributes, string? name);

    [DllImport("kernel32", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetInformationJobObject(IntPtr job, int infoClass, ref ExtendedLimitInformation info, uint length);

    [DllImport("kernel32", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);

    [DllImport("kernel32")]
    private static extern IntPtr GetCurrentProcess();
}
