using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text.Json;
using LocalBack.Core.Util;

namespace LocalBack.Core.Crypto;

/// <summary>
/// Data keys of unlocked destinations, by destination id. Kept in memory for this process and, so backups run
/// unattended after a restart, saved to keys.json in the data folder: on Windows protected with DPAPI for the
/// signed-in user, elsewhere (development only) base64. The password itself is never stored.
/// </summary>
public static class KeyStore
{
    private static readonly object Gate = new();
    private static readonly Dictionary<string, byte[]> Keys = new();
    private static string? _file;

    /// <summary>Loads saved keys from <paramref name="dataDir"/> and makes it the place new keys are saved to.</summary>
    public static void Init(string dataDir)
    {
        lock (Gate)
        {
            _file = Path.Combine(dataDir, "keys.json");
            Keys.Clear();
            try
            {
                if (!File.Exists(_file)) return;
                var map = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(_file)) ?? new();
                foreach (var (id, value) in map)
                {
                    try { Keys[id] = Unprotect(Convert.FromBase64String(value)); }
                    catch (Exception ex) when (ex is CryptographicException or FormatException)
                    {
                        Log.Warn($"Saved key for destination {id} cannot be read on this PC; the password will be asked for.");
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
            {
                Log.Warn($"Could not read saved keys: {ex.Message}");
            }
        }
    }

    public static byte[]? TryGet(string driveId)
    {
        lock (Gate) return Keys.TryGetValue(driveId, out var k) ? k : null;
    }

    /// <summary>Keeps the key for this process and, with <paramref name="persist"/>, for future runs on this PC.</summary>
    public static void Remember(string driveId, byte[] key, bool persist)
    {
        lock (Gate)
        {
            Keys[driveId] = key;
            if (persist) Save();
        }
    }

    public static void Forget(string driveId)
    {
        lock (Gate)
        {
            if (Keys.Remove(driveId)) Save();
        }
    }

    /// <summary>Only for tests: drops every key of this process.</summary>
    public static void ClearForTests()
    {
        lock (Gate)
        {
            Keys.Clear();
            _file = null;
        }
    }

    private static void Save()
    {
        if (_file == null) return;
        try
        {
            var map = Keys.ToDictionary(kv => kv.Key, kv => Convert.ToBase64String(Protect(kv.Value)));
            AtomicFile.WriteAllText(_file, JsonSerializer.Serialize(map));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CryptographicException)
        {
            Log.Warn($"Could not save keys: {ex.Message}");
        }
    }

    private static byte[] Protect(byte[] key) => OperatingSystem.IsWindows() ? Dpapi.Protect(key) : key;
    private static byte[] Unprotect(byte[] blob) => OperatingSystem.IsWindows() ? Dpapi.Unprotect(blob) : blob;

    private static class Dpapi
    {
        private static readonly byte[] Entropy = "LocalBack destination key"u8.ToArray();

        [SupportedOSPlatform("windows")]
        public static byte[] Protect(byte[] data) => ProtectedData.Protect(data, Entropy, DataProtectionScope.CurrentUser);

        [SupportedOSPlatform("windows")]
        public static byte[] Unprotect(byte[] data) => ProtectedData.Unprotect(data, Entropy, DataProtectionScope.CurrentUser);
    }
}
