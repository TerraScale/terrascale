using TerraBase.Features.Callers;
using TerraBase.Features.Sql;
using TerraBase.Hosting;
using TerraScale.Endpoints;

namespace TerraBase.Features.Rows;

internal sealed class CreateRowEndpoint(RowsService rows) : Endpoint<RowInput, Result<RowAnswer, SqlRefusal, Validation, Unauthorized, Forbidden, NotFound, Unavailable>>
{
    public override void Configure()
    {
        Post(RowsService.Rows);
        Name("createRow");
        Summary("Inserts a row, checking the create rule for end users");
        Tags("Rows");
        AllowAnonymous();
        Metadata(ListenerMetadata.Client);
    }

    public override async ValueTask<Result<RowAnswer, SqlRefusal, Validation, Unauthorized, Forbidden, NotFound, Unavailable>> HandleAsync(RowInput request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        return RowsResponses.Single(await rows.RunAsync(HttpContext.Caller, request.Project, request.Database,
            [new RowCommand(RowAction.Create, request.Table, null, request.Row)], cancellationToken));
    }
}
