using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Http.Features;
using TerraScale.Authentication.Contracts;

namespace TerraScale.Authentication.Features.Platform;

/// <summary>Creates an API key.</summary>
/// <param name="Name">1 to 128 characters after trimming.</param>
/// <param name="Scopes">A non-empty set of TerraBase scopes.</param>
/// <param name="Database">Project keys only: the TerraBase database the key is narrowed to.</param>
/// <param name="ExpiresAt">Optional expiry, in the future.</param>
/// <param name="AllowedCidrs">Optional IP allowlist: up to 32 IPv4 or IPv6 CIDRs or addresses; absent or empty allows any address.</param>
public sealed record CreateApiKeyRequest(
    [property: JsonPropertyName("name")] string? Name,
    [property: JsonPropertyName("scopes")] IReadOnlyList<string>? Scopes,
    [property: JsonPropertyName("database")] string? Database = null,
    [property: JsonPropertyName("expires_at")] DateTimeOffset? ExpiresAt = null,
    [property: JsonPropertyName("allowed_cidrs")] IReadOnlyList<string>? AllowedCidrs = null);

/// <summary>Replaces an IP allowlist.</summary>
/// <param name="AllowedCidrs">Up to 32 IPv4 or IPv6 CIDRs or addresses; empty allows any address.</param>
public sealed record AllowedCidrsRequest([property: JsonPropertyName("allowed_cidrs")] IReadOnlyList<string>? AllowedCidrs);

/// <summary>Rotates an API key.</summary>
/// <param name="PreviousSecretTtl">Seconds the previous key string keeps working, 0 to 86400 (default 0).</param>
public sealed record RotateApiKeyRequest([property: JsonPropertyName("previous_secret_ttl")] int? PreviousSecretTtl = null);

/// <summary>An API key, never its secret.</summary>
/// <param name="Id">Its id: the OAuth client id and the <c>sub</c> of its tokens.</param>
/// <param name="Name">Its name.</param>
/// <param name="OrganizationId">The owning organization.</param>
/// <param name="ProjectId">The owning project; null for an organization key.</param>
/// <param name="Database">The database it is narrowed to, or null.</param>
/// <param name="Scopes">Its scopes.</param>
/// <param name="CreatedBy">The creating user.</param>
/// <param name="CreatedAt">Creation time.</param>
/// <param name="ExpiresAt">Its expiry, or null.</param>
/// <param name="PreviousSecretExpiresAt">Until when the key string before the last rotation still works, or null.</param>
/// <param name="LastUsedAt">Its last successful token exchange (up to 5 minutes stale), or null when it was never used.</param>
/// <param name="AllowedCidrs">The canonical CIDRs its tokens must be used from; empty allows any address.</param>
public sealed record ApiKeyBody(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("organization_id")] string OrganizationId,
    [property: JsonPropertyName("project_id")] string? ProjectId,
    [property: JsonPropertyName("database")] string? Database,
    [property: JsonPropertyName("scopes")] IReadOnlyList<string> Scopes,
    [property: JsonPropertyName("created_by")] string CreatedBy,
    [property: JsonPropertyName("created_at")] DateTimeOffset CreatedAt,
    [property: JsonPropertyName("expires_at")] DateTimeOffset? ExpiresAt,
    [property: JsonPropertyName("previous_secret_expires_at")] DateTimeOffset? PreviousSecretExpiresAt,
    [property: JsonPropertyName("last_used_at")] DateTimeOffset? LastUsedAt,
    [property: JsonPropertyName("allowed_cidrs")] IReadOnlyList<string> AllowedCidrs);

/// <summary>An API key with its key string, shown once.</summary>
/// <param name="ApiKey">The key.</param>
/// <param name="Key">The whole key string; its first 24 characters are the id.</param>
public sealed record ApiKeySecretBody(
    [property: JsonPropertyName("api_key")] ApiKeyBody ApiKey,
    [property: JsonPropertyName("key")] string Key);

/// <summary>A page of API keys.</summary>
/// <param name="Items">The keys.</param>
/// <param name="Next">The next page's <c>after</c>, or null.</param>
public sealed record ApiKeyPageBody(
    [property: JsonPropertyName("items")] IReadOnlyList<ApiKeyBody> Items,
    [property: JsonPropertyName("next")] string? Next);

