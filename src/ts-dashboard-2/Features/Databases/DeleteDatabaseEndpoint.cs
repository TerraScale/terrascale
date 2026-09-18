using TerraScale.Dashboard2;
using TerraScale.Endpoints;

namespace TerraScale.Dashboard2.Features.Databases;

public sealed class DeleteDatabaseEndpoint(DashboardStore store) : EndpointWithoutRequest<IResult>
{
    public override void Configure()
    {
        Post("/api/v1/dashboard/databases/delete");
        Summary("Deletes a logical database after name confirmation");
        Tags("Databases");
    }

    public override async Task<IResult> HandleAsync(CancellationToken ct)
    {
        var form = await HttpContext.Request.ReadFormAsync(ct);
        var databaseId = form["databaseId"].ToString();
        var confirm = form["confirm"].ToString().Trim();
        var database = store.FindDatabase(databaseId);
        if (database is null || confirm != database.Name)
        {
            return Results.Redirect(
                "/databases?delete=" + Uri.EscapeDataString(databaseId),
                false,
                false);
        }

        store.DeleteDatabase(databaseId);
        return Results.Redirect("/databases", false, false);
    }
}
