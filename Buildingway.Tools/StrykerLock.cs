namespace Buildingway.Tools;

/// <summary>
/// One `buildingway stryker` run per user at a time. Two runs at once each size themselves to
/// half the free memory, so together they could still use it all; a run that waited sizes itself
/// to what the other left. The lock is the file opened without sharing (flock on
/// Unix, a sharing mode on Windows), so it is released when the process ends, however it ends.
/// </summary>
internal sealed class StrykerLock : IDisposable
{
    private readonly FileStream stream;

    private readonly string infoPath;

    private StrykerLock(FileStream stream, string infoPath)
    {
        this.stream = stream;
        this.infoPath = infoPath;
    }

    /// <summary>The lock file: in XDG_RUNTIME_DIR on Linux when set, so runs with different TMPDIRs share it, else in the temp folder.</summary>
    public static string DefaultPath(string? runtimeDir, string tempDir) =>
        Path.Combine(string.IsNullOrEmpty(runtimeDir) ? tempDir : runtimeDir, "buildingway-stryker.lock");

    /// <summary>
    /// Takes the lock, waiting while another run holds it; <paramref name="waiting"/> gets one
    /// line naming the holder when it has to wait. Null when <paramref name="cancel"/> fires first.
    /// </summary>
    /// <param name="holder">What this run writes beside the lock for a waiting run to show.</param>
    public static StrykerLock? Acquire(string path, string holder, Action<string> waiting, TimeSpan poll, CancellationToken cancel)
    {
        var infoPath = path + ".info";
        var told = false;
        while (true)
        {
            try
            {
                var stream = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
                File.WriteAllText(infoPath, holder);
                return new StrykerLock(stream, infoPath);
            }
            catch (IOException)
            {
                if (!told)
                {
                    told = true;
                    waiting($"Waiting for another buildingway stryker run to finish ({Holder(infoPath)})…");
                }
            }
            if (cancel.WaitHandle.WaitOne(poll))
            {
                return null;
            }
        }
    }

    private static string Holder(string infoPath)
    {
        try
        {
            var text = File.ReadAllText(infoPath).Trim();
            return text.Length > 0 ? text : "holder unknown";
        }
        catch (IOException)
        {
            return "holder unknown";
        }
    }

    public void Dispose()
    {
        try
        {
            File.Delete(infoPath);
        }
        catch (IOException)
        {
            // Overwritten by the next run anyway
        }
        stream.Dispose();
    }
}