/// <summary><c>POST /v1/terrascale/organizations/{organization}/api-keys</c>.</summary>
public sealed class CreateOrganizationApiKeyEndpoint(ApiKeys keys, PlatformAuthentication authentication, TimeProvider clock) : ApiKeyEndpoint(keys, authentication, clock, project: false)
{
    /// <inheritdoc />
    public override void Configure()
    {
        Post($"{Organization}/api-keys");
        this.Document("create_organization_api_key", "Create an organization API key", "Needs admin. The key reaches every project of the organization, one project per token. The key string is answered once.", "API keys", typeof(CreateApiKeyRequest), StatusCodes.Status201Created, typeof(ApiKeySecretBody));
    }

    /// <inheritdoc />
    protected override Task<IResult> HandleAsync(UserRecord caller, CancellationToken cancellationToken) => this.CreateAsync(caller, cancellationToken);
}

/// <summary><c>GET /v1/terrascale/organizations/{organization}/api-keys</c>.</summary>
public sealed class ListOrganizationApiKeysEndpoint(ApiKeys keys, PlatformAuthentication authentication, TimeProvider clock) : ApiKeyEndpoint(keys, authentication, clock, project: false)
{
    /// <inheritdoc />
    public override void Configure()
    {
        Get($"{Organization}/api-keys");
        this.Document("list_organization_api_keys", "List organization API keys", "Needs admin. Unrevoked keys, never secrets, in creation order.", "API keys", null, StatusCodes.Status200OK, typeof(ApiKeyPageBody), paged: true);
    }

    /// <inheritdoc />
    protected override Task<IResult> HandleAsync(UserRecord caller, CancellationToken cancellationToken) => this.ListAsync(caller, cancellationToken);
}

/// <summary><c>GET /v1/terrascale/organizations/{organization}/api-keys/{key}</c>.</summary>
public sealed class GetOrganizationApiKeyEndpoint(ApiKeys keys, PlatformAuthentication authentication, TimeProvider clock) : ApiKeyEndpoint(keys, authentication, clock, project: false)
{
    /// <inheritdoc />
    public override void Configure()
    {
        Get($"{Organization}/api-keys/{{key}}");
        this.Document("get_organization_api_key", "Get an organization API key", "Needs admin.", "API keys", null, StatusCodes.Status200OK, typeof(ApiKeyBody));
    }

    /// <inheritdoc />
    protected override Task<IResult> HandleAsync(UserRecord caller, CancellationToken cancellationToken) => this.GetAsync(caller, cancellationToken);
}

/// <summary><c>DELETE /v1/terrascale/organizations/{organization}/api-keys/{key}</c>.</summary>
public sealed class RevokeOrganizationApiKeyEndpoint(ApiKeys keys, PlatformAuthentication authentication, TimeProvider clock) : ApiKeyEndpoint(keys, authentication, clock, project: false)
{
    /// <inheritdoc />
    public override void Configure()
    {
        Delete($"{Organization}/api-keys/{{key}}");
        this.Document("revoke_organization_api_key", "Revoke an organization API key", "Needs admin. No token is issued for it afterwards; TerraBase refuses tokens already issued within 3 seconds in every region.", "API keys", null, StatusCodes.Status204NoContent, null);
    }

    /// <inheritdoc />
    protected override Task<IResult> HandleAsync(UserRecord caller, CancellationToken cancellationToken) => this.RevokeAsync(caller, cancellationToken);
}

/// <summary><c>POST /v1/terrascale/organizations/{organization}/api-keys/{key}/rotate</c>.</summary>
public sealed class RotateOrganizationApiKeyEndpoint(ApiKeys keys, PlatformAuthentication authentication, TimeProvider clock) : ApiKeyEndpoint(keys, authentication, clock, project: false)
{
    /// <inheritdoc />
    public override void Configure()
    {
        Post($"{Organization}/api-keys/{{key}}/rotate");
        this.Document("rotate_organization_api_key", "Rotate an organization API key", "Needs admin. Issues a new secret for the same id; the previous one keeps working for previous_secret_ttl seconds. 409 rotation_pending while the previous secret's grace is active.", "API keys", typeof(RotateApiKeyRequest), StatusCodes.Status200OK, typeof(ApiKeySecretBody), conflict: true);
    }

    /// <inheritdoc />
    protected override Task<IResult> HandleAsync(UserRecord caller, CancellationToken cancellationToken) => this.RotateAsync(caller, cancellationToken);
}

