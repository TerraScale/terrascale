using TerraScale.Dashboard2;
using TerraScale.Endpoints;

namespace TerraScale.Dashboard2.Features.Organization;

public sealed class CreateProjectEndpoint(DashboardStore store) : EndpointWithoutRequest<IResult>
{
    public override void Configure()
    {
        Post("/api/v1/dashboard/organization/projects");
        Summary("Creates a project in the active organization");
        Tags("Organization");
    }

    public override async Task<IResult> HandleAsync(CancellationToken ct)
    {
        var form = await HttpContext.Request.ReadFormAsync(ct);
        var name = form["name"].ToString().Trim();
        if (name.Length > 0)
        {
            var region = form["regions"].Where(r => !string.IsNullOrEmpty(r)).FirstOrDefault();
            store.CreateProject(name, region ?? store.ActiveProject.Region);
        }

        return Results.Redirect("/organization", false, false);
    }
}
