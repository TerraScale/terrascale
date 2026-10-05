using TerraScale.Endpoints;
using TerraScale.TerraBase.Engine;
using TerraScale.TerraBase.Features.UserAuth;
using TerraScale.TerraBase.Hosting;

namespace TerraScale.TerraBase.Features.Management.Schema;

/// <summary><c>GET …/databases/{database}/collections</c>: lists the collections of a database with their sizes.</summary>
public sealed class ListCollectionsEndpoint(IManagementService management, ISchemaService schema) : EndpointWithoutRequest<IResult>
{
    /// <inheritdoc />
    public override void Configure()
    {
        Get(ManagementRoute.Database + "/collections");
        Name("listDatabaseCollections");
        Summary("Lists the collections of a database");
        Description("Every collection, in name order, with its live document count and their JSON bytes." + " " + ManagementRoute.IdentityDescription);
        Tags(ManagementRoute.Tag);
        Produces<CollectionList>();
        Produces<ErrorBody>(StatusCodes.Status400BadRequest);
        Produces<ErrorBody>(StatusCodes.Status404NotFound);
        RequireAuthorization(UserAuthExtensions.PolicyName);
        Metadata(ListenerMetadata.Management);
    }

    /// <inheritdoc />
    public override async ValueTask<IResult> HandleAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await ManagementRoute.DatabaseAsync(HttpContext, management, cancellationToken) is { } database
                ? TypedResults.Ok(new CollectionList(await schema.ListCollectionsAsync(database, cancellationToken)))
                : TypedResults.NotFound();
        }
        catch (QueryCapacityException)
        {
            return TypedResults.BadRequest("query too expensive");
        }
    }
}
