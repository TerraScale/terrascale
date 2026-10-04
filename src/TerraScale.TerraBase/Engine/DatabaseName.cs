using System.Diagnostics.CodeAnalysis;
using System.Text.RegularExpressions;

namespace TerraScale.TerraBase.Engine;

/// <summary>
/// A validated database name: lowercase ASCII letter first, then letters, digits, <c>_</c> or
/// <c>-</c>, at most 63 characters. Safe as a file name on every supported platform.
/// </summary>
public readonly partial record struct DatabaseName
{
    private DatabaseName(string value)
    {
        Value = value;
    }

    /// <summary>The internal database that holds TerraBase's own state (API keys); clients cannot name it.</summary>
    public static DatabaseName System { get; } = new("_system");

    /// <summary>The name.</summary>
    public string Value { get; }

    /// <summary>Parses a client-supplied name.</summary>
    public static bool TryParse([NotNullWhen(true)] string? value, out DatabaseName name)
    {
        name = value is not null && Pattern().IsMatch(value) ? new DatabaseName(value) : default;
        return name.Value is not null;
    }

    /// <inheritdoc />
    public override string ToString() => Value;

    [GeneratedRegex("^[a-z][a-z0-9_-]{0,62}$", RegexOptions.CultureInvariant)]
    private static partial Regex Pattern();
}
