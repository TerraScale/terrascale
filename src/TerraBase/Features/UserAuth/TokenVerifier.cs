using System.Buffers;
using System.Buffers.Text;
using System.Collections.Concurrent;
using System.Collections.Frozen;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;
using TerraBase.Configuration;
using TerraBase.Features.Callers;
using TerraBase.Features.Catalog;
using TerraBase.Features.Rules;
using TerraBase.Hosting;

namespace TerraBase.Features.UserAuth;

internal sealed record UntrustedIssuer
{
    public static UntrustedIssuer Instance { get; } = new();
}

internal readonly union IssuerTrust(TrustedIssuer, UntrustedIssuer, BackendUnavailable);

internal sealed class TokenVerifier(TerraBaseOptions options, IHttpClientFactory http, CatalogStore catalog, AuthenticationGate gate, TimeProvider time)
{
    public const string HttpClientName = "ts-auth";

    private const string TenantUserUse = "user";

    private const string TenantCustomUse = "custom";

    private const string ProjectDataPath = "/v1/projects/";

    private const int MaxProjectIssuers = 1024;

    private const int MaxTokenChars = 16 * 1024;

    private const int MaxVerifiedTokens = 4096;

    private static readonly TimeSpan ProjectIssuerIdle = TimeSpan.FromHours(1);

    private static readonly JsonWebTokenHandler Tokens = new() { MaximumTokenSizeInBytes = MaxTokenChars };

    private static readonly string[] Algorithms =
    [
        SecurityAlgorithms.EcdsaSha256, SecurityAlgorithms.EcdsaSha384, SecurityAlgorithms.EcdsaSha512,
        SecurityAlgorithms.RsaSha256, SecurityAlgorithms.RsaSha384, SecurityAlgorithms.RsaSha512,
        SecurityAlgorithms.RsaSsaPssSha256, SecurityAlgorithms.RsaSsaPssSha384, SecurityAlgorithms.RsaSsaPssSha512,
    ];

    private static readonly string[] Types = ["at+jwt", "application/at+jwt", "JWT"];

    private static readonly string[] TenantRefusedClaims = ["project_id", "instance_id", "token_kind", "platform_role", "key_id", "key_gen"];

    private readonly string? _platformIssuer = options.AuthIssuer?.TrimEnd('/');

    private readonly IssuerEntry? _platform = options.AuthIssuer is { } issuer ? CreateIssuer(http, issuer) : null;

    private readonly Dictionary<string, IssuerSlot> _projects = new(StringComparer.Ordinal);

    private readonly Lock _issuers = new();

    private readonly ConcurrentDictionary<TokenDigest, VerifiedTokenEntry> _verifiedTokens = new();

    private readonly Lock _trimGate = new();

    private int _verifiedCount;

    public bool TryVerifyCached(ReadOnlySpan<char> token, [NotNullWhen(true)] out Caller? caller)
    {
        if (_platform is null || _platformIssuer is null || token.Length is 0 or > MaxTokenChars)
        {
            caller = null;
            return false;
        }

        return TryGetCachedToken(TokenDigest.HashUtf8(token), out caller);
    }

    public Task WarmPlatformAsync(CancellationToken cancellationToken) =>
        _platform is { } entry ? CurrentConfigurationAsync(entry, cancellationToken) : Task.CompletedTask;

    public ValueTask<CallerAuthenticationOutcome> VerifyAsync(string token, CancellationToken cancellationToken)
    {
        if (_platform is null || _platformIssuer is not { } platformIssuer)
        {
            return ValueTask.FromResult<CallerAuthenticationOutcome>(new CredentialRejected("bearer tokens need TERRABASE_AUTH_ISSUER", "invalid_token"));
        }

        if (token.Length is 0 or > MaxTokenChars)
        {
            return ValueTask.FromResult<CallerAuthenticationOutcome>(new CredentialRejected("the bearer token is malformed", "invalid_token"));
        }

        var digest = TokenDigest.HashUtf8(token);
        if (TryGetCachedToken(digest, out var cached))
        {
            return ValueTask.FromResult<CallerAuthenticationOutcome>(cached);
        }

        return VerifySlowAsync(token, digest, platformIssuer, cancellationToken);
    }

