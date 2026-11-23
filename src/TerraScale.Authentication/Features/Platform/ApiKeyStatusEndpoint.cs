using Microsoft.AspNetCore.Http.HttpResults;
using System.Collections.Concurrent;
using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json.Serialization;
using TerraScale.Authentication.Configuration;
using TerraScale.Authentication.Contracts;
using TerraScale.Authentication.Features.Oidc;
using TerraScale.Authentication.Features.SigningKeys;
using TerraScale.Authentication.Hosting;
using TerraScale.Authentication.Security;
using TerraScale.Endpoints;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace TerraScale.Authentication.Features.Platform;

public sealed record ApiKeyStatusBody([property: JsonPropertyName("key_id")] string KeyId);

public sealed class ApiKeyStatusEndpoint(ApiKeys keys, KeyRing ring, AuthOptions options, IConfiguration configuration, TimeProvider clock) : EndpointWithoutRequest<IResult>
{
    private const int MaxTokenLength = 8 * 1024;

    private const int MaxInFlight = 512;

    private const int KeyChecksPerSecond = 20;
    private const int MaxTrackedKeys = 16 * 1024;
    private static readonly ConcurrentDictionary<string, (long Second, int Count)> KeyChecks = new(StringComparer.Ordinal);
    private static int inFlight;

    public override void Configure()
    {
        Get("/v1/terrascale/api-keys/self");
        Name("get_api_key_status");
        Summary("Check an API key token");
        Description("Bearer: an access token issued for an API key (client credentials). 200 while the key exists and is unexpired, the token's key string still works (a rotation's grace included), and its organization and project exist. 401 key_revoked or key_expired otherwise, invalid_token for any other token. Read from storage on every call, never cached; 503 when storage does not answer.");
        Tags("API keys");
        Produces<ApiKeyStatusBody>();
        Produces<ErrorBody>(StatusCodes.Status401Unauthorized);
        Produces<ErrorBody>(StatusCodes.Status429TooManyRequests);
        Produces<ErrorBody>(StatusCodes.Status503ServiceUnavailable);
        Metadata(new RequiresSecurity(SecuritySchemes.ApiKeyToken));
    }

    public override async ValueTask<IResult> HandleAsync(CancellationToken cancellationToken)
    {
        this.HttpContext.Response.Headers.CacheControl = "no-store";
        if ((configuration["TS_REGION"] ?? (options.Development ? "gru" : null)) != "gru")
        {
            return Unavailable("API key status is answered by the GRU authority only.");
        }

        try
        {
            if (Interlocked.Increment(ref inFlight) > MaxInFlight)
            {
                return Unavailable("Too many API key checks in flight; retry.");
            }

            return await this.CheckAsync(cancellationToken);
        }
        finally
        {
            Interlocked.Decrement(ref inFlight);
        }
    }

    private async Task<IResult> CheckAsync(CancellationToken cancellationToken)
    {
        if (!AuthenticationHeaderValue.TryParse(this.HttpContext.Request.Headers.Authorization, out var header)
            || !string.Equals(header.Scheme, "Bearer", StringComparison.OrdinalIgnoreCase)
            || header.Parameter is not { Length: > 0 and <= MaxTokenLength } token)
        {
            return this.Refuse("invalid_token", "Present an access token issued for an API key.");
        }

        IDictionary<string, object>? claims;
        var signing = await ring.GetPublicAsync(OidcIssuer.InstanceId, Jwt.KeyId(token), cancellationToken);
        try
        {
            claims = await Jwt.ValidateAsync(token, signing, "at+jwt", OidcIssuer.Issuer(options).AbsoluteUri, AuthOptions.TerraBaseAudience);
        }
        finally
        {
            foreach (var key in signing)
            {
                key.Dispose();
            }
        }

        if (claims is null || Claim(claims, PlatformGrants.KeyIdClaim) is not { } keyId || Claim(claims, Claims.Subject) != keyId
            || Claim(claims, PlatformGrants.KeyGenerationClaim) is not { } generation)
        {
            return this.Refuse("invalid_token", "Present an access token issued for an API key.");
        }

        if (!this.Admit(keyId + " " + generation + " " + Claim(claims, PlatformGrants.ProjectClaim)))
        {
            this.HttpContext.Response.Headers.RetryAfter = "1";
            return TypedResults.Json(new ErrorBody(new ErrorDetail("rate_limited", "Too many checks for this API key; retry.")), PlatformApiJson.Default.ErrorBody,
                statusCode: StatusCodes.Status429TooManyRequests);
        }

        string? refusal;
        try
        {
            refusal = await keys.RefusalAsync(keyId, generation, Claim(claims, PlatformGrants.OrganizationClaim), Claim(claims, PlatformGrants.ProjectClaim),
                cancellationToken);
        }
        catch (Exception error) when (error is HttpRequestException or TimeoutException)
        {
            throw ApiException.Unavailable(error);
        }

        return refusal switch
        {
            "key_expired" => this.Refuse(refusal, "The API key has expired."),
            not null => this.Refuse(refusal, "The API key was revoked, rotated, or its organization or project was deleted."),
            _ => TypedResults.Json(new ApiKeyStatusBody(keyId), PlatformApiJson.Default.ApiKeyStatusBody),
        };
    }

    private bool Admit(string checkedKey)
    {
        if (KeyChecks.Count >= MaxTrackedKeys)
        {
            KeyChecks.Clear();
        }

        var second = clock.GetUtcNow().ToUnixTimeSeconds();
        return KeyChecks.AddOrUpdate(checkedKey, (second, 1), (_, seen) => seen.Second == second ? (second, seen.Count + 1) : (second, 1)).Count <= KeyChecksPerSecond;
    }

    private JsonHttpResult<ErrorBody> Unavailable(string message)
    {
        this.HttpContext.Response.Headers.RetryAfter = "1";
        return TypedResults.Json(new ErrorBody(new ErrorDetail("unavailable", message)), PlatformApiJson.Default.ErrorBody, statusCode: StatusCodes.Status503ServiceUnavailable);
    }

    private static string? Claim(IDictionary<string, object> claims, string name) =>
        claims.TryGetValue(name, out var value) && Convert.ToString(value, CultureInfo.InvariantCulture) is { Length: > 0 } text ? text : null;

    private JsonHttpResult<ErrorBody> Refuse(string code, string message)
    {
        this.HttpContext.Response.Headers.WWWAuthenticate = $"Bearer realm=\"ts-auth\", error=\"invalid_token\", error_description=\"{code}\"";
        return TypedResults.Json(new ErrorBody(new ErrorDetail(code, message)), PlatformApiJson.Default.ErrorBody, statusCode: StatusCodes.Status401Unauthorized);
    }
}
