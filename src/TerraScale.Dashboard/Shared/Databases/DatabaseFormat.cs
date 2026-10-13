namespace TerraScale.Dashboard.Shared.Databases;

public static class DatabaseFormat
{
    public static string Tone(string status) => status switch { "ready" => "emerald", "failed" => "rose", "provisioning" or "accepted" or "pending" => "amber", _ => "slate" };
    public static string Region(string code) => code.ToUpperInvariant() switch { "SFO" => "SFO · San Francisco", "GRU" => "GRU · São Paulo", "" => "Not configured", _ => code.ToUpperInvariant() };
    public static string Bytes(long bytes) => bytes < 1024 ? $"{bytes} B" : bytes < 1024 * 1024 ? $"{bytes / 1024d:0.0} KiB" : $"{bytes / (1024d * 1024):0.0} MiB";
}
