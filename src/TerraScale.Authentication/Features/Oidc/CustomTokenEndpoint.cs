using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Http.Metadata;
using TerraScale.Authentication.Contracts;
using TerraScale.Authentication.Features.Platform;
using TerraScale.Authentication.Features.RateLimiting;
using TerraScale.Authentication.Features.SignIn;
using TerraScale.Authentication.Hosting;
using TerraScale.Endpoints;
using MediaType = Microsoft.Net.Http.Headers.MediaTypeHeaderValue;

namespace TerraScale.Authentication.Features.Oidc;

public sealed record CustomTokenRequest(
    [property: JsonPropertyName("user_id")] string? UserId,
    [property: JsonPropertyName("database")] string? Database = null,
    [property: JsonPropertyName("claims")] JsonElement Claims = default);

public sealed record CustomTokenBody(
    [property: JsonPropertyName("access_token")] string AccessToken,
    [property: JsonPropertyName("token_type")] string TokenType,
    [property: JsonPropertyName("expires_in")] long ExpiresIn)
{
    public override string ToString() => nameof(CustomTokenBody);
}

public sealed class CustomTokenEndpoint(CustomTokens tokens, ApiKeys keys, AttemptLimiter limiter) : EndpointWithoutRequest<IResult>
{
    public const string Path = "/v1/{project}/custom-tokens";

    private const int MaxBody = 16 * 1024;

    internal static bool Matches(PathString path) =>
        (path.Value ?? string.Empty).TrimEnd('/').Split('/') is ["", var version, { Length: > 0 }, var name]
        && version.Equals("v1", StringComparison.OrdinalIgnoreCase) && name.Equals("custom-tokens", StringComparison.OrdinalIgnoreCase);

    public override void Configure()
    {
        Post(Path);
        Name("mintCustomToken");
        Summary("Mint a custom token for one end user of the project");
        Description("Backend only. Requires a live project API key of this project with auth:tokens:mint and no database restriction; no other scope is needed, and the key's and project's IP allowlists apply. The body names a canonical ULID user_id, an optional database ULID the token is restricted to, and optional claims (at most 32 members and 1000 bytes serialized, names [a-z][a-z0-9_]{0,62}, values strings of at most 256 characters, finite numbers, booleans, or arrays of at most 64 strings or 64 numbers). Unknown members are rejected. The token is an at+jwt signed by the project tenant's key ring under the tenant issuer, with token_use custom and only the project's TerraBase data audiences, so ts-auth's own endpoints refuse it. It expires after 300 seconds and is not refreshable; revoking the key stops minting at once. Shares the token IP budget and limits each key to 1000 requests a minute. Never send the project key to a browser.");
        Tags("Custom tokens");
        Metadata(new RequiresSecurity(SecuritySchemes.ProjectApiKey), new AcceptsMetadata(["application/json"], typeof(CustomTokenRequest)));
        Produces<CustomTokenBody>();
        Produces<ErrorBody>(StatusCodes.Status400BadRequest);
        Produces<ErrorBody>(StatusCodes.Status401Unauthorized);
        Produces<ErrorBody>(StatusCodes.Status403Forbidden);
        Produces<ErrorBody>(StatusCodes.Status404NotFound);
        Produces<OAuthErrorBody>(StatusCodes.Status429TooManyRequests);
        Produces<ErrorBody>(StatusCodes.Status503ServiceUnavailable);
    }

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
            if (await keys.AuthenticateAsync(secret, cancellationToken) is not { } key)
            {
                this.HttpContext.Response.Headers.WWWAuthenticate = "Bearer realm=\"ts-auth\"";
                return Results.Json(new ErrorBody(new ErrorDetail("unauthorized", "Present a live project API key with auth:tokens:mint.")),
                    CustomTokenJson.Default.ErrorBody, statusCode: StatusCodes.Status401Unauthorized);
            }

            if (await limiter.AcquireAsync(OidcIssuer.InstanceId, "custom-token-key", key.Id, PlatformGrants.KeyRequestsPerMinute,
                TimeSpan.FromMinutes(1), cancellationToken) is { } wait)
            {
                return HostedAuth.TooMany(wait);
            }

            await tokens.CheckAsync(key, project, this.HttpContext.Connection.RemoteIpAddress, cancellationToken);
            var body = await this.BodyAsync(cancellationToken);
            return Results.Json(await tokens.IssueAsync(key, project, body, cancellationToken), CustomTokenJson.Default.CustomTokenBody);
        }
        catch (Exception error) when (error is HttpRequestException or TimeoutException)
        {
            throw ApiException.Unavailable(error);
        }
    }

    private async Task<CustomTokenRequest> BodyAsync(CancellationToken cancellationToken)
    {
        try
        {
            var request = this.HttpContext.Request;
            if (request.HasJsonContentType() && MediaType.TryParse(request.ContentType, out var media)
                && (media.Charset.Length == 0 || media.Charset.Equals("utf-8", StringComparison.OrdinalIgnoreCase)))
            {
                using var buffer = new MemoryStream();
                var chunk = new byte[4096];
                int read;
                while ((read = await request.Body.ReadAsync(chunk, cancellationToken)) > 0 && buffer.Length + read <= MaxBody)
                {
                    buffer.Write(chunk, 0, read);
                }

                if (read == 0 && JsonSerializer.Deserialize(buffer.GetBuffer().AsSpan(0, (int)buffer.Length), CustomTokenJson.Default.CustomTokenRequest) is { } body)
                {
                    return body;
                }
            }
        }
        catch (Exception error) when (error is JsonException or BadHttpRequestException)
        {
            throw CustomTokens.Invalid();
        }

        throw CustomTokens.Invalid();
    }
}

[JsonSourceGenerationOptions(UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, MaxDepth = 4)]
[JsonSerializable(typeof(CustomTokenRequest))]
[JsonSerializable(typeof(CustomTokenBody))]
[JsonSerializable(typeof(ErrorBody))]
internal sealed partial class CustomTokenJson : JsonSerializerContext;
