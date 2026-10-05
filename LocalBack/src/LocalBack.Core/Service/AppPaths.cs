namespace LocalBack.Core.Service;

/// <summary>Where LocalBack keeps its own files on the PC.</summary>
public sealed class AppPaths
{
    public string DataDir { get; }
    public string SettingsFile => Path.Combine(DataDir, "settings.json");
    public string IndexFile => Path.Combine(DataDir, "index.db");
    public string TempDir { get; }

    public AppPaths(string? dataDir = null, string? tempDir = null)
    {
        DataDir = dataDir
            ?? Environment.GetEnvironmentVariable("LOCALBACK_HOME")
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LocalBack");
        TempDir = tempDir ?? Path.Combine(Path.GetTempPath(), "LocalBack");
        Directory.CreateDirectory(DataDir);
    }
}