    private async ValueTask<CallerAuthenticationOutcome> VerifySlowAsync(
        string token, TokenDigest digest, string platformIssuer, CancellationToken cancellationToken)
    {
        if (!Tokens.CanReadToken(token) || Read(token) is not { } unverified)
        {
            return new CredentialRejected("the bearer token is malformed", "invalid_token");
        }

        var trust = await TrustAsync(unverified, platformIssuer, cancellationToken);
        return trust switch
        {
            TrustedIssuer trusted => await VerifyTrustedAsync(token, digest, trusted, cancellationToken),
            UntrustedIssuer => new CredentialRejected("the bearer token's issuer is not trusted", "invalid_token"),
            BackendUnavailable unavailable => unavailable,
        };
    }

    private async ValueTask<CallerAuthenticationOutcome> VerifyTrustedAsync(
        string token, TokenDigest digest, TrustedIssuer trusted, CancellationToken cancellationToken)
    {
        try
        {
            return await VerifyPinnedAsync(token, digest, trusted, cancellationToken);
        }
        finally
        {
            Unpin(trusted.Entry);
        }
    }

    private async ValueTask<CallerAuthenticationOutcome> VerifyPinnedAsync(
        string token, TokenDigest digest, TrustedIssuer trusted, CancellationToken cancellationToken)
    {
        if (await CurrentConfigurationAsync(trusted.Entry, cancellationToken) is not { } state)
        {
            return new BackendUnavailable("ts-auth signing keys are unavailable; retry", "token_keys_pending");
        }

        var validated = await Tokens.ValidateTokenAsync(token, Parameters(trusted, state.Configuration.SigningKeys));
        if (!validated.IsValid && validated.Exception is SecurityTokenSignatureKeyNotFoundException)
        {
            if (trusted.Tenant is { } project)
            {
                using var permit = await gate.EnterAsync(cancellationToken);
                if (!permit.IsAcquired)
                {
                    return AuthenticationGate.Busy;
                }

                var known = await catalog.GetProjectAsync(project, cancellationToken);
                var refusal = known switch
                {
                    ProjectRecord => null,
                    ResourceNotFound => Forget(trusted.Issuer),
                    BackendUnavailable unavailable => (CallerAuthenticationOutcome?)unavailable,
                };
                if (refusal is { } refused)
                {
                    return refused;
                }
            }

            if (await RefreshFreshAsync(trusted.Entry, cancellationToken) is not { } refreshed)
            {
                return new BackendUnavailable("ts-auth signing keys are unavailable; retry", "token_keys_pending");
            }

            state = refreshed;
            validated = await Tokens.ValidateTokenAsync(token, Parameters(trusted, state.Configuration.SigningKeys));
        }

        if (!validated.IsValid || validated.SecurityToken is not JsonWebToken jwt)
        {
            return new CredentialRejected("the bearer token is invalid or expired", "invalid_token");
        }

        var outcome = trusted.Tenant is { } tenant ? ClassifyTenant(jwt, tenant) : Classify(jwt);
        return outcome switch
        {
            Caller caller => Remember(digest, caller, trusted.Entry, state, jwt),
            CredentialRejected rejected => rejected,
            BackendUnavailable unavailable => unavailable,
        };
    }

    private CallerAuthenticationOutcome Remember(
        TokenDigest digest, Caller caller, IssuerEntry entry, FreshConfigurationState state, JsonWebToken jwt)
    {
        if (TryReadLifetime(jwt, out var expiresAt, out var notBefore))
        {
            var now = time.GetUtcNow();
            var ticks = time.GetTimestamp();
            if (now < expiresAt
                && (notBefore is not { } nbf || now >= nbf)
                && !entry.Retired
                && ReferenceEquals(entry.Fresh, state)
                && time.GetElapsedTime(state.FetchedTimestamp, ticks) < ConfigurationLifetime)
            {
                CacheVerifiedToken(digest, new VerifiedTokenEntry(caller, entry, state, expiresAt, notBefore), now, ticks);
            }
        }

        return caller;
    }

    private bool TryGetCachedToken(TokenDigest digest, [NotNullWhen(true)] out Caller? caller)
    {
        if (_verifiedTokens.TryGetValue(digest, out var entry))
        {
            var now = time.GetUtcNow();
            var ticks = time.GetTimestamp();
            if (!entry.Issuer.Retired
                && ReferenceEquals(entry.Issuer.Fresh, entry.Configuration)
                && time.GetElapsedTime(entry.Configuration.FetchedTimestamp, ticks) < ConfigurationLifetime
                && now < entry.ExpiresAt
                && (entry.NotBefore is not { } notBefore || now >= notBefore))
            {
                Volatile.Write(ref entry.Issuer.LastUsed, ticks);
                caller = entry.Caller;
                return true;
            }

            if (entry.Issuer.Retired
                || !ReferenceEquals(entry.Issuer.Fresh, entry.Configuration)
                || time.GetElapsedTime(entry.Configuration.FetchedTimestamp, ticks) >= ConfigurationLifetime
                || now >= entry.ExpiresAt)
            {
                if (_verifiedTokens.TryRemove(digest, out _))
                {
                    _ = Interlocked.Decrement(ref _verifiedCount);
                }
            }
        }

        caller = null;
        return false;
    }

