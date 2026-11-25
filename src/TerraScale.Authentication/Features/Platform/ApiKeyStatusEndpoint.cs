using Microsoft.AspNetCore.Http.HttpResults;
using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text.Json.Serialization;
using System.Threading.RateLimiting;
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

public sealed class ApiKeyStatusEndpoint(ApiKeys keys, KeyRing ring, AuthOptions options, TimeProvider clock) : EndpointWithoutRequest<IResult>
{
    private const int MaxTokenLength = 8 * 1024;

    private const int MaxInFlight = 512;

    private const int MaxCachedChecks = 16 * 1024;

    private const int PeerChecksPerSecond = 100;

    private const int PeerBurst = 200;

    private const int PeerConcurrency = 32;

    private static readonly TimeSpan CheckLifetime = TimeSpan.FromSeconds(1);

    private static readonly ConcurrentDictionary<string, SharedCheck> Checks = new(StringComparer.Ordinal);

    private static readonly PartitionedRateLimiter<IPAddress> Peers = PartitionedRateLimiter.CreateChained(
        PartitionedRateLimiter.Create<IPAddress, IPAddress>(static peer => RateLimitPartition.GetTokenBucketLimiter(peer, static _ => new TokenBucketRateLimiterOptions
        {
            TokenLimit = PeerBurst,
            TokensPerPeriod = PeerChecksPerSecond,
            ReplenishmentPeriod = TimeSpan.FromSeconds(1),
            QueueLimit = 0,
            AutoReplenishment = true,
        })),
        PartitionedRateLimiter.Create<IPAddress, IPAddress>(static peer => RateLimitPartition.GetConcurrencyLimiter(peer, static _ => new ConcurrencyLimiterOptions
        {
            PermitLimit = PeerConcurrency,
            QueueLimit = 0,
        })));

    private static int inFlight;

    private static long nextSweep;

    public override void Configure()
    {
        Get("/v1/terrascale/api-keys/self");
        Name("get_api_key_status");
        Summary("Check an API key token");
        Description("Bearer: an access token issued for an API key (client credentials). 200 while the key exists and is unexpired, the token's key string still works (a rotation's grace included), and its organization and project exist. 401 key_revoked or key_expired otherwise, invalid_token for any other token. Every region answers from strong storage reads; concurrent checks of one key token share one read and its answer is reused for at most 1 second, so a revocation shows within 1 second. 429 rate_limited past 100 checks a second (bursts of 200) or 32 concurrent checks from one address (IPv6 per /64), counted before the token is verified; 503 when storage does not answer or too many checks are in flight.");
        Tags("API keys");
        Produces<ApiKeyStatusBody>();
        Produces<ErrorBody>(StatusCodes.Status401Unauthorized);
        Produces<ErrorBody>(StatusCodes.Status429TooManyRequests);
        Produces<ErrorBody>(StatusCodes.Status503ServiceUnavailable);
        Metadata(new RequiresSecurity(SecuritySchemes.ApiKeyToken));
    }

    public override async ValueTask<IResult> HandleAsync(CancellationToken cancellationToken)
    {
        HttpContext.Response.Headers.CacheControl = "no-store";
        using var admitted = Peers.AttemptAcquire(Peer(HttpContext.Connection.RemoteIpAddress));
        if (!admitted.IsAcquired)
        {
            HttpContext.Response.Headers.RetryAfter = "1";
            return TypedResults.Json(new ErrorBody(new ErrorDetail("rate_limited", "Too many API key checks from this address; retry.")), PlatformApiJson.Default.ErrorBody,
                statusCode: StatusCodes.Status429TooManyRequests);
        }

        try
        {
            if (Interlocked.Increment(ref inFlight) > MaxInFlight)
            {
                HttpContext.Response.Headers.RetryAfter = "1";
                return TypedResults.Json(new ErrorBody(new ErrorDetail("unavailable", "Too many API key checks in flight; retry.")), PlatformApiJson.Default.ErrorBody,
                    statusCode: StatusCodes.Status503ServiceUnavailable);
            }

            return await CheckAsync(cancellationToken);
        }
        finally
        {
            Interlocked.Decrement(ref inFlight);
        }
    }

