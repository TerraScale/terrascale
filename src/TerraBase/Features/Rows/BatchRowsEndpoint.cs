using TerraBase.Errors;
using TerraBase.Features.ApiKeys;
using TerraBase.Features.Management;
using TerraBase.Features.Shapes;
using TerraBase.Hosting;
using TerraScale.Endpoints;

namespace TerraBase.Features.Rows;

internal sealed class BatchRowsEndpoint(RowsRouteServices services) : EndpointWithoutRequest<IResult>
{
    public override void Configure()
    {
        Post(RowsRoute.Batch);
        Name("batchRows");
        Summary("Writes rows in one transaction");
        Description("{\"operations\":[…]} with 1 to 100 operations, each {\"op\":\"create\",\"table\":…,\"row\":{…}}, {\"op\":\"update\",\"table\":…,\"key\":…,\"row\":{…}} "
            + "or {\"op\":\"delete\",\"table\":…,\"key\":…}, across any tables of the database. They run in order in one transaction, all or nothing; "
            + "answers 200 {\"results\":[{\"key\":…,\"row\":…}]} in the same order, or the first failing operation's error with details.index, having applied nothing. "
            + "Each operation is checked as its single-row route." + RowsRoute.Description);
        Tags("Rows");
        Metadata(new RequestBodyMetadata(typeof(RowsBatchBody), DatabaseRoute.JsonMediaType));
        Produces<RowsBatchAnswer>();
        Produces<RowsErrorBody>(StatusCodes.Status400BadRequest);
        Produces<RowsErrorBody>(StatusCodes.Status403Forbidden);
        Produces<RowsErrorBody>(StatusCodes.Status404NotFound);
        Produces<RowsErrorBody>(StatusCodes.Status409Conflict);
        Produces<ErrorBody>(StatusCodes.Status413PayloadTooLarge);
        Produces<ErrorBody>(StatusCodes.Status421MisdirectedRequest);
        Produces<ErrorBody>(StatusCodes.Status429TooManyRequests);
        Produces<ErrorBody>(StatusCodes.Status503ServiceUnavailable);
        RequireAuthorization(ApiKeyScopes.SqlWrite);
        Metadata(ListenerMetadata.Client, new DatabaseRegions.HomeWrites());
    }

    public override ValueTask<IResult> HandleAsync(CancellationToken cancellationToken) =>
        RowsRoute.ServeAsync(HttpContext, services, body: true, batch: true, Operations,
            answers => TypedResults.Json(new RowsBatchAnswer(answers), RowsJsonContext.Default.RowsBatchAnswer), cancellationToken);

    private static object Operations(byte[]? json)
    {
        if (RequestJson.Read<RowsBatchBody>(json, "invalid_request") is not RowsBatchBody { Operations: { Count: > 0 and <= RowsRoute.MaxOperations } given })
        {
            return RowsRoute.Invalid($"operations holds 1 to {RowsRoute.MaxOperations} operations");
        }

        List<RowOperation> operations = [];
        for (var index = 0; index < given.Count; index++)
        {
            var parsed = Operation(given[index]);
            if (parsed is RowRefused refused)
            {
                return refused with { Details = (refused.Details ?? new RowsErrorDetails()) with { Index = index } };
            }

            operations.Add((RowOperation)parsed);
        }

        return operations;
    }

    private static object Operation(RowsBatchOperation? operation)
    {
        if (operation is not { Table: { } table } || !ShapeTables.IsValidName(table))
        {
            return operation?.Table is null
                ? RowsRoute.Invalid("each operation names op and table")
                : new RowRefused(StatusCodes.Status404NotFound, "unknown_table", "the database has no such table");
        }

        var key = operation.Op is "update" or "delete" ? RowsRoute.Key(operation.Key) : null;
        if (key is RowRefused badKey)
        {
            return badKey;
        }

        var values = operation.Op is "create" or "update" ? RowsRoute.Values(operation.Row) : null;
        if (values is RowRefused badRow)
        {
            return badRow;
        }

        return operation.Op switch
        {
            "create" when operation.Key is null => new CreateRow(table, (IReadOnlyDictionary<string, object?>)values!),
            "update" => new UpdateRow(table, (long)key!, (IReadOnlyDictionary<string, object?>)values!),
            "delete" when operation.Row is null => new DeleteRow(table, (long)key!),
            _ => RowsRoute.Invalid("op is create (table, row), update (table, key, row) or delete (table, key)"),
        };
    }
}
