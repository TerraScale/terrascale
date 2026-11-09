using System.Diagnostics.CodeAnalysis;
using System.Text.RegularExpressions;

namespace TerraBase.Engine;

public readonly partial record struct DatabaseName
{
    private DatabaseName(string value)
    {
        Value = value;
    }

    public string Value { get; }

    public static bool TryParse([NotNullWhen(true)] string? value, out DatabaseName name)
    {
        name = value is not null && Pattern().IsMatch(value) && !ReservedDeviceName().IsMatch(value) ? new DatabaseName(value) : default;
        return name.Value is not null;
    }

    public override string ToString() => Value;

    [GeneratedRegex(@"\A[a-z][a-z0-9_-]{0,62}\z", RegexOptions.CultureInvariant)]
    private static partial Regex Pattern();

    [GeneratedRegex(@"\A(con|prn|aux|nul|com[0-9]|lpt[0-9])\z", RegexOptions.CultureInvariant)]
    private static partial Regex ReservedDeviceName();
}
