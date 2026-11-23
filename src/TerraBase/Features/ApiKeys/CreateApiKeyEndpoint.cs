using TerraBase.Features.Callers;
using TerraBase.Features.Catalog;
using TerraBase.Features.Management;
using TerraBase.Hosting;
using TerraScale.Endpoints;

namespace TerraBase.Features.ApiKeys;

internal sealed class CreateApiKeyEndpoint(ApiKeyStore keys, CatalogStore catalog, TimeProvider time)
    : Endpoint<CreateApiKeyInput, Result<Created<CreatedApiKeyBody>, Validation, Forbidden, Conflict, Unavailable>>
{
    private static readonly TimeSpan MaxLifetime = TimeSpan.FromDays(366);

    public override void Configure()
    {
        Post("/admin/api-keys");
        Name("createApiKey");
        Summary("Issues a scoped client API key");
        Description("organizationId is required; projectId narrows the key to one project and databaseId (with projectId) to one database. expiresAt is at most 366 days away.");
        Tags("Administration");
        AllowAnonymous();
        Metadata(ListenerMetadata.Internal);
    }

    public override async ValueTask<Result<Created<CreatedApiKeyBody>, Validation, Forbidden, Conflict, Unavailable>> HandleAsync(
        CreateApiKeyInput request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!PlanFlow.IsOperator(HttpContext.Caller, "admin"))
        {
            return new Forbidden("issuing API keys needs the admin grant", "forbidden_caller");
        }

        if (string.IsNullOrWhiteSpace(request.Name) || request.Name.Length > 100)
        {
            return new Validation("name must be 1 to 100 characters", "invalid_request");
        }

        if (request.Scopes is not { Count: > 0 } scopes || scopes.Except(Scopes.ApiKey).Any())
        {
            return new Validation(string.Concat("scopes must be a non-empty subset of: ", string.Join(", ", Scopes.ApiKey)), "invalid_request");
        }

        if (request.ExpiresAt is { } expires && (expires <= time.GetUtcNow() || expires - time.GetUtcNow() > MaxLifetime))
        {
            return new Validation("expiresAt must be in the future and at most 366 days away", "invalid_request");
        }

        var unknown = new Validation("organizationId must name an organization, projectId one of its projects and databaseId one of that project's databases", "invalid_request");
        if (ManagementService.Id(request.OrganizationId) is not { } organizationId || (request.DatabaseId is not null && request.ProjectId is null))
        {
            return unknown;
        }

        var projectId = ManagementService.Id(request.ProjectId);
        var databaseId = ManagementService.Id(request.DatabaseId);
        if ((request.ProjectId is not null && projectId is null) || (request.DatabaseId is not null && databaseId is null)
            || !await ReachesAsync(organizationId, projectId, databaseId, cancellationToken))
        {
            return unknown;
        }

        return await keys.CreateAsync(request.Name, [.. scopes.Distinct(StringComparer.Ordinal)], organizationId, projectId, databaseId, request.ExpiresAt, cancellationToken) switch
        {
            IssuedApiKey issued => new Created<CreatedApiKeyBody>(string.Concat("/admin/api-keys/", issued.Key.Id), new CreatedApiKeyBody(issued.Key.Id, issued.Key.Name,
                issued.Key.Scopes, issued.Key.OrganizationId.ToString(), issued.Key.ProjectId?.ToString(), issued.Key.DatabaseId?.ToString(), issued.Secret, issued.Key.ExpiresAt)),
            Conflict conflict => conflict,
            Unavailable unavailable => unavailable,
        };
    }

    private async ValueTask<bool> ReachesAsync(Ulid organizationId, Ulid? projectId, Ulid? databaseId, CancellationToken cancellationToken)
    {
        var organization = await catalog.GetOrganizationAsync(organizationId, cancellationToken) switch
        {
            OrganizationRecord => true,
            NotFound or Unavailable => false,
        };
        if (!organization || projectId is not { } project)
        {
            return organization;
        }

        var inOrganization = await catalog.GetProjectAsync(project, cancellationToken) switch
        {
            ProjectRecord record => record.OrganizationId == organizationId,
            NotFound or Unavailable => false,
        };
        if (!inOrganization || databaseId is not { } database)
        {
            return inOrganization;
        }

        return await catalog.GetDatabaseAsync(database, cancellationToken) switch
        {
            DatabaseRecord record => record.ProjectId == project,
            NotFound or Unavailable => false,
        };
    }
}
