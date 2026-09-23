namespace TerraScale.Auth.Admin;

using TerraScale.Auth.Store;
using TerraScale.Endpoints;
using static TerraScale.Auth.Admin.AdminPortal;

/// <summary>Datastar fragment for the selected provider; every request checks the live admin session.</summary>
internal sealed class GetAdminProviderSetupEndpoint : IEndpoint
{
    public RouteHandlerBuilder Map(IEndpointRouteBuilder routes) =>
        routes.MapGet("/ui/{slug}/admin/providers/{provider}", HandleAsync);

    private static async Task<IResult> HandleAsync(
        string slug, string provider, HttpRequest request, AuthStore store, Users.SessionService sessions, CancellationToken ct)
    {
        var gate = await RequireOrgAdminAsync(slug, request, store, sessions, ct);
        if (gate is null)
            return Results.Redirect($"/ui/{slug}/sign-in");
        if (gate.Value.AdminOrgs.Count == 0)
            return Forbidden();
        var html = SetupHtml(provider, slug, gate.Value.AdminOrgs);
        if (html is null)
            return Results.NotFound();
        // Datastar expects one elements line. Encode CR/LF inside user-supplied
        // organization names so they cannot change the SSE frame.
        var safeHtml = html.Replace("\r", "&#13;", StringComparison.Ordinal).Replace("\n", "&#10;", StringComparison.Ordinal);
        return Results.Text($"event: datastar-patch-elements\ndata: elements {safeHtml}\n\n", "text/event-stream; charset=utf-8");
    }
}
