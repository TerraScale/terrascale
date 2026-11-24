using Microsoft.Extensions.Caching.Memory;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;

namespace TerraScale.Platform.TerraBase;

public sealed record TokenAccepted;

public readonly union ValidateTokenOutcome(TokenAccepted, TokenRejected, PlatformUnavailable);

public sealed record TokenRejected(string Message);

public sealed class OidcTokens : IDisposable
{
    public const string HttpClientName = "ts-auth";

    private const string ConfigurationPath = ".well-known/openid-configuration";

    private static readonly string[] Algorithms =
    [
        SecurityAlgorithms.EcdsaSha256, SecurityAlgorithms.EcdsaSha384, SecurityAlgorithms.EcdsaSha512,
        SecurityAlgorithms.RsaSha256, SecurityAlgorithms.RsaSha384, SecurityAlgorithms.RsaSha512,
        SecurityAlgorithms.RsaSsaPssSha256, SecurityAlgorithms.RsaSsaPssSha384, SecurityAlgorithms.RsaSsaPssSha512,
    ];

    private static readonly string[] Types = ["at+jwt", "application/at+jwt", "JWT"];

    private readonly MemoryCache _projects = new(new MemoryCacheOptions { SizeLimit = 1024 });
    private readonly IssuerEntry? _platform;
    private readonly string[] _audiences;
    private readonly JsonWebTokenHandler _handler = new() { MaximumTokenSizeInBytes = 16 * 1024 };
    private readonly PlatformOptions _options;
    private readonly IHttpClientFactory _http;
    private readonly TimeProvider _time;

