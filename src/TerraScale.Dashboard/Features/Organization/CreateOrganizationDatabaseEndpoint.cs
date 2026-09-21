using TerraScale.Dashboard;
using TerraScale.Endpoints;

namespace TerraScale.Dashboard.Features.Organization;

public sealed class CreateOrganizationDatabaseEndpoint(DashboardStore store) : EndpointWithoutRequest<IResult>
{
    public override void Configure()
    {
        Post("/api/v1/dashboard/organizations/create-database");
        Summary("Creates the first project for the new organization");
        Tags("Organization");
    }

    public override async Task<IResult> HandleAsync(CancellationToken ct)
    {
        var form = await HttpContext.Request.ReadFormAsync(ct);
        var name = form["projectName"].ToString().Trim();
        if (!store.CanProvisionResources)
            return Results.Redirect("/organizations/new?step=payment&error=" + Uri.EscapeDataString("Complete Business billing before creating resources."), false, false);
        if (name.Length > 0)
        {
            var region = form["regions"].Where(r => !string.IsNullOrEmpty(r)).FirstOrDefault();
            store.CreateProject(name, region ?? store.ActiveProject.Region);
        }

        return Results.Redirect("/organization", false, false);
    }
}
