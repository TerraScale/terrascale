using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Http.HttpResults;
using TerraScale.Dashboard.Features.Data;
using TerraScale.Endpoints;

namespace TerraScale.Dashboard.Features.Databases;

public sealed class MutateTableRowEndpoint(DashboardSessions sessions, AccountService accounts, DataService data, DatabaseService databases, IDatabaseBackend backend, SqlRuns runs)
    : EndpointWithoutRequest<IResult>
{
    private const string WriteDenied = "Write access is required to change data or schema. Your role can run read-only statements.";

    public override void Configure() => Post("/api/v1/dashboard/databases/tables/rows").AuthenticateInHandler();

    public override async ValueTask<IResult> HandleAsync(CancellationToken cancellationToken)
    {
        var http = HttpContext;
        var context = await AccountRequest.RequireAsync(http, sessions, accounts);
        if (context is null)
        {
            return TypedResults.Unauthorized();
        }

        var form = await FormRequests.ReadAsync(http, cancellationToken);
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

        var scope = DatabaseService.Scope(context);
        var home = await databases.HomeAsync(context, database, cancellationToken);
        var catalog = await backend.SqlTablesAsync(scope, home, database.Id, cancellationToken);
        var table = form["table"].ToString();
        var outcome = await TableRowMutations.ApplyAsync(data, context, database.Id, catalog, table, form["operation"].ToString(), form["payload"].ToString(), cancellationToken);
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
        return TypedResults.Json(new TableRowMutationResponse(true, mutated.Result.RowsAffected, mutated.Sql, snapshot?.RowCount, snapshot?.Preview.Columns, snapshot?.Preview.Rows),
            TableRowJson.Default.TableRowMutationResponse);
    }

    private static JsonHttpResult<TableRowMutationRefusal> Refused(int status, string message)
    {
        return TypedResults.Json(new TableRowMutationRefusal(false, message), TableRowJson.Default.TableRowMutationRefusal, statusCode: status);
    }
}

internal sealed record TableRowSnapshot(long RowCount, DatabaseSqlResult Preview);

internal sealed record TableRowMutationResponse(bool Ok, long RowsAffected, string Sql, long? RowCount,
    IReadOnlyList<DatabaseSqlColumn>? Columns, IReadOnlyList<IReadOnlyList<JsonElement>>? Rows);

internal sealed record TableRowMutationRefusal(bool Ok, string Error);

[JsonSourceGenerationOptions(JsonSerializerDefaults.Web)]
[JsonSerializable(typeof(TableRowMutationResponse))]
[JsonSerializable(typeof(TableRowMutationRefusal))]
internal sealed partial class TableRowJson : JsonSerializerContext;
