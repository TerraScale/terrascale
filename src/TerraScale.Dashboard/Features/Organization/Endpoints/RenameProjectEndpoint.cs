using System.Collections.Immutable;
using TerraScale.Dashboard;
using TerraScale.Endpoints;

namespace TerraScale.Dashboard.Features.Organization.Endpoints;

public sealed class RenameProjectEndpoint(DashboardStore store) : EndpointWithoutRequest<IResult>
{
    public override void Configure()
    {
        Post("/api/v1/dashboard/organization/projects/rename");
        Summary("Renames a project and updates its placement");
        Tags("Organization");
    }

    public override async Task<IResult> HandleAsync(CancellationToken ct)
    {
        var form = await HttpContext.Request.ReadFormAsync(ct);
        var regions = ImmutableArray.CreateRange(
            form["regions"].Where(r => !string.IsNullOrEmpty(r)).Select(r => r!.ToString()));
        store.RenameProject(
            form["projectId"].ToString(),
            form["name"].ToString(),
            form["backupRegion"].ToString(),
            regions);
        return Results.Redirect("/organization", false, false);
    }
}
