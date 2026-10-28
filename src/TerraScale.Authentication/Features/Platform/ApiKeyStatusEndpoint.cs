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

/// <summary>An API key token that still acts.</summary>
/// <param name="KeyId">The key it was issued for.</param>
public sealed record ApiKeyStatusBody([property: JsonPropertyName("key_id")] string KeyId);

/// <summary>
/// <c>GET /v1/terrascale/api-keys/self</c>: whether the API key a bearer access token was issued for still acts. TerraBase
/// asks before it serves the key's tokens and statements, in every region, and refuses them when this answers 401
/// (docs/terrabase/key-revocation.md). The token is its own credential: no other secret is needed to ask.
/// </summary>
public sealed class ApiKeyStatusEndpoint(ApiKeys keys, KeyRing ring, AuthOptions options, IConfiguration configuration, TimeProvider clock) : EndpointWithoutRequest<IResult>
{
    private const int MaxTokenLength = 8 * 1024;

    // Checks in flight on this instance, signature checks included: past this, 503 (TerraBase then refuses the key).
    // TerraBase asks once per key per node per second at most, so normal polling stays far below it.
    private const int MaxInFlight = 512;

    // Storage reads per key token kind per second on this instance; past it, 429 for that key alone (TerraBase refuses it).
    // Every TerraBase node asks about one at most about once a second, so only a caller hammering its own key reaches it.
    private const int KeyChecksPerSecond = 20;
    private const int MaxTrackedKeys = 16 * 1024;
    private static readonly ConcurrentDictionary<string, (long Second, int Count)> KeyChecks = new(StringComparer.Ordinal);
    private static int inFlight;

    /// <inheritdoc />
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
        // The handler checks the bearer itself, as AccountDeletionStatusEndpoint checks its receipt.
        Metadata(new RequiresSecurity(SecuritySchemes.ApiKeyToken));
    }

    /// <inheritdoc />
    public override async ValueTask<IResult> HandleAsync(CancellationToken cancellationToken)
    {
        this.HttpContext.Response.Headers.CacheControl = "no-store";
        // Revocations are written on the GRU authority's storage; another region's copy may lag behind an acknowledged one.
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

            return await this.CheckAsync(cancellationToken).ConfigureAwait(false);
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
        var signing = await ring.GetPublicAsync(OidcIssuer.InstanceId, Jwt.KeyId(token), cancellationToken).ConfigureAwait(false);
        try
        {
            claims = await Jwt.ValidateAsync(token, signing, "at+jwt", OidcIssuer.Issuer(options).AbsoluteUri, AuthOptions.TerraBaseAudience).ConfigureAwait(false);
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

        // Per key, key string and project: the unit TerraBase asks about, so a key used in many projects is not throttled.
        if (!this.Admit(keyId + " " + generation + " " + Claim(claims, PlatformGrants.ProjectClaim)))
        {
            this.HttpContext.Response.Headers.RetryAfter = "1";
            return Results.Json(new ErrorBody(new ErrorDetail("rate_limited", "Too many checks for this API key; retry.")), PlatformApiJson.Default.ErrorBody,
                statusCode: StatusCodes.Status429TooManyRequests);
        }

        string? refusal;
        try
        {
            refusal = await keys.RefusalAsync(keyId, generation, Claim(claims, PlatformGrants.OrganizationClaim), Claim(claims, PlatformGrants.ProjectClaim),
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception error) when (error is HttpRequestException or TimeoutException)
        {
            throw ApiException.Unavailable(error);
        }

        return refusal switch
        {
            "key_expired" => this.Refuse(refusal, "The API key has expired."),
            not null => this.Refuse(refusal, "The API key was revoked, rotated, or its organization or project was deleted."),
            _ => Results.Json(new ApiKeyStatusBody(keyId), PlatformApiJson.Default.ApiKeyStatusBody),
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

    private IResult Unavailable(string message)
    {
        this.HttpContext.Response.Headers.RetryAfter = "1";
        return Results.Json(new ErrorBody(new ErrorDetail("unavailable", message)), PlatformApiJson.Default.ErrorBody, statusCode: StatusCodes.Status503ServiceUnavailable);
    }

    private static string? Claim(IDictionary<string, object> claims, string name) =>
        claims.TryGetValue(name, out var value) && Convert.ToString(value, CultureInfo.InvariantCulture) is { Length: > 0 } text ? text : null;

    private IResult Refuse(string code, string message)
    {
        this.HttpContext.Response.Headers.WWWAuthenticate = $"Bearer realm=\"ts-auth\", error=\"invalid_token\", error_description=\"{code}\"";
        return Results.Json(new ErrorBody(new ErrorDetail(code, message)), PlatformApiJson.Default.ErrorBody, statusCode: StatusCodes.Status401Unauthorized);
    }
}
