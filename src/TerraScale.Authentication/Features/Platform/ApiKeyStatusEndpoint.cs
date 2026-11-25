using Microsoft.AspNetCore.Http.HttpResults;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
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

internal sealed record KeyStatusChecked(string? Refusal);

internal sealed record KeyStatusBusy;

internal readonly union KeyStatusRead(KeyStatusChecked, KeyStatusBusy);

public sealed class ApiKeyStatusEndpoint(ApiKeys keys, KeyRing ring, AuthOptions options, TimeProvider clock) : EndpointWithoutRequest<IResult>
{
    private const int MaxTokenLength = 8 * 1024;

    private const int MaxInFlight = 512;

    private const int MaxStorageReads = 64;

    private const int MaxCachedChecks = 16 * 1024;

    private const int MaxTrackedPeers = 16 * 1024;

    private const int PeerChecksPerSecond = 100;

    private const int PeerBurst = 200;

    private const int PeerConcurrency = 32;

    private static readonly TimeSpan CheckLifetime = TimeSpan.FromSeconds(1);

    private static readonly TimeSpan ReadTimeout = TimeSpan.FromSeconds(5);

    private static readonly TimeSpan WaitTimeout = ReadTimeout * 2;

    private static readonly Lock CheckGate = new();

    private static readonly Dictionary<string, SharedCheck> Checks = new(StringComparer.Ordinal);

    private static readonly Lock PeerGate = new();

    private static readonly Dictionary<IPAddress, PeerBudget> Peers = [];

    private static readonly SemaphoreSlim StorageReads = new(MaxStorageReads, MaxStorageReads);

    private static int inFlight;

    private static long nextCheckSweep;

    private static long nextPeerSweep;

    public override void Configure()
    {
        Get("/v1/terrascale/api-keys/self");
        Name("get_api_key_status");
        Summary("Check an API key token");
        Description("Bearer: an access token issued for an API key (client credentials). 200 while the key exists and is unexpired, the token's key string still works (a rotation's grace included), and its organization and project exist. 401 key_revoked or key_expired otherwise, invalid_token for any other token. Each check makes several strong-consistency reads of the storage replica serving the answering ts-auth instance, after every write that instance has seen, so it reflects what that replica held while the check ran. A call can join a check of the same key token that is still running, which may have started before a revocation reached that replica, and a completed check's answer is reused for at most 1 second. 429 rate_limited past 100 checks a second (bursts of 200) or 32 concurrent checks from one address (IPv6 per /64), counted before the token is verified, or while 16,384 other addresses are active; 503 when storage does not answer within 10 seconds, too many checks are in flight, or 16,384 distinct key tokens are being checked.");
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
        var peer = Peer(HttpContext.Connection.RemoteIpAddress);
        if (!AdmitPeer(peer))
        {
            HttpContext.Response.Headers.RetryAfter = "1";
            return TypedResults.Json(new ErrorBody(new ErrorDetail("rate_limited", "Too many API key checks from this address; retry.")), PlatformApiJson.Default.ErrorBody,
                statusCode: StatusCodes.Status429TooManyRequests);
        }

        try
        {
            return Interlocked.Increment(ref inFlight) > MaxInFlight
                ? Busy("Too many API key checks in flight; retry.")
                : await CheckAsync(cancellationToken);
        }
        finally
        {
            Interlocked.Decrement(ref inFlight);
            ReleasePeer(peer);
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

        var started = clock.GetTimestamp();
        using var expiry = new CancellationTokenSource(WaitTimeout, clock);
        using var bounded = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, expiry.Token);
        var lookup = ring.GetPublicAsync(OidcIssuer.InstanceId, Jwt.KeyId(token), bounded.Token);
        IReadOnlyList<Es256Key>? signing = null;
        try
        {
            if (await WithinAsync(lookup, started, cancellationToken) && !lookup.IsCanceled
                && lookup.Exception?.InnerException is not ApiException { Code: KeyRing.NoSigningKeysCode })
            {
                signing = await lookup;
            }
        }
        finally
        {
            if (signing is null)
            {
                _ = ReleaseAsync(lookup);
            }
        }

        if (signing is null)
        {
            return Busy("Signing keys are not available; retry.");
        }

        IDictionary<string, object>? claims;
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
        if (Spent(started))
        {
            return Busy("API key checks are waiting on storage; retry.");
        }

        if (Share(string.Join('\n', keyId, generation, organization, project), deadline => keys.RefusalAsync(keyId, generation, organization, project, deadline))
            is not { } check)
        {
            return Busy("Too many distinct API key checks are in progress; retry.");
        }

        var read = check.Read.Value;
        if (!await WithinAsync(read, started, cancellationToken))
        {
            return Busy("API key checks are waiting on storage; retry.");
        }

        var status = await read;
        return status switch
        {
            KeyStatusBusy => Busy("API key checks are waiting on storage; retry."),
            KeyStatusChecked { Refusal: null } => TypedResults.Json(new ApiKeyStatusBody(keyId), PlatformApiJson.Default.ApiKeyStatusBody),
            KeyStatusChecked { Refusal: "key_expired" } => Refuse("key_expired", "The API key has expired."),
            KeyStatusChecked { Refusal: { } refusal } => Refuse(refusal, "The API key was revoked, rotated, or its organization or project was deleted."),
        };
    }

    private async Task<bool> WithinAsync(Task work, long started, CancellationToken cancellationToken)
    {
        var remaining = WaitTimeout - clock.GetElapsedTime(started);
        if (!work.IsCompleted && remaining > TimeSpan.Zero)
        {
            using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            await Task.WhenAny(work, Task.Delay(remaining, clock, stop.Token));
            await stop.CancelAsync();
        }

        cancellationToken.ThrowIfCancellationRequested();
        return work.IsCompleted && !Spent(started);
    }

