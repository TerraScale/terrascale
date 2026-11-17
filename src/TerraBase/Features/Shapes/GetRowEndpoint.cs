using System.Globalization;
using System.Text.Json;
using Microsoft.OpenApi;
using TerraBase.Errors;
using TerraBase.Features.ApiKeys;
using TerraBase.Features.Management.Usage;
using TerraBase.Features.UserAuth;
using TerraBase.Hosting;
using TerraBase.Tenancy;
using TerraScale.Endpoints;

namespace TerraBase.Features.Shapes;

internal sealed record RowResponse(JsonElement Row);

internal sealed class GetRowEndpoint(ITenantDirectory tenants, IShapeRows rows, IOperationAllowance allowance) : EndpointWithoutRequest<IResult>
{
    private static readonly Validation BadKey = new("key must be a row id as a canonical decimal integer");

    private static readonly Validation BadColumns = new($"columns is at most once, non-empty and at most {ShapeFilters.MaxColumns} characters");

    public override void Configure()
    {
        Get(DatabaseRoute.Template + "/tables/{table}/rows/{key}");
        Name("getRow");
        Summary("Reads one row");
        Description("Requires sql:read. key is the row's key as shapes report it: the row id as a decimal string. columns optionally names the columns to return, comma-separated. "
            + "An end-user token reads a row only when the table's read rule allows it, and only the rule's readable columns; a missing row and a hidden one both answer 404 row_not_found. "
            + "Counts as one SQL read in the operation allowance.");
        Tags("Rows");
        Produces<RowResponse>();
        Produces<ErrorBody>(StatusCodes.Status400BadRequest);
        Produces<ErrorBody>(StatusCodes.Status401Unauthorized);
        Produces<ErrorBody>(StatusCodes.Status403Forbidden);
        Produces<ErrorBody>(StatusCodes.Status404NotFound);
        Produces<ErrorBody>(StatusCodes.Status429TooManyRequests);
        Produces<ErrorBody>(StatusCodes.Status503ServiceUnavailable);
        Metadata(new OpenApiParameter
        {
            Name = "columns",
            In = ParameterLocation.Query,
            Description = "Comma-separated columns to return; every readable column when omitted.",
            Schema = new OpenApiSchema { Type = JsonSchemaType.String },
        });
        RequireAuthorization(ApiKeyScopes.SqlRead);
        Metadata(new ListenerMetadata(TerraBaseListener.Client), OriginNoStore.Instance);
    }

    public override async ValueTask<IResult> HandleAsync(CancellationToken cancellationToken) =>
        await DatabaseRoute.CheckTenantAsync(HttpContext, tenants, cancellationToken) switch
        {
            TenantContext database => await ReadAsync(database, cancellationToken),
            Validation error => ErrorResponse.From(error),
            NotFound error => ErrorResponse.From(error),
            Unavailable error => ErrorResponse.From(error),
        };

    private async ValueTask<IResult> ReadAsync(TenantContext database, CancellationToken cancellationToken)
    {
        var table = HttpContext.Request.RouteValues["table"] as string;
        if (!ShapeTables.IsValidName(table))
        {
            return ErrorResponse.From(ShapeRoute.BadTable);
        }

        if (Key(HttpContext.Request.RouteValues["key"] as string) is not { } key)
        {
            return ErrorResponse.From(BadKey);
        }

        string? columns = null;
        if (HttpContext.Request.Query.TryGetValue("columns", out var lists))
        {
            if (lists.Count != 1 || lists[0] is not { Length: > 0 and <= ShapeFilters.MaxColumns } list)
            {
                return ErrorResponse.From(BadColumns);
            }

            columns = list;
        }

        switch (await allowance.CheckAsync(database, MeteredOperation.SqlRead, cancellationToken))
        {
            case QuotaExceeded error:
                return ErrorResponse.From(error);
            case Unavailable error:
                return ErrorResponse.From(error);
        }

        var viewer = HttpContext.Features.Get<ProjectAccess>()?.Auth is { } auth ? new ShapeViewer(auth) : null;
        return await rows.ReadRowAsync(database, table!, key, new ShapeQuery(null, columns, viewer), cancellationToken) switch
        {
            ShapeRow row => TypedResults.Bytes(row.Json, "application/json"),
            Validation error => ErrorResponse.From(error),
            Forbidden error => ErrorResponse.From(error),
            NotFound error => ErrorResponse.From(error),
            Unavailable error => ErrorResponse.From(error),
        };
    }

    private static long? Key(string? text) =>
        text is { Length: > 0 and <= 20 } && long.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var key)
        && key.ToString(CultureInfo.InvariantCulture) == text
            ? key
            : null;
}