    private async Task<IResult> CheckAsync(CancellationToken cancellationToken)
    {
        if (!AuthenticationHeaderValue.TryParse(HttpContext.Request.Headers.Authorization, out var header)
            || !string.Equals(header.Scheme, "Bearer", StringComparison.OrdinalIgnoreCase)
            || header.Parameter is not { Length: > 0 and <= MaxTokenLength } token)
        {
            return Refuse("invalid_token", "Present an access token issued for an API key.");
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
            return Refuse("invalid_token", "Present an access token issued for an API key.");
        }

        var organization = Claim(claims, PlatformGrants.OrganizationClaim);
        var project = Claim(claims, PlatformGrants.ProjectClaim);
        var refusal = await SharedRefusalAsync(string.Join('\n', keyId, generation, organization, project),
            () => keys.RefusalAsync(keyId, generation, organization, project, CancellationToken.None), cancellationToken);
        return refusal switch
        {
            "key_expired" => Refuse(refusal, "The API key has expired."),
            not null => Refuse(refusal, "The API key was revoked, rotated, or its organization or project was deleted."),
            _ => TypedResults.Json(new ApiKeyStatusBody(keyId), PlatformApiJson.Default.ApiKeyStatusBody),
        };
    }

    private async Task<string?> SharedRefusalAsync(string checkedKey, Func<Task<string?>> read, CancellationToken cancellationToken)
    {
        var candidate = new SharedCheck(new Lazy<Task<string?>>(read), clock.GetTimestamp());
        var check = Checks.TryGetValue(checkedKey, out var current) && Fresh(current) ? current
            : Checks.Count < MaxCachedChecks || Sweep() ? Checks.AddOrUpdate(checkedKey, candidate, (_, existing) => Fresh(existing) ? existing : candidate)
            : candidate;
        try
        {
            return await check.Refusal.Value.WaitAsync(cancellationToken);
        }
        finally
        {
            if (check.Refusal.Value is { IsFaulted: true } or { IsCanceled: true })
            {
                Checks.TryRemove(KeyValuePair.Create(checkedKey, check));
            }
        }
    }

    private bool Fresh(SharedCheck check) => clock.GetElapsedTime(check.Started) < CheckLifetime;

    private bool Sweep()
    {
        var now = clock.GetTimestamp();
        var due = Interlocked.Read(ref nextSweep);
        if (now >= due && Interlocked.CompareExchange(ref nextSweep, now + (long)(CheckLifetime.TotalSeconds * clock.TimestampFrequency), due) == due)
        {
            foreach (var entry in Checks)
            {
                if (!Fresh(entry.Value))
                {
                    Checks.TryRemove(entry);
                }
            }
        }

        return Checks.Count < MaxCachedChecks;
    }

    private static IPAddress Peer(IPAddress? address)
    {
        if (address is null)
        {
            return IPAddress.IPv6None;
        }

        if (address.IsIPv4MappedToIPv6)
        {
            return address.MapToIPv4();
        }

        if (address.AddressFamily != AddressFamily.InterNetworkV6)
        {
            return address;
        }

        Span<byte> bytes = stackalloc byte[16];
        if (!address.TryWriteBytes(bytes, out _))
        {
            return address;
        }

        bytes[8..].Clear();
        return new IPAddress(bytes);
    }

    private static string? Claim(IDictionary<string, object> claims, string name) =>
        claims.TryGetValue(name, out var value) && Convert.ToString(value, CultureInfo.InvariantCulture) is { Length: > 0 } text ? text : null;

    private JsonHttpResult<ErrorBody> Refuse(string code, string message)
    {
        HttpContext.Response.Headers.WWWAuthenticate = $"Bearer realm=\"ts-auth\", error=\"invalid_token\", error_description=\"{code}\"";
        return TypedResults.Json(new ErrorBody(new ErrorDetail(code, message)), PlatformApiJson.Default.ErrorBody, statusCode: StatusCodes.Status401Unauthorized);
    }

    private sealed record SharedCheck(Lazy<Task<string?>> Refusal, long Started);
}
