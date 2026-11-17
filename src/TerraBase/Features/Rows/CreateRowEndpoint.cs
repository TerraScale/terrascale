using TerraBase.Errors;
using TerraBase.Features.ApiKeys;
using TerraBase.Features.Management;
using TerraBase.Hosting;
using TerraScale.Endpoints;

namespace TerraBase.Features.Rows;

internal sealed class CreateRowEndpoint(RowsRouteServices services) : EndpointWithoutRequest<IResult>
{
    public override void Configure()
    {
        Post(RowsRoute.Rows);
        Name("createRow");
        Summary("Creates a row");
        Description("{\"row\":{…}} with the columns to set; answers 201 {\"key\":\"…\",\"row\":{…} or null}, where key is the row id. "
            + "INSERT OR ABORT: a UNIQUE or primary-key conflict answers 409 row_exists (details.key when the caller may read that row), "
            + "a NOT NULL, CHECK or foreign-key failure 400 constraint_failed." + RowsRoute.Description);
        Tags("Rows");
        Metadata(new RequestBodyMetadata(typeof(RowBody), DatabaseRoute.JsonMediaType));
        Produces<RowAnswer>(StatusCodes.Status201Created);
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

    public override async ValueTask<IResult> HandleAsync(CancellationToken cancellationToken) =>
        RowsRoute.Table(HttpContext) is not string table
            ? RowsRoute.Error((RowRefused)RowsRoute.Table(HttpContext))
            : await RowsRoute.ServeAsync(HttpContext, services, body: true, batch: false,
                json => RowsRoute.Single(json, values => new CreateRow(table, values)),
                answers => TypedResults.Json(answers[0], RowsJsonContext.Default.RowAnswer, statusCode: StatusCodes.Status201Created), cancellationToken);
}
