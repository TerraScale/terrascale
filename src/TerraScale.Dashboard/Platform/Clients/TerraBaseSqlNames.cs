namespace TerraScale.Dashboard.Platform.Clients;

public static class TerraBaseSqlNames
{
    private static readonly string[] Prefixes = ["_tb_", "_ts_", "sqlite_stat"];

    public static bool IsInternalTable(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        return name.StartsWith("sqlite_", StringComparison.OrdinalIgnoreCase)
            || Prefixes.Any(prefix => name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
    }
}