    private void CacheVerifiedToken(TokenDigest digest, VerifiedTokenEntry entry, DateTimeOffset now, long ticks)
    {
        if (Volatile.Read(ref _verifiedCount) >= MaxVerifiedTokens)
        {
            TrimVerifiedTokens(now, ticks);
        }

        if (Volatile.Read(ref _verifiedCount) < MaxVerifiedTokens && _verifiedTokens.TryAdd(digest, entry))
        {
            _ = Interlocked.Increment(ref _verifiedCount);
        }
    }

    private void TrimVerifiedTokens(DateTimeOffset now, long ticks)
    {
        if (!_trimGate.TryEnter())
        {
            return;
        }

        try
        {
            if (Volatile.Read(ref _verifiedCount) < MaxVerifiedTokens)
            {
                return;
            }

            var removed = 0;
            foreach (var (key, value) in _verifiedTokens)
            {
                if (value.Issuer.Retired
                    || !ReferenceEquals(value.Issuer.Fresh, value.Configuration)
                    || time.GetElapsedTime(value.Configuration.FetchedTimestamp, ticks) >= ConfigurationLifetime
                    || now >= value.ExpiresAt)
                {
                    if (_verifiedTokens.TryRemove(key, out _))
                    {
                        _ = Interlocked.Decrement(ref _verifiedCount);
                        removed++;
                    }
                }
            }

            if (Volatile.Read(ref _verifiedCount) >= MaxVerifiedTokens)
            {
                var target = MaxVerifiedTokens / 4;
                foreach (var (key, _) in _verifiedTokens)
                {
                    if (removed >= target)
                    {
                        break;
                    }

                    if (_verifiedTokens.TryRemove(key, out _))
                    {
                        _ = Interlocked.Decrement(ref _verifiedCount);
                        removed++;
                    }
                }
            }
        }
        finally
        {
            _trimGate.Exit();
        }
    }

    private static bool TryReadLifetime(JsonWebToken jwt, out DateTimeOffset expiresAt, out DateTimeOffset? notBefore)
    {
        if (jwt.ValidTo == DateTime.MinValue)
        {
            expiresAt = default;
            notBefore = null;
            return false;
        }

        expiresAt = new DateTimeOffset(DateTime.SpecifyKind(jwt.ValidTo, DateTimeKind.Utc));
        notBefore = jwt.ValidFrom == DateTime.MinValue
            ? null
            : new DateTimeOffset(DateTime.SpecifyKind(jwt.ValidFrom, DateTimeKind.Utc));
        return true;
    }

    private async ValueTask<IssuerTrust> TrustAsync(JsonWebToken token, string platformIssuer, CancellationToken cancellationToken)
    {
        if (string.Equals(token.Issuer, platformIssuer, StringComparison.Ordinal) && _platform is { } platform)
        {
            return new TrustedIssuer(platformIssuer, Pin(platform), options.AuthAudiences, Tenant: null);
        }

        if (!Uri.TryCreate(token.Issuer, UriKind.Absolute, out var issuer) || issuer.Segments is not [.., var last] || !IsCanonicalUlid(last)
            || !Ulid.TryParse(last, out var project))
        {
            return UntrustedIssuer.Instance;
        }

        var projectIssuer = new Uri(new Uri(platformIssuer), last).AbsoluteUri;
        if (!string.Equals(token.Issuer, projectIssuer, StringComparison.Ordinal))
        {
            return UntrustedIssuer.Instance;
        }

        var audiences = options.AuthAudiences.Select(audience => string.Concat(audience.TrimEnd('/'), ProjectDataPath, last)).ToArray();
        var cached = PinCached(projectIssuer);
        IssuerTrust? decided = cached switch
        {
            IssuerEntry existing => new TrustedIssuer(projectIssuer, existing, audiences, project),
            RetiredIssuer => AuthenticationGate.Busy,
            MissingIssuer => null,
        };
        if (decided is { } early)
        {
            return early;
        }

        using var permit = await gate.EnterAsync(cancellationToken);
        if (!permit.IsAcquired)
        {
            return AuthenticationGate.Busy;
        }

        var known = await catalog.GetProjectAsync(project, cancellationToken);
        return known switch
        {
            ProjectRecord => PinNew(projectIssuer) is { } created ? new TrustedIssuer(projectIssuer, created, audiences, project) : AuthenticationGate.Busy,
            ResourceNotFound => UntrustedIssuer.Instance,
            BackendUnavailable unavailable => unavailable,
        };
    }

