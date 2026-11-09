using TerraBase.Features.Sql.Routing;

namespace TerraBase.Features.Sql;

internal static class WritePreference
{
    public const string Header = "TerraScale-Write";

    public static WriteOverride? Parse(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        null or "" or "auto" => WriteOverride.Auto,
        "home" => WriteOverride.Home,
        "local" => WriteOverride.Local,
        _ => null,
    };
}
