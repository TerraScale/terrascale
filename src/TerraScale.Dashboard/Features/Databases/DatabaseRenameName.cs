using System.Text.RegularExpressions;

namespace TerraScale.Dashboard.Features.Databases;

internal static partial class DatabaseRenameName
{
    internal static bool IsValid(string name) => Pattern().IsMatch(name);
    [GeneratedRegex(@"\A(?!(?:con|prn|aux|nul|com[0-9]|lpt[0-9])\z)[a-z][a-z0-9_-]{0,62}\z", RegexOptions.CultureInvariant)]
    private static partial Regex Pattern();
}
