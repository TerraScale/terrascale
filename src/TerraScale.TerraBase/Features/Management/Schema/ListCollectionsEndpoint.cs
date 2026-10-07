using TerraBase.Errors;
using TerraBase.Features.UserAuth;
using TerraBase.Hosting;
using TerraScale.Endpoints;

namespace TerraBase.Features.Management.Schema;

/// <summary><c>GET …/databases/{database}/collections</c>: lists the collections of a database with their sizes.</summary>
public sealed class ListCollectionsEndpoint(IManagementService management, ISchemaService schema) : EndpointWithoutRequest<IResult>
{
    /// <inheritdoc />
    public override void Configure()
    {
        Get(ManagementRoute.Database + "/collections");
        Name("listDatabaseCollections");
        Summary("Lists the collections of a database");
        Description("Every collection, in name order, with its live document count and their JSON bytes. Exhausting the engine read budget answers 400 invalid_query, query too expensive." + " " + ManagementRoute.IdentityDescription);
        Tags(ManagementRoute.Tag);
        Produces<CollectionList>();
        Produces<ErrorBody>(StatusCodes.Status400BadRequest);
        Produces<ErrorBody>(StatusCodes.Status404NotFound);
        RequireAuthorization(UserAuthExtensions.PolicyName);
        Metadata(ListenerMetadata.Management);
    }

    /// <inheritdoc />
    public override async ValueTask<IResult> HandleAsync(CancellationToken cancellationToken) =>
        await ManagementRoute.DatabaseAsync(HttpContext, management, cancellationToken) is not { } database
            ? ErrorResponse.From(new NotFound())
            : await schema.ListCollectionsAsync(database, cancellationToken) switch
            {
                CollectionList collections => TypedResults.Ok(collections),
                Validation error => ErrorResponse.From(error),
            };
}
