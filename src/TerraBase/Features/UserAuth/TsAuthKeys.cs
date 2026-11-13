using System.Diagnostics.Metrics;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;
using TerraBase.Configuration;
using TerraBase.Errors;
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
    private Task<RefreshIssuerKeysResult>? _fetch;
    private readonly string _storedPath;
    private Task? _firstFetch;
    private DateTimeOffset? _lastUnknownKeyRefresh;

    public TsAuthKeys(
        TerraBaseOptions options, IHttpClientFactory http, TimeProvider time, IMeterFactory meters, IHostApplicationLifetime lifetime, ILogger<TsAuthKeys> logger)
    {
        _http = http;
        _time = time;
        _logger = logger;
        _stopping = lifetime.ApplicationStopping;
        _issuer = options.AuthIssuer;
        _discovery = _issuer is null ? null : _issuer.TrimEnd('/') + "/.well-known/openid-configuration";
        _storedPath = Path.Combine(options.DataDirectory, "auth-signing-keys.json");
        var meter = meters.Create(Telemetry.MeterName);
        _fetches = meter.CreateCounter<long>("terrabase.auth.key_fetches", "{fetch}", "ts-auth signing key fetches, by outcome (success or failure).");
        if (_issuer is not null)
        {
            meter.CreateObservableGauge("terrabase.auth.signing_keys_loaded", () => Keys is null ? 0 : 1, unit: null,
                "1 while a usable issuer signing key set is loaded; 0 while token verification is unavailable.");
            meter.CreateObservableGauge("terrabase.auth.signing_keys_age", () => KeyAgeSeconds, "s",
                "Age of the last successful issuer signing key fetch, including across restarts; -1 before any keys load.");
        }
    }

    private sealed record Snapshot(IReadOnlyList<SecurityKey> Keys, DateTimeOffset FetchedAt);

    private double KeyAgeSeconds => Volatile.Read(ref _snapshot) is { } snapshot ? (_time.GetUtcNow() - snapshot.FetchedAt).TotalSeconds : -1;

    public IReadOnlyList<SecurityKey>? Keys => Volatile.Read(ref _snapshot) is { } snapshot
        && _time.GetUtcNow() - snapshot.FetchedAt is var age && age >= TimeSpan.Zero && age < MaxKeyAge ? snapshot.Keys : null;

    public async ValueTask<RefreshIssuerKeysResult> RefreshAsync(CancellationToken cancellationToken)
    {
        Task<RefreshIssuerKeysResult> fetch;
        lock (_gate)
        {
            if (_fetch is null || _fetch.IsCompleted)
            {
                var now = _time.GetUtcNow();
                if (_issuer is null || (_lastUnknownKeyRefresh is { } last && now - last < UnknownKeyInterval))
                {
                    return new Unavailable(TokenKeysPending.Message);
                }

                _lastUnknownKeyRefresh = now;
                _fetch = Task.Run(FetchAsync, CancellationToken.None);
            }

            if (_lastUnknownKeyRefresh is null || _time.GetUtcNow() - _lastUnknownKeyRefresh >= UnknownKeyInterval)
            {
                _lastUnknownKeyRefresh = _time.GetUtcNow();
            }

            fetch = _fetch;
        }

        return await fetch.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<IReadOnlyList<SecurityKey>?> JoinAsync(CancellationToken cancellationToken)
    {
        Task<RefreshIssuerKeysResult>? fetch;
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

        await _firstFetch.WaitAsync(stoppingToken).ConfigureAwait(false);
        while (!stoppingToken.IsCancellationRequested)
        {
            var delay = Keys is null ? StartupRetry : RefreshInterval;
            if (Volatile.Read(ref _snapshot) is { } snapshot && Keys is not null)
            {
                var remaining = MaxKeyAge - (_time.GetUtcNow() - snapshot.FetchedAt);
                delay = remaining < delay ? remaining : delay;
            }

            await Task.Delay(delay > TimeSpan.Zero ? delay : StartupRetry, _time, stoppingToken).ConfigureAwait(false);
            _ = await JoinOrStartFetch().WaitAsync(stoppingToken).ConfigureAwait(false);
        }
    }

    private Task<RefreshIssuerKeysResult> JoinOrStartFetch()
    {
        lock (_gate)
        {
            if (_fetch is null || _fetch.IsCompleted)
            {
                _fetch = Task.Run(FetchAsync, CancellationToken.None);
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

            var keySet = new JsonWebKeySet(await GetJsonAsync(client, configuration.JwksUri, cancellation.Token).ConfigureAwait(false));
            SecurityKey[] keys = [.. keySet.GetSigningKeys().Where(key => key is AsymmetricSecurityKey or JsonWebKey { Kty: "RSA" or "EC" })];
            if (keys.Length == 0)
            {
                _logger.ZLogError($"The ts-auth key set has no RSA or EC signing key; it was not used");
                return Fail();
            }

            var fetchedAt = _time.GetUtcNow();
            Volatile.Write(ref _snapshot, new Snapshot(keys, fetchedAt));
            await StoreAsync(keySet, fetchedAt, cancellation.Token).ConfigureAwait(false);
            _logger.ZLogInformation($"Fetched {keys.Length:@Count} ts-auth public signing keys");
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
