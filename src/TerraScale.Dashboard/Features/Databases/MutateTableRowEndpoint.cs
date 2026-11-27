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
        var context = await AccountRequest.RequireAsync(http, sessions, accounts);
        if (context is null)
        {
            return TypedResults.Unauthorized();
        }

        var formOutcome = await FormRequests.TryReadAsync(http, cancellationToken);
        if (formOutcome is FormReadRefusal formRefusal)
        {
            return Refused(formRefusal.Status, formRefusal.Message);
        }

        var form = formOutcome switch
        {
            IFormCollection parsed => parsed,
            FormReadRefusal => throw new System.Diagnostics.UnreachableException(),
        };

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

        var regionsOutcome = await backend.TryRegionsAsync(cancellationToken);
        if (regionsOutcome is DatabaseBackendFailure regionsFailure)
        {
            return Refused(regionsFailure.Status, regionsFailure.Message);
        }

        var regions = regionsOutcome switch
        {
            IReadOnlyList<DatabaseRegion> list => list,
            DatabaseBackendFailure => [],
        };
        if (DatabaseRegion.Resolve(regions, database.Region ?? context.Project?.Region) is not { } home)
        {
            return Refused(StatusCodes.Status503ServiceUnavailable, "This database's region is not a TerraBase region.");
        }

        var scope = DatabaseService.Scope(context);
        var catalogOutcome = await backend.TrySqlTablesAsync(scope, home, database.Id, cancellationToken);
        if (catalogOutcome is DatabaseBackendFailure catalogFailure)
        {
            return Refused(catalogFailure.Status, catalogFailure.Message);
        }

        var catalog = catalogOutcome switch
        {
            DatabaseSqlCatalog loaded => loaded,
            DatabaseBackendFailure => new DatabaseSqlCatalog([], false),
        };
        var table = form["table"].ToString();
        var outcome = await TableRowMutations.ApplyAsync(backend, scope, home, database.Id, catalog, table, form["operation"].ToString(), form["payload"].ToString(), cancellationToken);
        return outcome switch
        {
            TableRowMutated mutated => await MutatedAsync(http, context, database.Id, scope, home, table, mutated, cancellationToken),
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
        return TypedResults.Json(new TableRowMutationResponse(true, mutated.RowsAffected, mutated.ExpectedRows, mutated.MatchedRows, mutated.Sql,
            snapshot?.RowCount, snapshot?.Preview.Columns, snapshot?.Preview.Rows), TableRowJson.Default.TableRowMutationResponse);
    }

    private static JsonHttpResult<TableRowMutationRefusal> Refused(int status, string message)
    {
        return TypedResults.Json(new TableRowMutationRefusal(false, message), TableRowJson.Default.TableRowMutationRefusal, statusCode: status);
    }
}

internal sealed record TableRowSnapshot(long RowCount, DatabaseSqlResult Preview);

internal sealed record TableRowMutationResponse(bool Ok, long RowsAffected, int ExpectedRows, int MatchedRows, string Sql, long? RowCount,
    IReadOnlyList<DatabaseSqlColumn>? Columns, IReadOnlyList<IReadOnlyList<JsonElement>>? Rows);

internal sealed record TableRowMutationRefusal(bool Ok, string Error);

[JsonSourceGenerationOptions(JsonSerializerDefaults.Web)]
[JsonSerializable(typeof(TableRowMutationResponse))]
[JsonSerializable(typeof(TableRowMutationRefusal))]
[JsonSerializable(typeof(string[]))]
[JsonSerializable(typeof(IReadOnlyList<DatabaseTableColumn>))]
internal sealed partial class TableRowJson : JsonSerializerContext;
