using TerraScale.Dashboard2;
using TerraScale.Endpoints;

namespace TerraScale.Dashboard2.Features.Organization;

public sealed class RenameProjectEndpoint(DashboardStore store) : EndpointWithoutRequest<IResult>
{
    public override void Configure()
    {
        Post("/api/v1/dashboard/organization/projects/rename");
        Summary("Renames a project");
        Tags("Organization");
    }

    public override async Task<IResult> HandleAsync(CancellationToken ct)
    {
        var form = await HttpContext.Request.ReadFormAsync(ct);
        store.RenameProject(form["projectId"].ToString(), form["name"].ToString());
        return Results.Redirect("/organization", false, false);
    }
}
