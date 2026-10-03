using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace TerraScale.Auth.Security;

/// <summary>Random tokens, digests and constant-time comparison (port of <c>auth-security/src/tokens.rs</c>).</summary>
public static class Tokens
{
    private const string Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789";

    /// <summary>A URL-safe alphanumeric token drawn from the OS CSPRNG.</summary>
    public static string RandomToken(int length) => RandomNumberGenerator.GetString(Alphabet, length);

    /// <summary>Fresh random bytes from the OS CSPRNG.</summary>
    public static byte[] RandomBytes(int length) => RandomNumberGenerator.GetBytes(length);

    /// <summary>A zero-padded numeric code of 1–9 digits, or <see langword="null"/> for other lengths.</summary>
    public static string? RandomDigits(int length)
    {
        if (length is < 1 or > 9)
        {
            return null;
        }

        var upper = (int)Math.Pow(10, length);
        return RandomNumberGenerator.GetInt32(upper).ToString($"D{length}", System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>A prefixed identifier, its secret, and the combined bearer value (<c>{prefix}_{id}_{secret}</c>).</summary>
    public static (string Id, string Secret, string Combined) NewSecret(string prefix)
    {
        var id = $"{prefix}_{RandomToken(22)}";
        var secret = RandomToken(40);
        return (id, secret, $"{id}_{secret}");
    }

    /// <summary>Lowercase hex SHA-256 of the UTF-8 value.</summary>
    public static string Sha256Hex(string value) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    /// <summary>
    /// Compares two strings without leaking the position of the first difference. The raw
    /// UTF-16 code units are compared, so malformed strings (an unpaired surrogate versus
    /// U+FFFD, say) never collapse to the same bytes through encoding replacement.
    /// </summary>
    public static bool FixedTimeEquals(string left, string right)
    {
        ArgumentNullException.ThrowIfNull(left);
        ArgumentNullException.ThrowIfNull(right);
        return CryptographicOperations.FixedTimeEquals(
            MemoryMarshal.AsBytes(left.AsSpan()),
            MemoryMarshal.AsBytes(right.AsSpan()));
    }

    /// <summary>HMAC-SHA256 of the message.</summary>
    public static byte[] HmacSha256(byte[] key, byte[] message) => HMACSHA256.HashData(key, message);

    /// <summary>Verifies an HMAC-SHA256 signature in constant time.</summary>
    public static bool VerifyHmacSha256(byte[] key, byte[] message, byte[] signature) =>
        CryptographicOperations.FixedTimeEquals(HmacSha256(key, message), signature);
}
