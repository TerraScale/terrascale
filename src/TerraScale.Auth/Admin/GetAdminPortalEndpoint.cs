namespace TerraScale.Auth.Admin;

using TerraScale.Auth.Api;
using TerraScale.Auth.Configuration;
using TerraScale.Auth.Connections;
using TerraScale.Auth.Crypto;
using TerraScale.Auth.Store;
using TerraScale.Auth.Ui;
using TerraScale.Endpoints;
using static TerraScale.Auth.Admin.AdminPortal;

internal sealed class GetAdminPortalEndpoint : IEndpoint
{
    public RouteHandlerBuilder Map(IEndpointRouteBuilder routes) =>
        routes.MapGet("/ui/{slug}/admin", HandleAsync);

    private static async Task<IResult> HandleAsync(
            string slug, HttpRequest request, AuthStore store, Users.SessionService sessions, AuthOptions options, CancellationToken ct)
    {
        var gate = await RequireOrgAdminAsync(slug, request, store, sessions, ct);
        if (gate is null)
            return Results.Redirect($"/ui/{slug}/sign-in?next={Uri.EscapeDataString($"/ui/{slug}/admin")}");
        if (gate.Value.AdminOrgs.Count == 0)
            return Forbidden();
        return await PageAsync(gate.Value, store, options, ct);
    }
}
