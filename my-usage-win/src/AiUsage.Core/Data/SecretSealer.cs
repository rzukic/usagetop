using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;

namespace AiUsage.Data;

/// <summary>
/// How a token is made unreadable before it touches the disk. The Windows app and
/// the server want different answers, so <see cref="Credentials"/> is handed one.
/// </summary>
public interface ISecretSealer
{
    string Seal(string value);

    /// <summary>Null when the value cannot be opened: wrong key, wrong user, damage.</summary>
    string? Unseal(string value);
}

/// <summary>
/// DPAPI under the current Windows account, so the file on disk is useless to
/// anything else that can read it -- the counterpart of the phone's
/// EncryptedSharedPreferences.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class DpapiSealer : ISecretSealer
{
    public string Seal(string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        var sealedBytes = ProtectedData.Protect(bytes, null, DataProtectionScope.CurrentUser);
        return Convert.ToBase64String(sealedBytes);
    }

    public string? Unseal(string value)
    {
        try
        {
            var sealedBytes = Convert.FromBase64String(value);
            var bytes = ProtectedData.Unprotect(sealedBytes, null, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(bytes);
        }
        catch
        {
            return null;
        }
    }
}

/// <summary>
/// AES-256-GCM under a key the caller supplies, for machines with no DPAPI. Each
/// value is `nonce | tag | ciphertext`, base64.
///
/// The key is only as safe as wherever it is kept. Handed in through the
/// environment it lives outside the data folder, so a copy of that folder alone
/// gives nothing away; generated into a file beside the data it stops casual
/// reading and nothing more.
/// </summary>
public sealed class AesSealer : ISecretSealer
{
    private const int NonceSize = 12;
    private const int TagSize = 16;

    private readonly byte[] _key;

    public AesSealer(byte[] key)
    {
        if (key.Length != 32) throw new ArgumentException("The key must be 32 bytes", nameof(key));
        _key = key;
    }

    /// <summary>Any passphrase, stretched to a key. Deterministic, so restarts agree.</summary>
    public static AesSealer FromPassphrase(string passphrase) => new(
        Rfc2898DeriveBytes.Pbkdf2(
            Encoding.UTF8.GetBytes(passphrase),
            "ai-usage.credentials.v1"u8.ToArray(),
            200_000,
            HashAlgorithmName.SHA256,
            32));

    public string Seal(string value)
    {
        var plain = Encoding.UTF8.GetBytes(value);
        var nonce = RandomNumberGenerator.GetBytes(NonceSize);
        var cipher = new byte[plain.Length];
        var tag = new byte[TagSize];

        using var aes = new AesGcm(_key, TagSize);
        aes.Encrypt(nonce, plain, cipher, tag);

        var packed = new byte[NonceSize + TagSize + cipher.Length];
        nonce.CopyTo(packed, 0);
        tag.CopyTo(packed, NonceSize);
        cipher.CopyTo(packed, NonceSize + TagSize);
        return Convert.ToBase64String(packed);
    }

    public string? Unseal(string value)
    {
        try
        {
            var packed = Convert.FromBase64String(value);
            if (packed.Length < NonceSize + TagSize) return null;

            var nonce = packed.AsSpan(0, NonceSize);
            var tag = packed.AsSpan(NonceSize, TagSize);
            var cipher = packed.AsSpan(NonceSize + TagSize);
            var plain = new byte[cipher.Length];

            using var aes = new AesGcm(_key, TagSize);
            aes.Decrypt(nonce, cipher, tag, plain);
            return Encoding.UTF8.GetString(plain);
        }
        catch
        {
            return null;
        }
    }
}
