using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Http.Metadata;
using TerraScale.Authentication.Contracts;
using TerraScale.Authentication.Features.Platform;
using TerraScale.Authentication.Features.RateLimiting;
using TerraScale.Authentication.Features.SignIn;
using TerraScale.Authentication.Hosting;
using TerraScale.DotNet;
using TerraScale.Endpoints;

namespace TerraScale.Authentication.Features.Oidc;

/// <summary>An identity asserted by a trusted project backend; unknown claims are refused.</summary>
public sealed record EndUserTokenRequest(
    [property: JsonPropertyName("user_id")] string? UserId,
    [property: JsonPropertyName("claims")] EndUserTokenClaims? Claims = null);

/// <summary>Only rule roles are accepted; no JWT or project authority can be overridden.</summary>
public sealed record EndUserTokenClaims([property: JsonPropertyName("roles")] IReadOnlyList<string>? Roles = null);

/// <summary>A short-lived project-scoped end-user access token.</summary>
public sealed record EndUserTokenBody(
    [property: JsonPropertyName("access_token")] string AccessToken,
    [property: JsonPropertyName("token_type")] string TokenType,
    [property: JsonPropertyName("expires_in")] long ExpiresIn)
{
    /// <inheritdoc />
    public override string ToString() => nameof(EndUserTokenBody);
}

/// <summary>Server-to-server identity assertion using a freshly authenticated project API key.</summary>
public sealed class EndUserTokenEndpoint(EndUserAccessTokens tokens, AttemptLimiter limiter) : EndpointWithoutRequest<IResult>
{
    private const string Prefix = "/v1/terrascale/projects/";
    private const string Suffix = "/end-user-tokens";
    private const int MaxBody = 16 * 1024;

    internal static bool Matches(PathString path)
    {
        var value = path.Value?.TrimEnd('/');
        return value is not null && value.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase)
            && value.EndsWith(Suffix, StringComparison.OrdinalIgnoreCase)
            && !value.AsSpan(Prefix.Length, Math.Max(0, value.Length - Prefix.Length - Suffix.Length)).Contains('/');
    }

    /// <inheritdoc />
    public override void Configure()
    {
        Post(Prefix + "{project}" + Suffix);
        Name("mintEndUserToken");
        Summary("Assert an end-user identity for one project");
        Description("Backend only. Requires a live unrestricted project API key with documents:read and documents:write. Accepts a canonical ULID and optional roles (64 maximum, 128 characters each). Unknown members are rejected. The complete signed JWT must fit 8192 ASCII bytes; oversized tokens return 400 invalid_request. Tokens expire within 900 seconds, capped at key expiry. Revocation prevents future minting; issued tokens validate offline until expiry plus clock skew. Shares the token IP budget (default 1000/minute) and limits each key to 1000/minute. Never send the project key to a browser.");
        Tags("API keys");
        Metadata(new RequiresSecurity(SecuritySchemes.ProjectApiKey), new AcceptsMetadata(["application/json"], typeof(EndUserTokenRequest)));
        Produces<EndUserTokenBody>();
        Produces<ErrorBody>(StatusCodes.Status400BadRequest);
        Produces<ErrorBody>(StatusCodes.Status401Unauthorized);
        Produces<OAuthErrorBody>(StatusCodes.Status429TooManyRequests);
        Produces<ErrorBody>(StatusCodes.Status503ServiceUnavailable);
    }

    /// <inheritdoc />
    public override async ValueTask<IResult> HandleAsync(CancellationToken cancellationToken)
    {
        this.HttpContext.Response.Headers.CacheControl = "no-store";
        try
        {
            var request = this.HttpContext.Request;
            var project = this.HttpContext.GetRouteValue("project") as string ?? string.Empty;
            var secret = request.Headers.Authorization.Count == 1
                && AuthenticationHeaderValue.TryParse(request.Headers.Authorization.ToString(), out var authorization)
                && authorization.Scheme.Equals("Bearer", StringComparison.OrdinalIgnoreCase) ? authorization.Parameter : null;
            if (await tokens.AuthorizeAsync(project, secret, cancellationToken) is not { } key)
            {
                return this.Unauthorized();
            }

            if (await limiter.AcquireAsync(OidcIssuer.InstanceId, "end-user-token-key", key.Id, PlatformGrants.KeyRequestsPerMinute,
                TimeSpan.FromMinutes(1), cancellationToken) is { } wait)
            {
                return HostedAuth.TooMany(wait);
            }

            var body = await this.BodyAsync(cancellationToken);
            var roles = body.Claims?.Roles ?? [];
            if (!TerraScaleIds.IsValid(body.UserId) || !EndUserAccessTokens.ValidRoles(roles))
            {
                throw Invalid();
            }

            return await tokens.IssueAsync(project, secret!, body.UserId!, roles, cancellationToken) is { } token
                ? Results.Json(token, EndUserTokenJson.Default.EndUserTokenBody)
                : this.Unauthorized();
        }
        catch (Exception error) when (error is HttpRequestException or TimeoutException)
        {
            throw ApiException.Unavailable(error);
        }
    }

    private IResult Unauthorized()
    {
        this.HttpContext.Response.Headers.WWWAuthenticate = "Bearer realm=\"ts-auth\"";
        return Results.Json(new ErrorBody(new ErrorDetail("unauthorized", "Present an unrestricted project API key with both document scopes.")),
            EndUserTokenJson.Default.ErrorBody, statusCode: StatusCodes.Status401Unauthorized);
    }

    private async Task<EndUserTokenRequest> BodyAsync(CancellationToken cancellationToken)
    {
        try
        {
            var request = this.HttpContext.Request;
            if (request.HasJsonContentType()
                && Microsoft.Net.Http.Headers.MediaTypeHeaderValue.TryParse(request.ContentType, out var media)
                && (media.Charset.Length == 0 || media.Charset.Equals("utf-8", StringComparison.OrdinalIgnoreCase)))
            {
                using var buffer = new MemoryStream();
                var chunk = new byte[4096];
                int read;
                while ((read = await request.Body.ReadAsync(chunk, cancellationToken)) > 0 && buffer.Length + read <= MaxBody)
                {
                    buffer.Write(chunk, 0, read);
                }

                if (read == 0 && JsonSerializer.Deserialize(buffer.GetBuffer().AsSpan(0, (int)buffer.Length), EndUserTokenJson.Default.EndUserTokenRequest) is { } body)
                {
                    return body;
                }
            }
        }
        catch (Exception error) when (error is JsonException or BadHttpRequestException)
        {
        }

        throw Invalid();
    }

    private static ApiException Invalid() => new(StatusCodes.Status400BadRequest, "invalid_request",
        "Supply a UTF-8 JSON object of at most 16384 bytes with user_id (canonical ULID) and optional claims.roles (at most 64 nonempty strings of 128 characters, without controls). No other members are accepted.");
}

[JsonSourceGenerationOptions(UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, MaxDepth = 4)]
[JsonSerializable(typeof(EndUserTokenRequest))]
[JsonSerializable(typeof(EndUserTokenBody))]
[JsonSerializable(typeof(ErrorBody))]
internal sealed partial class EndUserTokenJson : JsonSerializerContext;