    private IssuerEntry Pin(IssuerEntry entry)
    {
        lock (_issuers)
        {
            entry.Pins++;
            return entry;
        }
    }

    private void Unpin(IssuerEntry entry)
    {
        lock (_issuers)
        {
            entry.Pins--;
            if (entry.Pins == 0 && entry.Retired && _projects.TryGetValue(entry.Issuer, out var slot) && ReferenceEquals(slot.Entry, entry))
            {
                _projects.Remove(entry.Issuer);
            }
        }
    }

    private IssuerLookup PinCached(string issuer)
    {
        lock (_issuers)
        {
            if (!_projects.TryGetValue(issuer, out var slot))
            {
                return MissingIssuer.Instance;
            }

            if (slot.Entry.Retired)
            {
                return RetiredIssuer.Instance;
            }

            var now = time.GetTimestamp();
            var lastUsed = Volatile.Read(ref slot.Entry.LastUsed);
            if (slot.Entry.Pins == 0 && time.GetElapsedTime(lastUsed, now) > ProjectIssuerIdle)
            {
                slot.Entry.Retired = true;
                _projects.Remove(issuer);
                return MissingIssuer.Instance;
            }

            Volatile.Write(ref slot.Entry.LastUsed, now);
            slot.Entry.Pins++;
            return slot.Entry;
        }
    }

    private IssuerEntry? PinNew(string issuer)
    {
        lock (_issuers)
        {
            var now = time.GetTimestamp();
            if (_projects.TryGetValue(issuer, out var slot))
            {
                if (slot.Entry.Retired)
                {
                    return null;
                }

                Volatile.Write(ref slot.Entry.LastUsed, now);
                slot.Entry.Pins++;
                return slot.Entry;
            }

            if (_projects.Count >= MaxProjectIssuers)
            {
                string? idle = null;
                var oldest = long.MaxValue;
                foreach (var (key, candidate) in _projects)
                {
                    var candidateLastUsed = Volatile.Read(ref candidate.Entry.LastUsed);
                    if (candidate.Entry.Pins == 0 && candidateLastUsed < oldest)
                    {
                        idle = key;
                        oldest = candidateLastUsed;
                    }
                }

                if (idle is null)
                {
                    return null;
                }

                if (_projects.Remove(idle, out var evicted))
                {
                    evicted.Entry.Retired = true;
                }
            }

            var created = CreateIssuer(http, issuer);
            created.Pins = 1;
            Volatile.Write(ref created.LastUsed, now);
            _projects.Add(issuer, new IssuerSlot(created));
            return created;
        }
    }

    private CallerAuthenticationOutcome Forget(string issuer)
    {
        lock (_issuers)
        {
            if (_projects.TryGetValue(issuer, out var slot))
            {
                slot.Entry.Retired = true;
                if (slot.Entry.Pins == 0)
                {
                    _projects.Remove(issuer);
                }
            }
        }

        return new CredentialRejected("the bearer token's issuer is not trusted", "invalid_token");
    }

    private static IssuerEntry CreateIssuer(IHttpClientFactory http, string issuer)
    {
        var metadataAddress = string.Concat(issuer.TrimEnd('/'), "/.well-known/openid-configuration");
        var retriever = new HttpDocumentRetriever(http.CreateClient(HttpClientName)) { RequireHttps = new Uri(issuer).Scheme == Uri.UriSchemeHttps };
        return new IssuerEntry(issuer, metadataAddress, retriever);
    }

    private static bool IsCanonicalUlid(string value) =>
        value.Length == 26 && Ulid.TryParse(value, out var parsed) && string.Equals(parsed.ToString(), value, StringComparison.Ordinal);

    private static JsonWebToken? Read(string token)
    {
        try
        {
            return Tokens.ReadJsonWebToken(token);
        }
        catch (Exception error) when (error is SecurityTokenMalformedException or ArgumentException)
        {
            return null;
        }
    }

