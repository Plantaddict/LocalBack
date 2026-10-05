namespace LocalBack.Core.Util;

/// <summary>Tiny append-only log in the data folder; rolls over at 1 MB.</summary>
public static class Log
{
    private static readonly object Gate = new();
    private static string? _file;

    public static void Init(string folder)
    {
        Directory.CreateDirectory(folder);
        _file = Path.Combine(folder, "localback.log");
    }

    public static void Info(string message) => Write("INFO", message);
    public static void Warn(string message) => Write("WARN", message);
    public static void Error(string message, Exception? ex = null) =>
        Write("ERROR", ex == null ? message : $"{message}: {ex.GetType().Name}: {ex.Message}");

    private static void Write(string level, string message)
    {
        var file = _file;
        if (file == null) return;
        lock (Gate)
        {
            try
            {
                var fi = new FileInfo(file);
                if (fi.Exists && fi.Length > 1024 * 1024)
                    File.Move(file, file + ".1", overwrite: true);
                File.AppendAllText(file, $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss} {level} {message}{Environment.NewLine}");
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
