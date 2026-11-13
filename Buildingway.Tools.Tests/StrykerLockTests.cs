namespace Buildingway.Tools.Tests;

public sealed class StrykerLockTests : IDisposable
{
    private readonly string root = Directory.CreateTempSubdirectory("buildingway-lock").FullName;

    public void Dispose() => Directory.Delete(root, recursive: true);

    private string LockPath => Path.Combine(root, "buildingway-stryker.lock");

    private static readonly TimeSpan Poll = TimeSpan.FromMilliseconds(20);

    [Fact]
    public void ASecondRunWaitsAndNamesTheHolder()
    {
        var waits = new List<string>();
        using var first = StrykerLock.Acquire(LockPath, "pid 1 in /repo/a", waits.Add, Poll, CancellationToken.None);
        Assert.NotNull(first);
        Assert.Empty(waits);

        using var gaveUp = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
        var second = StrykerLock.Acquire(LockPath, "pid 2 in /repo/b", waits.Add, Poll, gaveUp.Token);

        Assert.Null(second);
        // One line however long it waits
        Assert.Equal(["Waiting for another buildingway stryker run to finish (pid 1 in /repo/a)…"], waits);
    }

    [Fact]
    public async Task AWaitingRunGetsTheLockWhenTheHolderFinishes()
    {
        var first = StrykerLock.Acquire(LockPath, "pid 1", _ => { }, Poll, CancellationToken.None)!;
        var waits = new List<string>();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        var second = Task.Run(() => StrykerLock.Acquire(LockPath, "pid 2", waits.Add, Poll, timeout.Token));
        while (waits.Count == 0 && !second.IsCompleted)
        {
            await Task.Delay(10);
        }
        first.Dispose();

        using var got = await second;
        Assert.NotNull(got);
        Assert.Single(waits);
        Assert.Equal("pid 2", File.ReadAllText(LockPath + ".info"));
    }

    [Fact]
    public void TheInfoFileGoesWithTheLock()
    {
        var held = StrykerLock.Acquire(LockPath, "pid 1", _ => { }, Poll, CancellationToken.None)!;
        Assert.Equal("pid 1", File.ReadAllText(LockPath + ".info"));

        held.Dispose();

        Assert.False(File.Exists(LockPath + ".info"));
        // Free again
        using var again = StrykerLock.Acquire(LockPath, "pid 3", _ => throw new InvalidOperationException("waited"), Poll, CancellationToken.None);
        Assert.NotNull(again);
    }

    [Fact]
    public void AnUnknownHolderIsSaidSo()
    {
        using var first = new FileStream(LockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        var waits = new List<string>();
        using var gaveUp = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        Assert.Null(StrykerLock.Acquire(LockPath, "pid 2", waits.Add, Poll, gaveUp.Token));

        Assert.Equal(["Waiting for another buildingway stryker run to finish (holder unknown)…"], waits);
    }

    [Fact]
    public void AnEmptyHolderIsUnknownToo()
    {
        // The holder took the lock but hasn't written its details yet
        using var first = new FileStream(LockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        File.WriteAllText(LockPath + ".info", " \n");
        var waits = new List<string>();
        using var gaveUp = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        Assert.Null(StrykerLock.Acquire(LockPath, "pid 2", waits.Add, Poll, gaveUp.Token));

        Assert.Equal(["Waiting for another buildingway stryker run to finish (holder unknown)…"], waits);
    }

    [Theory]
    [InlineData("/run/user/1000", "/tmp/", "/run/user/1000/buildingway-stryker.lock")]
    [InlineData("", "/tmp/", "/tmp/buildingway-stryker.lock")]
    [InlineData(null, "/tmp/", "/tmp/buildingway-stryker.lock")]
    public void TheLockIsInTheRuntimeDirWhenThereIsOne(string? runtimeDir, string temp, string expected)
    {
        Assert.Equal(expected, StrykerLock.DefaultPath(runtimeDir, temp).Replace('\\', '/'));
    }
}
