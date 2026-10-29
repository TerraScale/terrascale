using System.Text.Json.Serialization;
using System.ComponentModel;
using ModelContextProtocol.Server;
using TerraScale.Dashboard.Platform.Mcp;

namespace TerraScale.Dashboard.Features.QuerySubscriptions;

[McpServerToolType]
public sealed class QuerySubscriptionMcpTools(IHttpContextAccessor http, QuerySubscriptionService service)
{
    [McpServerTool(Name = "list_query_subscriptions", ReadOnly = true, UseStructuredContent = true, Destructive = false, OpenWorld = false, Idempotent = true), Description("List the project's saved shared-result configurations. Saved metadata does not establish live delivery.")]
    public async Task<McpQuerySubscriptionList> List(string? organizationId = null, string? projectId = null,
        [Description(CursorPaging.Description)] int limit = 100, string after = "")
    {
        var context = DashboardMcp.Scope(http.HttpContext, organizationId, projectId);
        var page = CursorPaging.Page((await service.ListAsync(context, Cancellation)).Subscriptions, item => item.Id,
            CursorPaging.Scope(context, "query-subscriptions"), limit, after);
        return new(page.Items, page.NextCursor, page.HasMore, page.Limit);
    }

    private CancellationToken Cancellation => http.HttpContext?.RequestAborted ?? default;
}

public sealed record McpQuerySubscriptionList(IReadOnlyList<QuerySubscriptionView> Subscriptions, [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? NextCursor, bool HasMore, int Limit);
