using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Http.Features;
using TerraScale.Authentication.Contracts;
using TerraScale.Authentication.Errors;

namespace TerraScale.Authentication.Features.Platform;

public sealed record CreateApiKeyRequest(
    [property: JsonPropertyName("name")] string? Name,
    [property: JsonPropertyName("scopes")] IReadOnlyList<string>? Scopes,
    [property: JsonPropertyName("database")] string? Database = null,
    [property: JsonPropertyName("expires_at")] DateTimeOffset? ExpiresAt = null,
    [property: JsonPropertyName("allowed_cidrs")] IReadOnlyList<string>? AllowedCidrs = null);

public sealed record AllowedCidrsRequest([property: JsonPropertyName("allowed_cidrs")] IReadOnlyList<string>? AllowedCidrs);

public sealed record RotateApiKeyRequest([property: JsonPropertyName("previous_secret_ttl")] int? PreviousSecretTtl = null);

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

public sealed record ApiKeySecretBody(
    [property: JsonPropertyName("api_key")] ApiKeyBody ApiKey,
    [property: JsonPropertyName("key")] string Key);

public sealed record ApiKeyPageBody(
    [property: JsonPropertyName("items")] IReadOnlyList<ApiKeyBody> Items,
    [property: JsonPropertyName("next")] string? Next);

public sealed class CreateOrganizationApiKeyEndpoint(ApiKeys keys, PlatformAuthentication authentication, TimeProvider clock) : ApiKeyEndpoint(keys, authentication, clock, project: false)
{
    public override void Configure()
    {
        Post($"{Organization}/api-keys");
        this.Describe("create_organization_api_key", "Create an organization API key", "Needs admin. The key reaches every project of the organization, one project per token. The key string is answered once. 429 rate_limited with Retry-After past 20 API keys created per user per hour, organization and project keys together.", "API keys", typeof(CreateApiKeyRequest), StatusCodes.Status201Created, typeof(ApiKeySecretBody));
        Produces<ErrorBody>(StatusCodes.Status429TooManyRequests);
    }

    protected override Task<IResult> HandleAsync(UserRecord caller, CancellationToken cancellationToken) => this.CreateAsync(caller, cancellationToken);
}

public sealed class ListOrganizationApiKeysEndpoint(ApiKeys keys, PlatformAuthentication authentication, TimeProvider clock) : ApiKeyEndpoint(keys, authentication, clock, project: false)
{
    public override void Configure()
    {
        Get($"{Organization}/api-keys");
        this.Describe("list_organization_api_keys", "List organization API keys", "Needs admin. Unrevoked keys, never secrets, in creation order.", "API keys", null, StatusCodes.Status200OK, typeof(ApiKeyPageBody), paged: true);
    }

    protected override Task<IResult> HandleAsync(UserRecord caller, CancellationToken cancellationToken) => this.ListAsync(caller, cancellationToken);
}

public sealed class GetOrganizationApiKeyEndpoint(ApiKeys keys, PlatformAuthentication authentication, TimeProvider clock) : ApiKeyEndpoint(keys, authentication, clock, project: false)
{
    public override void Configure()
    {
        Get($"{Organization}/api-keys/{{key}}");
        this.Describe("get_organization_api_key", "Get an organization API key", "Needs admin.", "API keys", null, StatusCodes.Status200OK, typeof(ApiKeyBody));
    }

    protected override Task<IResult> HandleAsync(UserRecord caller, CancellationToken cancellationToken) => this.GetAsync(caller, cancellationToken);
}

public sealed class RevokeOrganizationApiKeyEndpoint(ApiKeys keys, PlatformAuthentication authentication, TimeProvider clock) : ApiKeyEndpoint(keys, authentication, clock, project: false)
{
    public override void Configure()
    {
        Delete($"{Organization}/api-keys/{{key}}");
        this.Describe("revoke_organization_api_key", "Revoke an organization API key", "Needs admin. No token is issued for it afterwards; TerraBase refuses tokens already issued within 3 seconds in every region.", "API keys", null, StatusCodes.Status204NoContent, null);
    }

    protected override Task<IResult> HandleAsync(UserRecord caller, CancellationToken cancellationToken) => this.RevokeAsync(caller, cancellationToken);
}

