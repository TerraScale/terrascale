using TerraScale.Endpoints;
using TerraScale.TerraBase.Engine;
using TerraScale.TerraBase.Features.UserAuth;
using TerraScale.TerraBase.Hosting;

namespace TerraScale.TerraBase.Features.Management.Schema;

/// <summary><c>GET …/databases/{database}/collections/{collection}/schema</c>: infers the fields of a collection.</summary>
public sealed class GetCollectionSchemaEndpoint(IManagementService management, ISchemaService schema) : EndpointWithoutRequest<IResult>
{
    /// <inheritdoc />
    public override void Configure()
    {
        Get(ManagementRoute.Database + "/collections/{collection}/schema");
        Name("getCollectionSchema");
        Summary("Infers the fields of a collection");
        Description("Read-only and inferred: TerraBase has no declared schemas. Samples the first 1000 live documents in id order (at most 8 MiB of JSON) and reports object paths up to depth 8, how many sampled documents contain them and their JSON types. Arrays are not descended into. At most 10000 paths, 1024 UTF-8 bytes per path, 1 MiB of paths total and an 8 MiB response; excess or an exhausted scan budget answers 400 schema too expensive. 404 when the collection does not exist; an empty collection has no fields." + " " + ManagementRoute.IdentityDescription);
        Tags(ManagementRoute.Tag);
        Produces<CollectionSchema>();
        Produces<ErrorBody>(StatusCodes.Status400BadRequest);
        Produces<ErrorBody>(StatusCodes.Status404NotFound);
        RequireAuthorization(UserAuthExtensions.PolicyName);
        Metadata(ListenerMetadata.Management);
    }

    /// <inheritdoc />
    public override async ValueTask<IResult> HandleAsync(CancellationToken cancellationToken)
    {
        if (!DatabaseName.TryParse(ManagementRoute.Value(HttpContext, "collection"), out var collection))
        {
            throw new BadHttpRequestException("collection must be a lowercase name of at most 63 characters");
        }

        try
        {
            return await ManagementRoute.DatabaseAsync(HttpContext, management, cancellationToken) is { } database
                && await schema.InferAsync(database, collection.Value, cancellationToken) is { } inferred
                ? TypedResults.Ok(inferred)
                : TypedResults.NotFound();
        }
        catch (QueryCapacityException)
        {
            return TypedResults.BadRequest("schema too expensive");
        }
    }
}
