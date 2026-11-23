using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;

namespace TerraScale.Platform.TerraBase;

public sealed record TokenRejected(string Message);

public sealed class OidcTokens
{
    private readonly ConfigurationManager<OpenIdConnectConfiguration>? _configuration;
    private readonly JsonWebTokenHandler _handler = new();
    private readonly PlatformOptions _options;

    public OidcTokens(PlatformOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options;
        if (options.AuthIssuer is { } issuer)
        {
            _configuration = new ConfigurationManager<OpenIdConnectConfiguration>(
                new Uri(issuer, ".well-known/openid-configuration").AbsoluteUri,
                new OpenIdConnectConfigurationRetriever(),
                new HttpDocumentRetriever { RequireHttps = issuer.Scheme == Uri.UriSchemeHttps });
        }
    }

    public bool IsEnabled => _configuration is not null;

    public async ValueTask<Result<Done, TokenRejected, Unavailable>> ValidateAsync(string token, CancellationToken cancellationToken)
    {
        if (_configuration is not { } configuration || _options.AuthIssuer is not { } issuer)
        {
            return new Done();
        }

        OpenIdConnectConfiguration document;
        try
        {
            document = await configuration.GetConfigurationAsync(cancellationToken);
        }
        catch (InvalidOperationException error)
        {
            return new Unavailable($"the identity provider keys are unavailable: {error.Message}", "jwks_unavailable");
        }
        catch (IOException error)
        {
            return new Unavailable($"the identity provider keys are unavailable: {error.Message}", "jwks_unavailable");
        }

        var result = await _handler.ValidateTokenAsync(token, new TokenValidationParameters
        {
            ValidIssuers = [issuer.AbsoluteUri, issuer.AbsoluteUri.TrimEnd('/')],
            ValidAudience = _options.AuthAudience,
            IssuerSigningKeys = document.SigningKeys,
            ValidateLifetime = true,
            ClockSkew = TimeSpan.FromSeconds(30),
        });
        return result.IsValid ? new Done() : new TokenRejected("the bearer token is invalid or expired");
    }
}
