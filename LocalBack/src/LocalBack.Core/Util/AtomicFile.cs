namespace LocalBack.Core.Util;

/// <summary>Write-to-temp-then-rename, so a crash or unplug never leaves a half-written file behind.</summary>
public static class AtomicFile
{
    public static void Write(string path, Action<Stream> write)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tmp = path + ".tmp-" + Guid.NewGuid().ToString("N")[..8];
        try
        {
            using (var fs = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024))
            {
                write(fs);
                fs.Flush(true);
            }
            File.Move(tmp, path, overwrite: true);
        }
        catch
        {
            TryDelete(tmp);
            throw;
        }
    }

    public static void WriteAllText(string path, string text) =>
        Write(path, s => { using var w = new StreamWriter(s, leaveOpen: true); w.Write(text); });

    public static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.SetAttributes(path, FileAttributes.Normal);
                File.Delete(path);
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
