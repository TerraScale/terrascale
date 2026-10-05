using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Geralt;

namespace TerraScale.Auth.Security;

/// <summary>
/// Argon2id (RFC 9106, libsodium) password hashes as PHC strings:
/// <c>$argon2id$v=19$m=19456,t=2,p=1$&lt;salt&gt;$&lt;tag&gt;</c>, a random 16-byte salt and a
/// 32-byte tag. The parameters are the OWASP baseline and match the Rust service.
/// </summary>
public static partial class Passwords
{
    /// <summary>Memory cost in KiB.</summary>
    public const int MemoryKib = 19_456;

    /// <summary>Time cost (passes over memory).</summary>
    public const int Passes = 2;

    // A stored hash is trusted data, but a corrupted or planted one must not be able to
    // exhaust memory or CPU, so its work factors are bounded before any derivation: at most
    // 64 MiB and a memory × passes cost of the RFC 9106 second recommended profile (64 MiB,
    // t=3), about five times this service's own cost. Hashes imported from encoders using the
    // standard profiles still verify.
    private const int MaxMemoryKib = 65_536;
    private const int MaxPasses = 10;
    private const int MaxLanes = 8;
    private const long MaxCost = 65_536L * 3;

    /// <summary>Hashes <paramref name="password"/> with a fresh salt.</summary>
    public static string Hash(string password)
    {
        ArgumentNullException.ThrowIfNull(password);
        var encoded = new char[Argon2id.HashSize];
        var bytes = Encoding.UTF8.GetBytes(password);
        try
        {
            Argon2id.ComputeHash(encoded, bytes, Passes, MemoryKib * 1024);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }

        return new string(encoded).TrimEnd('\0');
    }

    /// <summary>Checks <paramref name="password"/> against a PHC hash; malformed or oversized hashes never match.</summary>
    public static bool Verify(string password, string phc)
    {
        ArgumentNullException.ThrowIfNull(password);
        if (phc is null || phc.Length > Argon2id.HashSize || Phc().Match(phc) is not { Success: true } match
            || !Within(match, "m", MaxMemoryKib, out var memory) || !Within(match, "t", MaxPasses, out var passes)
            || !Within(match, "p", MaxLanes, out _) || (long)memory * passes > MaxCost)
        {
            return false;
        }

        var bytes = Encoding.UTF8.GetBytes(password);
        try
        {
            return Argon2id.VerifyHash(phc, bytes);
        }
        catch (ArgumentException)
        {
            return false;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    /// <summary>
    /// Checks a known-answer vector from the RFC 9106 reference implementation. The single-file
    /// executable extracts libsodium at startup, so the process runs this before serving: a
    /// missing or broken native library stops it instead of failing every sign-in.
    /// </summary>
    public static bool SelfTest()
    {
        const string vector = "$argon2id$v=19$m=64,t=1,p=1$dHMtYXV0aC12ZWN0b3IxNg$DSktwS9SktskGJvD3C/lWK0F3U9f7EbVnqf4UsOE8FQ";
        return Verify("correct horse battery staple", vector) && !Verify("correct horse battery stapler", vector);
    }

    private static bool Within(Match match, string group, int maximum, out int value) =>
        int.TryParse(match.Groups[group].ValueSpan, NumberStyles.None, CultureInfo.InvariantCulture, out value)
        && value >= 1 && value <= maximum;

    [GeneratedRegex(@"\A\$argon2id\$v=19\$m=(?<m>[0-9]{1,7}),t=(?<t>[0-9]{1,2}),p=(?<p>[0-9]{1,2})\$[A-Za-z0-9+/]{11,}\$[A-Za-z0-9+/]{6,}\z")]
    private static partial Regex Phc();
}
