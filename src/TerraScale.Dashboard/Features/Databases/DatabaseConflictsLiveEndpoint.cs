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
            if (listing is not DatabaseConflictList list)
            {
                return new(LiveErrorView.Create(new("conflict-results", 422, DatabaseMutations.MessageOf(DatabaseMutations.RefusedConflict(listing)))), true);
            }

            var page = new DatabaseDetailPage(ShellModel.For(HttpContext, context, database.Name + " · Conflicts"), database, "conflicts", [])
            { Conflicts = new(list, table, since, until, before) };
            return new(DatabaseConflictResults.Create(page));
        }, cancellationToken);
}
