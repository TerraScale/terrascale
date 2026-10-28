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
        [Description("1h, 24h, 7d, or 30d. Defaults to 7d; other values are rejected.")] string range = "7d",
        string? organizationId = null, string? projectId = null)
    {
        var normalized = range.Length == 0 ? "7d" : range;
        if (normalized is not ("1h" or "24h" or "7d" or "30d")) throw new AccountAccessException("Choose 1h, 24h, 7d or 30d.", 400);
        var context = await AccountScope.RefreshAsync(accounts, Scope(organizationId, projectId), Cancellation);
        return HomeMcpPresentation.For(context, await measurements.ReadAsync(context, normalized, Cancellation), normalized);
    }

    private AccountContext Scope(string? organizationId, string? projectId) => DashboardMcp.Scope(http.HttpContext, organizationId, projectId);
    private CancellationToken Cancellation => http.HttpContext?.RequestAborted ?? default;
}
