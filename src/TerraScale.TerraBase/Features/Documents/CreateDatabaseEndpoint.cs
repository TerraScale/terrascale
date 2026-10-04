using TerraScale.Endpoints;
using TerraScale.TerraBase.Features.ApiKeys;
using TerraScale.TerraBase.Hosting;

namespace TerraScale.TerraBase.Features.Documents;

/// <summary>Database as returned by the client API.</summary>
/// <param name="Name">Database name.</param>
public sealed record DatabaseBody(string Name);

/// <summary><c>PUT /v1/databases/{database}</c>: creates a database (idempotent).</summary>
public sealed class CreateDatabaseEndpoint(IDocumentService documents) : EndpointWithoutRequest<IResult>
{
    /// <inheritdoc />
    public override void Configure()
    {
        Put("/v1/databases/{database}");
        Name("createDatabase");
        Summary("Creates a database");
        Tags("Documents");
        Produces<DatabaseBody>(StatusCodes.Status201Created);
        Produces<DatabaseBody>();
        RequireAuthorization(ApiKeyScopes.DocumentsWrite);
        Metadata(ListenerMetadata.Client);
    }

    /// <inheritdoc />
    public override async ValueTask<IResult> HandleAsync(CancellationToken cancellationToken)
    {
        var database = DocumentRoute.Database(HttpContext);
        var body = new DatabaseBody(database.Value);
        return await documents.CreateDatabaseAsync(database, cancellationToken)
            ? TypedResults.Created($"/v1/databases/{database}", body)
            : TypedResults.Ok(body);
    }
}
