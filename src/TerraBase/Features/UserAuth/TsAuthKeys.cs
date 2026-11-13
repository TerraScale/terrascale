using System.Diagnostics.Metrics;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;
using TerraBase.Configuration;
using TerraBase.Errors;
using TerraBase.Hosting;
using ZLogger;

namespace TerraBase.Features.UserAuth;

internal sealed class TsAuthKeys : BackgroundService, IIssuerKeys
{
    public const string HttpClientName = "ts-auth";

    public const int MaxResponseBytes = 1024 * 1024;

    public static readonly TimeSpan FetchTimeout = TimeSpan.FromSeconds(10);
    public static readonly TimeSpan StartupRetry = TimeSpan.FromSeconds(2);
    public static readonly TimeSpan RefreshInterval = TimeSpan.FromMinutes(10);

    public static readonly TimeSpan UnknownKeyInterval = TimeSpan.FromSeconds(30);

    private readonly IHttpClientFactory _http;
    private readonly TimeProvider _time;
    private readonly ILogger<TsAuthKeys> _logger;
    private readonly CancellationToken _stopping;
    private readonly string? _issuer;
    private readonly string? _discovery;
    private readonly Counter<long> _fetches;
    private readonly Lock _gate = new();
    private IReadOnlyList<SecurityKey>? _keys;
    private Task<IReadOnlyList<SecurityKey>?>? _fetch;
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
        _fetches = meters.Create(Telemetry.MeterName)
            .CreateCounter<long>("terrabase.auth.key_fetches", "{fetch}", "ts-auth signing key fetches, by outcome (success or failure).");
    }

    public IReadOnlyList<SecurityKey>? Keys => Volatile.Read(ref _keys);

    public async ValueTask<RefreshIssuerKeysResult> RefreshAsync(CancellationToken cancellationToken)
    {
        Task<IReadOnlyList<SecurityKey>?> fetch;
        lock (_gate)
        {
            if (_fetch is null || _fetch.IsCompleted)
            {
                var now = _time.GetUtcNow();
                if (_issuer is null || (_lastUnknownKeyRefresh is { } last && now - last < UnknownKeyInterval))
                {
                    return Keys is { } cached ? cached : new Unavailable("ts-auth signing keys are not loaded");
                }

                _lastUnknownKeyRefresh = now;
                _fetch = Task.Run(FetchAsync, CancellationToken.None);
            }

            fetch = _fetch;
        }

        var keys = await fetch.WaitAsync(cancellationToken).ConfigureAwait(false);
        return keys is not null ? keys : new Unavailable("ts-auth signing keys are not loaded");
    }

    public async ValueTask<IReadOnlyList<SecurityKey>?> JoinAsync(CancellationToken cancellationToken)
    {
        Task<IReadOnlyList<SecurityKey>?>? fetch;
        lock (_gate)
        {
            fetch = _fetch;
        }

        return fetch is null || fetch.IsCompleted ? Keys : await fetch.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    public override Task StartAsync(CancellationToken cancellationToken)
    {
        _firstFetch = _issuer is null ? null : JoinOrStartFetch();
        return base.StartAsync(cancellationToken);
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
            await Task.Delay(Keys is null ? StartupRetry : RefreshInterval, _time, stoppingToken).ConfigureAwait(false);
            await JoinOrStartFetch().WaitAsync(stoppingToken).ConfigureAwait(false);
        }
    }

    private Task<IReadOnlyList<SecurityKey>?> JoinOrStartFetch()
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

    private async Task<IReadOnlyList<SecurityKey>?> FetchAsync()
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

            var keySet = new JsonWebKeySet(await GetJsonAsync(client, configuration.JwksUri, cancellation.Token).ConfigureAwait(false));
            SecurityKey[] keys = [.. keySet.GetSigningKeys().Where(key => key is AsymmetricSecurityKey or JsonWebKey { Kty: "RSA" or "EC" })];
            if (keys.Length == 0)
            {
                _logger.ZLogError($"The ts-auth key set has no RSA or EC signing key; it was not used");
                return Fail();
            }

            Volatile.Write(ref _keys, keys);
            _fetches.Add(1, new KeyValuePair<string, object?>("outcome", "success"));
            return keys;
        }
        catch (Exception error)
        {
            if (!_stopping.IsCancellationRequested)
            {
                if (Keys is null)
                {
                    _logger.ZLogWarning($"Could not fetch ts-auth signing keys from auth-issuer ({error.GetType().Name:@Error}); this node is not ready until they load");
                }
                else
                {
                    _logger.ZLogWarning($"Could not fetch ts-auth signing keys from auth-issuer ({error.GetType().Name:@Error}); keeping {Keys.Count:@Count} cached keys");
                }
            }

            return Fail();
        }
    }

    private IReadOnlyList<SecurityKey>? Fail()
    {
        _fetches.Add(1, new KeyValuePair<string, object?>("outcome", "failure"));
        return Keys;
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
