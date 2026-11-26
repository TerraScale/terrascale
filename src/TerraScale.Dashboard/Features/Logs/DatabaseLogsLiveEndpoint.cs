using TerraScale.Dashboard.Common.Live;
using TerraScale.Dashboard.Common.UI;
using TerraScale.Endpoints;

namespace TerraScale.Dashboard.Features.Logs;

public sealed class DatabaseLogsLiveEndpoint(DatabaseService databases, DatabaseLogBackend logs) : EndpointWithoutRequest<IResult>
{
    public override void Configure() => Get("/databases/{databaseId}/logs/live").AuthenticateInHandler();

    public override async ValueTask<IResult> HandleAsync(CancellationToken cancellationToken) =>
        await LiveDatabaseView.StreamAsync(HttpContext, "request-log-results", async (context, database, cancellationToken) =>
        {
            var home = await databases.HomeAsync(context, database, cancellationToken);
            var batch = await logs.ReadAsync(DatabaseService.Scope(context), home, database.Id, "", cancellationToken);
            var page = new DatabaseLogPage(ShellModel.For(HttpContext, context, database.Name + " · Logs"), database, "", batch, "");
            return new(DatabaseLogResults.Create(page));
        }, cancellationToken);
}
