using TerraScale.Endpoints;
using TerraScale.TerraBase.Features.ApiKeys;
using TerraScale.TerraBase.Hosting;
using TerraScale.TerraBase.Tenancy;

namespace TerraScale.TerraBase.Features.Documents;

/// <summary>Database as returned by the client API.</summary>
/// <param name="Id">Database id (a lowercase hyphenated UUID), for keys narrowed to it.</param>
/// <param name="ProjectId">Owning project.</param>
/// <param name="Name">Database name, unique within the project.</param>
public sealed record DatabaseBody(string Id, string ProjectId, string Name);

/// <summary><c>PUT /v1/projects/{project}/databases/{database}</c>: creates a database in a project (idempotent).</summary>
public sealed class CreateDatabaseEndpoint(ITenantDirectory tenants) : EndpointWithoutRequest<IResult>
{
    /// <inheritdoc />
    public override void Configure()
    {
        Put(DocumentRoute.DatabaseRoute);
        Name("createDatabase");
        Summary("Creates a database");
        Description("{project} is a project of the API key's organization. A key narrowed to one database cannot create others. A project the key does not reach answers 404.");
        Tags("Documents");
        Produces<DatabaseBody>(StatusCodes.Status201Created);
        Produces<DatabaseBody>();
        RequireAuthorization(ApiKeyScopes.DocumentsWrite);
        Metadata(ListenerMetadata.Client);
    }

    /// <inheritdoc />
    public override async ValueTask<IResult> HandleAsync(CancellationToken cancellationToken)
    {
        var name = DocumentRoute.Database(HttpContext);
        var key = ApiKey.Of(HttpContext);
        var project = DocumentRoute.Project(HttpContext);
        if (key.DatabaseId is not null)
        {
            // Only its own database exists for such a key, and it already does.
            return TypedResults.Ok(Body(await DocumentRoute.TenantAsync(HttpContext, tenants, cancellationToken), name.Value));
        }

        if ((key.ProjectId is not null && !string.Equals(key.ProjectId, project, StringComparison.Ordinal))
            || await tenants.CreateDatabaseAsync(key.OrganizationId, project, name.Value, cancellationToken) is not ({ } database, var created))
        {
            throw DocumentRoute.UnknownDatabase();
        }

        var body = Body(database, name.Value);
        return created ? TypedResults.Created($"/v1/projects/{project}/databases/{name}", body) : TypedResults.Ok(body);
    }

    private static DatabaseBody Body(TenantContext database, string name) => new(database.DatabaseId!, database.ProjectId!, name);
}
