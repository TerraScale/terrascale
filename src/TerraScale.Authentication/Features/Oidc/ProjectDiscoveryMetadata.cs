using OpenIddict.Abstractions;
using OpenIddict.Server;
using TerraScale.Authentication.Features.AgentId;
using TerraScale.Authentication.Features.Agents;
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
        var tenant = context.Transaction.GetProperty<string>(TrustPublicOrigin.TenantProperty);
        if (tenant == AgentIdIssuer.Slug)
        {
            context.GrantTypes.IntersectWith([GrantTypes.AuthorizationCode]);
            context.Scopes.Clear();
            context.Scopes.UnionWith(AgentIdIssuer.SupportedScopes);
            context.Claims.Clear();
            context.Claims.UnionWith(AgentIdIssuer.SupportedClaims);
            context.ResponseTypes.IntersectWith([ResponseTypes.Code]);
            context.PromptValues.Clear();
            context.EndSessionEndpoint = null;
            context.RevocationEndpoint = null;
            context.RevocationEndpointAuthenticationMethods.Clear();
            context.DeviceAuthorizationEndpoint = null;
            context.MtlsDeviceAuthorizationEndpointAlias = null;
            context.DeviceAuthorizationEndpointAuthenticationMethods.Clear();
            context.TokenEndpointAuthenticationMethods.Clear();
            context.TokenEndpointAuthenticationMethods.UnionWith([ClientAuthenticationMethods.None, ClientAuthenticationMethods.ClientSecretBasic, ClientAuthenticationMethods.ClientSecretPost]);
            context.SubjectTypes.Clear();
            context.SubjectTypes.Add(SubjectTypes.Pairwise);
            return ValueTask.CompletedTask;
        }

        if (tenant is not null && Tenancy.IsProject(tenant))
        {
            context.GrantTypes.IntersectWith([GrantTypes.AuthorizationCode, GrantTypes.RefreshToken, GrantTypes.ClientCredentials]);
            context.Scopes.IntersectWith([Scopes.OpenId, Scopes.Email, Scopes.Profile, Scopes.OfflineAccess]);
            context.DeviceAuthorizationEndpoint = null;
            context.MtlsDeviceAuthorizationEndpointAlias = null;
            context.DeviceAuthorizationEndpointAuthenticationMethods.Clear();
        }

        context.GrantTypes.Add(AgentTokens.GrantType);
        return ValueTask.CompletedTask;
    }
}
