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
        return Key != null ? ChunkedAesGcm.CreateDecryptor(file, Key) : file;
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
