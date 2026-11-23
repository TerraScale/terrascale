using TerraBase.Features.Callers;
using TerraBase.Features.Sql;
using TerraBase.Hosting;
using TerraScale.Endpoints;

namespace TerraBase.Features.Rows;

internal sealed class CreateRowEndpoint(RowsService rows) : Endpoint<RowInput, CreatedRowResponse>
{
    public override void Configure()
    {
        Post(RowsService.Rows);
        Name("createRow");
        Summary("Inserts a row, checking the create rule for end users");
        Description("Answers 201 Created with {\"key\", \"row\"} and Location naming the row.");
        Tags("Rows");
        AllowAnonymous();
        Metadata(ListenerMetadata.Client);
    }

    public override async ValueTask<CreatedRowResponse> HandleAsync(RowInput request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var outcome = await rows.RunAsync(HttpContext.Caller, request.Project, request.Database,
            [new RowCommand(RowAction.Create, request.Table, null, request.Row)], batch: false, cancellationToken);
        return RowsResponses.Created(outcome, HttpContext.Request.Path.ToUriComponent());
    }
}
