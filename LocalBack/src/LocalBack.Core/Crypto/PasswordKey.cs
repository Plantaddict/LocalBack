using System.Security.Cryptography;
using System.Text.Json.Serialization;

namespace LocalBack.Core.Crypto;

/// <summary>
/// How a destination's data key is protected by a password. The data key is random; the password only wraps it,
/// so changing the password re-wraps 32 bytes instead of re-encrypting every file.
/// </summary>
public sealed class PasswordKey
{
    [JsonPropertyName("kdf")] public string Kdf { get; set; } = "pbkdf2-sha256";
    [JsonPropertyName("iter")] public int Iterations { get; set; } = 600_000;
    [JsonPropertyName("salt")] public string Salt { get; set; } = "";
    [JsonPropertyName("nonce")] public string Nonce { get; set; } = "";
    /// <summary>The data key encrypted with the password-derived key, tag appended.</summary>
    [JsonPropertyName("wrapped")] public string Wrapped { get; set; } = "";

    public static byte[] NewDataKey() => RandomNumberGenerator.GetBytes(ChunkedAesGcm.KeySize);

    public static PasswordKey Wrap(byte[] dataKey, string password)
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        var nonce = RandomNumberGenerator.GetBytes(12);
        var pk = new PasswordKey { Salt = Convert.ToBase64String(salt), Nonce = Convert.ToBase64String(nonce) };
        var kek = pk.Derive(password, salt);
        var wrapped = new byte[dataKey.Length + 16];
        using (var aes = new AesGcm(kek, 16))
            aes.Encrypt(nonce, dataKey, wrapped.AsSpan(0, dataKey.Length), wrapped.AsSpan(dataKey.Length), "LocalBack key"u8);
        CryptographicOperations.ZeroMemory(kek);
        pk.Wrapped = Convert.ToBase64String(wrapped);
        return pk;
    }

    /// <summary>The data key, or null when the password is wrong.</summary>
    public byte[]? Unwrap(string password)
    {
        if (Kdf != "pbkdf2-sha256") throw new CryptographicException($"Unknown key derivation {Kdf}; update LocalBack.");
        var salt = Convert.FromBase64String(Salt);
        var nonce = Convert.FromBase64String(Nonce);
        var wrapped = Convert.FromBase64String(Wrapped);
        if (wrapped.Length != ChunkedAesGcm.KeySize + 16) throw new CryptographicException("Damaged key record.");
        var kek = Derive(password, salt);
        try
        {
            var key = new byte[ChunkedAesGcm.KeySize];
            using var aes = new AesGcm(kek, 16);
            aes.Decrypt(nonce, wrapped.AsSpan(0, key.Length), wrapped.AsSpan(key.Length), key, "LocalBack key"u8);
            return key;
        }
        catch (AuthenticationTagMismatchException)
        {
            return null;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(kek);
        }
    }

    private byte[] Derive(string password, byte[] salt) =>
        Rfc2898DeriveBytes.Pbkdf2(password.Normalize(System.Text.NormalizationForm.FormKC), salt, Iterations, HashAlgorithmName.SHA256, ChunkedAesGcm.KeySize);
}
