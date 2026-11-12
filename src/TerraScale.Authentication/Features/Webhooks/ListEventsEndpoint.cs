using TerraScale.Authentication.Contracts;
using TerraScale.Authentication.Features.Platform;
using TerraScale.Authentication.Features.ProjectTenants;

namespace TerraScale.Authentication.Features.Webhooks;

public sealed class ListEventsEndpoint(TenantDirectory tenants, PlatformAuthentication authentication, WebhookHistory history)
    : ProjectTenantEndpoint(tenants, authentication)
{
    public override void Configure()
    {
        Get(Root + "/events");
        this.Describe("list_events", "List retained project events", "Events in ascending ULID order, retained 30 days. after is the previous page next cursor; limit is 1 to 100, default 50. Needs the admin project role.", "Project tenants", null, StatusCodes.Status200OK, typeof(WebhookEventPage), paged: true);
    }

    protected override async Task<IResult> HandleAsync(UserRecord caller, CancellationToken cancellationToken)
    {
        var projectId = await this.AccessAsync(caller, Roles.Admin, cancellationToken);
        var (limit, after) = this.Paging(50);
        return Results.Json(await history.EventsAsync(projectId, after, limit, cancellationToken), WebhooksJson.Default.WebhookEventPage);
    }
}