    public OidcTokens(PlatformOptions options, IHttpClientFactory http, TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(time);
        _options = options;
        _http = http;
        _time = time;
        _audiences = options.AuthAudience?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) ?? [];
        if (options.AuthIssuer is { } issuer)
        {
            _platform = CreateEntry(http, issuer);
        }
    }

    public async ValueTask<ValidateTokenOutcome> ValidateAsync(string token, string project, CancellationToken cancellationToken)
    {
        if (_platform is not { } entry || _options.AuthIssuer is not { } issuer || _audiences.Length == 0)
        {
            return new TokenAccepted();
        }

        var invalid = new TokenRejected("the bearer token is invalid or expired");
        if (!_handler.CanReadToken(token) || Read(token) is not { } jwt)
        {
            return invalid;
        }

        var platformIssuer = issuer.AbsoluteUri.TrimEnd('/');
        var tenant = jwt.Issuer != platformIssuer && jwt.Issuer != issuer.AbsoluteUri;
        IReadOnlyList<string> audiences = _audiences;
        if (tenant)
        {
            if (project.Length != 26 || !Ulid.TryParse(project, out var id) || id.ToString() != project)
            {
                return invalid;
            }

            var projectIssuer = new Uri(issuer, "../" + project);
            if (jwt.Issuer != projectIssuer.AbsoluteUri)
            {
                return invalid;
            }

            entry = ProjectEntry(projectIssuer);
            audiences = [.. _audiences.Select(audience => string.Concat(audience.TrimEnd('/'), "/v1/projects/", project))];
        }

        var validIssuers = tenant ? new[] { jwt.Issuer } : new[] { issuer.AbsoluteUri, platformIssuer };
        if (await CurrentConfigurationAsync(entry, cancellationToken) is not { } metadata)
        {
            return new PlatformUnavailable("the identity provider keys are unavailable; retry", "jwks_unavailable");
        }

        var result = await _handler.ValidateTokenAsync(token, Parameters(validIssuers, audiences, tenant, metadata.SigningKeys));
        if (!result.IsValid && result.Exception is SecurityTokenSignatureKeyNotFoundException)
        {
            if (await RefreshFreshAsync(entry, cancellationToken) is not { } refreshed)
            {
                return new PlatformUnavailable("the identity provider keys are unavailable; retry", "jwks_unavailable");
            }

            result = await _handler.ValidateTokenAsync(token, Parameters(validIssuers, audiences, tenant, refreshed.SigningKeys));
        }

        return result.IsValid ? new TokenAccepted() : invalid;
    }

    public void Dispose() => _projects.Dispose();

    private TokenValidationParameters Parameters(
        string[] validIssuers,
        IReadOnlyList<string> audiences,
        bool tenant,
        IEnumerable<SecurityKey> keys) => new()
        {
            ValidIssuers = validIssuers,
            ValidAudiences = audiences,
            ValidateAudience = audiences.Count > 0,
            IgnoreTrailingSlashWhenValidatingAudience = !tenant,
            IssuerSigningKeys = keys,
            ValidAlgorithms = Algorithms,
            ValidTypes = Types,
            RequireSignedTokens = true,
            RequireExpirationTime = true,
            ValidateLifetime = true,
            ClockSkew = TimeSpan.FromSeconds(30),
            LifetimeValidator = (notBefore, expires, _, _) =>
            {
                var now = _time.GetUtcNow().UtcDateTime;
                return expires is { } until && until >= now.AddSeconds(-30) && (notBefore is null || notBefore.Value <= now.AddSeconds(30));
            },
        };

    private JsonWebToken? Read(string token)
    {
        try
        {
            return _handler.ReadJsonWebToken(token);
        }
        catch (SecurityTokenMalformedException)
        {
            return null;
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    private IssuerEntry ProjectEntry(Uri issuer)
    {
        if (_projects.TryGetValue(issuer, out IssuerEntry? cached) && cached is not null)
        {
            return cached;
        }

        var created = CreateEntry(_http, issuer);
        _projects.Set(issuer, created, new MemoryCacheEntryOptions { Size = 1, SlidingExpiration = TimeSpan.FromHours(1) });
        return created;
    }

    private static IssuerEntry CreateEntry(IHttpClientFactory http, Uri issuer)
    {
        var metadataAddress = issuer.AbsoluteUri.TrimEnd('/') + "/" + ConfigurationPath;
        var retriever = new HttpDocumentRetriever(http.CreateClient(HttpClientName)) { RequireHttps = issuer.Scheme == Uri.UriSchemeHttps };
        var manager = new ConfigurationManager<OpenIdConnectConfiguration>(metadataAddress, new OpenIdConnectConfigurationRetriever(), retriever)
        {
            AutomaticRefreshInterval = TimeSpan.FromHours(1),
            RefreshInterval = TimeSpan.FromSeconds(30),
        };
        return new IssuerEntry(metadataAddress, retriever, manager);
    }

    private static async Task<OpenIdConnectConfiguration?> ConfigurationAsync(
        ConfigurationManager<OpenIdConnectConfiguration> manager,
        CancellationToken cancellationToken)
    {
        try
        {
            return await manager.GetConfigurationAsync(cancellationToken);
        }
        catch (Exception error) when (error is InvalidOperationException or HttpRequestException or IOException
            || (error is OperationCanceledException && !cancellationToken.IsCancellationRequested))
        {
            return null;
        }
    }

    private static readonly TimeSpan UnknownKeyRefreshCooldown = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan FreshConfigurationLifetime = TimeSpan.FromSeconds(35);

    private async Task<OpenIdConnectConfiguration?> CurrentConfigurationAsync(IssuerEntry entry, CancellationToken cancellationToken)
    {
        var fresh = entry.Fresh;
        if (fresh is not null && _time.GetElapsedTime(fresh.FetchedTimestamp) < FreshConfigurationLifetime)
        {
            return fresh.Configuration;
        }

        return await ConfigurationAsync(entry.Manager, cancellationToken);
    }

    private async Task<OpenIdConnectConfiguration?> RefreshFreshAsync(IssuerEntry entry, CancellationToken cancellationToken)
    {
        await entry.RefreshGate.WaitAsync(cancellationToken);
        try
        {
            if (entry.LastRefreshAttemptTimestamp != 0 && _time.GetElapsedTime(entry.LastRefreshAttemptTimestamp) < UnknownKeyRefreshCooldown)
            {
                if (entry.LastRefreshFailed)
                {
                    return null;
                }

                return entry.Fresh?.Configuration ?? await ConfigurationAsync(entry.Manager, cancellationToken);
            }

            entry.Manager.RequestRefresh();
            var fetched = await FetchFreshAsync(entry, cancellationToken);
            var now = _time.GetTimestamp();
            entry.LastRefreshAttemptTimestamp = now;
            entry.LastRefreshFailed = fetched is null;
            if (fetched is not null)
            {
                entry.Fresh = new FreshConfigurationState(fetched, now);
            }

            return fetched;
        }
        finally
        {
            _ = entry.RefreshGate.Release();
        }
    }

    private static async Task<OpenIdConnectConfiguration?> FetchFreshAsync(IssuerEntry entry, CancellationToken cancellationToken)
    {
        try
        {
            return await OpenIdConnectConfigurationRetriever.GetAsync(entry.MetadataAddress, entry.Retriever, cancellationToken);
        }
        catch (Exception error) when (error is InvalidOperationException or HttpRequestException or IOException
            || (error is OperationCanceledException && !cancellationToken.IsCancellationRequested))
        {
            return null;
        }
    }

    private sealed record FreshConfigurationState(OpenIdConnectConfiguration Configuration, long FetchedTimestamp);

    private sealed class IssuerEntry(string metadataAddress, HttpDocumentRetriever retriever, ConfigurationManager<OpenIdConnectConfiguration> manager)
    {
        public string MetadataAddress { get; } = metadataAddress;
        public HttpDocumentRetriever Retriever { get; } = retriever;
        public ConfigurationManager<OpenIdConnectConfiguration> Manager { get; } = manager;
        public SemaphoreSlim RefreshGate { get; } = new(1, 1);
        public volatile FreshConfigurationState? Fresh;
        public long LastRefreshAttemptTimestamp;
        public bool LastRefreshFailed;
    }
}
