using TerraScale.Dashboard;
using TerraScale.Endpoints;

namespace TerraScale.Dashboard.Features.Data;

/// <summary>Creates a saved query or updates one (revision-protected), mirroring
/// the v1 saved-query contract the DataSavedQueriesPanel drives.</summary>
public sealed class SaveQueryEndpoint(DashboardStore store) : EndpointWithoutRequest<IResult>
{
    public override void Configure()
    {
        Post("/api/v1/dashboard/data/saved-queries");
        Summary("Creates or revision-updates a saved project query");
        Tags("Data");
    }

    public override async Task<IResult> HandleAsync(CancellationToken ct)
    {
        var form = await HttpContext.Request.ReadFormAsync(ct);
        var name = form["name"].ToString().Trim();
        var query = form["query"].ToString().Trim();
        var id = form["savedQueryId"].ToString();
        var revision = form["revision"].ToString();
        var view = form["view"].ToString() is { Length: > 0 } v ? v : "queries";

        if (name.Length == 0 || query.Length == 0)
        {
            return Results.Redirect($"/data?view={view}&error=" + Uri.EscapeDataString("A name and SQL query are required."), false, false);
        }

        if (id.Length > 0)
        {
            if (!store.UpdateSavedQuery(id, name, query, revision))
            {
                return Results.Redirect($"/data?view={view}&edit={id}&conflict=1", false, false);
            }
        }
        else
        {
            store.SaveQuery(name, query);
        }

        return Results.Redirect($"/data?view={view}", false, false);
    }
}
