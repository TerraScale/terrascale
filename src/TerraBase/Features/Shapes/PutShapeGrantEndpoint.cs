using TerraBase.Errors;
using TerraBase.Features.ApiKeys;
using TerraBase.Features.Catalog;
using TerraBase.Features.Management;
using TerraBase.Hosting;
using TerraBase.Tenancy;
using TerraScale.Endpoints;

namespace TerraBase.Features.Shapes;

internal sealed class PutShapeGrantEndpoint(IManagementService management, IShapeLineages lineages, IShapeGrants grants, TimeProvider time)
    : Endpoint<ShapeGrantRequest, IResult>
{
    public override void Configure()
    {
        Put(ShapeGrantRoute.Grant);
        Metadata(AnchoredRevocation.Instance);
        Name("putReadGrant");
        Summary("Grants end users read on a table");
        Description(ShapeGrantRoute.Description);
        Tags("Live");
        Produces<ShapeGrantBody>();
        Produces<ErrorBody>(StatusCodes.Status400BadRequest);
        Produces<ErrorBody>(StatusCodes.Status404NotFound);
        Produces<ErrorBody>(StatusCodes.Status503ServiceUnavailable);
        RequireAuthorization(ApiKeyScopes.DatabasesWrite);
        Metadata(new ProjectManagementRoute(), ListenerMetadata.Management);
    }

    public override async ValueTask<IResult> HandleAsync(ShapeGrantRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var checkedGrant = ShapeGrantRoute.Check(HttpContext.Request.RouteValues["table"] as string, request, time.GetUtcNow());
        if (checkedGrant is not ShapeGrant grant)
        {
            return ErrorResponse.From((Validation)checkedGrant);
        }

        var resolved = await ShapeGrantRoute.DatabaseAsync(HttpContext, management, cancellationToken);
        if (resolved is not TenantContext database)
        {
            return (IResult)resolved;
        }

        var probe = new ShapeQuery(grant.Where is { } where ? ShapeFilters.Bind(where, ShapeGrantRoute.Probe) : null,
            grant.Columns is { } columns ? string.Join(',', columns) : null);
        switch (await lineages.CheckAsync(database, grant.Table, probe, cancellationToken))
        {
            case ResolvedShape:
                break;
            case Validation error:
                return ErrorResponse.From(error);
            case Forbidden error:
                return ErrorResponse.From(error);
            case NotFound error:
                return ErrorResponse.From(error);
            case QuotaExceeded error:
                return ErrorResponse.From(error);
            case Unavailable error:
                return ErrorResponse.From(error);
        }

        return await grants.PutGrantAsync(database.DatabaseId!, grant, cancellationToken) switch
        {
            ShapeGrant saved => TypedResults.Ok(ShapeGrantBody.From(saved)),
            Unavailable error => ErrorResponse.From(error),
        };
    }
}