/// <summary><c>PUT /v1/terrascale/organizations/{organization}/api-keys/{key}/allowed-cidrs</c>.</summary>
public sealed class SetOrganizationApiKeyAllowedCidrsEndpoint(ApiKeys keys, PlatformAuthentication authentication, TimeProvider clock) : ApiKeyEndpoint(keys, authentication, clock, project: false)
{
    /// <inheritdoc />
    public override void Configure()
    {
        Put($"{Organization}/api-keys/{{key}}/allowed-cidrs");
        this.Document("set_organization_api_key_allowed_cidrs", "Set an organization API key's IP allowlist", AllowedCidrsDescription, "API keys", typeof(AllowedCidrsRequest), StatusCodes.Status200OK, typeof(ApiKeyBody));
    }

    /// <inheritdoc />
    protected override Task<IResult> HandleAsync(UserRecord caller, CancellationToken cancellationToken) => this.SetAllowedCidrsAsync(caller, cancellationToken);
}

/// <summary><c>POST /v1/terrascale/organizations/{organization}/projects/{project}/api-keys</c>.</summary>
public sealed class CreateProjectApiKeyEndpoint(ApiKeys keys, PlatformAuthentication authentication, TimeProvider clock) : ApiKeyEndpoint(keys, authentication, clock, project: true)
{
    /// <inheritdoc />
    public override void Configure()
    {
        Post($"{Project}/api-keys");
        this.Document("create_project_api_key", "Create a project API key", "Needs the project's member role; scopes must be within the caller's. The key string is answered once.", "API keys", typeof(CreateApiKeyRequest), StatusCodes.Status201Created, typeof(ApiKeySecretBody));
    }

    /// <inheritdoc />
    protected override Task<IResult> HandleAsync(UserRecord caller, CancellationToken cancellationToken) => this.CreateAsync(caller, cancellationToken);
}

/// <summary><c>GET /v1/terrascale/organizations/{organization}/projects/{project}/api-keys</c>.</summary>
public sealed class ListProjectApiKeysEndpoint(ApiKeys keys, PlatformAuthentication authentication, TimeProvider clock) : ApiKeyEndpoint(keys, authentication, clock, project: true)
{
    /// <inheritdoc />
    public override void Configure()
    {
        Get($"{Project}/api-keys");
        this.Document("list_project_api_keys", "List project API keys", "Needs the project's member role. Unrevoked keys, never secrets, in creation order.", "API keys", null, StatusCodes.Status200OK, typeof(ApiKeyPageBody), paged: true);
    }

    /// <inheritdoc />
    protected override Task<IResult> HandleAsync(UserRecord caller, CancellationToken cancellationToken) => this.ListAsync(caller, cancellationToken);
}

/// <summary><c>GET /v1/terrascale/organizations/{organization}/projects/{project}/api-keys/{key}</c>.</summary>
public sealed class GetProjectApiKeyEndpoint(ApiKeys keys, PlatformAuthentication authentication, TimeProvider clock) : ApiKeyEndpoint(keys, authentication, clock, project: true)
{
    /// <inheritdoc />
    public override void Configure()
    {
        Get($"{Project}/api-keys/{{key}}");
        this.Document("get_project_api_key", "Get a project API key", "Needs the project's member role.", "API keys", null, StatusCodes.Status200OK, typeof(ApiKeyBody));
    }

    /// <inheritdoc />
    protected override Task<IResult> HandleAsync(UserRecord caller, CancellationToken cancellationToken) => this.GetAsync(caller, cancellationToken);
}

/// <summary><c>DELETE /v1/terrascale/organizations/{organization}/projects/{project}/api-keys/{key}</c>.</summary>
public sealed class RevokeProjectApiKeyEndpoint(ApiKeys keys, PlatformAuthentication authentication, TimeProvider clock) : ApiKeyEndpoint(keys, authentication, clock, project: true)
{
    /// <inheritdoc />
    public override void Configure()
    {
        Delete($"{Project}/api-keys/{{key}}");
        this.Document("revoke_project_api_key", "Revoke a project API key", "Needs the project's member role. No token is issued for it afterwards; TerraBase refuses tokens already issued within 3 seconds in every region.", "API keys", null, StatusCodes.Status204NoContent, null);
    }

    /// <inheritdoc />
    protected override Task<IResult> HandleAsync(UserRecord caller, CancellationToken cancellationToken) => this.RevokeAsync(caller, cancellationToken);
}

