namespace TerraScale.Platform.Features.Shapes.Endpoints;

public sealed class GetRowEndpoint(ShapeReader reader) : Endpoint<GetRowRequest, ReadShapeRowOutcome>
{
    public override void Configure()
    {
        Get(ShapeParameters.RowRoute);
        Name("getRow");
        Summary("Reads one row");
        Description("Requires sql:read. key is the row id as a decimal string, as shapes report it. columns optionally names the columns to return, comma-separated. "
            + "The read runs through TerraBase as the caller, so Rules apply; a missing row and a hidden one both answer 404 row_not_found.");
        Tags("Rows");
        AllowAnonymous();
    }

    public override async ValueTask<ReadShapeRowOutcome> HandleAsync(
        GetRowRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (ShapeParameters.Only(HttpContext.Request.Query, "columns") is { } unknown)
        {
            return unknown;
        }

        return await reader.RowAsync(HttpContext, request, cancellationToken);
    }
}
