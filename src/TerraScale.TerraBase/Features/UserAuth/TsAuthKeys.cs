using System.Diagnostics.Metrics;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;
using TerraBase.Configuration;
using TerraBase.Errors;
using TerraBase.Hosting;

namespace TerraBase.Features.UserAuth;

/// <summary>
/// ts-auth's signing keys, read through OIDC discovery (<c>{auth-issuer}/.well-known/openid-configuration</c>, then its
/// <c>jwks_uri</c>) and cached in memory. Each fetch has one <see cref="FetchTimeout"/> deadline for both documents and stops
/// at shutdown. Startup waits for the first fetch; a background
/// loop then retries every <see cref="StartupRetry"/> until one succeeds, and refreshes every <see cref="RefreshInterval"/>.
/// A failed fetch keeps the cached keys. Requests never fetch, except through <see cref="RefreshAsync"/>.
/// </summary>
internal sealed partial class TsAuthKeys : BackgroundService, IIssuerKeys
{
    /// <summary>Name of the <see cref="HttpClient"/> that reaches ts-auth.</summary>
    public const string HttpClientName = "ts-auth";

    /// <summary>Largest discovery or JWKS document read; a larger one fails the fetch.</summary>
    public const int MaxDocumentBytes = 1024 * 1024;

    /// <summary>Deadline of one fetch, discovery and JWKS together.</summary>
    public static readonly TimeSpan FetchTimeout = TimeSpan.FromSeconds(10);
    public static readonly TimeSpan StartupRetry = TimeSpan.FromSeconds(2);
    public static readonly TimeSpan RefreshInterval = TimeSpan.FromMinutes(10);

    /// <summary>Least time between two refreshes caused by an unknown key id.</summary>
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

    /// <inheritdoc />
    public bool IsReady => _issuer is null || Keys is not null;

    /// <inheritdoc />
    public IReadOnlyList<SecurityKey>? Keys => Volatile.Read(ref _keys);

    /// <inheritdoc />
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

            // A fetch already running answers this caller too, without counting against the interval.
            fetch = _fetch;
        }

        var keys = await fetch.WaitAsync(cancellationToken).ConfigureAwait(false);
        return keys is not null ? keys : new Unavailable("ts-auth signing keys are not loaded");
    }

    public override async Task StartAsync(CancellationToken cancellationToken)
    {
        _firstFetch = _issuer is null ? null : JoinOrStartFetch();
        await base.StartAsync(cancellationToken).ConfigureAwait(false);
        if (_firstFetch is not null)
        {
            // Ready from the first request when ts-auth answers; a slow ts-auth delays startup by at most FetchTimeout.
            await _firstFetch.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
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

    // Never throws: a failure keeps the cached keys, so ts-auth being down after startup does not stop validation.
    private async Task<IReadOnlyList<SecurityKey>?> FetchAsync()
    {
        try
        {
            // Shared by every waiting request, so bounded by its own deadline and shutdown, never by one request.
            using var deadline = new CancellationTokenSource(FetchTimeout, _time);
            using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token, _stopping);
            var client = _http.CreateClient(HttpClientName);
            var configuration = OpenIdConnectConfiguration.Create(await GetDocumentAsync(client, _discovery, cancellation.Token).ConfigureAwait(false));
            if (!string.Equals(configuration.Issuer, _issuer, StringComparison.Ordinal))
            {
                // OIDC Discovery 1.0 §4.3: a document naming another issuer must not be used, so its jwks_uri is never fetched.
                LogWrongIssuer(_logger);
                return Fail();
            }

            var keySet = new JsonWebKeySet(await GetDocumentAsync(client, configuration.JwksUri, cancellation.Token).ConfigureAwait(false));
            // Asymmetric keys only: a symmetric ("oct") key would let anyone holding it sign tokens.
            SecurityKey[] keys = [.. keySet.GetSigningKeys().Where(key => key is AsymmetricSecurityKey or JsonWebKey { Kty: "RSA" or "EC" })];
            if (keys.Length == 0)
            {
                LogNoKeys(_logger);
                return Fail();
            }

            Volatile.Write(ref _keys, keys);
            _fetches.Add(1, new KeyValuePair<string, object?>("outcome", "success"));
            return keys;
        }
        catch (Exception error)
        {
            // Any failure, whatever ts-auth sent: an escaping exception would fault the shared fetch and stop the host.
            // The type only: messages can carry the issuer URL, and settings are never logged by value.
            if (!_stopping.IsCancellationRequested)
            {
                LogFetchFailed(_logger, error.GetType().Name, Keys?.Count ?? 0);
            }

            return Fail();
        }
    }

    private IReadOnlyList<SecurityKey>? Fail()
    {
        _fetches.Add(1, new KeyValuePair<string, object?>("outcome", "failure"));
        return Keys;
    }

    // Every document must come from an https URL with a 2xx answer. The client follows no redirects, so a 3xx fails here.
    private static async Task<string> GetDocumentAsync(HttpClient client, string? address, CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(address, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
        {
            throw new ArgumentException("ts-auth documents must be served over https");
        }

        using var response = await client.GetAsync(uri, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not fetch ts-auth signing keys from auth-issuer ({Error}); keeping {Count} cached keys")]
    private static partial void LogFetchFailed(ILogger logger, string error, int count);

    [LoggerMessage(Level = LogLevel.Error, Message = "The ts-auth discovery document names an issuer other than auth-issuer; its keys were not used")]
    private static partial void LogWrongIssuer(ILogger logger);

    [LoggerMessage(Level = LogLevel.Error, Message = "The ts-auth key set has no RSA or EC signing key; it was not used")]
    private static partial void LogNoKeys(ILogger logger);
}
