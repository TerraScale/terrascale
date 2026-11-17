using TerraScale.Dashboard.Common.Live;
using TerraScale.Dashboard.Common.UI;
using TerraScale.Endpoints;

namespace TerraScale.Dashboard.Features.Databases;

public sealed class DatabaseTablesLiveEndpoint(IDatabaseBackend backend, DatabaseService databases) : EndpointWithoutRequest<IResult>
{
    public override void Configure() => Get("/databases/{databaseId}/tables/live");

    public override async ValueTask<IResult> HandleAsync(CancellationToken cancellationToken) =>
        await LiveDatabaseView.StreamAsync(HttpContext, "tables-browser", async (context, database, cancellationToken) =>
        {
            var scope = DatabaseService.Scope(context);
            var home = await databases.HomeAsync(context, database, cancellationToken);
            var tables = await DatabaseTables.LoadAsync(backend, scope, home, database.Id, cancellationToken);
            var selected = HttpContext.Request.Query["table"].ToString();
            var shown = tables.Tables.FirstOrDefault(table => table.Name == selected)
                ?? (selected.Length == 0 && tables.Tables.Count > 0 ? tables.Tables[0] : null);
            var preview = shown is null ? null : await DatabaseTables.PreviewAsync(backend, scope, home, database.Id, shown.Name, cancellationToken);
            var page = new DatabaseDetailPage(ShellModel.For(HttpContext, context, database.Name + " · Tables"), database, "tables", [])
            { Tables = tables, Table = shown?.Name ?? "", Preview = preview };
            return new(DatabaseTablesView.Create(page));
        }, cancellationToken, DatabaseTablesHints.ChangesAsync(HttpContext, cancellationToken));
}
