using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Serialization;
using OpenIddict.Abstractions;
using OpenIddict.Server;
using TerraScale.Authentication.Contracts;
using static OpenIddict.Abstractions.OpenIddictConstants;
using static OpenIddict.Server.OpenIddictServerEvents;

namespace TerraScale.Authentication.Features.Oidc;

/// <summary>Private reference-token proof ancestry; it is never published as an access or identity token claim.</summary>
internal static class CredentialLineage
{
    internal const string BindingsProperty = "ts-auth.credential-bindings";

    private const string Claim = "ts_auth_credential_bindings";

    internal const string ConflictNotice = "This credential expired, was already used, or lost a concurrent sign-in or rotation in another region. Sign in again or use the latest credential.";

    internal static IReadOnlyList<CredentialBinding> Read(ClaimsPrincipal principal) =>
        principal.GetClaim(Claim) is { } json ? JsonSerializer.Deserialize(json, CredentialLineageJson.Default.CredentialBindingArray)
            ?? throw new JsonException("Missing credential ancestry.") : [];

    internal static void Attach(ClaimsPrincipal principal, IReadOnlyList<CredentialBinding> bindings) =>
        principal.SetClaim(Claim, bindings.Count == 0 ? null : JsonSerializer.Serialize(bindings.ToArray(), CredentialLineageJson.Default.CredentialBindingArray));
}

/// <summary>Binds every stored token entry before the generated credential is returned.</summary>
public sealed class BindIssuedCredential(IOpenIddictTokenManager tokens) : IOpenIddictServerHandler<GenerateTokenContext>
{
    /// <inheritdoc />
    public async ValueTask HandleAsync(GenerateTokenContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var bindings = context.Transaction.GetProperty<IReadOnlyList<CredentialBinding>>(CredentialLineage.BindingsProperty) ?? [];
        if (bindings.Count == 0 || !context.CreateTokenEntry)
        {
            return;
        }

        var id = context.Principal.GetTokenId() ?? throw new InvalidOperationException("A stored credential has no token id.");
        var token = await tokens.FindByIdAsync(id, context.CancellationToken).ConfigureAwait(false) as OidcToken
            ?? throw new InvalidOperationException("A stored credential has no token entry.");
        token.Bindings = bindings;
        await tokens.UpdateAsync(token, context.CancellationToken).ConfigureAwait(false);
    }
}

/// <summary>Rejects losing ancestry and replays before OpenIddict can revoke the winning authorization on reuse.</summary>
public sealed class ValidateCredentialLineage(IOpenIddictTokenStore<OidcToken> tokens) : IOpenIddictServerHandler<ValidateTokenContext>
{
    /// <inheritdoc />
    public async ValueTask HandleAsync(ValidateTokenContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.TokenId is { } id
            && (await tokens.FindByIdAsync(id, context.CancellationToken).ConfigureAwait(false) is not { } token
                || token.Status is Statuses.Redeemed or Statuses.Revoked))
        {
            context.Reject(Errors.InvalidToken, CredentialLineage.ConflictNotice);
        }
    }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(CredentialBinding[]))]
internal sealed partial class CredentialLineageJson : JsonSerializerContext;
