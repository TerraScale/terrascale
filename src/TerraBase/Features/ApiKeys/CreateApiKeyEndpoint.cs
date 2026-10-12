using TerraBase.Errors;
using TerraBase.Features.Replication;
using TerraBase.Hosting;
using TerraScale.Endpoints;

namespace TerraBase.Features.ApiKeys;

/// <summary>A key to issue.</summary>
/// <param name="Name">Operator-chosen label, 1 to 100 characters.</param>
/// <param name="Scopes">Scopes to grant; see <see cref="ApiKeyScopes"/>.</param>
/// <param name="OrganizationId">The organization (tenant) the key acts for.</param>
/// <param name="ProjectId">Narrows the key to one project of the organization.</param>
/// <param name="DatabaseId">Narrows the key to one database of <paramref name="ProjectId"/>, which it then requires.</param>
/// <param name="ExpiresAt">When the key stops authenticating, at most 366 days away; none lasts until revoked.</param>
public sealed record CreateApiKeyRequest(
    string Name, IReadOnlyList<string> Scopes, string? OrganizationId = null, string? ProjectId = null, string? DatabaseId = null, DateTimeOffset? ExpiresAt = null);

/// <summary>An issued key. <paramref name="Secret"/> is shown only in this response.</summary>
/// <param name="Id">Public identifier.</param>
/// <param name="Name">Label.</param>
/// <param name="Scopes">Granted scopes.</param>
/// <param name="OrganizationId">The organization the key acts for.</param>
/// <param name="ProjectId">The only project it reaches, if narrowed.</param>
/// <param name="DatabaseId">The only database it reaches, if narrowed.</param>
/// <param name="Secret">The bearer secret for <c>Authorization: Bearer</c>.</param>
/// <param name="ExpiresAt">When the key stops authenticating, if it expires.</param>
public sealed record CreatedApiKeyBody(
    string Id, string Name, IReadOnlyList<string> Scopes, string OrganizationId, string? ProjectId, string? DatabaseId, string Secret, DateTimeOffset? ExpiresAt = null);

/// <summary>
/// <c>POST /admin/api-keys</c>: issues a scoped client API key for one organization. Served only on the internal
/// administrative listener, so it trusts the local operator.
/// </summary>
public sealed class CreateApiKeyEndpoint(IApiKeyStore keys, TimeProvider time, ICatalogGate gate) : Endpoint<CreateApiKeyRequest, IResult>
{
    private const int MaxNameLength = 100;

    // A key with an expiry lives at most a year; rotate by issuing a new one and revoking the old one.
    private static readonly TimeSpan MaxLifetime = TimeSpan.FromDays(366);

    /// <inheritdoc />
    public override void Configure()
    {
        Post("/admin/api-keys");
        Name("createApiKey");
        Summary("Issues a scoped client API key");
        Description("organizationId is required: the key reaches only that organization's databases. projectId narrows it to one of its projects, and databaseId (which requires projectId) to one database of that project. expiresAt (optional, in the future, at most 366 days away) ends it; without it the key lasts until revoked.");
        Tags("Administration");
        Produces<CreatedApiKeyBody>(StatusCodes.Status201Created);
        Produces<ErrorBody>(StatusCodes.Status400BadRequest);
        Produces<ErrorBody>(StatusCodes.Status503ServiceUnavailable);
        AllowAnonymous();
    }

    /// <inheritdoc />
    public override async ValueTask<IResult> HandleAsync(CreateApiKeyRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        // Anonymous (the local operator), but it checks the scope against the catalog: not on an unverified one (CatalogGate).
        if (!gate.IsOpen)
        {
            return ErrorResponse.From(new Unavailable("this machine's catalog is not verified against the other machines yet", "catalog_unverified"));
        }

        if (string.IsNullOrWhiteSpace(request.Name) || request.Name.Length > MaxNameLength)
        {
            return ErrorResponse.From(new Validation("name must be 1 to 100 characters"));
        }

        if (request.Scopes is not { Count: > 0 } || request.Scopes.Except(ApiKeyScopes.All).Any())
        {
            return ErrorResponse.From(new Validation($"scopes must be a non-empty subset of: {string.Join(", ", ApiKeyScopes.All)}"));
        }

        if (request.ExpiresAt is { } expires && (expires <= time.GetUtcNow() || expires - time.GetUtcNow() > MaxLifetime))
        {
            return ErrorResponse.From(new Validation("expiresAt must be in the future and at most 366 days away"));
        }

        const string UnknownScope =
            "organizationId must name an organization, projectId one of its projects and databaseId (only with projectId) one of that project's databases";
        if (string.IsNullOrEmpty(request.OrganizationId) || (request.DatabaseId is not null && request.ProjectId is null))
        {
            return ErrorResponse.From(new Validation(UnknownScope));
        }

        // The store checks the scope in the issuing write.
        return await keys.CreateAsync(request.Name, [.. request.Scopes.Distinct()], request.OrganizationId, request.ProjectId, request.DatabaseId, cancellationToken, request.ExpiresAt) switch
        {
            IssuedApiKey issued => TypedResults.Created(
                $"/admin/api-keys/{issued.Key.Id}",
                new CreatedApiKeyBody(issued.Key.Id, issued.Key.Name, issued.Key.Scopes, issued.Key.OrganizationId, issued.Key.ProjectId, issued.Key.DatabaseId, issued.Secret, issued.Key.ExpiresAt)),
            NotFound => ErrorResponse.From(new Validation(UnknownScope)),
            Unavailable error => ErrorResponse.From(error),
        };
    }
}