public sealed class RotateOrganizationApiKeyEndpoint(ApiKeys keys, PlatformAuthentication authentication, TimeProvider clock) : ApiKeyEndpoint(keys, authentication, clock, project: false)
{
    public override void Configure()
    {
        Post($"{Organization}/api-keys/{{key}}/rotate");
        this.Describe("rotate_organization_api_key", "Rotate an organization API key", "Needs admin. Issues a new secret for the same id; the previous one keeps working for previous_secret_ttl seconds. Rotation is always available: it ends any older previous secret immediately; the current secret becomes the previous one for the requested grace. At most two secrets remain valid.", "API keys", typeof(RotateApiKeyRequest), StatusCodes.Status200OK, typeof(ApiKeySecretBody), conflict: true);
    }

    protected override Task<IResult> HandleAsync(UserRecord caller, CancellationToken cancellationToken) => this.RotateAsync(caller, cancellationToken);
}

public sealed class SetOrganizationApiKeyAllowedCidrsEndpoint(ApiKeys keys, PlatformAuthentication authentication, TimeProvider clock) : ApiKeyEndpoint(keys, authentication, clock, project: false)
{
    public override void Configure()
    {
        Put($"{Organization}/api-keys/{{key}}/allowed-cidrs");
        this.Describe("set_organization_api_key_allowed_cidrs", "Set an organization API key's IP allowlist", AllowedCidrsDescription, "API keys", typeof(AllowedCidrsRequest), StatusCodes.Status200OK, typeof(ApiKeyBody));
    }

    protected override Task<IResult> HandleAsync(UserRecord caller, CancellationToken cancellationToken) => this.SetAllowedCidrsAsync(caller, cancellationToken);
}

public sealed class CreateProjectApiKeyEndpoint(ApiKeys keys, PlatformAuthentication authentication, TimeProvider clock) : ApiKeyEndpoint(keys, authentication, clock, project: true)
{
    public override void Configure()
    {
        Post($"{Project}/api-keys");
        this.Describe("create_project_api_key", "Create a project API key", "Needs the project's member role; scopes must be within the caller's. The key string is answered once. 429 rate_limited with Retry-After past 20 API keys created per user per hour, organization and project keys together.", "API keys", typeof(CreateApiKeyRequest), StatusCodes.Status201Created, typeof(ApiKeySecretBody));
        Produces<ErrorBody>(StatusCodes.Status429TooManyRequests);
    }

    protected override Task<IResult> HandleAsync(UserRecord caller, CancellationToken cancellationToken) => this.CreateAsync(caller, cancellationToken);
}

public sealed class ListProjectApiKeysEndpoint(ApiKeys keys, PlatformAuthentication authentication, TimeProvider clock) : ApiKeyEndpoint(keys, authentication, clock, project: true)
{
    public override void Configure()
    {
        Get($"{Project}/api-keys");
        this.Describe("list_project_api_keys", "List project API keys", "Needs the project's member role. Unrevoked keys, never secrets, in creation order.", "API keys", null, StatusCodes.Status200OK, typeof(ApiKeyPageBody), paged: true);
    }

    protected override Task<IResult> HandleAsync(UserRecord caller, CancellationToken cancellationToken) => this.ListAsync(caller, cancellationToken);
}

public sealed class GetProjectApiKeyEndpoint(ApiKeys keys, PlatformAuthentication authentication, TimeProvider clock) : ApiKeyEndpoint(keys, authentication, clock, project: true)
{
    public override void Configure()
    {
        Get($"{Project}/api-keys/{{key}}");
        this.Describe("get_project_api_key", "Get a project API key", "Needs the project's member role.", "API keys", null, StatusCodes.Status200OK, typeof(ApiKeyBody));
    }

    protected override Task<IResult> HandleAsync(UserRecord caller, CancellationToken cancellationToken) => this.GetAsync(caller, cancellationToken);
}

public sealed class RevokeProjectApiKeyEndpoint(ApiKeys keys, PlatformAuthentication authentication, TimeProvider clock) : ApiKeyEndpoint(keys, authentication, clock, project: true)
{
    public override void Configure()
    {
        Delete($"{Project}/api-keys/{{key}}");
        this.Describe("revoke_project_api_key", "Revoke a project API key", "Needs the project's member role. No token is issued for it afterwards; TerraBase refuses tokens already issued within 3 seconds in every region.", "API keys", null, StatusCodes.Status204NoContent, null);
    }