    private static CallerAuthenticationOutcome ClassifyTenant(JsonWebToken jwt, Ulid project)
    {
        var invalid = new CredentialRejected("the project tenant token's claims are invalid", "invalid_token");
        if (!jwt.TryGetPayloadValue<string>(JwtRegisteredClaimNames.Sub, out var subject) || !IsCanonicalUlid(subject)
            || !jwt.TryGetPayloadValue<string>("token_use", out var use) || use is not (TenantUserUse or TenantCustomUse)
            || TenantRefusedClaims.Any(name => jwt.TryGetPayloadValue<object>(name, out _)))
        {
            return invalid;
        }

        var custom = use == TenantCustomUse;
        string? database = null;
        if (jwt.TryGetPayloadValue<object>("database", out _)
            && (!custom || !jwt.TryGetPayloadValue<string>("database", out database) || !IsCanonicalUlid(database)))
        {
            return invalid;
        }

        if (Payload(jwt) is not { } payload)
        {
            return invalid;
        }

        using (payload)
        {
            return RulesAuth.Read(subject, payload.RootElement, custom) is { } auth
                ? new EndUserCaller(project, database, auth)
                : invalid;
        }
    }

    private static CallerAuthenticationOutcome Classify(JsonWebToken jwt)
    {
        var invalid = new CredentialRejected("the bearer token's claims are invalid", "invalid_token");
        if (!jwt.TryGetPayloadValue<string>(JwtRegisteredClaimNames.Sub, out var subject) || string.IsNullOrWhiteSpace(subject) || subject.Length > 128)
        {
            return invalid;
        }

        var delegated = jwt.TryGetPayloadValue<object>("act", out _);
        var projectId = jwt.TryGetPayloadValue<string>("project_id", out var project) ? project : null;
        var database = jwt.TryGetPayloadValue<string>("database", out var narrowed) ? narrowed : null;
        Ulid? projectUlid = projectId is not null && projectId.Length == 26 && Ulid.TryParse(projectId, out var parsed) ? parsed : null;
        if ((projectId is not null && projectUlid is null) || (database is not null && projectUlid is null))
        {
            return invalid;
        }

        if (projectUlid is { } scopedProject)
        {
            var organizationId = jwt.TryGetPayloadValue<string>("org_id", out var org) ? org : null;
            Ulid? organizationUlid = organizationId is not null && IsCanonicalUlid(organizationId) && Ulid.TryParse(organizationId, out var parsedOrg) ? parsedOrg : null;
            if (organizationId is not null && organizationUlid is null)
            {
                return invalid;
            }

            var scopes = jwt.TryGetPayloadValue<string>("scope", out var scope) && scope.Length <= 4096
                ? scope.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToFrozenSet(StringComparer.Ordinal)
                : FrozenSet<string>.Empty;
            return new ProjectCaller(subject, scopedProject, organizationUlid, database, scopes) { Delegated = delegated };
        }

        return subject.Length == 26 && Ulid.TryParse(subject, out _) ? new UserCaller(subject, ViaOperator: false) { Delegated = delegated } : invalid;
    }

