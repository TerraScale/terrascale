using TerraScale.Dashboard;
using TerraScale.Endpoints;

namespace TerraScale.Dashboard.Features.Shared;

public sealed class SwitchProjectEndpoint(DashboardStore store) : EndpointWithoutRequest<IResult>
{
    public override void Configure()
    {
        Post("/api/v1/dashboard/switch-project");
        Summary("Switches the active project for the dashboard session");
        Tags("Dashboard");
    }

    public override async Task<IResult> HandleAsync(CancellationToken ct)
    {
        var form = await HttpContext.Request.ReadFormAsync(ct);
        store.SwitchProject(form["projectId"].ToString());
        var target = form["returnUrl"].ToString() is { Length: > 0 } and var r && r.StartsWith('/') ? r : "/";
        return Results.Redirect(target, false, false);
    }
}