    protected override Task<IResult> HandleAsync(UserRecord caller, CancellationToken cancellationToken) => this.RevokeAsync(caller, cancellationToken);
}

public sealed class RotateProjectApiKeyEndpoint(ApiKeys keys, PlatformAuthentication authentication, TimeProvider clock) : ApiKeyEndpoint(keys, authentication, clock, project: true)
{
    public override void Configure()
    {
        Post($"{Project}/api-keys/{{key}}/rotate");
        this.Describe("rotate_project_api_key", "Rotate a project API key", "Needs the project's member role. Issues a new secret for the same id; the previous one keeps working for previous_secret_ttl seconds. Rotation is always available: it ends any older previous secret immediately; the current secret becomes the previous one for the requested grace. At most two secrets remain valid.", "API keys", typeof(RotateApiKeyRequest), StatusCodes.Status200OK, typeof(ApiKeySecretBody), conflict: true);
    }

    protected override Task<IResult> HandleAsync(UserRecord caller, CancellationToken cancellationToken) => this.RotateAsync(caller, cancellationToken);
}

public sealed class SetProjectApiKeyAllowedCidrsEndpoint(ApiKeys keys, PlatformAuthentication authentication, TimeProvider clock) : ApiKeyEndpoint(keys, authentication, clock, project: true)
{
    public override void Configure()
    {
        Put($"{Project}/api-keys/{{key}}/allowed-cidrs");
        this.Describe("set_project_api_key_allowed_cidrs", "Set a project API key's IP allowlist", AllowedCidrsDescription, "API keys", typeof(AllowedCidrsRequest), StatusCodes.Status200OK, typeof(ApiKeyBody));
    }

    protected override Task<IResult> HandleAsync(UserRecord caller, CancellationToken cancellationToken) => this.SetAllowedCidrsAsync(caller, cancellationToken);
}

public abstract class ApiKeyEndpoint(ApiKeys keys, PlatformAuthentication authentication, TimeProvider clock, bool project) : PlatformEndpoint(authentication)
{
    protected const string AllowedCidrsDescription = "The same role as creating the key. Replaces its IP allowlist: up to 32 IPv4 or IPv6 CIDRs or addresses, empty allows any address. TerraBase answers 403 ip_not_allowed to a token of the key used from another address, or from outside the project's allowlist; a change applies to tokens issued afterwards, within 60 seconds.";

    protected async Task<IResult> CreateAsync(UserRecord caller, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(caller);
        var body = await this.BodyAsync(PlatformApiJson.Default.CreateApiKeyRequest, cancellationToken);
        var name = PlatformDirectory.Name(body.Name) ?? throw Invalid("name must be 1 to 128 characters with a letter or digit.");
        if (body.Scopes is not { Count: > 0 } scopes || scopes.Distinct(StringComparer.Ordinal).Count() != scopes.Count || scopes.Except(TerraBaseScopes.All).Any())
        {
            throw Invalid("scopes must be a non-empty set of databases:read, databases:write, sql:read, sql:write, auth:read, auth:write and auth:tokens:mint.");
        }

        if (body.Database is not null && (!project || !ApiKeys.IsDatabaseId(body.Database)))
        {
            throw Invalid(project ? "database must be a TerraBase database ID (ULID)." : "Only project keys are narrowed to a database.");
        }

        if (body.ExpiresAt <= clock.GetUtcNow())
        {
            throw Invalid("expires_at must be in the future.");
        }

        var cidrs = ApiKeys.NormalizeCidrs(body.AllowedCidrs) ?? throw InvalidCidrs();
        var (key, secret) = await keys.CreateAsync(caller, this.RouteId("organization"), this.ProjectId(), name, scopes, body.Database, body.ExpiresAt, cidrs, cancellationToken);
        return TypedResults.Json(new ApiKeySecretBody(this.Map(key, null), secret), PlatformApiJson.Default.ApiKeySecretBody, statusCode: StatusCodes.Status201Created);
    }

    protected async Task<IResult> ListAsync(UserRecord caller, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(caller);
        var (limit, after) = this.Paging();
        var page = await keys.ListAsync(caller.Id, this.RouteId("organization"), this.ProjectId(), limit, after, cancellationToken);
        var used = await keys.LastUsedAsync(page.Items, cancellationToken);
        return TypedResults.Json(new ApiKeyPageBody([.. page.Items.Select(key => this.Map(key, used.TryGetValue(key.Id, out var at) ? at : null))], page.Next), PlatformApiJson.Default.ApiKeyPageBody);
    }

