using System.Runtime.InteropServices;

namespace Buildingway.Tools.Tests;

public sealed class StrykerTests
{
    private const long MiB = 1024 * 1024;

    private const long GiB = 1024 * MiB;

    [Fact]
    public void TheCoresLimitWhenMemoryIsPlentiful()
    {
        var plan = Stryker.Size([], cores: 12, availableBytes: 64 * GiB, runnerBytes: 768 * MiB);

        Assert.Equal(6, plan.Concurrency);
        Assert.Equal(32 * GiB, plan.Ceiling);
        Assert.Equal(["--concurrency", "6"], plan.Args);
    }

    [Fact]
    public void HalfTheAvailableMemoryLimitsWhenItIsScarce()
    {
        // The 2026-11-13 freeze: 12 cores, about 4.7 GiB available; 2.35 GiB / 768 MiB = 3
        var plan = Stryker.Size(["--mutate", "**/Tokeniser.cs"], cores: 12, availableBytes: 4813 * MiB, runnerBytes: 768 * MiB);

        Assert.Equal(3, plan.Concurrency);
        Assert.Equal(4813 * MiB / 2, plan.Ceiling);
        Assert.Equal(["--mutate", "**/Tokeniser.cs", "--concurrency", "3"], plan.Args);
    }

    [Fact]
    public void ARunnerFitsExactlyAtTheBoundary()
    {
        Assert.Equal(2, Stryker.Size([], 12, 4 * 768 * MiB, 768 * MiB).Concurrency);
        Assert.Equal(1, Stryker.Size([], 12, 4 * 768 * MiB - 1, 768 * MiB).Concurrency);
    }

    [Fact]
    public void OneCoreStillGetsOneRunner()
    {
        // cores / 2 is 0
        Assert.Equal(1, Stryker.Size([], cores: 1, availableBytes: 64 * GiB, runnerBytes: 768 * MiB).Concurrency);
        Assert.Equal(1, Stryker.Size([], cores: 3, availableBytes: 64 * GiB, runnerBytes: 768 * MiB).Concurrency);
    }

    [Fact]
    public void LowMemoryStillGetsOneRunner()
    {
        var plan = Stryker.Size([], cores: 12, availableBytes: 600 * MiB, runnerBytes: 768 * MiB);

        Assert.Equal(1, plan.Concurrency);
        Assert.Equal(300 * MiB, plan.Ceiling);
    }

    [Fact]
    public void NoMemoryAtAllStillGetsOneRunner()
    {
        Assert.Equal(1, Stryker.Size([], 12, 0, 768 * MiB).Concurrency);
    }

    [Theory]
    [InlineData("--concurrency", "8")]
    [InlineData("-c", "8")]
    [InlineData("--concurrency=8")]
    [InlineData("--concurrency:8")]
    [InlineData("-c=8")]
    [InlineData("-c:8")]
    public void AGivenConcurrencyIsLeftAlone(params string[] given)
    {
        string[] args = ["--mutate", "**/A.cs", .. given];

        var plan = Stryker.Size(args, cores: 12, availableBytes: 4 * GiB, runnerBytes: 768 * MiB);

        Assert.Null(plan.Concurrency);
        Assert.Equal(args, plan.Args);
        // The ceiling still applies
        Assert.Equal(2 * GiB, plan.Ceiling);
    }

    [Theory]
    [InlineData("--config-file")]
    [InlineData("-cf")]
    [InlineData("--concurrent")]
    [InlineData("c")]
    public void LookalikeArgumentsAreNotAConcurrency(string arg)
    {
        Assert.False(Stryker.GivesConcurrency([arg, "8"]));
    }

