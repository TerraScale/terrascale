using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace TerraScale.Authentication.Security;

public static class Tokens
{
    private const string Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789";

    public static string RandomToken(int length) => RandomNumberGenerator.GetString(Alphabet, length);

    public static string? RandomDigits(int length)
    {
        if (length is < 1 or > 9)
        {
            return null;
        }

        var upper = (int)Math.Pow(10, length);
        return RandomNumberGenerator.GetInt32(upper).ToString($"D{length}", System.Globalization.CultureInfo.InvariantCulture);
    }

    public static (string Id, string Secret, string Combined) NewSecret(string prefix)
    {
        var id = $"{prefix}_{RandomToken(22)}";
        var secret = RandomToken(40);
        return (id, secret, $"{id}_{secret}");
    }

    public static string Sha256Hex(string value) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    public static bool FixedTimeEquals(string left, string right)
    {
        ArgumentNullException.ThrowIfNull(left);
        ArgumentNullException.ThrowIfNull(right);
        return CryptographicOperations.FixedTimeEquals(
            MemoryMarshal.AsBytes(left.AsSpan()),
            MemoryMarshal.AsBytes(right.AsSpan()));
    }
}
