using TerraBase.Engine;
using TerraBase.Errors;
using TerraBase.Features.ApiKeys;
using TerraBase.Hosting;
using TerraBase.Tenancy;
using TerraScale.Endpoints;

namespace TerraBase.Features.Management.Schema;

/// <summary><c>GET …/databases/{database}/schema/{collection}</c>: infers the fields of a collection.</summary>
public sealed class GetCollectionSchemaEndpoint(IManagementService management, ISchemaService schema) : EndpointWithoutRequest<IResult>
{
    /// <inheritdoc />
    public override void Configure()
    {
        Get(ProjectApiRoute.Database + "/schema/{collection}");
        Name("getCollectionSchema");
        Summary("Infers the fields of a collection");
        Description("Returns declared uniqueConstraints alongside inferred fields, independently of any declared validation schema. Samples the first 1000 live documents in id order (at most 8 MiB of JSON) and reports object paths up to depth 8, how many sampled documents contain them and their JSON types. Arrays are not descended into. At most 10000 paths, 1024 UTF-8 bytes per path, 1 MiB of paths total and an 8 MiB response; excess or an exhausted scan budget answers 400 invalid_query, schema too expensive. 404 when the collection does not exist; an empty collection has no inferred fields. PUT on this route declares unique keys before writing records." + " " + ProjectApiRoute.IdentityDescription);
        Tags("Schema");
        Produces<CollectionSchema>();
        Produces<ErrorBody>(StatusCodes.Status400BadRequest);
        Produces<ErrorBody>(StatusCodes.Status404NotFound);
        Produces<ErrorBody>(StatusCodes.Status503ServiceUnavailable);
        RequireAuthorization(ApiKeyScopes.DatabasesRead);
        Metadata(new ProjectManagementRoute(), ListenerMetadata.Management);
    }

    /// <inheritdoc />
    public override async ValueTask<IResult> HandleAsync(CancellationToken cancellationToken)
    {
        if (!DatabaseName.TryParse(HttpContext.Request.RouteValues["collection"] as string, out var collection))
        {
            return ErrorResponse.From(new Validation("collection must be a lowercase name of at most 63 characters"));
        }

        return await ManagementRoute.DatabaseAsync(HttpContext, management, cancellationToken) switch
        {
            TenantContext database => await schema.InferAsync(database, collection.Value, cancellationToken) switch
            {
                CollectionSchema inferred => TypedResults.Ok(inferred),
                NotFound error => ErrorResponse.From(error),
                Validation error => ErrorResponse.From(error),
            },
            NotFound => ErrorResponse.From(new NotFound()),
            Unavailable error => ErrorResponse.From(error),
        };
    }
}
