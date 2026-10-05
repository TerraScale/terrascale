using TerraScale.Endpoints;
using TerraScale.TerraBase.Features.UserAuth;
using TerraScale.TerraBase.Hosting;

namespace TerraScale.TerraBase.Features.Management.Indexes;

/// <summary><c>DELETE …/indexes/{index}</c>: drops a secondary index.</summary>
public sealed class DropIndexEndpoint(IManagementService management, IIndexService indexes) : EndpointWithoutRequest<IResult>
{
    /// <inheritdoc />
    public override void Configure()
    {
        Delete(ManagementRoute.Index);
        Name("dropIndex");
        Summary("Drops a secondary index");
        Description(ManagementRoute.IdentityDescription);
        Tags(ManagementRoute.Tag);
        Produces<ErrorBody>(StatusCodes.Status404NotFound);
        Produces<ErrorBody>(StatusCodes.Status403Forbidden);
        RequireAuthorization(UserAuthExtensions.PolicyName);
        Metadata(ListenerMetadata.Management, new ProducesResponseTypeMetadata(StatusCodes.Status204NoContent, typeof(void)));
    }

    /// <inheritdoc />
    public override async ValueTask<IResult> HandleAsync(CancellationToken cancellationToken)
    {
        var writable = await ManagementRoute.WritableDatabaseAsync(HttpContext, management, cancellationToken);
        if (writable.Value is not { } database)
        {
            return ManagementRoute.Error(writable.Error);
        }

        return await indexes.DropAsync(database, ManagementRoute.Value(HttpContext, "index"), cancellationToken)
            ? TypedResults.NoContent()
            : TypedResults.NotFound();
    }
}
