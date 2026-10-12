using System.ComponentModel;
using ModelContextProtocol.Server;
using TerraScale.Dashboard.Platform.Mcp;

namespace TerraScale.Dashboard.Features.QuerySubscriptions;

[McpServerToolType]
public sealed class QuerySubscriptionMcpTools(IHttpContextAccessor http, QuerySubscriptionService service)
{
    [McpServerTool(Name = "list_query_subscriptions", ReadOnly = true, UseStructuredContent = true, Destructive = false, OpenWorld = false, Idempotent = true), Description("List the project's saved shared-result configurations. Saved metadata does not establish live delivery.")]
    public Task<QuerySubscriptionList> List(string? organizationId = null, string? projectId = null) => service.ListAsync(DashboardMcp.Scope(http.HttpContext, organizationId, projectId), Cancellation);

    [McpServerTool(Name = "attach_query_subscription", Destructive = false, OpenWorld = false), Description("Request shared-result configuration. The current producer has no management/delivery contract; this returns an unavailable error.")]
    public Task Attach(string databaseId, string name, string query, string transport, string? organizationId = null, string? projectId = null) => Refuse(organizationId, projectId);

    [McpServerTool(Name = "pause_query_subscription", Destructive = false, OpenWorld = false, Idempotent = true), Description("Request pausing a shared-result configuration; unavailable until its producer management contract exists.")]
    public Task Pause(string subscriptionId, string? organizationId = null, string? projectId = null) => Refuse(organizationId, projectId);

    [McpServerTool(Name = "resume_query_subscription", Destructive = false, OpenWorld = false, Idempotent = true), Description("Request resuming a shared-result configuration; unavailable until its producer management contract exists.")]
    public Task Resume(string subscriptionId, string? organizationId = null, string? projectId = null) => Refuse(organizationId, projectId);

    [McpServerTool(Name = "detach_query_subscription", Destructive = true, OpenWorld = false), Description("Request detaching a shared-result configuration; unavailable until its producer management contract exists.")]
    public Task Detach(string subscriptionId, string? organizationId = null, string? projectId = null) => Refuse(organizationId, projectId);

    private CancellationToken Cancellation => http.HttpContext?.RequestAborted ?? default;
    private Task Refuse(string? organizationId, string? projectId) => service.RefuseMutationAsync(DashboardMcp.Scope(http.HttpContext, organizationId, projectId), Cancellation);
}
