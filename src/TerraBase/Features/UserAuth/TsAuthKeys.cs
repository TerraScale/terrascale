using System.Diagnostics.Metrics;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;
using TerraBase.Configuration;
using TerraBase.Errors;
using TerraBase.Features.Clock;
using TerraBase.Hosting;
using ZLogger;

namespace TerraBase.Features.UserAuth;

internal sealed partial class TsAuthKeys : BackgroundService, IIssuerKeys
{
    public const string HttpClientName = "ts-auth";

    public const int MaxResponseBytes = 1024 * 1024;

    public static readonly TimeSpan FetchTimeout = TimeSpan.FromSeconds(10);
    public static readonly TimeSpan StartupRetry = TimeSpan.FromSeconds(2);
    public static readonly TimeSpan RefreshInterval = TimeSpan.FromMinutes(10);

    public static readonly TimeSpan MaxKeyAge = TimeSpan.FromHours(24);

    public static readonly TimeSpan UnknownKeyInterval = TimeSpan.FromSeconds(30);

    private readonly IHttpClientFactory _http;
    private readonly TimeProvider _time;
    private readonly ILogger<TsAuthKeys> _logger;
    private readonly CancellationToken _stopping;
    private readonly string? _issuer;
    private readonly string? _discovery;
    private readonly Counter<long> _fetches;
    private readonly Lock _gate = new();
    private Snapshot? _snapshot;
    private Task<FetchOutcome>? _fetch;
    private readonly string _storedPath;
    private Task? _firstFetch;
    private readonly ClockTrust _clock;
    private readonly Counter<long> _storedRejected;
    private readonly SemaphoreSlim _store = new(1, 1);
    private readonly Dictionary<string, long> _unknown = new(StringComparer.Ordinal);
    private long? _cycleStarted;
    private long _refreshStarted;
    private bool _followupUsed;
    private bool _loadedStored;
    public static readonly TimeSpan HighWaterPeriod = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan FollowupInterval = TimeSpan.FromSeconds(15);

    public TsAuthKeys(
        TerraBaseOptions options, IHttpClientFactory http, TimeProvider time, IMeterFactory meters, IHostApplicationLifetime lifetime, ILogger<TsAuthKeys> logger, ClockTrust clock)
    {
        _clock = clock;
        _http = http;
        _time = time;
        _logger = logger;
        _stopping = lifetime.ApplicationStopping;
        _issuer = options.AuthIssuer;
        _discovery = _issuer is null ? null : _issuer.TrimEnd('/') + "/.well-known/openid-configuration";
        _storedPath = Path.Combine(options.DataDirectory, "auth-signing-keys.json");
        var meter = meters.Create(Telemetry.MeterName);
        _storedRejected = meter.CreateCounter<long>("terrabase.auth.stored_keys_rejected", "{snapshot}", "Stored signing key snapshots rejected, by bounded reason.");
        _fetches = meter.CreateCounter<long>("terrabase.auth.key_fetches", "{fetch}", "ts-auth signing key fetches, by outcome (success or failure).");
        if (_issuer is not null)
        {
            meter.CreateObservableGauge("terrabase.auth.signing_keys_loaded", () => Keys is null ? 0 : 1, unit: null,
                "1 while a usable issuer signing key set is loaded; 0 while token verification is unavailable.");
            meter.CreateObservableGauge("terrabase.auth.signing_keys_age", () => KeyAgeSeconds, "s",
                "Age of the last successful issuer signing key fetch, including across restarts; -1 before any keys load.");
        }
    }

    private sealed record Snapshot(IReadOnlyList<SecurityKey> Keys, PublicIssuerKeys Public, DateTimeOffset FetchedAt,
        DateTimeOffset LastKnownTime, TimeSpan AgeAtLoad, long LoadedAt);

    private TimeSpan Age(Snapshot snapshot) => snapshot.AgeAtLoad + _time.GetElapsedTime(snapshot.LoadedAt);

    private double KeyAgeSeconds => Volatile.Read(ref _snapshot) is { } snapshot ? Age(snapshot).TotalSeconds : -1;

    public IReadOnlyList<SecurityKey>? Keys => Volatile.Read(ref _snapshot) is { } snapshot
        && Age(snapshot) < MaxKeyAge ? snapshot.Keys : null;

