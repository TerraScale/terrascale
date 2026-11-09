using TerraBase.Engine;
using TerraBase.Errors;
using TerraBase.Features.ApiKeys;
using TerraBase.Hosting;
using TerraBase.Tenancy;
using TerraScale.Endpoints;

namespace TerraBase.Features.Management;

public sealed record DatabaseBody(string Id, string ProjectId, string Name);

internal sealed class CreateDatabaseEndpoint(ITenantDirectory tenants) : EndpointWithoutRequest<IResult>
{
    public override void Configure()
    {
        Put(DatabaseRoute.Template);
        Name("legacyCreateDatabase");
        Summary("Creates a database");
        Description("{project} is a project of the API key's organization. A key narrowed to one database cannot create others. A project the key does not reach answers 404. 409 database_limit when the organization's live databases reach its plan's allowance, counted at each database's home region, so a delete its home acknowledged with 204 has released its slot.");
        Tags("Databases");
        Produces<DatabaseBody>(StatusCodes.Status201Created);
        Produces<DatabaseBody>();
        Produces<ErrorBody>(StatusCodes.Status404NotFound);
        Produces<ErrorBody>(StatusCodes.Status409Conflict);
        Produces<ErrorBody>(StatusCodes.Status503ServiceUnavailable);
        RequireAuthorization(ApiKeyScopes.SqlWrite);
        Metadata(ListenerMetadata.Client);
    }

    public override async ValueTask<IResult> HandleAsync(CancellationToken cancellationToken) =>
        DatabaseRoute.CheckDatabase(HttpContext) switch
        {
            Validation error => ErrorResponse.From(error),
            DatabaseName name => await CreateAsync(name, cancellationToken),
        };

    private async ValueTask<IResult> CreateAsync(DatabaseName name, CancellationToken cancellationToken)
    {
        if (HttpContext.Features.Get<UserAuth.ProjectAccess>() is not null)
        {
            return ErrorResponse.From(DatabaseRoute.UnknownDatabase);
        }

        var key = ApiKey.Of(HttpContext);
        var project = DatabaseRoute.Project(HttpContext);
        if (key.DatabaseId is not null)
        {
            return await DatabaseRoute.CheckTenantAsync(HttpContext, tenants, cancellationToken) switch
            {
                TenantContext own => TypedResults.Ok(Body(own, name.Value)),
                Validation error => ErrorResponse.From(error),
                NotFound error => ErrorResponse.From(error),
                Unavailable error => ErrorResponse.From(error),
            };
        }

        if (key.ProjectId is not null && !string.Equals(key.ProjectId, project, StringComparison.Ordinal))
        {
            return ErrorResponse.From(DatabaseRoute.UnknownDatabase);
        }

        return await tenants.CreateDatabaseAsync(key.OrganizationId, project, name.Value, cancellationToken) switch
        {
            ProvisionedDatabase { Created: true } created => TypedResults.Created($"/v1/projects/{project}/databases/{name}", Body(created.Database, name.Value)),
            ProvisionedDatabase existing => TypedResults.Ok(Body(existing.Database, name.Value)),
            NotFound => ErrorResponse.From(DatabaseRoute.UnknownDatabase),
            Conflict error => ErrorResponse.From(error),
            Unavailable error => ErrorResponse.From(error),
        };
    }

    private static DatabaseBody Body(TenantContext database, string name) => new(database.DatabaseId!, database.ProjectId!, name);
}
