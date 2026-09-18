using TerraScale.Dashboard2;
using TerraScale.Endpoints;

namespace TerraScale.Dashboard2.Features.Databases;

public sealed class CreateDatabaseEndpoint(DashboardStore store) : EndpointWithoutRequest<IResult>
{
    public override void Configure()
    {
        Post("/api/v1/dashboard/databases/create");
        Summary("Creates a logical database for the active project");
        Tags("Databases");
    }

    public override async Task<IResult> HandleAsync(CancellationToken ct)
    {
        var form = await HttpContext.Request.ReadFormAsync(ct);
        var name = form["name"].ToString().Trim();
        if (name.Length == 0)
        {
            return Results.Redirect("/databases?error=" + Uri.EscapeDataString("Database name is required."), false, false);
        }

        store.CreateDatabase(name);
        return Results.Redirect("/databases", false, false);
    }
}