    private bool Spent(long started) => clock.GetElapsedTime(started) >= WaitTimeout;

    private static async Task ReleaseAsync(Task<IReadOnlyList<Es256Key>> lookup)
    {
        await Task.WhenAny(lookup);
        if (!lookup.IsCompletedSuccessfully)
        {
            _ = lookup.Exception;
            return;
        }

        foreach (var key in await lookup)
        {
            key.Dispose();
        }
    }

    private SharedCheck? Share(string checkedKey, Func<CancellationToken, Task<string?>> refusal)
    {
        lock (CheckGate)
        {
            var now = clock.GetTimestamp();
            if (Checks.TryGetValue(checkedKey, out var current) && Live(current, now))
            {
                return current;
            }

            if (current is null && Checks.Count >= MaxCachedChecks && !SweepChecks(now))
            {
                return null;
            }

            var started = new SharedCheck(check => ReadAsync(check, refusal));
            Checks[checkedKey] = started;
            return started;
        }
    }

    private async Task<KeyStatusRead> ReadAsync(SharedCheck check, Func<CancellationToken, Task<string?>> refusal)
    {
        if (!await StorageReads.WaitAsync(ReadTimeout))
        {
            return new KeyStatusBusy();
        }

        try
        {
            using var deadline = new CancellationTokenSource(ReadTimeout, clock);
            var read = refusal(deadline.Token);
            await Task.WhenAny(read);
            if (read.IsCanceled)
            {
                return new KeyStatusBusy();
            }

            var answer = new KeyStatusChecked(await read);
            check.Answer(clock.GetTimestamp());
            return answer;
        }
        finally
        {
            StorageReads.Release();
        }
    }

    private bool Live(SharedCheck check, long now) =>
        check.AnsweredAt is >= 0 and var answered
            ? clock.GetElapsedTime(answered, now) < CheckLifetime
            : !check.Read.IsValueCreated || !check.Read.Value.IsCompleted;

    private bool SweepChecks(long now)
    {
        if (now >= nextCheckSweep)
        {
            nextCheckSweep = now + Ticks(CheckLifetime);
            foreach (var key in Checks.Where(entry => !Live(entry.Value, now)).Select(entry => entry.Key).ToList())
            {
                Checks.Remove(key);
            }
        }

        return Checks.Count < MaxCachedChecks;
    }

    private bool AdmitPeer(IPAddress peer)
    {
        lock (PeerGate)
        {
            var now = clock.GetTimestamp();
            if (!Peers.TryGetValue(peer, out var budget))
            {
                if (Peers.Count >= MaxTrackedPeers && !SweepPeers(now))
                {
                    return false;
                }

                budget = new PeerBudget(PeerBurst, now);
                Peers.Add(peer, budget);
            }

            Refill(budget, now);
            if (budget.Active >= PeerConcurrency || budget.Tokens < 1)
            {
                return false;
            }

            budget.Tokens--;
            budget.Active++;
            return true;
        }
    }

    private static void ReleasePeer(IPAddress peer)
    {
        lock (PeerGate)
        {
            if (Peers.TryGetValue(peer, out var budget))
            {
                budget.Active--;
            }
        }
    }

    private bool SweepPeers(long now)
    {
        if (now >= nextPeerSweep)
        {
            nextPeerSweep = now + Ticks(CheckLifetime);
            foreach (var (peer, budget) in Peers.ToList())
            {
                Refill(budget, now);
                if (budget.Active == 0 && budget.Tokens >= PeerBurst)
                {
                    Peers.Remove(peer);
                }
            }
        }

        return Peers.Count < MaxTrackedPeers;
    }

    private void Refill(PeerBudget budget, long now)
    {
        budget.Tokens = Math.Min(PeerBurst, budget.Tokens + (clock.GetElapsedTime(budget.Refilled, now).TotalSeconds * PeerChecksPerSecond));
        budget.Refilled = now;
    }

    private long Ticks(TimeSpan span) => (long)(span.TotalSeconds * clock.TimestampFrequency);

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

    private JsonHttpResult<ErrorBody> Busy(string message)
    {
        HttpContext.Response.Headers.RetryAfter = "1";
        return TypedResults.Json(new ErrorBody(new ErrorDetail("temporarily_unavailable", message)), PlatformApiJson.Default.ErrorBody, statusCode: StatusCodes.Status503ServiceUnavailable);
    }

    private JsonHttpResult<ErrorBody> Refuse(string code, string message)
    {
        HttpContext.Response.Headers.WWWAuthenticate = $"Bearer realm=\"ts-auth\", error=\"invalid_token\", error_description=\"{code}\"";
        return TypedResults.Json(new ErrorBody(new ErrorDetail(code, message)), PlatformApiJson.Default.ErrorBody, statusCode: StatusCodes.Status401Unauthorized);
    }

    private sealed class SharedCheck
    {
        private long answeredAt = -1;

        public SharedCheck(Func<SharedCheck, Task<KeyStatusRead>> start) => Read = new(() => start(this));

        public Lazy<Task<KeyStatusRead>> Read { get; }

        public long AnsweredAt => Volatile.Read(ref answeredAt);

        public void Answer(long at) => Volatile.Write(ref answeredAt, at);
    }

    private sealed class PeerBudget(double tokens, long refilled)
    {
        public double Tokens { get; set; } = tokens;

        public long Refilled { get; set; } = refilled;

        public int Active { get; set; }
    }
}
