using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using TerraScale.Authentication.Contracts;
using TerraScale.Authentication.Features.RateLimiting;
using TerraScale.Authentication.Hosting;
using TerraScale.Endpoints;

namespace TerraScale.Authentication.Features.Platform;

public sealed record ScopedKeyRequest(
    [property: JsonPropertyName("project")] string? Project,
    [property: JsonPropertyName("database")] string? Database,
    [property: JsonPropertyName("access")] string? Access,
    [property: JsonPropertyName("ttl_seconds")] long? TtlSeconds = null);

public sealed record ScopedKeyBody(
    [property: JsonPropertyName("key")] string Key,
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("project_id")] string ProjectId,
    [property: JsonPropertyName("database")] string Database,
    [property: JsonPropertyName("access")] string Access,
    [property: JsonPropertyName("expires_at")] DateTimeOffset ExpiresAt);

public sealed class ScopedKeyEndpoint(ApiKeys keys, PlatformDirectory directory, AttemptLimiter limiter, TimeProvider clock) : EndpointWithoutRequest<IResult>
{
    private const int MaxBody = 4 * 1024;
    private const long DefaultTtlSeconds = 3600;
    private const long MinTtlSeconds = 60;
    private const long MaxTtlSeconds = 30L * 24 * 60 * 60;

    public override void Configure()
    {
        Post("/v1/terrascale/api-keys/self/scoped-keys");
        Name("create_scoped_api_key");
        Summary("Mint a scoped API key for an agent");
        Description("Authorization: Basic base64(keyId:key) of an API key. Mints a new API key that reaches one database (ULID) of one project: access read grants documents:read, read_write documents:read and documents:write. ttl_seconds is 60 to 2,592,000 (default 3600) and the key never outlives its parent. project is required for an organization key and must equal a project key's project. The parent must hold the scopes and, when narrowed, the same database. A scoped key mints no keys; revoking or rotating the parent revokes it. The key string is answered once.");
        Tags("API keys");
        Metadata(new Microsoft.AspNetCore.Http.Metadata.AcceptsMetadata(["application/json"], typeof(ScopedKeyRequest)));
        Produces<ScopedKeyBody>(StatusCodes.Status201Created);
        Produces<ErrorBody>(StatusCodes.Status400BadRequest);
        Produces<ErrorBody>(StatusCodes.Status401Unauthorized);
        Produces<ErrorBody>(StatusCodes.Status403Forbidden);
        Produces<ErrorBody>(StatusCodes.Status429TooManyRequests);
        Produces<ErrorBody>(StatusCodes.Status503ServiceUnavailable);
        Metadata(new RequiresSecurity(SecuritySchemes.ClientBasic));
    }

