namespace TerraScale.Auth.Crypto;

/// <summary>
/// Password hashing on Argon2id with PHC string encoding
/// (<c>$argon2id$v=19$m=…,t=…,p=…$salt$hash</c>). OWASP-style defaults.
/// </summary>
public static class PasswordHasher
{
    /// <summary>Default memory (KiB), passes, and lanes for new hashes.</summary>
    public const int DefaultMemoryKiB = 19_456; // 19 MiB
    public const int DefaultPasses = 2;
    public const int DefaultLanes = 1;

    /// <summary>Hashes a password; returns the PHC string.</summary>
    public static string Hash(string password, int memoryKiB = DefaultMemoryKiB, int passes = DefaultPasses, int lanes = DefaultLanes)
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        var tag = Argon2id.Derive(
            System.Text.Encoding.UTF8.GetBytes(password),
            salt,
            memoryKiB: memoryKiB,
            passes: passes,
            lanes: lanes,
            tagSize: 32);
        return $"$argon2id$v=19$m={memoryKiB},t={passes},p={lanes}${Encodings.Base64UrlEncode(salt).Replace('-', '+').Replace('_', '/')}$"
            + Convert.ToBase64String(tag).TrimEnd('=');
    }

    /// <summary>Verifies a password against a PHC string in constant time over the tag.</summary>
    public static bool Verify(string password, string phc)
    {
        if (!phc.StartsWith("$argon2id$v=19$", StringComparison.Ordinal))
            return false;
        var parts = phc.Split('$', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 5)
            return false;

        var parameters = parts[2].Split(',');
        var memory = 0;
        var passes = 0;
        var lanes = 0;
        foreach (var parameter in parameters)
        {
            var pair = parameter.Split('=', 2);
            if (pair.Length != 2)
                return false;
            if (!int.TryParse(pair[1], System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var parsed))
                return false;
            switch (pair[0])
            {
                case "m": memory = parsed; break;
                case "t": passes = parsed; break;
                case "p": lanes = parsed; break;
            }
        }
        if (memory < 8 * lanes || passes < 1 || lanes < 1)
            return false;

        byte[] salt;
        byte[] expected;
        try
        {
            salt = Base64DecodePadded(parts[3]);
            expected = Base64DecodePadded(parts[4]);
        }
        catch (FormatException)
        {
            return false;
        }

        if (expected.Length < 4)
            return false; // Argon2id requires a tag of at least 4 bytes

        var actual = Argon2id.Derive(
            System.Text.Encoding.UTF8.GetBytes(password),
            salt,
            memoryKiB: memory,
            passes: passes,
            lanes: lanes,
            tagSize: expected.Length);
        return System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(actual, expected);
    }

    private static byte[] Base64DecodePadded(string value)
    {
        var padded = value.Replace('-', '+').Replace('_', '/');
        padded = padded.PadRight(padded.Length + (4 - padded.Length % 4) % 4, '=');
        return Convert.FromBase64String(padded);
    }
}
