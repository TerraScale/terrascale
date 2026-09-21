namespace TerraScale.Auth.Admin;

using TerraScale.Auth.Api;
using TerraScale.Auth.Connections;
using TerraScale.Auth.Crypto;
using TerraScale.Auth.Store;
using TerraScale.Auth.Ui;
using TerraScale.Endpoints;
using static TerraScale.Auth.Admin.AdminPortal;

internal sealed class UpdateAdminConnectionEndpoint : IEndpoint
{
    public RouteHandlerBuilder Map(IEndpointRouteBuilder routes) =>
        routes.MapPost("/ui/{slug}/admin/connections/{connectionId}/{action}", HandleAsync);

    private static async Task<IResult> HandleAsync(
            string slug, string connectionId, string action, HttpRequest request, AuthStore store, Users.SessionService sessions, CancellationToken ct)
    {
        var gate = await RequireOrgAdminAsync(slug, request, store, sessions, ct);
        if (gate is null)
            return Results.Redirect($"/ui/{slug}/sign-in");
        var (instance, user, adminOrgs) = gate.Value;
        if (adminOrgs.Count == 0)
            return Forbidden();

        if (action is not ("enable" or "disable"))
            return ApiResults.NotFound();
        var connection = await store.GetConnectionAsync(connectionId, ct);
        if (connection is null || connection.InstanceId != instance.Id)
            return ApiResults.NotFound();
        // Only connections owned by an org the user administers; instance-level
        // connections are provisioned through the management API, not the portal.
        if (connection.OrgId is null || adminOrgs.All(o => o.Id != connection.OrgId))
            return Forbidden();

        await store.UpsertConnectionAsync(connection with { Enabled = action == "enable" }, ct);
        await store.CreateAuditEventAsync(new AuditEvent
        {
            Id = "evt_" + SecureTokens.RandomToken(20),
            InstanceId = instance.Id,
            Type = action == "enable" ? "connection.enabled" : "connection.disabled",
            ActorId = user.Id,
            Data = new Dictionary<string, object> { ["org_id"] = connection.OrgId, ["connection_id"] = connection.Id },
            CreatedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
        }, ct);
        return Results.Redirect($"/ui/{slug}/admin");
    }
}
