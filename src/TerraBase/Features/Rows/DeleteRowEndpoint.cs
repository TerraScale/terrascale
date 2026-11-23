using TerraBase.Features.Callers;
using TerraBase.Features.Sql;
using TerraBase.Hosting;
using TerraScale.Endpoints;

namespace TerraBase.Features.Rows;

internal sealed class DeleteRowEndpoint(RowsService rows) : Endpoint<RowInput, DeletedRowResponse>
{
    public override void Configure()
    {
        Delete(RowsService.Row);
        Name("deleteRow");
        Summary("Deletes a row, checking the delete rule for end users");
        Tags("Rows");
        AllowAnonymous();
        Metadata(ListenerMetadata.Client);
    }

    public override async ValueTask<DeletedRowResponse> HandleAsync(RowInput request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        return RowsService.Key(request.Key) switch
        {
            long key => RowsResponses.Deleted(await rows.RunAsync(HttpContext.Caller, request.Project, request.Database,
                [new RowCommand(RowAction.Delete, request.Table, key, request.Row)], batch: false, cancellationToken)),
            InvalidInput invalid => invalid,
        };
    }
}
