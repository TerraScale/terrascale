using System.Diagnostics;
using TerraScale.DotNet.TerraBase;

namespace TerraScale.EntityFrameworkCore.Storage;

internal static class TerraBaseConsistencyNames
{
    public static string Format(TerraBaseConsistencyMode consistency) => consistency switch
    {
        TerraBaseConsistencyMode.Fast => "fast",
        TerraBaseConsistencyMode.Strong => "strong",
        _ => throw new UnreachableException(),
    };

    public static bool TryParse(string text, out TerraBaseConsistencyMode consistency)
    {
        (var parsed, consistency) = text.ToLowerInvariant() switch
        {
            "fast" => (true, TerraBaseConsistencyMode.Fast),
            "strong" => (true, TerraBaseConsistencyMode.Strong),
            _ => (false, TerraBaseConsistencyMode.Fast),
        };
        return parsed;
    }
}
