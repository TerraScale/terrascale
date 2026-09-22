using TerraScale.Dashboard;
using TerraScale.Endpoints;

namespace TerraScale.Dashboard.Features.Data.Endpoints;

public sealed class DeleteSavedQueryEndpoint(DashboardStore store) : EndpointWithoutRequest<IResult>
{
    public override void Configure()
    {
        Post("/api/v1/dashboard/data/saved-queries/delete");
        Summary("Deletes a saved project query");
        Tags("Data");
    }

    public override async Task<IResult> HandleAsync(CancellationToken ct)
    {
        var form = await HttpContext.Request.ReadFormAsync(ct);
        var id = form["savedQueryId"].ToString();
        var view = form["view"].ToString() is { Length: > 0 } v ? v : "queries";
        store.DeleteSavedQueryAndReturnName(id);
        return Results.Redirect($"/data?view={view}", false, false);
    }
}
