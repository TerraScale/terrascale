using OpenIddict.Abstractions;
using OpenIddict.Server;
using TerraScale.Authentication.Features.Oidc;
using static OpenIddict.Abstractions.OpenIddictConstants;
using static OpenIddict.Server.OpenIddictServerEvents;

namespace TerraScale.Authentication.Features.AgentId;

public sealed class EncryptAgentIdAccessTokens : IOpenIddictServerHandler<GenerateTokenContext>
{
    public ValueTask HandleAsync(GenerateTokenContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.TokenType == TokenTypeIdentifiers.AccessToken && context.Transaction.GetProperty<string>(TrustPublicOrigin.TenantProperty) == AgentIdIssuer.Slug)
        {
            context.EncryptionCredentials = context.Options.EncryptionCredentials[0];
        }

        return ValueTask.CompletedTask;
    }
}
