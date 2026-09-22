using TerraScale.Dashboard;
using TerraScale.Endpoints;

namespace TerraScale.Dashboard.Features.Databases.Endpoints;

/// <summary>Port of requestDatabaseBranchAction: provisions a preview branch
/// as a new database copy in the same region.</summary>
public sealed class RequestBranchEndpoint(DashboardStore store) : EndpointWithoutRequest<IResult>
{
    public override void Configure()
    {
        Post("/api/v1/dashboard/databases/branch");
        Summary("Requests a preview branch for a database");
        Tags("Databases");
    }

    public override async Task<IResult> HandleAsync(CancellationToken ct)
    {
        var form = await HttpContext.Request.ReadFormAsync(ct);
        var databaseId = form["databaseId"].ToString();
        if (store.FindDatabase(databaseId) is { } source)
        {
            store.CreateDatabase($"{source.Name}-preview-branch");
            return Results.Redirect("/databases/" + databaseId + "?branched=1", false, false);
        }

        return Results.Redirect("/databases", false, false);
    }
}
