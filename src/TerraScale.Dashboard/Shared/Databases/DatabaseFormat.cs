namespace TerraScale.Dashboard.Shared.Databases;

public static class DatabaseFormat
{
    public static string Tone(string status) => status switch { "ready" => "success", "failed" => "danger", "provisioning" or "accepted" or "pending" => "warning", _ => "neutral" };
    public static string Region(string code) => code switch { "" => "Not configured", _ => TerraScale.Dashboard.Shared.UI.Regions.Name(code) is { Length: > 0 } name ? $"{code.ToLowerInvariant()} · {name}" : code.ToLowerInvariant() };
    public static string Bytes(long bytes) => bytes < 1024 ? $"{bytes} B" : bytes < 1024 * 1024 ? $"{bytes / 1024d:0.0} KiB" : $"{bytes / (1024d * 1024):0.0} MiB";
}
