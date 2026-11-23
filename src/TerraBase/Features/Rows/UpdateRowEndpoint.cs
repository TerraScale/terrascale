using TerraBase.Features.Callers;
using TerraBase.Features.Sql;
using TerraBase.Hosting;
using TerraScale.Endpoints;

namespace TerraBase.Features.Rows;

internal sealed class UpdateRowEndpoint(RowsService rows) : Endpoint<RowInput, Result<RowAnswer, SqlRefusal, Validation, Unauthorized, Forbidden, NotFound, Unavailable>>
{
    public override void Configure()
    {
        Patch(RowsService.Row);
        Name("updateRow");
        Summary("Updates a row's columns, checking the update rule for end users");
        Tags("Rows");
        AllowAnonymous();
        Metadata(ListenerMetadata.Client);
    }

    public override async ValueTask<Result<RowAnswer, SqlRefusal, Validation, Unauthorized, Forbidden, NotFound, Unavailable>> HandleAsync(RowInput request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        return RowsService.Key(request.Key) switch
        {
            long key => RowsResponses.Single(await rows.RunAsync(HttpContext.Caller, request.Project, request.Database,
                [new RowCommand(RowAction.Update, request.Table, key, request.Row)], cancellationToken)),
            Validation invalid => invalid,
        };
    }
}
