using System.Security.Cryptography;
using LocalBack.Core.Crypto;
using LocalBack.Core.Util;

namespace LocalBack.Core.Storage;

/// <summary>
/// Content-addressed file store: objects\ab\cdef… holds each unique file content once.
/// Plain files only, so it works on FAT32 and exFAT.
/// </summary>
public sealed class BlobStore
{
    public string Root { get; }
    private string TempDir => Path.Combine(Root, "tmp");

    /// <summary>When set, blobs are written encrypted and read decrypted. Hashes are always of the plaintext.</summary>
    public byte[]? Key { get; set; }

    public BlobStore(string root)
    {
        Root = root;
    }

    public string PathFor(string hash) => Path.Combine(Root, hash[..2], hash[2..]);

    public bool Exists(string hash) => File.Exists(PathFor(hash));

    /// <summary>
    /// Copies the rest of <paramref name="source"/> into the store, hashing as it goes.
    /// Returns the content hash. If the blob already exists the copy is discarded.
    /// </summary>
    public string Put(Stream source, CancellationToken ct = default)
    {
        Directory.CreateDirectory(TempDir);
        var tmp = Path.Combine(TempDir, Guid.NewGuid().ToString("N"));
        string hash;
        try
        {
            using (var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
            using (var file = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write, FileShare.None, Hashing.BufferSize, FileOptions.SequentialScan))
            {
                var dst = Key != null ? ChunkedAesGcm.CreateEncryptor(file, Key) : file;
                try
                {
                    var buffer = new byte[Hashing.BufferSize];
                    int read;
                    while ((read = source.Read(buffer, 0, buffer.Length)) > 0)
                    {
                        ct.ThrowIfCancellationRequested();
                        sha.AppendData(buffer, 0, read);
                        dst.Write(buffer, 0, read);
                    }
                }
                finally
                {
                    if (!ReferenceEquals(dst, file)) dst.Dispose(); // writes the final chunk
                }
                file.Flush(true);
                hash = Convert.ToHexString(sha.GetHashAndReset()).ToLowerInvariant();
            }

            var final = PathFor(hash);
            if (File.Exists(final))
            {
                AtomicFile.TryDelete(tmp);
            }
            else
            {
                Directory.CreateDirectory(Path.GetDirectoryName(final)!);
                try
                {
                    File.Move(tmp, final);
                }
                catch (IOException) when (File.Exists(final))
                {
                    AtomicFile.TryDelete(tmp);
                }
            }
            return hash;
        }
        catch
        {
            AtomicFile.TryDelete(tmp);
            throw;
        }
    }

    public Stream OpenRead(string hash)
    {
        var file = new FileStream(PathFor(hash), FileMode.Open, FileAccess.Read, FileShare.Read, Hashing.BufferSize, FileOptions.SequentialScan);
        if (Key == null) return file;
        // A blob written before the password was added is still plain until EncryptInPlace reaches it.
        Span<byte> head = stackalloc byte[4];
        int got = file.Read(head);
        file.Position = 0;
        return got == 4 && ChunkedAesGcm.LooksEncrypted(head) ? ChunkedAesGcm.CreateDecryptor(file, Key) : file;
    }

    /// <summary>Encrypts a blob that was stored plain. Nothing happens when it is already encrypted or gone.</summary>
    public void EncryptInPlace(string hash)
    {
        var key = Key ?? throw new InvalidOperationException("No key.");
        var path = PathFor(hash);
        if (!File.Exists(path) || IsEncryptedFile(path)) return;
        Directory.CreateDirectory(TempDir);
        var tmp = Path.Combine(TempDir, Guid.NewGuid().ToString("N"));
        try
        {
            using (var src = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, Hashing.BufferSize, FileOptions.SequentialScan))
            using (var file = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write, FileShare.None, Hashing.BufferSize, FileOptions.SequentialScan))
            {
                using (var enc = ChunkedAesGcm.CreateEncryptor(file, key)) src.CopyTo(enc, Hashing.BufferSize);
                file.Flush(true);
            }
            File.Move(tmp, path, overwrite: true);
        }
        catch
        {
            AtomicFile.TryDelete(tmp);
            throw;
        }
    }

    internal static bool IsEncryptedFile(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 16, FileOptions.None);
        Span<byte> head = stackalloc byte[4];
        return fs.Read(head) == 4 && ChunkedAesGcm.LooksEncrypted(head);
    }

    public long SizeOf(string hash)
    {
        var fi = new FileInfo(PathFor(hash));
        return fi.Exists ? fi.Length : 0;
    }

    public void Delete(string hash) => AtomicFile.TryDelete(PathFor(hash));

    /// <summary>Every stored blob with its size on disk.</summary>
    public IEnumerable<(string Hash, long Size)> EnumerateAll()
    {
        if (!Directory.Exists(Root)) yield break;
        foreach (var dir in new DirectoryInfo(Root).EnumerateDirectories())
        {
            if (dir.Name.Length != 2) continue;
            foreach (var f in dir.EnumerateFiles())
            {
                var hash = dir.Name + f.Name;
                if (Hashing.IsValid(hash))
                    yield return (hash, f.Length);
            }
        }
    }

    /// <summary>Removes copies left behind by an interrupted run.</summary>
    public void CleanTemp()
    {
        if (!Directory.Exists(TempDir)) return;
        foreach (var f in Directory.EnumerateFiles(TempDir))
            AtomicFile.TryDelete(f);
    }
}