    private static JsonDocument? Payload(JsonWebToken jwt)
    {
        var encoded = jwt.EncodedPayload;
        var bytes = new byte[Base64Url.GetMaxDecodedLength(encoded.Length)];
        if (Base64Url.DecodeFromChars(encoded, bytes, out _, out var written) != OperationStatus.Done)
        {
            return null;
        }

        try
        {
            return JsonDocument.Parse(bytes.AsMemory(0, written));
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private TokenValidationParameters Parameters(TrustedIssuer trusted, IEnumerable<SecurityKey> keys) => new()
    {
        ValidIssuer = trusted.Issuer,
        ValidAudiences = trusted.Audiences,
        ValidateAudience = trusted.Audiences.Count > 0,
        IssuerSigningKeys = keys,
        ValidAlgorithms = Algorithms,
        ValidTypes = Types,
        RequireSignedTokens = true,
        RequireExpirationTime = true,
        ClockSkew = TimeSpan.FromSeconds(30),
        LifetimeValidator = (notBefore, expires, _, _) =>
        {
            var now = time.GetUtcNow().UtcDateTime;
            return expires is { } until && until >= now.AddSeconds(-30) && (notBefore is null || notBefore.Value <= now.AddSeconds(30));
        },
    };

    private static readonly TimeSpan RefreshCooldown = TimeSpan.FromSeconds(5);

    private static readonly TimeSpan ConfigurationLifetime = TimeSpan.FromHours(1);

    private async Task<FreshConfigurationState?> CurrentConfigurationAsync(IssuerEntry entry, CancellationToken cancellationToken)
    {
        if (entry.Fresh is { } fresh && time.GetElapsedTime(fresh.FetchedTimestamp) < ConfigurationLifetime)
        {
            return fresh;
        }

        return await Retrieval(entry, unknownKey: false).WaitAsync(cancellationToken) ?? entry.Fresh;
    }

    private async Task<FreshConfigurationState?> RefreshFreshAsync(IssuerEntry entry, CancellationToken cancellationToken) =>
        await Retrieval(entry, unknownKey: true).WaitAsync(cancellationToken);

    private Task<FreshConfigurationState?> Retrieval(IssuerEntry entry, bool unknownKey)
    {
        lock (entry.Gate)
        {
            if (entry.Inflight is { } running)
            {
                return running;
            }

            var (attempted, failed) = unknownKey
                ? (entry.LastUnknownKeyAttemptTimestamp, entry.LastUnknownKeyFailed)
                : (entry.LastRefreshAttemptTimestamp, entry.LastRefreshFailed);
            if (attempted != 0 && time.GetElapsedTime(attempted) < RefreshCooldown)
            {
                return Task.FromResult(failed ? null : entry.Fresh);
            }

            var started = RetrieveAsync(Pin(entry), unknownKey);
            entry.Inflight = started;
            return started;
        }
    }

    private async Task<FreshConfigurationState?> RetrieveAsync(IssuerEntry entry, bool unknownKey)
    {
        await Task.Yield();
        OpenIdConnectConfiguration? fetched = null;
        FreshConfigurationState? state = null;
        try
        {
            fetched = await FetchAsync(entry);
        }
        finally
        {
            var now = time.GetTimestamp();
            lock (entry.Gate)
            {
                entry.LastRefreshAttemptTimestamp = now;
                entry.LastRefreshFailed = fetched is null;
                if (unknownKey)
                {
                    entry.LastUnknownKeyAttemptTimestamp = now;
                    entry.LastUnknownKeyFailed = fetched is null;
                }

                if (fetched is not null)
                {
                    state = new FreshConfigurationState(fetched, now);
                    entry.Fresh = state;
                }

                entry.Inflight = null;
            }

            Unpin(entry);
        }

        return state;
    }

    private static async Task<OpenIdConnectConfiguration?> FetchAsync(IssuerEntry entry)
    {
        try
        {
            return await OpenIdConnectConfigurationRetriever.GetAsync(entry.MetadataAddress, entry.Retriever, CancellationToken.None);
        }
        catch (Exception error) when (error is InvalidOperationException or HttpRequestException or IOException or OperationCanceledException
            or JsonException or ArgumentException or FormatException or SecurityTokenException)
        {
            return null;
        }
    }

    private sealed record VerifiedTokenEntry(
        Caller Caller,
        IssuerEntry Issuer,
        FreshConfigurationState Configuration,
        DateTimeOffset ExpiresAt,
        DateTimeOffset? NotBefore);
}

internal sealed record FreshConfigurationState(OpenIdConnectConfiguration Configuration, long FetchedTimestamp);

internal sealed class IssuerEntry(string issuer, string metadataAddress, HttpDocumentRetriever retriever)
{
    public string Issuer { get; } = issuer;
    public string MetadataAddress { get; } = metadataAddress;
    public HttpDocumentRetriever Retriever { get; } = retriever;
    public Lock Gate { get; } = new();
    public Task<FreshConfigurationState?>? Inflight;
    public volatile FreshConfigurationState? Fresh;
    public long LastRefreshAttemptTimestamp;
    public long LastUnknownKeyAttemptTimestamp;
    public long LastUsed;
    public bool LastUnknownKeyFailed;
    public int Pins;
    public volatile bool Retired;
    public bool LastRefreshFailed;
}

internal sealed class IssuerSlot(IssuerEntry entry)
{
    public IssuerEntry Entry { get; } = entry;
}

internal sealed record MissingIssuer
{
    public static MissingIssuer Instance { get; } = new();
}

internal sealed record RetiredIssuer
{
    public static RetiredIssuer Instance { get; } = new();
}

internal readonly union IssuerLookup(IssuerEntry, MissingIssuer, RetiredIssuer);

internal sealed record TrustedIssuer(string Issuer, IssuerEntry Entry, IReadOnlyList<string> Audiences, Ulid? Tenant);
