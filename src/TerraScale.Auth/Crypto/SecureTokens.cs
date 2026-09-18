namespace TerraScale.Auth.Crypto;

/// <summary>Cryptographically secure token/id generation and constant-time comparison.</summary>
public static class SecureTokens
{
    private static readonly char[] Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789".ToCharArray();

    /// <summary>Random URL-safe alphanumeric string.</summary>
    public static string RandomToken(int length)
    {
        var chars = new char[length];
        RandomNumberGenerator.GetItems(Alphabet, chars);
        return new string(chars);
    }

    /// <summary>Random opaque secret with a prefix and an embedded display id, Clerk-style: prefix_id_secret.</summary>
    public static (string Id, string Secret, string Combined) NewSecret(string prefix)
    {
        var id = prefix + "_" + RandomToken(22);
        var secret = RandomToken(40);
        return (id, secret, $"{id}_{secret}");
    }

    /// <summary>SHA-256 hex digest; used to store token secrets at rest.</summary>
    public static string Sha256Hex(string value) => Encodings.HexEncode(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(value)));

    /// <summary>Constant-time equality for secret material.</summary>
    public static bool FixedTimeEquals(string left, string right) =>
        System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(
            System.Text.Encoding.UTF8.GetBytes(left),
            System.Text.Encoding.UTF8.GetBytes(right));
}
