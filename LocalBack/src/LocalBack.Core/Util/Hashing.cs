using System.Security.Cryptography;

namespace LocalBack.Core.Util;

public static class Hashing
{
    public const int BufferSize = 1024 * 1024;

    /// <summary>SHA-256 of the rest of the stream as lowercase hex.</summary>
    public static string Sha256(Stream stream)
    {
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[BufferSize];
        int read;
        while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
            sha.AppendData(buffer, 0, read);
        return Convert.ToHexString(sha.GetHashAndReset()).ToLowerInvariant();
    }

    public static bool IsValid(string hash) =>
        hash.Length == 64 && hash.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
}
