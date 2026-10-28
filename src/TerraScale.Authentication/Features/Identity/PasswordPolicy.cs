using System.Buffers;
using System.Collections.Frozen;
using System.IO.Compression;
using System.Text;

namespace TerraScale.Authentication.Features.Identity;

/// <summary>Why <see cref="PasswordPolicy.Check"/> refused a new password.</summary>
public enum PasswordProblem
{
    /// <summary>The password meets the policy.</summary>
    None,

    /// <summary>Missing, malformed UTF-16, or outside <see cref="PasswordPolicy.MinCharacters"/> to <see cref="PasswordPolicy.MaxCharacters"/>.</summary>
    Length,

    /// <summary>On the common-password list, one short run repeated, or the account's own email address.</summary>
    Common,
}

/// <summary>
/// The new-password rules for password-only (single-factor) accounts, after NIST SP 800-63B: 15 to 256 Unicode
/// characters (code points) of any kind, no composition rules, and refusal of common, repetitive or context-specific
/// choices. The common list is checked in memory, so no password ever leaves the process. Sign-in only needs
/// <see cref="Verifiable"/>, so the policy never locks out an existing account.
/// </summary>
public static class PasswordPolicy
{
    /// <summary>Fewest Unicode characters in a new password.</summary>
    public const int MinCharacters = 15;

    /// <summary>Most Unicode characters in any password (at most 1024 UTF-8 bytes for Argon2id).</summary>
    public const int MaxCharacters = 256;

    // Passwords of 15+ characters from SecLists (MIT) 100k-most-used-passwords-NCSC and
    // xato-net-10-million-passwords-1000000, NFKC-normalized and lower-cased, one per line, gzip.
    private const string CommonResource = "assets/passwords/common.txt.gz";

    private static readonly Lazy<FrozenSet<string>> CommonPasswords = new(LoadCommon);

    /// <summary>Whether <paramref name="password"/> can be checked against a stored hash: well-formed, 1 to <see cref="MaxCharacters"/> characters.</summary>
    public static bool Verifiable(string? password) => Characters(password) is >= 1 and <= MaxCharacters;

    /// <summary>What, if anything, keeps <paramref name="password"/> from being set for the account at <paramref name="email"/>.</summary>
    public static PasswordProblem Check(string? password, string? email)
    {
        if (Characters(password) is < MinCharacters or > MaxCharacters)
        {
            return PasswordProblem.Length;
        }

        var normalized = Normalize(password!);
        var address = Characters(email) > 0 ? Normalize(email!.Trim()) : null;
        return Repetitive(normalized)
            || CommonPasswords.Value.Contains(normalized)
            || (address is not null && (normalized == address || normalized == address.Split('@')[0]))
            ? PasswordProblem.Common
            : PasswordProblem.None;
    }

    /// <summary>The page message for <paramref name="problem"/>.</summary>
    public static string Message(PasswordProblem problem) => problem switch
    {
        PasswordProblem.Length => $"Choose a password of {MinCharacters} to {MaxCharacters} characters. A passphrase of a few words works well.",
        _ => "That password is too common or easy to guess. Choose a longer or less predictable one.",
    };

    // Code points, or -1 for null or unpaired surrogates.
    private static int Characters(string? password)
    {
        if (password is null)
        {
            return -1;
        }

        var count = 0;
        for (var rest = password.AsSpan(); !rest.IsEmpty; count++)
        {
            if (Rune.DecodeFromUtf16(rest, out _, out var used) != OperationStatus.Done)
            {
                return -1;
            }

            rest = rest[used..];
        }

        return count;
    }

    private static string Normalize(string value) => value.Normalize(NormalizationForm.FormKC).ToLowerInvariant();

    // One run repeated end to end, such as "aaaaaaaaaaaaaaa" or "abcabcabcabcabc".
    private static bool Repetitive(string value)
    {
        for (var period = 1; period <= value.Length / 2; period++)
        {
            if (value.Length % period == 0 && value.AsSpan(period).SequenceEqual(value.AsSpan(0, value.Length - period)))
            {
                return true;
            }
        }

        return false;
    }

    private static FrozenSet<string> LoadCommon()
    {
        using var stream = typeof(PasswordPolicy).Assembly.GetManifestResourceStream(CommonResource)
            ?? throw new InvalidOperationException($"Missing embedded resource {CommonResource}.");
        using var reader = new StreamReader(new GZipStream(stream, CompressionMode.Decompress), Encoding.UTF8);
        var words = new List<string>();
        while (reader.ReadLine() is { } line)
        {
            if (line.Length > 0)
            {
                words.Add(line);
            }
        }

        return words.ToFrozenSet(StringComparer.Ordinal);
    }
}
