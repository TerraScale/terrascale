namespace TerraScale.Dashboard.Platform.Mcp
{
    // Helpers shared by the MCP tool classes that each feature owns.
    public static class McpScope
    {
        public static AccountContext Account(HttpContext? http)
        {
            return http?.Items[DashboardMcp.ContextKey] as AccountContext ?? throw new AccountAccessException("Authentication is required.", 401);
        }

        public static string Alias(string primary, string? alias, string name, bool required = true)
        {
            if (primary.Length > 0 && !string.IsNullOrEmpty(alias) && primary != alias)
            {
                throw new AccountAccessException("Conflicting " + name + " values were provided.", 422);
            }

            var result = primary.Length > 0 ? primary : alias ?? "";
            if (required && result.Length == 0)
            {
                throw new AccountAccessException(name + " is required.", 422);
            }

            return result;
        }

        public static void DemandManagement(AccountContext context)
        {
            if (!context.CanManage)
            {
                throw new AccountAccessException("Organization management access is required.");
            }
        }
    }

    public sealed record McpDeleted(bool Deleted);
}