/// <summary><c>POST /v1/terrascale/organizations/{organization}/projects/{project}/api-keys/{key}/rotate</c>.</summary>
public sealed class RotateProjectApiKeyEndpoint(ApiKeys keys, PlatformAuthentication authentication, TimeProvider clock) : ApiKeyEndpoint(keys, authentication, clock, project: true)
{
    /// <inheritdoc />
    public override void Configure()
    {
        Post($"{Project}/api-keys/{{key}}/rotate");
        this.Document("rotate_project_api_key", "Rotate a project API key", "Needs the project's member role. Issues a new secret for the same id; the previous one keeps working for previous_secret_ttl seconds. 409 rotation_pending while the previous secret's grace is active.", "API keys", typeof(RotateApiKeyRequest), StatusCodes.Status200OK, typeof(ApiKeySecretBody), conflict: true);
    }

    /// <inheritdoc />
    protected override Task<IResult> HandleAsync(UserRecord caller, CancellationToken cancellationToken) => this.RotateAsync(caller, cancellationToken);
}

/// <summary><c>PUT /v1/terrascale/organizations/{organization}/projects/{project}/api-keys/{key}/allowed-cidrs</c>.</summary>
public sealed class SetProjectApiKeyAllowedCidrsEndpoint(ApiKeys keys, PlatformAuthentication authentication, TimeProvider clock) : ApiKeyEndpoint(keys, authentication, clock, project: true)
{
    /// <inheritdoc />
    public override void Configure()
    {
        Put($"{Project}/api-keys/{{key}}/allowed-cidrs");
        this.Document("set_project_api_key_allowed_cidrs", "Set a project API key's IP allowlist", AllowedCidrsDescription, "API keys", typeof(AllowedCidrsRequest), StatusCodes.Status200OK, typeof(ApiKeyBody));
    }

    /// <inheritdoc />
    protected override Task<IResult> HandleAsync(UserRecord caller, CancellationToken cancellationToken) => this.SetAllowedCidrsAsync(caller, cancellationToken);
}

/// <summary>What the organization and project API key endpoints share.</summary>
public abstract class ApiKeyEndpoint(ApiKeys keys, PlatformAuthentication authentication, TimeProvider clock, bool project) : PlatformEndpoint(authentication)
{
    /// <summary>How an allowlist is described in the API reference.</summary>
    protected const string AllowedCidrsDescription = "The same role as creating the key. Replaces its IP allowlist: up to 32 IPv4 or IPv6 CIDRs or addresses, empty allows any address. TerraBase answers 403 ip_not_allowed to a token of the key used from another address, or from outside the project's allowlist; a change applies to tokens issued afterwards, within 60 seconds.";

    /// <summary>Creates a key from the body.</summary>
    protected async Task<IResult> CreateAsync(UserRecord caller, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(caller);
        var body = await this.BodyAsync(PlatformApiJson.Default.CreateApiKeyRequest, cancellationToken);
        var name = PlatformDirectory.Name(body.Name) ?? throw Invalid("name must be 1 to 128 characters with a letter or digit.");
        if (body.Scopes is not { Count: > 0 } scopes || scopes.Distinct(StringComparer.Ordinal).Count() != scopes.Count || scopes.Except(TerraBaseScopes.All).Any())
        {
            throw Invalid("scopes must be a non-empty set of databases:read, databases:write, documents:read and documents:write.");
        }

        if (body.Database is not null && (!project || !ApiKeys.IsDatabaseName(body.Database)))
        {
            throw Invalid(project ? "database must be a TerraBase database name." : "Only project keys are narrowed to a database.");
        }

        if (body.ExpiresAt <= clock.GetUtcNow())
        {
            throw Invalid("expires_at must be in the future.");
        }

        var cidrs = ApiKeys.NormalizeCidrs(body.AllowedCidrs) ?? throw InvalidCidrs();
        var (key, secret) = await keys.CreateAsync(caller, this.RouteId("organization"), this.ProjectId(), name, scopes, body.Database, body.ExpiresAt, cidrs, cancellationToken);
        return Results.Json(new ApiKeySecretBody(this.Map(key, null), secret), PlatformApiJson.Default.ApiKeySecretBody, statusCode: StatusCodes.Status201Created);
    }

