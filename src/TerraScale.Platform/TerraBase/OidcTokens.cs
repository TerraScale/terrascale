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
    private const string ConfigurationPath = ".well-known/openid-configuration";
    private readonly MemoryCache _projects = new(new MemoryCacheOptions { SizeLimit = 1024 });
    private readonly ConfigurationManager<OpenIdConnectConfiguration>? _configuration;
    private readonly JsonWebTokenHandler _handler = new();
    private readonly PlatformOptions _options;

    public OidcTokens(PlatformOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options;
        if (options.AuthIssuer is { } issuer)
        {
            _configuration = Manager(issuer);
        }
    }

    public async ValueTask<ValidateTokenOutcome> ValidateAsync(string token, string project, CancellationToken cancellationToken)
    {
        if (_configuration is not { } configuration || _options.AuthIssuer is not { } issuer || _options.AuthAudience is not { } audience)
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

            configuration = ProjectManager(projectIssuer);
            audience = string.Concat(audience.TrimEnd('/'), "/v1/projects/", project);
        }

        OpenIdConnectConfiguration document;
        try
        {
            document = await configuration.GetConfigurationAsync(cancellationToken);
        }
        catch (InvalidOperationException error)
        {
            return new PlatformUnavailable($"the identity provider keys are unavailable: {error.Message}", "jwks_unavailable");
        }
        catch (IOException error)
        {
            return new PlatformUnavailable($"the identity provider keys are unavailable: {error.Message}", "jwks_unavailable");
        }

        var result = await _handler.ValidateTokenAsync(token, new TokenValidationParameters
        {
            ValidIssuers = tenant ? [jwt.Issuer] : [issuer.AbsoluteUri, platformIssuer],
            ValidAudience = audience,
            IgnoreTrailingSlashWhenValidatingAudience = !tenant,
            IssuerSigningKeys = document.SigningKeys,
            ValidateLifetime = true,
            ClockSkew = TimeSpan.FromSeconds(30),
        });
        return result.IsValid ? new TokenAccepted() : invalid;
    }

    public void Dispose() => _projects.Dispose();

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

    private ConfigurationManager<OpenIdConnectConfiguration> ProjectManager(Uri issuer)
    {
        if (_projects.TryGetValue(issuer, out ConfigurationManager<OpenIdConnectConfiguration>? cached) && cached is not null)
        {
            return cached;
        }

        var created = Manager(issuer);
        _projects.Set(issuer, created, new MemoryCacheEntryOptions { Size = 1, SlidingExpiration = TimeSpan.FromHours(1) });
        return created;
    }

    private static ConfigurationManager<OpenIdConnectConfiguration> Manager(Uri issuer) => new(
        issuer.AbsoluteUri.TrimEnd('/') + "/" + ConfigurationPath,
        new OpenIdConnectConfigurationRetriever(),
        new HttpDocumentRetriever { RequireHttps = issuer.Scheme == Uri.UriSchemeHttps });
}
