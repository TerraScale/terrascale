using TerraScale.Endpoints;

namespace TerraScale.Dashboard.Features.Authentication.Endpoints;

public sealed class AuthActionEndpoint(DashboardStore store) : EndpointWithoutRequest<IResult>
{
    public override void Configure()
    {
        Post("/api/v1/auth-management/{section}/{id}/actions/{action}");
        Summary("Applies an authentication resource action");
        Tags("Authentication");
    }

    public override Task<IResult> HandleAsync(CancellationToken ct)
    {
        var section = HttpContext.Request.RouteValues["section"]?.ToString() ?? "";
        var id = HttpContext.Request.RouteValues["id"]?.ToString() ?? "";
        var action = HttpContext.Request.RouteValues["action"]?.ToString() ?? "";
        if (!store.ApplyAuthAction(section, id, action)) return Task.FromResult<IResult>(Results.NotFound());
        return Task.FromResult<IResult>(Results.Redirect($"/auth/{section}?toast=" + Uri.EscapeDataString("Demo action applied. No external requests were sent.")));
    }
}
