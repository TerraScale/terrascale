namespace TerraScale.Auth.Crypto;

/// <summary>TOTP (RFC 6238) over HOTP (RFC 4226), written from scratch. SHA-1, SHA-256, and SHA-512 variants.</summary>
public static class Totp
{
    /// <summary>Computes the HOTP value for a counter, as a zero-padded numeric string of <paramref name="digits"/> digits.</summary>
    public static string Hotp(ReadOnlySpan<byte> secret, ulong counter, int digits = 6, string algorithm = "SHA1")
    {
        Span<byte> counterBytes = stackalloc byte[8];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt64BigEndian(counterBytes, counter);

        var mac = algorithm switch
        {
            "SHA256" => System.Security.Cryptography.HMACSHA256.HashData(secret, counterBytes),
            "SHA512" => System.Security.Cryptography.HMACSHA512.HashData(secret, counterBytes),
            _ => System.Security.Cryptography.HMACSHA1.HashData(secret, counterBytes),
        };

        var offset = mac[^1] & 0x0F;
        var binary = ((mac[offset] & 0x7F) << 24)
            | (mac[offset + 1] << 16)
            | (mac[offset + 2] << 8)
            | mac[offset + 3];
        return (binary % (int)Math.Pow(10, digits)).ToString(new string('0', digits), System.Globalization.CultureInfo.InvariantCulture).PadLeft(digits, '0');
    }

    /// <summary>Computes the TOTP value at a point in time (30-second step by default).</summary>
    public static string Compute(ReadOnlySpan<byte> secret, DateTimeOffset timestamp, int digits = 6, int stepSeconds = 30, string algorithm = "SHA1") =>
        Hotp(secret, (ulong)(timestamp.ToUnixTimeSeconds() / stepSeconds), digits, algorithm);

    /// <summary>Verifies a code within a ±<paramref name="window"/> step tolerance, returning the matched step offset.</summary>
    public static bool Verify(ReadOnlySpan<byte> secret, string code, DateTimeOffset timestamp, int window = 1, int digits = 6, int stepSeconds = 30, string algorithm = "SHA1")
    {
        if (code.Length != digits || !code.All(char.IsDigit))
            return false;
        var step = timestamp.ToUnixTimeSeconds() / stepSeconds;
        for (var offset = -window; offset <= window; offset++)
        {
            if (System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(
                    System.Text.Encoding.UTF8.GetBytes(Hotp(secret, (ulong)(step + offset), digits, algorithm)),
                    System.Text.Encoding.UTF8.GetBytes(code)))
                return true;
        }
        return false;
    }
}
