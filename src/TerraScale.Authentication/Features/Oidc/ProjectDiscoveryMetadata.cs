using OpenIddict.Abstractions;
using OpenIddict.Server;
using static OpenIddict.Abstractions.OpenIddictConstants;
using static OpenIddict.Server.OpenIddictServerEvents;

namespace TerraScale.Authentication.Features.Oidc;

/// <summary>Limits project discovery to the grants, scopes and endpoints served by project tenants.</summary>
public sealed class ProjectDiscoveryMetadata : IOpenIddictServerHandler<HandleConfigurationRequestContext>
{
    /// <inheritdoc />
    public ValueTask HandleAsync(HandleConfigurationRequestContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.Transaction.GetProperty<string>(TrustPublicOrigin.TenantProperty) is { } tenant && Tenancy.IsProject(tenant))
        {
            // Server options also enable platform-only grants; project client permissions do not.
            context.GrantTypes.IntersectWith([GrantTypes.AuthorizationCode, GrantTypes.RefreshToken]);
            context.Scopes.IntersectWith([Scopes.OpenId, Scopes.Email, Scopes.Profile, Scopes.OfflineAccess]);
            context.DeviceAuthorizationEndpoint = null;
            context.MtlsDeviceAuthorizationEndpointAlias = null;
            context.DeviceAuthorizationEndpointAuthenticationMethods.Clear();
        }

        return ValueTask.CompletedTask;
    }
}
