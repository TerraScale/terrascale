using TerraBase.Errors;
using TerraBase.Features.ApiKeys;
using TerraBase.Features.Management;
using TerraBase.Hosting;
using TerraBase.Tenancy;
using TerraScale.Endpoints;

namespace TerraBase.Features.Shapes;

internal sealed class GetShapeGrantEndpoint(IManagementService management, IShapeGrants grants) : EndpointWithoutRequest<IResult>
{
    public override void Configure()
    {
        Get(ShapeGrantRoute.Grant);
        Name("getReadGrant");
        Summary("Reads a table's read grant");
        Description(ShapeGrantRoute.Description);
        Tags("Live");
        Produces<ShapeGrantBody>();
        Produces<ErrorBody>(StatusCodes.Status404NotFound);
        Produces<ErrorBody>(StatusCodes.Status503ServiceUnavailable);
        RequireAuthorization(ApiKeyScopes.DatabasesRead);
        Metadata(new ProjectManagementRoute(), ListenerMetadata.Management);
    }

    public override async ValueTask<IResult> HandleAsync(CancellationToken cancellationToken)
    {
        if (HttpContext.Request.RouteValues["table"] is not string table || !ShapeTables.IsValidName(table))
        {
            return ErrorResponse.From(ShapeRoute.BadTable);
        }

        var resolved = await ShapeGrantRoute.DatabaseAsync(HttpContext, management, cancellationToken);
        if (resolved is not TenantContext database)
        {
            return (IResult)resolved;
        }

        return await grants.GetGrantAsync(database.DatabaseId!, table, cancellationToken) switch
        {
            ShapeGrant grant => TypedResults.Ok(ShapeGrantBody.From(grant)),
            NotFound error => ErrorResponse.From(error),
            Unavailable error => ErrorResponse.From(error),
        };
    }
}
