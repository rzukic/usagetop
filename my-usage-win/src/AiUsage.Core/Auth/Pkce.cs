using System.Security.Cryptography;
using System.Text;

namespace AiUsage.Auth;

/// <summary>PKCE helpers for the authorization-code flow.</summary>
public static class Pkce
{
    /// <summary>43-char base64url secret, kept until the token exchange.</summary>
    public static string Verifier() => RandomToken();

    /// <summary>S256 challenge: base64url(SHA-256(verifier)).</summary>
    public static string Challenge(string verifier)
    {
        var digest = SHA256.HashData(Encoding.ASCII.GetBytes(verifier));
        return Base64Url(digest);
    }

    /// <summary>CSRF token echoed back on the callback and compared there.</summary>
    public static string State() => RandomToken();

    private static string RandomToken() => Base64Url(RandomNumberGenerator.GetBytes(32));

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
