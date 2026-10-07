using System.Diagnostics.CodeAnalysis;
using System.Text.RegularExpressions;

namespace TerraBase.Engine;

/// <summary>
/// A validated database name: lowercase ASCII letter first, then letters, digits, <c>_</c> or
/// <c>-</c>, at most 63 characters, and not a Windows device name (<c>con</c>, <c>nul</c>, <c>com1</c>, …).
/// Safe as a file name on every platform.
/// </summary>
public readonly partial record struct DatabaseName
{
    private DatabaseName(string value)
    {
        Value = value;
    }

    /// <summary>The name.</summary>
    public string Value { get; }

    /// <summary>Parses a client-supplied name.</summary>
    public static bool TryParse([NotNullWhen(true)] string? value, out DatabaseName name)
    {
        name = value is not null && Pattern().IsMatch(value) && !ReservedDeviceName().IsMatch(value) ? new DatabaseName(value) : default;
        return name.Value is not null;
    }

    /// <inheritdoc />
    public override string ToString() => Value;

    // \z, not $: $ also matches before a trailing newline.
    [GeneratedRegex(@"\A[a-z][a-z0-9_-]{0,62}\z", RegexOptions.CultureInvariant)]
    private static partial Regex Pattern();

    // Windows device names stay invalid file names even with an extension.
    [GeneratedRegex(@"\A(con|prn|aux|nul|com[0-9]|lpt[0-9])\z", RegexOptions.CultureInvariant)]
    private static partial Regex ReservedDeviceName();
}
