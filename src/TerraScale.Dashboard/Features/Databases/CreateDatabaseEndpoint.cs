using TerraScale.Dashboard;
using TerraScale.Endpoints;

namespace TerraScale.Dashboard.Features.Databases;

public sealed class CreateDatabaseEndpoint(DashboardStore store, DashboardSecurityOptions security, TerrabaseDataPlane? dataPlane = null) : EndpointWithoutRequest<IResult>
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

        if (!security.DemoMode)
        {
            // Live mode provisions the database in TerraBase through the SDK
            // before the dashboard records it; the world row persists with the
            // terrabase name so the two stay linked.
            try
            {
                var provisioned = await dataPlane!.CreateDatabaseAsync(name, $"op_{Guid.NewGuid():N}"[..28], ct);
                store.CreateDatabase(provisioned);
            }
            catch (HttpRequestException exception)
            {
                return Results.Redirect("/databases?error=" + Uri.EscapeDataString($"TerraBase rejected the database: {exception.Message}"), false, false);
            }
        }
        else
        {
            store.CreateDatabase(name);
        }

        return Results.Redirect("/databases", false, false);
    }
}
