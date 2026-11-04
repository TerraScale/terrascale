using TerraBase.Errors;
using TerraBase.Features.ApiKeys;
using TerraBase.Features.Catalog;
using TerraBase.Features.Management;
using TerraBase.Hosting;
using TerraBase.Tenancy;
using TerraScale.Endpoints;

namespace TerraBase.Features.Shapes;

internal sealed class DeleteShapeGrantEndpoint(IManagementService management, IShapeGrants grants) : EndpointWithoutRequest<IResult>
{
    public override void Configure()
    {
        Delete(ShapeGrantRoute.Grant);
        Metadata(AnchoredRevocation.Instance);
        Name("deleteReadGrant");
        Summary("Removes a table's read grant");
        Description("End-user tokens can no longer read the table's shape. " + ProjectApiRoute.IdentityDescription);
        Tags("Live");
        Produces(StatusCodes.Status204NoContent);
        Produces<ErrorBody>(StatusCodes.Status404NotFound);
        Produces<ErrorBody>(StatusCodes.Status503ServiceUnavailable);
        RequireAuthorization(ApiKeyScopes.DatabasesWrite);
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

        return await grants.DeleteGrantAsync(database.DatabaseId!, table, cancellationToken) switch
        {
            true => TypedResults.NoContent(),
            false => ErrorResponse.From(new NotFound("the table has no read grant", "no_read_grant")),
            Unavailable error => ErrorResponse.From(error),
        };
    }
}
