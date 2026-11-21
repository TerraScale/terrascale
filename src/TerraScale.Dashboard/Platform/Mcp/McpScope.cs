namespace TerraScale.Dashboard.Platform.Mcp
{
    // Helpers shared by the MCP tool classes that each feature owns.
    public static class McpScope
    {
        public static AccountContext Account(HttpContext? http)
        {
            return http?.Items[DashboardMcp.ContextKey] as AccountContext ?? throw new AccountAccessException("Authentication is required.", 401);
        }
    }
}