    protected async Task<IResult> GetAsync(UserRecord caller, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(caller);
        var key = await keys.GetAsync(caller.Id, this.RouteId("organization"), this.ProjectId(), this.KeyId(), cancellationToken);
        var used = await keys.LastUsedAsync([key], cancellationToken);
        return TypedResults.Json(this.Map(key, used.TryGetValue(key.Id, out var at) ? at : null), PlatformApiJson.Default.ApiKeyBody);
    }

    protected async Task<IResult> RevokeAsync(UserRecord caller, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(caller);
        await keys.RevokeAsync(caller.Id, this.RouteId("organization"), this.ProjectId(), this.KeyId(), cancellationToken);
        return TypedResults.NoContent();
    }

    protected async Task<IResult> RotateAsync(UserRecord caller, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(caller);
        var request = this.HttpContext.Request;
        var empty = request.ContentLength is 0
            || request.ContentLength is null && this.HttpContext.Features.Get<IHttpRequestBodyDetectionFeature>()?.CanHaveBody != true;
        var body = empty ? new RotateApiKeyRequest() : await this.BodyAsync(PlatformApiJson.Default.RotateApiKeyRequest, cancellationToken);
        var ttl = TimeSpan.FromSeconds(body.PreviousSecretTtl ?? 0);
        if (ttl < TimeSpan.Zero || ttl > ApiKeys.MaxPreviousSecretTtl)
        {
            throw Invalid("previous_secret_ttl must be 0 to 86400 seconds.");
        }

        var proofs = this.HttpContext.RequestServices.GetRequiredService<StepUpProofs>();
        return await proofs.ConsumeAsync(caller, StepUpProofs.Proof(this.HttpContext.Request), "rotate_api_key", "ts-auth", null, cancellationToken) switch
        {
            StepUpConsumption rotation => await this.RotateAsync(rotation.Caller, ttl, cancellationToken),
            Forbidden failure => ErrorResults.ToResult(failure),
        };
    }

    private async Task<IResult> RotateAsync(UserRecord caller, TimeSpan ttl, CancellationToken cancellationToken)
    {
        var (key, secret) = await keys.RotateAsync(caller, this.RouteId("organization"), this.ProjectId(), this.KeyId(), ttl, cancellationToken);
        var used = await keys.LastUsedAsync([key], cancellationToken);
        return TypedResults.Json(new ApiKeySecretBody(this.Map(key, used.TryGetValue(key.Id, out var at) ? at : null), secret), PlatformApiJson.Default.ApiKeySecretBody);
    }

    protected async Task<IResult> SetAllowedCidrsAsync(UserRecord caller, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(caller);
        var body = await this.BodyAsync(PlatformApiJson.Default.AllowedCidrsRequest, cancellationToken);
        var cidrs = (body.AllowedCidrs is null ? null : ApiKeys.NormalizeCidrs(body.AllowedCidrs)) ?? throw InvalidCidrs();
        var key = await keys.SetAllowedCidrsAsync(caller.Id, this.RouteId("organization"), this.ProjectId(), this.KeyId(), cidrs, cancellationToken);
        var used = await keys.LastUsedAsync([key], cancellationToken);
        return TypedResults.Json(this.Map(key, used.TryGetValue(key.Id, out var at) ? at : null), PlatformApiJson.Default.ApiKeyBody);
    }

    protected static ApiException InvalidCidrs() => Invalid("allowed_cidrs must be up to 32 IPv4 or IPv6 CIDRs or addresses.");

    private ApiKeyBody Map(ApiKeyRecord key, DateTimeOffset? lastUsedAt) =>
        new(key.Id, key.Name, key.OrganizationId, key.ProjectId, key.Database, key.Scopes, key.CreatedBy, key.CreatedAt, key.ExpiresAt,
            key.PreviousSecretExpiresAt > clock.GetUtcNow() ? key.PreviousSecretExpiresAt : null, lastUsedAt, key.AllowedCidrs ?? []);

    private string? ProjectId() => project ? this.RouteId("project") : null;

    private string KeyId() => this.HttpContext.GetRouteValue("key") is string value && ApiKeys.IsKeyId(value) ? value : throw PlatformDirectory.NotFound();
}
