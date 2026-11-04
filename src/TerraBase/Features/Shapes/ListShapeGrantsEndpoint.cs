using TerraBase.Errors;
using TerraBase.Features.ApiKeys;
using TerraBase.Features.Management;
using TerraBase.Hosting;
using TerraBase.Tenancy;
using TerraScale.Endpoints;

namespace TerraBase.Features.Shapes;

internal sealed class ListShapeGrantsEndpoint(IManagementService management, IShapeGrants grants) : EndpointWithoutRequest<IResult>
{
    public override void Configure()
    {
        Get(ShapeGrantRoute.Grants);
        Name("listReadGrants");
        Summary("Lists a database's read grants");
        Description(ShapeGrantRoute.Description);
        Tags("Live");
        Produces<ShapeGrantsBody>();
        Produces<ErrorBody>(StatusCodes.Status404NotFound);
        Produces<ErrorBody>(StatusCodes.Status503ServiceUnavailable);
        RequireAuthorization(ApiKeyScopes.DatabasesRead);
        Metadata(new ProjectManagementRoute(), ListenerMetadata.Management);
    }

    public override async ValueTask<IResult> HandleAsync(CancellationToken cancellationToken)
    {
        var resolved = await ShapeGrantRoute.DatabaseAsync(HttpContext, management, cancellationToken);
        if (resolved is not TenantContext database)
        {
            return (IResult)resolved;
        }

        return await grants.ListGrantsAsync(database.DatabaseId!, cancellationToken) switch
        {
            IReadOnlyList<ShapeGrant> list => TypedResults.Ok(new ShapeGrantsBody([.. list.Select(ShapeGrantBody.From)])),
            Unavailable error => ErrorResponse.From(error),
        };
    }
}