    /// <summary>Lists the keys, oldest first.</summary>
    protected async Task<IResult> ListAsync(UserRecord caller, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(caller);
        var (limit, after) = this.Paging();
        var page = await keys.ListAsync(caller.Id, this.RouteId("organization"), this.ProjectId(), limit, after, cancellationToken);
        var used = await keys.LastUsedAsync(page.Items, cancellationToken);
        return Results.Json(new ApiKeyPageBody([.. page.Items.Select(key => this.Map(key, used.TryGetValue(key.Id, out var at) ? at : null))], page.Next), PlatformApiJson.Default.ApiKeyPageBody);
    }

    /// <summary>Reads one key.</summary>
    protected async Task<IResult> GetAsync(UserRecord caller, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(caller);
        var key = await keys.GetAsync(caller.Id, this.RouteId("organization"), this.ProjectId(), this.KeyId(), cancellationToken);
        var used = await keys.LastUsedAsync([key], cancellationToken);
        return Results.Json(this.Map(key, used.TryGetValue(key.Id, out var at) ? at : null), PlatformApiJson.Default.ApiKeyBody);
    }

    /// <summary>Revokes one key.</summary>
    protected async Task<IResult> RevokeAsync(UserRecord caller, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(caller);
        await keys.RevokeAsync(caller.Id, this.RouteId("organization"), this.ProjectId(), this.KeyId(), cancellationToken);
        return Results.NoContent();
    }

    /// <summary>Rotates one key; the body is optional.</summary>
    protected async Task<IResult> RotateAsync(UserRecord caller, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(caller);
        var request = this.HttpContext.Request;
        // A body that is present, chunked or not, must be the JSON object; only a request without one rotates with no grace.
        var empty = request.ContentLength is 0
            || request.ContentLength is null && this.HttpContext.Features.Get<IHttpRequestBodyDetectionFeature>()?.CanHaveBody != true;
        var body = empty ? new RotateApiKeyRequest() : await this.BodyAsync(PlatformApiJson.Default.RotateApiKeyRequest, cancellationToken);
        var ttl = TimeSpan.FromSeconds(body.PreviousSecretTtl ?? 0);
        if (ttl < TimeSpan.Zero || ttl > ApiKeys.MaxPreviousSecretTtl)
        {
            throw Invalid("previous_secret_ttl must be 0 to 86400 seconds.");
        }

        var (key, secret) = await keys.RotateAsync(caller, this.RouteId("organization"), this.ProjectId(), this.KeyId(), ttl, cancellationToken);
        var used = await keys.LastUsedAsync([key], cancellationToken);
        return Results.Json(new ApiKeySecretBody(this.Map(key, used.TryGetValue(key.Id, out var at) ? at : null), secret), PlatformApiJson.Default.ApiKeySecretBody);
    }

    /// <summary>Replaces one key's IP allowlist.</summary>
    protected async Task<IResult> SetAllowedCidrsAsync(UserRecord caller, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(caller);
        var body = await this.BodyAsync(PlatformApiJson.Default.AllowedCidrsRequest, cancellationToken);
        var cidrs = (body.AllowedCidrs is null ? null : ApiKeys.NormalizeCidrs(body.AllowedCidrs)) ?? throw InvalidCidrs();
        var key = await keys.SetAllowedCidrsAsync(caller.Id, this.RouteId("organization"), this.ProjectId(), this.KeyId(), cidrs, cancellationToken);
        var used = await keys.LastUsedAsync([key], cancellationToken);
        return Results.Json(this.Map(key, used.TryGetValue(key.Id, out var at) ? at : null), PlatformApiJson.Default.ApiKeyBody);
    }

    /// <summary>The answer to an allowlist that is not up to 32 IPv4 or IPv6 CIDRs or addresses.</summary>
    protected static ApiException InvalidCidrs() => Invalid("allowed_cidrs must be up to 32 IPv4 or IPv6 CIDRs or addresses.");

    // The previous key string's expiry is shown only while it still works.
    private ApiKeyBody Map(ApiKeyRecord key, DateTimeOffset? lastUsedAt) =>
        new(key.Id, key.Name, key.OrganizationId, key.ProjectId, key.Database, key.Scopes, key.CreatedBy, key.CreatedAt, key.ExpiresAt,
            key.PreviousSecretExpiresAt > clock.GetUtcNow() ? key.PreviousSecretExpiresAt : null, lastUsedAt, key.AllowedCidrs ?? []);

    private string? ProjectId() => project ? this.RouteId("project") : null;

    private string KeyId() => this.HttpContext.GetRouteValue("key") is string value && ApiKeys.IsKeyId(value) ? value : throw PlatformDirectory.NotFound();
}
