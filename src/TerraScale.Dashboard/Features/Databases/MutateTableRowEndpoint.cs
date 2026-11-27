using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Http.HttpResults;
using TerraScale.Dashboard.Features.Data;
using TerraScale.Endpoints;

namespace TerraScale.Dashboard.Features.Databases;

public sealed class MutateTableRowEndpoint(DashboardSessions sessions, AccountService accounts, IDatabaseBackend backend, SqlRuns runs)
    : EndpointWithoutRequest<IResult>
{
    private const string WriteDenied = "Write access is required to change data or schema. Your role can run read-only statements.";

    public override void Configure() => Post("/api/v1/dashboard/databases/tables/rows").AuthenticateInHandler();

    public override async ValueTask<IResult> HandleAsync(CancellationToken cancellationToken)
    {
        var http = HttpContext;
        RequestBodies.Allow(http, 3L * TableRowMutations.PayloadBytes + 8192);
        var required = await AccountRequest.RequireAsync(http, sessions, accounts);
        if (!AccountRequest.TryResolveApi(http, required, out var context, out var rejection))
        {
            return rejection;
        }

        var outcome = await FormRequests.TryReadAsync(http, cancellationToken);
        return outcome switch
        {
            IFormCollection form => await HandleFormAsync(http, context, form, cancellationToken),
            FormReadRefusal { Status: StatusCodes.Status413PayloadTooLarge } => Refused(StatusCodes.Status413PayloadTooLarge, "The change is too large. Send fewer edits at a time."),
            FormReadRefusal refusal => Refused(refusal.Status, refusal.Message),
        };
    }

    private async Task<IResult> HandleFormAsync(HttpContext http, AccountContext context, IFormCollection form, CancellationToken cancellationToken)
    {
        if (!Csrf.Validate(http, form))
        {
            return Refused(StatusCodes.Status400BadRequest, "Invalid form token. Reload the page and try again.");
        }

        var id = form["databaseId"].ToString();
        if (context.Project?.Databases.GetValueOrDefault(id) is not { } database || database.ProjectId != context.Project.Id)
        {
            return TerraScaleIds.IsValid(id)
                ? Refused(StatusCodes.Status404NotFound, "Database " + id + " was not found in this project.")
                : Refused(StatusCodes.Status422UnprocessableEntity, DatabaseService.DatabaseIdRule);
        }

        if (!context.CanWriteProjectData)
        {
            return Refused(StatusCodes.Status403Forbidden, WriteDenied);
        }

        if (PlatformDatabases.IsPlatformProject(database.ProjectId))
        {
            return Refused(StatusCodes.Status403Forbidden, "Platform databases are read-only here.");
        }

        var outcome = await backend.TryRegionsAsync(cancellationToken);
        return outcome switch
        {
            IReadOnlyList<DatabaseRegion> regions => await HandleRegionsAsync(http, context, database, form, regions, cancellationToken),
            DatabaseBackendFailure failure => Refused(failure.Status, failure.Message),
        };
    }

    private async Task<IResult> HandleRegionsAsync(HttpContext http, AccountContext context, DatabaseRecord database, IFormCollection form,
        IReadOnlyList<DatabaseRegion> regions, CancellationToken cancellationToken)
    {
        if (DatabaseRegion.Resolve(regions, database.Region ?? context.Project?.Region) is not { } home)
        {
            return Refused(StatusCodes.Status503ServiceUnavailable, "This database's region is not a TerraBase region.");
        }

        var scope = DatabaseService.Scope(context);
        var outcome = await backend.TrySqlTablesAsync(scope, home, database.Id, cancellationToken);
        return outcome switch
        {
            DatabaseSqlCatalog catalog => await HandleCatalogAsync(http, context, database.Id, scope, home, catalog, form, cancellationToken),
            DatabaseBackendFailure failure => Refused(failure.Status, failure.Message),
        };
    }

    private async Task<IResult> HandleCatalogAsync(HttpContext http, AccountContext context, string databaseId, DatabaseScope scope, DatabaseRegion home,
        DatabaseSqlCatalog catalog, IFormCollection form, CancellationToken cancellationToken)
    {
        var table = form["table"].ToString();
        var outcome = await TableRowMutations.ApplyAsync(backend, scope, home, databaseId, catalog, table, form["operation"].ToString(), form["payload"].ToString(), cancellationToken);
        return outcome switch
        {
            TableRowMutated mutated => await MutatedAsync(http, context, databaseId, scope, home, table, mutated, cancellationToken),
            TableRowTableMissing missing => Refused(StatusCodes.Status422UnprocessableEntity, "Table " + missing.Table + " was not found. Reload the tables list."),
            TableRowPayloadInvalid invalid => Refused(StatusCodes.Status400BadRequest, invalid.Message),
            TableRowColumnUnknown unknown => Refused(StatusCodes.Status422UnprocessableEntity, "Column " + unknown.Column + " is not in " + unknown.Table + "."),
            TableRowKeyRequired key => Refused(StatusCodes.Status422UnprocessableEntity, key.Message),
            TableRowSqlRefused refused => Refused(refused.Status, refused.Message),
        };
    }

    private async Task<IResult> MutatedAsync(HttpContext http, AccountContext context, string databaseId, DatabaseScope scope, DatabaseRegion home, string table,
        TableRowMutated mutated, CancellationToken cancellationToken)
    {
        await OptionalPanel.RunAsync(http, "sql-history", async () =>
        {
            var workspace = await runs.ReadWorkspaceAsync(context, databaseId, cancellationToken);
            await runs.SaveAsync(context, databaseId, workspace is null ? "" : workspace.Draft, SqlHistory.Run(workspace is null ? [] : workspace.History, mutated.Sql),
                mutated.Result, "", StatusCodes.Status200OK, cancellationToken);
        });
        var snapshot = await OptionalPanel.LoadAsync<TableRowSnapshot?>(http, "table-rows", async () =>
            new TableRowSnapshot(await DatabaseTables.CountAsync(backend, scope, home, databaseId, table, cancellationToken),
                await DatabaseTables.PreviewAsync(backend, scope, home, databaseId, table, cancellationToken)), () => null);
        IReadOnlyList<IReadOnlyList<string>>? rawRows = snapshot is null
            ? null
            : [.. snapshot.Preview.Rows.Select(row => (IReadOnlyList<string>)[.. row.Select(cell => cell.GetRawText())])];
        return TypedResults.Json(new TableRowMutationResponse(true, mutated.RowsAffected, mutated.ExpectedRows, mutated.MatchedRows, mutated.Sql,
            snapshot?.RowCount, snapshot?.Preview.Columns, rawRows), TableRowJson.Default.TableRowMutationResponse);
    }

    private static JsonHttpResult<TableRowMutationRefusal> Refused(int status, string message)
    {
        return TypedResults.Json(new TableRowMutationRefusal(false, message), TableRowJson.Default.TableRowMutationRefusal, statusCode: status);
    }
}

internal sealed record TableRowSnapshot(long RowCount, DatabaseSqlResult Preview);

internal sealed record TableRowMutationResponse(bool Ok, long RowsAffected, int ExpectedRows, int MatchedRows, string Sql, long? RowCount,
    IReadOnlyList<DatabaseSqlColumn>? Columns, IReadOnlyList<IReadOnlyList<string>>? Rows);

internal sealed record TableRowMutationRefusal(bool Ok, string Error);

[JsonSourceGenerationOptions(JsonSerializerDefaults.Web)]
[JsonSerializable(typeof(TableRowMutationResponse))]
[JsonSerializable(typeof(TableRowMutationRefusal))]
[JsonSerializable(typeof(string[]))]
[JsonSerializable(typeof(IReadOnlyList<DatabaseTableColumn>))]
internal sealed partial class TableRowJson : JsonSerializerContext;
