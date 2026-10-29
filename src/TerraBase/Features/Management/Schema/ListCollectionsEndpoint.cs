using TerraBase.Errors;
using TerraBase.Features.ApiKeys;
using TerraBase.Hosting;
using TerraBase.Tenancy;
using TerraScale.Endpoints;

namespace TerraBase.Features.Management.Schema;

/// <summary><c>GET …/databases/{database}/schema</c>: lists the collections of a database with their sizes.</summary>
internal sealed class ListCollectionsEndpoint(IManagementService management, ISchemaService schema) : EndpointWithoutRequest<IResult>
{
    /// <inheritdoc />
    public override void Configure()
    {
        Get(ProjectApiRoute.Database + "/schema");
        Name("getDatabaseSchema");
        Summary("Lists the collections of a database");
        Description("Every collection, in name order, with its live document count and their JSON bytes. Exhausting the engine read budget answers 400 invalid_query, query too expensive." + " " + ProjectApiRoute.IdentityDescription);
        Tags("Schema");
        Produces<CollectionList>();
        Produces<ErrorBody>(StatusCodes.Status400BadRequest);
        Produces<ErrorBody>(StatusCodes.Status404NotFound);
        Produces<ErrorBody>(StatusCodes.Status503ServiceUnavailable);
        RequireAuthorization(ApiKeyScopes.DatabasesRead);
        Metadata(new ProjectManagementRoute(), ListenerMetadata.Management);
    }

    /// <inheritdoc />
    public override async ValueTask<IResult> HandleAsync(CancellationToken cancellationToken) =>
        await ManagementRoute.DatabaseAsync(HttpContext, management, cancellationToken) switch
        {
            TenantContext database => await schema.ListCollectionsAsync(database, cancellationToken) switch
            {
                CollectionList collections => TypedResults.Ok(collections),
                Validation error => ErrorResponse.From(error),
            },
            NotFound => ErrorResponse.From(new NotFound()),
            Unavailable error => ErrorResponse.From(error),
        };
}
