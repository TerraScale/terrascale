using TerraBase.Engine;
using TerraBase.Errors;
using TerraBase.Features.ApiKeys;
using TerraBase.Hosting;
using TerraBase.Tenancy;
using TerraScale.Endpoints;

namespace TerraBase.Features.Management.Schema;

/// <summary><c>PUT …/schema/{collection}</c>: declares a collection's unique fields and compound keys.</summary>
internal sealed class PutCollectionSchemaEndpoint(IManagementService management, IUniqueSchemaService schema) : Endpoint<UniqueSchemaRequest, IResult>
{
    /// <inheritdoc />
    public override void Configure()
    {
        Put(ProjectApiRoute.Database + "/schema/{collection}");
        Name("declareCollectionSchema");
        Summary("Declares collection uniqueness");
        Description("fields[].name plus unique:true declares a single unique scalar. indexes[].name, fields and unique:true declares a compound key. At most 32 keys with 1 to 4 dotted paths. normalization is none (default), lowercase (invariant lowercase), or trim_lowercase (trim then invariant lowercase), applied to strings. Missing or null components do not claim a key. Arrays and objects are invalid unique values. Keys fit 16 KiB each and 64 KiB per document. Declarations require an empty collection; unchanged declarations are idempotent. Local duplicate writes return 409 unique_violation and roll back their whole transaction. Concurrent replicated duplicates retain their source states; the smallest UTF-8 document key wins each unique value. A document must win every key to be query-visible; GET of a losing key returns 409 unique_violation. This is convergent uniqueness, not globally serialized admission during a partition." + " " + ProjectApiRoute.IdentityDescription);
        Tags("Schema");
        Produces<UniqueSchema>();
        Produces<ErrorBody>(StatusCodes.Status400BadRequest);
        Produces<ErrorBody>(StatusCodes.Status403Forbidden);
        Produces<ErrorBody>(StatusCodes.Status404NotFound);
        Produces<ErrorBody>(StatusCodes.Status409Conflict);
        Produces<ErrorBody>(StatusCodes.Status503ServiceUnavailable);
        RequireAuthorization(ApiKeyScopes.DatabasesWrite);
        Metadata(new ProjectManagementRoute(), ListenerMetadata.Management);
    }

    /// <inheritdoc />
    public override async ValueTask<IResult> HandleAsync(UniqueSchemaRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!DatabaseName.TryParse(HttpContext.Request.RouteValues["collection"] as string, out var collection))
        {
            return ErrorResponse.From(new Validation("collection must be a lowercase name of at most 63 characters"));
        }

        return await ManagementRoute.CheckWritableDatabaseAsync(HttpContext, management, cancellationToken) switch
        {
            NotFound error => ErrorResponse.From(error),
            Forbidden error => ErrorResponse.From(error),
            Unavailable error => ErrorResponse.From(error),
            TenantContext database => await schema.DeclareAsync(database, collection.Value, request, cancellationToken) switch
            {
                UniqueSchema declared => TypedResults.Ok(declared),
                NotFound error => ErrorResponse.From(error),
                Conflict error => ErrorResponse.From(error),
                Validation error => ErrorResponse.From(error),
            },
        };
    }
}
