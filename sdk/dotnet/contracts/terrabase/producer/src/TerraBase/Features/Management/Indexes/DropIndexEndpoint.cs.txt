using TerraBase.Errors;
using TerraBase.Features.ApiKeys;
using TerraBase.Hosting;
using TerraBase.Tenancy;
using TerraScale.Endpoints;

namespace TerraBase.Features.Management.Indexes;

/// <summary><c>DELETE …/indexes/{index}</c>: drops a secondary index.</summary>
public sealed class DropIndexEndpoint(IManagementService management, IIndexService indexes) : EndpointWithoutRequest<IResult>
{
    /// <inheritdoc />
    public override void Configure()
    {
        Delete(ProjectApiRoute.Index);
        Name("dropIndex");
        Summary("Drops a secondary index");
        Description(ProjectApiRoute.IdentityDescription);
        Tags("Indexes");
        Produces<ErrorBody>(StatusCodes.Status404NotFound);
        Produces<ErrorBody>(StatusCodes.Status503ServiceUnavailable);
        Produces<ErrorBody>(StatusCodes.Status403Forbidden);
        RequireAuthorization(ApiKeyScopes.DatabasesWrite);
        Metadata(new ProjectManagementRoute(), ListenerMetadata.Management, new ProducesResponseTypeMetadata(StatusCodes.Status204NoContent, typeof(void)));
    }

    /// <inheritdoc />
    public override async ValueTask<IResult> HandleAsync(CancellationToken cancellationToken) =>
        await ManagementRoute.CheckWritableDatabaseAsync(HttpContext, management, cancellationToken) switch
        {
            NotFound error => ErrorResponse.From(error),
            Forbidden error => ErrorResponse.From(error),
            Unavailable error => ErrorResponse.From(error),
            TenantContext database => await indexes.DropAsync(database, ManagementRoute.Value(HttpContext, "index"), cancellationToken) switch
            {
                Done => TypedResults.NoContent(),
                NotFound error => ErrorResponse.From(error),
            },
        };
}
