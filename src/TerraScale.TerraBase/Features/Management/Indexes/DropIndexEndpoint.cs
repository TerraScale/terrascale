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
        Produces<string>(StatusCodes.Status404NotFound);
        RequireAuthorization(UserAuthExtensions.PolicyName);
        Metadata(ListenerMetadata.Management);
    }

    /// <inheritdoc />
    public override async ValueTask<IResult> HandleAsync(CancellationToken cancellationToken) =>
        await ManagementRoute.DatabaseAsync(HttpContext, management, cancellationToken) is { } database
        && await indexes.DropAsync(database, ManagementRoute.Value(HttpContext, "index"), cancellationToken)
            ? TypedResults.NoContent()
            : TypedResults.NotFound();
}