    [Fact]
    public void RunnerSizeDefaultsAndCanBeOverridden()
    {
        Assert.Equal(Stryker.DefaultRunnerMiB * MiB, Stryker.RunnerBytes(null));
        Assert.Equal(Stryker.DefaultRunnerMiB * MiB, Stryker.RunnerBytes(""));
        Assert.Equal(1536 * MiB, Stryker.RunnerBytes("1536"));
        Assert.Equal(MiB, Stryker.RunnerBytes("1"));
        // The largest size whose bytes fit in a long
        Assert.Equal(long.MaxValue / MiB * MiB, Stryker.RunnerBytes((long.MaxValue / MiB).ToString()));
        Assert.Throws<ToolException>(() => Stryker.RunnerBytes((long.MaxValue / MiB + 1).ToString()));
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-5")]
    [InlineData("1.5")]
    [InlineData("1.5G")]
    [InlineData(" 512")]
    [InlineData("99999999999999999")]
    public void ABadRunnerSizeIsRefused(string value)
    {
        var e = Assert.Throws<ToolException>(() => Stryker.RunnerBytes(value));

        Assert.Equal(2, e.ExitCode);
        Assert.Contains(Stryker.RunnerVariable, e.Message);
    }

    [Theory]
    [InlineData("true", true)]
    [InlineData("True", true)]
    [InlineData("TRUE", true)]
    [InlineData("false", false)]
    [InlineData("1", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void CiIsCiEqualsTrue(string? value, bool ci)
    {
        Assert.Equal(ci, Stryker.IsCi(value));
    }

    [Fact]
    public void MemAvailableIsReadFromMeminfo()
    {
        const string meminfo = """
            MemTotal:       16316308 kB
            MemFree:          512000 kB
            MemAvailable:    4921244 kB
            Buffers:          123456 kB
            """;

        Assert.Equal(4921244L * 1024, Stryker.MemAvailable(meminfo));
    }

    [Theory]
    [InlineData("MemTotal:       16316308 kB\nMemFree:          512000 kB\n")]
    [InlineData("MemAvailable:    lots kB\n")]
    [InlineData("MemAvailable:    4921244 MB\n")]
    [InlineData("")]
    public void MemAvailableIsNullWhenMissingOrUnreadable(string meminfo)
    {
        Assert.Null(Stryker.MemAvailable(meminfo));
    }

    [Fact]
    public void SystemdRunPutsStrykerInACappedScopeWithoutSwap()
    {
        Assert.Equal(
            ["--user", "--scope", "--quiet", "--unit", "buildingway-stryker-42", "-p", "MemoryMax=2147483648", "-p", "MemorySwapMax=0", "-p", "OOMPolicy=continue", "--", "dotnet", "stryker", "--concurrency", "3"],
            Stryker.SystemdRunArgs(2 * GiB, oomPolicy: true, Stryker.ScopeUnit(42), ["--concurrency", "3"]));
        Assert.Equal(
            ["--user", "--scope", "--quiet", "-p", "MemoryMax=2147483648", "-p", "MemorySwapMax=0", "--", "dotnet", "stryker"],
            Stryker.SystemdRunArgs(2 * GiB, oomPolicy: false, null, []));
    }

    [Fact]
    public void TheScopeCgroupIsFoundOnceTheProcessIsInIt()
    {
        const string unit = "buildingway-stryker-42";

        Assert.Equal(
            "/user.slice/user-1000.slice/user@1000.service/app.slice/buildingway-stryker-42.scope",
            Stryker.ScopeCgroup("0::/user.slice/user-1000.slice/user@1000.service/app.slice/buildingway-stryker-42.scope\n", unit));
        // Still in the caller's cgroup, just after starting: never touch those processes
        Assert.Null(Stryker.ScopeCgroup("0::/user.slice/user-1000.slice/user@1000.service/app.slice/app-claude.scope\n", unit));
        // Another run's scope whose pid starts the same
        Assert.Null(Stryker.ScopeCgroup("0::/user.slice/app.slice/buildingway-stryker-421.scope\n", unit));
        Assert.Null(Stryker.ScopeCgroup("0::/user.slice/app.slice/xbuildingway-stryker-42.scope\n", unit));
        // cgroup v1 lines only
        Assert.Null(Stryker.ScopeCgroup("12:memory:/user.slice/app.slice/buildingway-stryker-42.scope\n", unit));
        Assert.Equal("/a/buildingway-stryker-42.scope", Stryker.ScopeCgroup("12:memory:/x\n0::/a/buildingway-stryker-42.scope", unit));
    }

    [Fact]
    public void TestProcessesAreTheOomKillersPickNotStryker()
    {
        // The processes in a jaunt run's scope, 2026-11-13
        Assert.False(Stryker.IsExpendable(205862, root: 205862, "/usr/bin/dotnet stryker --concurrency 1 "));
        Assert.False(Stryker.IsExpendable(205875, root: 205862, "/usr/lib/dotnet/dotnet /home/u/.nuget/packages/dotnet-stryker/5.0.0/tools/net10.0/any/Stryker.CLI.dll --concurrency 1 "));
        Assert.True(Stryker.IsExpendable(206044, root: 205862, "/usr/bin/dotnet /tmp/j/.vstest/tools/net8.0/vstest.console.dll /parentprocessid:205875 /port:33349 "));
        Assert.True(Stryker.IsExpendable(207785, root: 205862, "/usr/lib/dotnet/dotnet exec --runtimeconfig /repo/Jaunt.Core.Tests/bin/Debug/net10.0/Jaunt.Core.Tests.runtimeconfig.json "));
        Assert.Equal("1000", Stryker.ExpendableOomScore);
    }

    [Fact]
    public void TheSummaryNamesConcurrencyAndCeiling()
    {
        var plan = Stryker.Size([], 12, 4813 * MiB, 768 * MiB);

        Assert.Equal(
            "Stryker: concurrency 3 (12 cores, 4813 MiB available, 768 MiB per runner); memory ceiling 2406 MiB (systemd scope)",
            Stryker.Summary(plan, 12, 4813 * MiB, 768 * MiB, "systemd scope"));
        Assert.Equal(
            "Stryker: concurrency as given; no memory ceiling",
            Stryker.Summary(Stryker.Size(["-c", "2"], 12, 4813 * MiB, 768 * MiB), 12, 4813 * MiB, 768 * MiB, null));
    }

    [Fact]
    public void JobObjectStructsHaveTheWin64Layout()
    {
        // JOBOBJECT_EXTENDED_LIMIT_INFORMATION is 144 bytes on x64 Windows and MEMORYSTATUSEX 64;
        // a wrong size makes SetInformationJobObject fail with ERROR_BAD_LENGTH
        Assert.Equal(144, Marshal.SizeOf<StrykerHost.ExtendedLimitInformation>());
        Assert.Equal(120, Marshal.OffsetOf<StrykerHost.ExtendedLimitInformation>(nameof(StrykerHost.ExtendedLimitInformation.JobMemoryLimit)).ToInt32());
        Assert.Equal(64, Marshal.SizeOf<StrykerHost.MemoryStatusEx>());
    }
}
