using TerraScale.Dashboard.Common.Live;
using TerraScale.Dashboard.Common.UI;
using TerraScale.Endpoints;

namespace TerraScale.Dashboard.Features.Databases;

public sealed class DatabaseConflictsLiveEndpoint(DatabaseConflicts conflicts) : EndpointWithoutRequest<IResult>
{
    public override void Configure() => Get("/databases/{databaseId}/conflicts/live");

    public override async ValueTask<IResult> HandleAsync(CancellationToken cancellationToken) =>
        await LiveDatabaseView.StreamAsync(HttpContext, "conflict-results", async (context, database, cancellationToken) =>
        {
            var query = HttpContext.Request.Query;
            var table = query["table"].ToString();
            var since = query["since"].ToString();
            var until = query["until"].ToString();
            var before = query["before"].ToString();
            var listing = await conflicts.ListAsync(context, database.Id, table, since, until, 50, before, cancellationToken);
            var page = new DatabaseDetailPage(ShellModel.For(HttpContext, context, database.Name + " · Conflicts"), database, "conflicts", [])
            { Conflicts = new(listing, table, since, until, before) };
            return new(DatabaseConflictResults.Create(page));
        }, cancellationToken);
}
