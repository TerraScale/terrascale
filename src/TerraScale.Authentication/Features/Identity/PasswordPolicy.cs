using System.Buffers;
using System.Collections.Frozen;
using System.IO.Compression;
using System.Text;

namespace TerraScale.Authentication.Features.Identity;

public enum PasswordProblem
{
    None,

    Length,

    Common,

    Breached,
}

public static class PasswordPolicy
{
    public const int MinCharacters = 10;

    public const int MaxCharacters = 256;

    private const string CommonResource = "assets/passwords/common.txt.gz";

    private static readonly Lazy<FrozenSet<string>> CommonPasswords = new(LoadCommon);

    public static bool Verifiable(string? password) => Characters(password) is >= 1 and <= MaxCharacters;

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

    public static string Message(PasswordProblem problem) => problem switch
    {
        PasswordProblem.Breached => "That password was found in a data breach. Choose a different password.",
        PasswordProblem.Length => $"Choose a password of {MinCharacters} to {MaxCharacters} characters. A passphrase of a few words works well.",
        _ => "That password is too common or easy to guess. Choose a longer or less predictable one.",
    };

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
