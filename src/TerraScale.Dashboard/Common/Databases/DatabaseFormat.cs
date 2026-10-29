namespace TerraScale.Dashboard.Common.Databases
{
    public static class DatabaseFormat
    {
        public static string Tone(string status)
        {
            return status switch { "ready" => "success", "failed" => "danger", "provisioning" or "accepted" or "pending" => "warning", _ => "neutral" };
        }

        public static string Region(string code)
        {
            return code switch { "" => "Not configured", _ => TerraScale.Dashboard.Common.UI.Regions.Name(code) is { Length: > 0 } name ? $"{code.ToLowerInvariant()} · {name}" : code.ToLowerInvariant() };
        }
    }
}
