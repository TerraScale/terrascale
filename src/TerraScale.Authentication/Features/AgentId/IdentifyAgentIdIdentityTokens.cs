using OpenIddict.Abstractions;
using OpenIddict.Server;
using TerraScale.Authentication.Features.Oidc;
using TerraScale.Authentication.Storage;
using static OpenIddict.Abstractions.OpenIddictConstants;
using static OpenIddict.Server.OpenIddictServerEvents;

namespace TerraScale.Authentication.Features.AgentId;

public sealed class IdentifyAgentIdIdentityTokens : IOpenIddictServerHandler<GenerateTokenContext>
{
    public ValueTask HandleAsync(GenerateTokenContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.TokenType == TokenTypeIdentifiers.IdentityToken && context.Transaction.GetProperty<string>(TrustPublicOrigin.TenantProperty) == AgentIdIssuer.Slug)
        {
            context.SecurityTokenDescriptor.Claims ??= new Dictionary<string, object>(StringComparer.Ordinal);
            context.SecurityTokenDescriptor.Claims[Claims.JwtId] = AuthIds.New();
        }

        return ValueTask.CompletedTask;
    }
}
