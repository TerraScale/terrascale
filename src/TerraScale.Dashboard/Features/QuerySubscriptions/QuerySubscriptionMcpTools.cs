using System.ComponentModel;
using ModelContextProtocol.Server;
using TerraScale.Dashboard.Platform.Mcp;

namespace TerraScale.Dashboard.Features.QuerySubscriptions;

[McpServerToolType]
public sealed class QuerySubscriptionMcpTools(IHttpContextAccessor http, QuerySubscriptionService service)
{
    [McpServerTool(Name = "list_query_subscriptions", ReadOnly = true, UseStructuredContent = true, Destructive = false, OpenWorld = false, Idempotent = true), Description("List the project's saved shared-result configurations. Saved metadata does not establish live delivery.")]
    public Task<QuerySubscriptionList> List(string? organizationId = null, string? projectId = null) => service.ListAsync(DashboardMcp.Scope(http.HttpContext, organizationId, projectId), Cancellation);

    private CancellationToken Cancellation => http.HttpContext?.RequestAborted ?? default;
}
