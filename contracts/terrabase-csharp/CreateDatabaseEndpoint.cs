using TerraBase.Engine;
using TerraBase.Errors;
using TerraBase.Features.ApiKeys;
using TerraBase.Features.Replication;
using TerraBase.Hosting;
using TerraBase.Tenancy;
using TerraScale.Endpoints;

namespace TerraBase.Features.Documents;

/// <summary>Database as returned by the client API.</summary>
/// <param name="Id">Database id (a canonical uppercase ULID), for keys narrowed to it.</param>
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
        Name("legacyCreateDatabase");
        Summary("Creates a database");
        Description("{project} is a project of the API key's organization. A key narrowed to one database cannot create others. A project the key does not reach answers 404.");
        Tags("Documents");
        Produces<DatabaseBody>(StatusCodes.Status201Created);
        Produces<DatabaseBody>();
        Produces<ErrorBody>(StatusCodes.Status404NotFound);
        Produces<ErrorBody>(StatusCodes.Status503ServiceUnavailable);
        RequireAuthorization(ApiKeyScopes.DocumentsWrite);
        Metadata(ListenerMetadata.Client, ServedByCatalogPrimary.WhenWriting);
    }

    /// <inheritdoc />
    public override async ValueTask<IResult> HandleAsync(CancellationToken cancellationToken) =>
        DocumentRoute.CheckDatabase(HttpContext) switch
        {
            Validation error => ErrorResponse.From(error),
            DatabaseName name => await CreateAsync(name, cancellationToken),
        };

    private async ValueTask<IResult> CreateAsync(DatabaseName name, CancellationToken cancellationToken)
    {
        if (HttpContext.Features.Get<UserAuth.ProjectAccess>() is not null)
        {
            return ErrorResponse.From(DocumentRoute.UnknownDatabase);
        }

        var key = ApiKey.Of(HttpContext);
        var project = DocumentRoute.Project(HttpContext);
        if (key.DatabaseId is not null)
        {
            // Only its own database exists for such a key, and it already does.
            return await DocumentRoute.CheckTenantAsync(HttpContext, tenants, cancellationToken) switch
            {
                TenantContext own => TypedResults.Ok(Body(own, name.Value)),
                Validation error => ErrorResponse.From(error),
                NotFound error => ErrorResponse.From(error),
                Unavailable error => ErrorResponse.From(error),
            };
        }

        if (key.ProjectId is not null && !string.Equals(key.ProjectId, project, StringComparison.Ordinal))
        {
            return ErrorResponse.From(DocumentRoute.UnknownDatabase);
        }

        return await tenants.CreateDatabaseAsync(key.OrganizationId, project, name.Value, cancellationToken) switch
        {
            ProvisionedDatabase { Created: true } created => TypedResults.Created($"/v1/projects/{project}/databases/{name}", Body(created.Database, name.Value)),
            ProvisionedDatabase existing => TypedResults.Ok(Body(existing.Database, name.Value)),
            NotFound => ErrorResponse.From(DocumentRoute.UnknownDatabase),
            Unavailable error => ErrorResponse.From(error),
        };
    }

    private static DatabaseBody Body(TenantContext database, string name) => new(database.DatabaseId!, database.ProjectId!, name);
}
