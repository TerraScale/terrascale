using System.Globalization;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using BCryptNet = BCrypt.Net.BCrypt;

namespace TerraScale.Authentication.Security;

public static partial class ImportedPasswords
{
    public static bool Valid(string? hash)
    {
        if (hash is not { Length: > 0 and <= 512 })
        {
            return false;
        }

        if (Bcrypt().IsMatch(hash))
        {
            return true;
        }

        if (Pbkdf2().Match(hash) is { Success: true } pbkdf2)
        {
            return Number(pbkdf2, "i", 10_000, 1_000_000, out _)
                && Base64(pbkdf2.Groups["s"].Value, 16, 64, padded: true)
                && Base64(pbkdf2.Groups["h"].Value, 32, 32, padded: true);
        }

        return Argon().Match(hash) is { Success: true } argon
            && Number(argon, "m", 8, 65_536, out var memory)
            && Number(argon, "t", 1, 10, out var passes)
            && Number(argon, "p", 1, 8, out var lanes) && memory >= 8 * lanes && (long)memory * passes <= 196_608
            && Base64(argon.Groups["s"].Value, 16, 16, padded: false)
            && Base64(argon.Groups["h"].Value, 32, 32, padded: false);
    }

    public static bool NeedsUpgrade(string hash) =>
        !hash.StartsWith("$argon2id$v=19$m=19456,t=2,p=1$", StringComparison.Ordinal);

    public static bool Verify(string password, string hash)
    {
        if (!Valid(hash))
        {
            return false;
        }

        if (hash.StartsWith("$2", StringComparison.Ordinal))
        {
            return BCryptNet.Verify(password, hash);
        }

        if (Pbkdf2().Match(hash) is { Success: true } match)
        {
            _ = Number(match, "i", 10_000, 1_000_000, out var iterations);
            var salt = Convert.FromBase64String(match.Groups["s"].Value);
            var expected = Convert.FromBase64String(match.Groups["h"].Value);
            var actual = Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations, HashAlgorithmName.SHA256, 32);
            try
            {
                return CryptographicOperations.FixedTimeEquals(actual, expected);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(actual);
            }
        }

        return Passwords.Verify(password, hash);
    }

    private static bool Number(Match match, string group, int minimum, int maximum, out int value) =>
        int.TryParse(match.Groups[group].ValueSpan, NumberStyles.None, CultureInfo.InvariantCulture, out value)
        && value >= minimum && value <= maximum;

    private static bool Base64(string value, int minimum, int maximum, bool padded)
    {
        var encoded = padded ? value : value.PadRight((value.Length + 3) / 4 * 4, '=');
        Span<byte> decoded = stackalloc byte[64];
        return Convert.TryFromBase64String(encoded, decoded, out var length) && length >= minimum && length <= maximum
            && (padded ? Convert.ToBase64String(decoded[..length]) : Convert.ToBase64String(decoded[..length]).TrimEnd('=')) == value;
    }

    [GeneratedRegex(@"\A\$2[aby]\$(0[4-9]|1[0-4])\$[./A-Za-z0-9]{21}[.Oeu][./A-Za-z0-9]{30}[.CGKOSWaeimquy26]\z")]
    private static partial Regex Bcrypt();

    [GeneratedRegex(@"\Apbkdf2_sha256\$(?<i>[1-9][0-9]{4,6})\$(?<s>[A-Za-z0-9+/=]{24,88})\$(?<h>[A-Za-z0-9+/]{43}=)\z")]
    private static partial Regex Pbkdf2();

    [GeneratedRegex(@"\A\$argon2id\$v=19\$m=(?<m>[1-9][0-9]{0,4}),t=(?<t>[1-9][0-9]?),p=(?<p>[1-8])\$(?<s>[A-Za-z0-9+/]{22})\$(?<h>[A-Za-z0-9+/]{43})\z")]
    private static partial Regex Argon();
}
