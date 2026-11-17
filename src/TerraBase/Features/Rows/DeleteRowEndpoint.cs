using TerraBase.Errors;
using TerraBase.Features.ApiKeys;
using TerraBase.Features.Management;
using TerraBase.Hosting;
using TerraScale.Endpoints;

namespace TerraBase.Features.Rows;

internal sealed class DeleteRowEndpoint(RowsRouteServices services) : EndpointWithoutRequest<IResult>
{
    public override void Configure()
    {
        Delete(RowsRoute.Row);
        Name("deleteRow");
        Summary("Deletes a row");
        Description("Deletes the row whose id is key and answers 204. A missing row, or one the caller may not read, answers 404 row_not_found; "
            + "an end user also needs the delete rule to hold (403 rule_denied)." + RowsRoute.Description);
        Tags("Rows");
        Produces(StatusCodes.Status204NoContent);
        Produces<RowsErrorBody>(StatusCodes.Status400BadRequest);
        Produces<RowsErrorBody>(StatusCodes.Status403Forbidden);
        Produces<RowsErrorBody>(StatusCodes.Status404NotFound);
        Produces<ErrorBody>(StatusCodes.Status413PayloadTooLarge);
        Produces<ErrorBody>(StatusCodes.Status421MisdirectedRequest);
        Produces<ErrorBody>(StatusCodes.Status429TooManyRequests);
        Produces<ErrorBody>(StatusCodes.Status503ServiceUnavailable);
        RequireAuthorization(ApiKeyScopes.SqlWrite);
        Metadata(ListenerMetadata.Client, new DatabaseRegions.HomeWrites());
    }

    public override async ValueTask<IResult> HandleAsync(CancellationToken cancellationToken)
    {
        if (RowsRoute.Table(HttpContext) is not string table)
        {
            return RowsRoute.Error((RowRefused)RowsRoute.Table(HttpContext));
        }

        if (RowsRoute.Key(HttpContext.Request.RouteValues["key"] as string) is not long key)
        {
            return RowsRoute.Error(RowsRoute.Invalid("key is a row id: one whole number"));
        }

        return await RowsRoute.ServeAsync(HttpContext, services, body: false, batch: false,
            _ => (IReadOnlyList<RowOperation>)[new DeleteRow(table, key)], _ => TypedResults.NoContent(), cancellationToken);
    }
}
