namespace LocalBack.Core.Service;

/// <summary>
/// One process at a time may run the engine against a data folder: the tray app holds this for its lifetime,
/// and command-line operations that write take it for their run. An exclusively opened file, so it is
/// released by the OS when the process ends, however it ends.
/// </summary>
public sealed class EngineLock : IDisposable
{
    private readonly FileStream _file;

    private EngineLock(FileStream file) => _file = file;

    public static string PathFor(string dataDir) => Path.Combine(dataDir, "engine.lock");

    /// <summary>Returns null when another process holds the lock.</summary>
    public static EngineLock? TryAcquire(string dataDir)
    {
        Directory.CreateDirectory(dataDir);
        try
        {
            var fs = new FileStream(PathFor(dataDir), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.None);
            try
            {
                fs.SetLength(0);
                var bytes = System.Text.Encoding.UTF8.GetBytes(Environment.ProcessId.ToString());
                fs.Write(bytes, 0, bytes.Length);
                fs.Flush();
            }
            catch (IOException) { }
            return new EngineLock(fs);
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Waits up to <paramref name="timeout"/> for the lock.</summary>
    public static EngineLock? Acquire(string dataDir, TimeSpan timeout)
    {
        var until = DateTime.UtcNow + timeout;
        while (true)
        {
            var l = TryAcquire(dataDir);
            if (l != null || DateTime.UtcNow >= until) return l;
            Thread.Sleep(250);
        }
    }

    public void Dispose() => _file.Dispose();
}
