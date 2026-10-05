using System.Text.Json;
using System.Text.Json.Serialization;
using LocalBack.Core.Crypto;
using LocalBack.Core.Util;

namespace LocalBack.Core.Storage;

/// <summary>
/// The LocalBack folder in a destination (a drive root, a folder on a drive, or a network share):
/// <code>
/// E:\LocalBack\
///   drive.json            identity of this destination, and the password-wrapped key when encrypted
///   objects\ab\cdef…      one blob per unique file content (encrypted when a key is set)
///   sets\&lt;id&gt;\           set.json, snapshots.jsonl, manifests\*.json.gz (manifests encrypted too)
/// </code>
/// </summary>
public sealed class DriveStore
{
    public const string FolderName = "LocalBack";

    public string DriveRoot { get; }
    public string Root { get; }
    public BlobStore Blobs { get; }
    public string SetsDir => Path.Combine(Root, "sets");
    private string IdentityFile => Path.Combine(Root, "drive.json");

    /// <summary>The data key when this destination is encrypted and unlocked.</summary>
    public byte[]? Key => Blobs.Key;
    public bool IsEncrypted => Identity.Password != null;
    /// <summary>Encrypted, and the key is not known on this PC: the password is needed.</summary>
    public bool IsLocked => IsEncrypted && Key == null;
    /// <summary>A password was added to a destination that already held backups, and some are still plain.</summary>
    public bool IsEncrypting => Identity.Encrypting;

    private DriveStore(string driveRoot)
    {
        DriveRoot = driveRoot;
        Root = Path.Combine(driveRoot, FolderName);
        Blobs = new BlobStore(Path.Combine(Root, "objects"));
    }

    private void LoadKey()
    {
        var id = Identity;
        if (id.Password != null) Blobs.Key = KeyStore.TryGet(id.Id);
    }

    /// <summary>Opens the store on a drive if it has one.</summary>
    public static DriveStore? TryOpen(string driveRoot)
    {
        var store = new DriveStore(driveRoot);
        if (!File.Exists(store.IdentityFile)) return null;
        store.LoadKey();
        return store;
    }

    /// <summary>
    /// Opens the store, creating the folder layout and identity on first use. A <paramref name="password"/> on
    /// first use makes the destination encrypted; it is ignored for an existing store.
    /// </summary>
    public static DriveStore OpenOrCreate(string driveRoot, string? password = null)
    {
        var store = new DriveStore(driveRoot);
        Directory.CreateDirectory(store.SetsDir);
        Directory.CreateDirectory(store.Blobs.Root);
        if (!File.Exists(store.IdentityFile))
        {
            var id = new DriveIdentity { Id = Guid.NewGuid().ToString("N"), Created = DateTimeOffset.UtcNow };
            if (!string.IsNullOrEmpty(password))
            {
                var key = PasswordKey.NewDataKey();
                id.Password = PasswordKey.Wrap(key, password);
                KeyStore.Remember(id.Id, key, persist: true);
            }
            AtomicFile.WriteAllText(store.IdentityFile, JsonSerializer.Serialize(id));
        }
        store.LoadKey();
        return store;
    }

    /// <summary>Checks the password and, if right, keeps the key for this process (and on this PC with <paramref name="remember"/>).</summary>
    public bool Unlock(string password, bool remember = true)
    {
        var id = Identity;
        if (id.Password == null) return true;
        var key = id.Password.Unwrap(password);
        if (key == null) return false;
        KeyStore.Remember(id.Id, key, persist: remember);
        Blobs.Key = key;
        return true;
    }

    /// <summary>
    /// Adds a password to a destination that was used without one. New writes are encrypted at once; the backups
    /// already there are encrypted by <see cref="EncryptPending"/>, which can be interrupted and resumed because
    /// reads accept plain and encrypted files alike until it finishes.
    /// </summary>
    public void Protect(string password)
    {
        if (string.IsNullOrEmpty(password)) throw new ArgumentException("A password is needed.", nameof(password));
        var id = Identity;
        if (id.Password != null) throw new InvalidOperationException("This destination is already encrypted.");
        var key = PasswordKey.NewDataKey();
        id.Password = PasswordKey.Wrap(key, password);
        id.Encrypting = true;
        AtomicFile.WriteAllText(IdentityFile, JsonSerializer.Serialize(id));
        _identity = id;
        KeyStore.Remember(id.Id, key, persist: true);
        Blobs.Key = key;
    }

    /// <summary>
    /// Encrypts whatever <see cref="Protect"/> left plain: every blob and manifest on the destination. Safe to call
    /// again after an interruption; files already encrypted are skipped. Reports (done, total) as it goes.
    /// </summary>
    public void EncryptPending(IProgress<(int Done, int Total)>? progress = null, CancellationToken ct = default)
    {
        if (!IsEncrypting) return;
        var key = Key ?? throw new InvalidOperationException("The destination is locked.");
        var blobs = Blobs.EnumerateAll().Select(b => b.Hash).ToList();
        var manifests = Sets().SelectMany(set => set.ManifestNames().Select(n => Path.Combine(set.ManifestsDir, n))).ToList();
        int total = blobs.Count + manifests.Count, done = 0;
        progress?.Report((0, total));
        foreach (var hash in blobs)
        {
            ct.ThrowIfCancellationRequested();
            Blobs.EncryptInPlace(hash);
            progress?.Report((++done, total));
        }
        foreach (var path in manifests)
        {
            ct.ThrowIfCancellationRequested();
            if (!Manifest.IsEncryptedFile(path)) Manifest.Write(path, Manifest.Read(path, key), key);
            progress?.Report((++done, total));
        }
        var id = Identity;
        id.Encrypting = false;
        AtomicFile.WriteAllText(IdentityFile, JsonSerializer.Serialize(id));
        _identity = id;
    }

    /// <summary>Re-wraps the data key with a new password. Nothing else on the destination changes.</summary>
    public bool ChangePassword(string current, string next)
    {
        var id = Identity;
        if (id.Password == null) throw new InvalidOperationException("This destination is not encrypted.");
        var key = id.Password.Unwrap(current);
        if (key == null) return false;
        id.Password = PasswordKey.Wrap(key, next);
        AtomicFile.WriteAllText(IdentityFile, JsonSerializer.Serialize(id));
        _identity = id;
        KeyStore.Remember(id.Id, key, persist: true);
        Blobs.Key = key;
        return true;
    }

    private DriveIdentity? _identity;

    /// <summary>Read once per instance: the identity never changes after the drive is set up.</summary>
    public DriveIdentity Identity
    {
        get
        {
            if (_identity != null) return _identity;
            try
            {
                _identity = JsonSerializer.Deserialize<DriveIdentity>(File.ReadAllText(IdentityFile)) ?? new DriveIdentity();
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
            {
                return new DriveIdentity();
            }
            return _identity;
        }
    }

    public SetStore Set(string id) => new(SetsDir, id, Key);

    public IEnumerable<SetStore> Sets()
    {
        if (!Directory.Exists(SetsDir)) yield break;
        foreach (var d in Directory.EnumerateDirectories(SetsDir))
            yield return new SetStore(SetsDir, Path.GetFileName(d), Key);
    }

    public (long Free, long Total) Space() => DiskSpace.Get(DriveRoot);
}

public sealed class DriveIdentity
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("created")] public DateTimeOffset Created { get; set; }
    /// <summary>Present when the destination is encrypted.</summary>
    [JsonPropertyName("password")] public PasswordKey? Password { get; set; }
    /// <summary>True while backups written before the password was added are still being encrypted.</summary>
    [JsonPropertyName("encrypting")] public bool Encrypting { get; set; }
}