    private sealed record FetchOutcome(RefreshIssuerKeysResult Result, long StartedAt);

    public async ValueTask<RefreshIssuerKeysResult> RefreshAsync(string? keyId, CancellationToken cancellationToken)
    {
        Task<FetchOutcome> fetch;
        var observed = _time.GetTimestamp();
        long firstSeenAt;
        lock (_gate)
        {
            if (_cycleStarted is null || _time.GetElapsedTime(_cycleStarted.Value, observed) >= UnknownKeyInterval)
            {
                _unknown.Clear();
            }

            var identity = ValidatedTokens.KeyOf(keyId ?? string.Empty);
            if (!_unknown.TryGetValue(identity, out firstSeenAt))
            {
                firstSeenAt = observed;
                if (_unknown.Count < 1024)
                {
                    _unknown.Add(identity, firstSeenAt);
                }
            }

            fetch = _fetch is { IsCompleted: false } ? _fetch : StartUnknownLocked(firstSeenAt);
        }

        while (true)
        {
            var outcome = await fetch.WaitAsync(cancellationToken).ConfigureAwait(false);
            if (outcome.StartedAt >= firstSeenAt
                || outcome.Result is IReadOnlyList<SecurityKey> keys && keys.Any(key => key.KeyId == keyId))
            {
                return outcome.Result;
            }

            lock (_gate)
            {
                fetch = _fetch is { } latest && !ReferenceEquals(latest, fetch) ? latest : StartUnknownLocked(firstSeenAt);
            }
        }
    }

    private Task<FetchOutcome> StartUnknownLocked(long firstSeenAt)
    {
        if (_issuer is null)
        {
            return Task.FromResult(new FetchOutcome(new Unavailable(TokenKeysPending.Message), _time.GetTimestamp()));
        }

        var now = _time.GetTimestamp();
        var delay = TimeSpan.Zero;
        if (_cycleStarted is { } cycle && _time.GetElapsedTime(cycle, now) < UnknownKeyInterval)
        {
            if (firstSeenAt <= _refreshStarted)
            {
                return Task.FromResult(new FetchOutcome(new Unavailable(TokenKeysPending.Message), _time.GetTimestamp()));
            }

            if (_followupUsed)
            {
                delay = UnknownKeyInterval - _time.GetElapsedTime(cycle, now);
                _cycleStarted = cycle + (long)(UnknownKeyInterval.TotalSeconds * _time.TimestampFrequency);
                _followupUsed = false;
            }
            else
            {
                _followupUsed = true;
                delay = FollowupInterval - _time.GetElapsedTime(_refreshStarted, now);
            }
        }
        else
        {
            _cycleStarted = now;
            _followupUsed = false;
        }

        _fetch = FetchAfterAsync(delay);
        return _fetch;
    }

    private async Task<FetchOutcome> FetchAfterAsync(TimeSpan delay)
    {
        await Task.Yield();
        if (delay > TimeSpan.Zero)
        {
            var remaining = Volatile.Read(ref _snapshot) is { } snapshot ? MaxKeyAge - Age(snapshot) : TimeSpan.Zero;
            delay = remaining < delay ? remaining : delay;
        }

        if (delay > TimeSpan.Zero)
        {
            await Task.Delay(delay, _time, _stopping).ConfigureAwait(false);
        }

        long started;
        lock (_gate)
        {
            started = _refreshStarted = _time.GetTimestamp();
            if (_cycleStarted is { } cycle && cycle > _refreshStarted)
            {
                _cycleStarted = _refreshStarted;
            }
        }

        return new FetchOutcome(await FetchAsync().ConfigureAwait(false), started);
    }

    public async ValueTask<IReadOnlyList<SecurityKey>?> JoinAsync(CancellationToken cancellationToken)
    {
        Task<FetchOutcome>? fetch;
        lock (_gate)
        {
            fetch = _fetch;
        }

        if (fetch is not null && !fetch.IsCompleted)
        {
            _ = await fetch.WaitAsync(cancellationToken).ConfigureAwait(false);
        }

        return Keys;
    }

