using TerraScale.Dashboard;
using TerraScale.Endpoints;

namespace TerraScale.Dashboard.Features.Settings;

public sealed class DeleteProjectEndpoint(DashboardStore store) : EndpointWithoutRequest<IResult>
{
    public override void Configure()
    {
        Post("/api/v1/dashboard/settings/delete-project");
        Summary("Permanently deletes the project after name confirmation");
        Tags("Settings");
    }

    public override async Task<IResult> HandleAsync(CancellationToken ct)
    {
        var form = await HttpContext.Request.ReadFormAsync(ct);
        var projectId = form["projectId"].ToString();
        var confirmName = form["confirmName"].ToString().Trim();
        var project = store.Projects.FirstOrDefault(p => p.Id == projectId);
        if (project is not null && confirmName == project.Name)
        {
            store.DeleteProject(projectId);
        }

        return Results.Redirect("/organization", false, false);
    }
}
