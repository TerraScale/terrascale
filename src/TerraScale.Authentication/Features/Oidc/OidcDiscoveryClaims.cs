using Microsoft.Extensions.Options;
using OpenIddict.Server;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace TerraScale.Authentication.Features.Oidc;

/// <summary>Advertises only emitted standard claims; private authorization and ancestry claims remain private.</summary>
public sealed class OidcDiscoveryClaims : IConfigureOptions<OpenIddictServerOptions>
{
    /// <inheritdoc />
    public void Configure(OpenIddictServerOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Claims.UnionWith([Claims.AuthenticationTime, Claims.Email, Claims.EmailVerified,
            Claims.AuthorizedParty, Claims.Nonce, Claims.AccessTokenHash, Claims.JwtId, Claims.ClientId, Claims.Scope]);
    }
}
