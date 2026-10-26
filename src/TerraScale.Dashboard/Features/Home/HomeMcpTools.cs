using System.ComponentModel;
using ModelContextProtocol.Server;
using TerraScale.Dashboard.Features.Accounts;
using TerraScale.Dashboard.Platform.Mcp;

namespace TerraScale.Dashboard.Features.Home;

[McpServerToolType]
public sealed class HomeMcpTools(IHttpContextAccessor http, AccountService accounts, IHomeMeasurements measurements)
{
    [McpServerTool(Name = "get_overview", ReadOnly = true, UseStructuredContent = true, Destructive = false, OpenWorld = false, Idempotent = true), Description("Read the selected project's measured operations and region health.")]
    public async Task<HomeMcpOverview> Overview(
        [Description("1h, 24h, 7d, or 30d. Defaults to 7d; unrecognized values also use 7d.")] string range = "7d",
        string? organizationId = null, string? projectId = null)
    {
        var context = Scope(organizationId, projectId);
        context = await AccountScope.RefreshAsync(accounts, context, Cancellation, requireProject: context.Role.Length == 0);
        var normalized = range is "1h" or "24h" or "7d" or "30d" ? range : "7d";
        return HomeMcpPresentation.For(context, await measurements.ReadAsync(context, normalized, Cancellation), normalized);
    }

    private AccountContext Scope(string? organizationId, string? projectId) => DashboardMcp.Scope(http.HttpContext, organizationId, projectId);
    private CancellationToken Cancellation => http.HttpContext?.RequestAborted ?? default;
}
