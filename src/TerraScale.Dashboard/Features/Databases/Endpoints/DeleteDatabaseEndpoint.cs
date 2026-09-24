using TerraScale.Dashboard;
using TerraScale.Endpoints;

namespace TerraScale.Dashboard.Features.Databases.Endpoints;

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
            var query = new List<string> { "delete=" + Uri.EscapeDataString(databaseId) };
            var filter = form["q"].ToString();
            if (filter.Length > 0)
            {
                query.Add("q=" + Uri.EscapeDataString(filter));
            }

            if (int.TryParse(form["page"].ToString(), out var page) && page > 1)
            {
                query.Add("page=" + page);
            }

            return Results.Redirect(
                "/databases?" + string.Join('&', query),
                false,
                false);
        }

        store.DeleteDatabase(databaseId);
        return Results.Redirect("/databases", false, false);
    }
}