    public override async Task StartAsync(CancellationToken cancellationToken)
    {
        await LoadStoredAsync(cancellationToken).ConfigureAwait(false);
        _firstFetch = _issuer is null ? null : JoinOrStartFetch();
        await base.StartAsync(cancellationToken).ConfigureAwait(false);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (_firstFetch is null)
        {
            return;
        }

        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1), _time);
        var checkpoint = _time.GetTimestamp();
        do
        {
            if (!_loadedStored && _clock.Trusted)
            {
                _loadedStored = true;
                await ActivateStoredAsync(stoppingToken).ConfigureAwait(false);
            }

            if (_time.GetElapsedTime(checkpoint) >= HighWaterPeriod)
            {
                if (Volatile.Read(ref _snapshot) is { } current)
                {
                    await StoreAsync(current, stoppingToken).ConfigureAwait(false);
                }

                checkpoint = _time.GetTimestamp();
            }

            var interval = Keys is null ? StartupRetry : RefreshInterval;
            lock (_gate)
            {
                if ((_fetch is null || _fetch.IsCompleted) && _time.GetElapsedTime(_refreshStarted) >= interval)
                {
                    _fetch = FetchAfterAsync(TimeSpan.Zero);
                }
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
    }

    private Task<FetchOutcome> JoinOrStartFetch()
    {
        lock (_gate)
        {
            if (_fetch is null || _fetch.IsCompleted)
            {
                _fetch = FetchAfterAsync(TimeSpan.Zero);
            }

            return _fetch;
        }
    }

    private async Task<RefreshIssuerKeysResult> FetchAsync()
    {
        try
        {
            using var deadline = new CancellationTokenSource(FetchTimeout, _time);
            using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token, _stopping);
            var client = _http.CreateClient(HttpClientName);
            var configuration = OpenIdConnectConfiguration.Create(await GetJsonAsync(client, _discovery, cancellation.Token).ConfigureAwait(false));
            if (!string.Equals(configuration.Issuer, _issuer, StringComparison.Ordinal))
            {
                _logger.ZLogError($"The ts-auth discovery document names an issuer other than auth-issuer; its keys were not used");
                return Fail();
            }

            if (_issuer is null || !Uri.TryCreate(configuration.JwksUri, UriKind.Absolute, out var jwks)
                || jwks.Scheme != Uri.UriSchemeHttps || jwks.GetLeftPart(UriPartial.Authority) != new Uri(_issuer).GetLeftPart(UriPartial.Authority)
                || !string.IsNullOrEmpty(jwks.UserInfo))
            {
                _logger.ZLogError($"The ts-auth key set address is outside auth-issuer; its keys were not used");
                return Fail();
            }

            var keySet = ReadKeySet(await GetJsonAsync(client, configuration.JwksUri, cancellation.Token).ConfigureAwait(false));
            var keys = SigningKeys(keySet);
            var loadedAt = _time.GetTimestamp();
            var fetchedAt = _time.GetUtcNow();
            var snapshot = new Snapshot(keys, PublicKeys(keySet), fetchedAt, fetchedAt, TimeSpan.Zero, loadedAt);
            Volatile.Write(ref _snapshot, snapshot);
            await StoreAsync(snapshot, cancellation.Token).ConfigureAwait(false);
            _logger.ZLogInformation($"Fetched {keys.Count:@Count} ts-auth public signing keys");
            _fetches.Add(1, new KeyValuePair<string, object?>("outcome", "success"));
            return keys;
        }
        catch (Exception error)
        {
            if (!_stopping.IsCancellationRequested)
            {
                _logger.ZLogWarning($"Could not fetch ts-auth signing keys from auth-issuer ({error.GetType().Name:@Error}); keeping {Keys?.Count ?? 0:@Count} cached keys");
            }

            return Fail();
        }
    }

    private RefreshIssuerKeysResult Fail()
    {
        _fetches.Add(1, new KeyValuePair<string, object?>("outcome", "failure"));
        return new Unavailable(TokenKeysPending.Message);
    }

    private static async Task<string> GetJsonAsync(HttpClient client, string? address, CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(address, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
        {
            throw new ArgumentException("ts-auth keys and discovery must be served over https");
        }

        using var response = await client.GetAsync(uri, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
    }
}
