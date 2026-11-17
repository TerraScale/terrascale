using TerraBase.Errors;
using TerraBase.Features.ApiKeys;
using TerraBase.Features.Management;
using TerraBase.Hosting;
using TerraScale.Endpoints;

namespace TerraBase.Features.Rows;

internal sealed class UpdateRowEndpoint(RowsRouteServices services) : EndpointWithoutRequest<IResult>
{
    public override void Configure()
    {
        Patch(RowsRoute.Row);
        Name("updateRow");
        Summary("Updates a row");
        Description("{\"row\":{…}} with only the columns to change, for the row whose id is key; answers 200 {\"key\":\"…\",\"row\":{…} or null}. "
            + "A missing row, or one the caller may not read, answers 404 row_not_found; the row id never changes. "
            + "UPDATE OR ABORT: conflicts answer as on create." + RowsRoute.Description);
        Tags("Rows");
        Metadata(new RequestBodyMetadata(typeof(RowBody), DatabaseRoute.JsonMediaType));
        Produces<RowAnswer>();
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

        return await RowsRoute.ServeAsync(HttpContext, services, body: true, batch: false,
            json => RowsRoute.Single(json, values => new UpdateRow(table, key, values)),
            answers => TypedResults.Json(answers[0], RowsJsonContext.Default.RowAnswer), cancellationToken);
    }
}
