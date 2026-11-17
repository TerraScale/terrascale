using TerraScale.Dashboard.Common.Live;
using TerraScale.Dashboard.Common.UI;
using TerraScale.Endpoints;

namespace TerraScale.Dashboard.Features.Databases;

public sealed class DatabaseReplicationLiveEndpoint(DatabaseService databases) : EndpointWithoutRequest<IResult>
{
    public override void Configure() => Get("/databases/{databaseId}/replication/live");

    public override async ValueTask<IResult> HandleAsync(CancellationToken cancellationToken) =>
        await LiveDatabaseView.StreamAsync(HttpContext, "replication", async (context, database, cancellationToken) =>
        {
            var replication = await DatabaseReplication.LoadAsync(HttpContext, context, database, databases, cancellationToken);
            var page = new DatabaseDetailPage(ShellModel.For(HttpContext, context, database.Name + " · Replication"), database, "replication", [])
            { Replication = replication };
            return new(DatabaseReplicationView.Create(page));
        }, cancellationToken);
}
