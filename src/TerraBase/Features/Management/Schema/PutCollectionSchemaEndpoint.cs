using TerraBase.Engine;
using TerraBase.Errors;
using TerraBase.Features.ApiKeys;
using TerraBase.Hosting;
using TerraBase.Tenancy;
using TerraScale.Endpoints;

namespace TerraBase.Features.Management.Schema;

public sealed class PutCollectionSchemaEndpoint(IManagementService management, IUniqueSchemaService schema) : Endpoint<UniqueSchemaRequest, IResult>
{
    public override void Configure()
    {
        Put(ProjectApiRoute.Database + "/schema/{collection}");
        Name("declareCollectionSchema");
        Summary("Declares collection uniqueness");
        Description("fields[].name plus unique:true declares a single unique scalar. indexes[].name, fields and unique:true declares a compound key. At most 32 keys with 1 to 4 dotted paths. normalization is none (default), lowercase (invariant lowercase), or trim_lowercase (trim then invariant lowercase), applied to strings. Missing or null components do not claim a key. Arrays and objects are invalid unique values. Keys fit 16 KiB each and 64 KiB per document. Declarations require an empty collection; unchanged declarations are idempotent. Duplicate writes return 409 unique_violation and roll back their whole transaction." + " " + ProjectApiRoute.IdentityDescription);
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
