using System.Text.Json;
using LocalBack.Core.Model;
using LocalBack.Core.Util;

namespace LocalBack.Core.Service;

public static class SettingsStore
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public static AppSettings Load(string path)
    {
        try
        {
            if (File.Exists(path))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path), Options) ?? new AppSettings();
        }
        catch (JsonException ex)
        {
            Log.Error("Settings file is damaged; keeping a copy and starting fresh", ex);
            try { File.Copy(path, path + ".damaged", overwrite: true); } catch (IOException) { }
        }
        return new AppSettings();
    }

    public static void Save(string path, AppSettings settings) =>
        AtomicFile.WriteAllText(path, JsonSerializer.Serialize(settings, Options));

    public static AppSettings Clone(AppSettings s) =>
        JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(s, Options), Options)!;

    public static BackupSet Clone(BackupSet s) =>
        JsonSerializer.Deserialize<BackupSet>(JsonSerializer.Serialize(s, Options), Options)!;
}