    public override async ValueTask<IResult> HandleAsync(CancellationToken cancellationToken)
    {
        HttpContext.Response.Headers.CacheControl = "no-store";
        if (Credential(HttpContext.Request) is not { } secret
            || await keys.AuthenticateAsync(secret, cancellationToken).ConfigureAwait(false) is not { } parent
            || await directory.OrganizationAsync(parent.OrganizationId, cancellationToken).ConfigureAwait(false) is null)
        {
            HttpContext.Response.Headers.WWWAuthenticate = "Basic realm=\"ts-auth\"";
            return Error(StatusCodes.Status401Unauthorized, "invalid_client", PlatformGrants.InvalidApiKeyNotice);
        }

        if (parent.ParentKeyId is not null)
        {
            return Error(StatusCodes.Status403Forbidden, "forbidden", "A scoped key cannot mint keys.");
        }

        if (!await limiter.TryAcquireAsync(PlatformDirectory.Instance, "scoped-key", parent.Id, ApiKeys.ScopedKeysPerMinute, cancellationToken).ConfigureAwait(false))
        {
            HttpContext.Response.Headers.RetryAfter = "60";
            return Error(StatusCodes.Status429TooManyRequests, "rate_limited", "Too many scoped keys for this API key; retry later.");
        }

        if (await BodyAsync(cancellationToken).ConfigureAwait(false) is not { } body)
        {
            return Error(StatusCodes.Status400BadRequest, "invalid_request", $"The body must be a JSON object of at most {MaxBody} bytes with project, database, access and ttl_seconds.");
        }

        if (!ApiKeys.IsDatabaseId(body.Database))
        {
            return Error(StatusCodes.Status400BadRequest, "invalid_request", "database must be a TerraBase database ID (ULID).");
        }

        IReadOnlyList<string>? scopes = body.Access switch
        {
            "read" => [TerraBaseScopes.DocumentsRead],
            "read_write" => [TerraBaseScopes.DocumentsRead, TerraBaseScopes.DocumentsWrite],
            _ => null,
        };
        if (scopes is null)
        {
            return Error(StatusCodes.Status400BadRequest, "invalid_request", "access must be read or read_write.");
        }

        var ttl = body.TtlSeconds ?? DefaultTtlSeconds;
        if (ttl is < MinTtlSeconds or > MaxTtlSeconds)
        {
            return Error(StatusCodes.Status400BadRequest, "invalid_request", $"ttl_seconds must be between {MinTtlSeconds} and {MaxTtlSeconds}.");
        }

        var project = parent.ProjectId ?? body.Project;
        if (project is null || !PlatformDirectory.IsId(project))
        {
            return Error(StatusCodes.Status400BadRequest, "invalid_request", "project is required for an organization key and must be a project ID.");
        }

        if (body.Project is not null && body.Project != project
            || parent.Database is { } narrowed && narrowed != body.Database
            || scopes.Except(parent.Scopes).Any()
            || await directory.ProjectAsync(project, cancellationToken).ConfigureAwait(false) is not { } found || found.Value.OrganizationId != parent.OrganizationId
            || await directory.ClosureAsync("organization", parent.OrganizationId, cancellationToken).ConfigureAwait(false) is not null
            || await directory.ClosureAsync("project", project, cancellationToken).ConfigureAwait(false) is not null)
        {
            return Error(StatusCodes.Status403Forbidden, "forbidden", "The project, database or access is beyond this API key's.");
        }

        var expiresAt = clock.GetUtcNow().AddSeconds(ttl);
        if (parent.ExpiresAt is { } end && end < expiresAt)
        {
            expiresAt = end;
        }

        var (key, minted) = await keys.CreateScopedAsync(parent, ApiKeys.Generation(secret), project, body.Database!, scopes, expiresAt, cancellationToken).ConfigureAwait(false);
        return Results.Json(new ScopedKeyBody(minted, key.Id, project, body.Database!, body.Access!, expiresAt), PlatformApiJson.Default.ScopedKeyBody,
            statusCode: StatusCodes.Status201Created);
    }

    private static string? Credential(HttpRequest request)
    {
        if (!AuthenticationHeaderValue.TryParse(request.Headers.Authorization, out var header)
            || !string.Equals(header.Scheme, "Basic", StringComparison.OrdinalIgnoreCase) || header.Parameter is not { Length: > 0 and <= 512 } encoded)
        {
            return null;
        }

        string decoded;
        try
        {
            decoded = Encoding.UTF8.GetString(Convert.FromBase64String(encoded));
        }
        catch (FormatException)
        {
            return null;
        }

        var colon = decoded.IndexOf(':', StringComparison.Ordinal);
        return colon > 0 && decoded[(colon + 1)..] is { Length: 79 } secret && secret[..26] == decoded[..colon] ? secret : null;
    }

    private async Task<ScopedKeyRequest?> BodyAsync(CancellationToken cancellationToken)
    {
        if (!HttpContext.Request.HasJsonContentType())
        {
            return null;
        }

        using var buffer = new MemoryStream();
        var chunk = new byte[1024];
        int read;
        while ((read = await HttpContext.Request.Body.ReadAsync(chunk, cancellationToken).ConfigureAwait(false)) > 0)
        {
            if (buffer.Length + read > MaxBody)
            {
                return null;
            }

            buffer.Write(chunk, 0, read);
        }

        try
        {
            return JsonSerializer.Deserialize(buffer.GetBuffer().AsSpan(0, (int)buffer.Length), PlatformApiJson.Default.ScopedKeyRequest);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static IResult Error(int status, string code, string message) =>
        Results.Json(new ErrorBody(new ErrorDetail(code, message)), PlatformApiJson.Default.ErrorBody, statusCode: status);
}
