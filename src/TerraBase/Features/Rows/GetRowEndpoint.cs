using TerraBase.Features.Callers;
using TerraBase.Features.Sql;
using TerraBase.Hosting;
using TerraScale.Endpoints;

namespace TerraBase.Features.Rows;

internal sealed class GetRowEndpoint(RowsService rows) : Endpoint<RowInput, RowResponse>
{
    public override void Configure()
    {
        Get(RowsService.Row);
        Name("getRow");
        Summary("Reads a row by key, filtered by the read rule for end users");
        Tags("Rows");
        AllowAnonymous();
        Metadata(ListenerMetadata.Client);
    }

    public override async ValueTask<RowResponse> HandleAsync(RowInput request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        return RowsService.Key(request.Key) switch
        {
            long key => RowsResponses.Single(await rows.RunAsync(HttpContext.Caller, request.Project, request.Database,
                [new RowCommand(RowAction.Read, request.Table, key, request.Row)], batch: false, cancellationToken)),
            InvalidInput invalid => invalid,
        };
    }
}
