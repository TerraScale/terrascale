using TerraBase.Features.Callers;
using TerraBase.Features.Sql;
using TerraBase.Hosting;
using TerraScale.Endpoints;

namespace TerraBase.Features.Rows;

internal sealed class BatchRowsEndpoint(RowsService rows)
    : Endpoint<RowsBatchInput, RowsBatchResponse>
{
    public override void Configure()
    {
        Post(RowsService.Batch);
        Name("batchRows");
        Summary("Runs up to 100 row operations as one transaction");
        Description("{\"operations\":[{\"op\":\"create|update|delete\",\"table\",\"key\"?,\"row\"?}]}: all or nothing, each checked against the rules for end users.");
        Tags("Rows");
        AllowAnonymous();
        Metadata(ListenerMetadata.Client);
    }

    public override async ValueTask<RowsBatchResponse> HandleAsync(
        RowsBatchInput request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Operations is not { Count: > 0 and <= RowsService.MaxOperations } operations)
        {
            return new InvalidInput("operations holds 1 to 100 operations", "invalid_request");
        }

        List<RowCommand> commands = [];
        foreach (var operation in operations)
        {
            var action = operation?.Op switch
            {
                "create" => RowAction.Create,
                "update" => RowAction.Update,
                "delete" => RowAction.Delete,
                _ => (RowAction?)null,
            };
            if (operation is null || action is not { } verb || operation.Table is not { Length: > 0 } table)
            {
                return new InvalidInput("each operation has op (create, update or delete) and table", "invalid_request");
            }

            long? key = null;
            if (verb != RowAction.Create)
            {
                if (RowsService.Key(operation.Key) switch { long parsed => parsed, InvalidInput => (long?)null } is not { } parsedKey)
                {
                    return new InvalidInput("update and delete need the row's integer key", "invalid_key");
                }

                key = parsedKey;
            }

            commands.Add(new RowCommand(verb, table, key, operation.Row));
        }

        return RowsResponses.Batch(await rows.RunAsync(HttpContext.Caller, request.Project, request.Database, commands, batch: true, cancellationToken));
    }
}
