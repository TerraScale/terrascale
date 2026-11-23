using System.Diagnostics;

namespace TerraScale.Contracts.Consistency;

public static class ConsistencyHeaders
{
    public const string Bookmark = "TerraScale-Bookmark";
    public const string Consistency = "TerraScale-Consistency";
    public const string FastValue = "fast";
    public const string StrongValue = "strong";

    public static string Format(ConsistencyMode mode) => mode switch
    {
        ConsistencyMode.Fast => FastValue,
        ConsistencyMode.Strong => StrongValue,
        _ => throw new UnreachableException(),
    };

    public static bool TryParse(ReadOnlySpan<char> value, out ConsistencyMode mode)
    {
        if (value.Equals(FastValue, StringComparison.OrdinalIgnoreCase))
        {
            mode = ConsistencyMode.Fast;
            return true;
        }

        if (value.Equals(StrongValue, StringComparison.OrdinalIgnoreCase))
        {
            mode = ConsistencyMode.Strong;
            return true;
        }

        mode = ConsistencyMode.Fast;
        return false;
    }
}
