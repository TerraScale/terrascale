using System.Text.Json.Serialization;
using TerraScale.Authentication.Contracts;
using TerraScale.Authentication.Features.Platform;

namespace TerraScale.Authentication.Features.Social;

/// <summary>Names of linked providers, without their subjects or OAuth client configuration.</summary>
/// <param name="Providers">Linked provider names.</param>
public sealed record LinkedProvidersBody([property: JsonPropertyName("providers")] IReadOnlyList<string> Providers);

/// <summary>The signed-in account's linked providers, for its profile.</summary>
public sealed class ListLinkedProvidersEndpoint(PlatformAuthentication authentication) : PlatformEndpoint(authentication)
{
    /// <inheritdoc />
    public override void Configure()
    {
        Get("/v1/terrascale/account/providers");
        this.Document("account_linked_providers", "List my linked sign-in providers", "Requires the account's live sign-in token. Returns provider names only.", "Account", null,
            StatusCodes.Status200OK, typeof(LinkedProvidersBody), roleChecked: false, scoped: false);
    }

    /// <inheritdoc />
    protected override Task<IResult> HandleAsync(UserRecord caller, CancellationToken cancellationToken) => Task.FromResult<IResult>(
        Results.Json(new LinkedProvidersBody([.. caller.LinkedProviders.Select(item => item.Provider).Distinct(StringComparer.Ordinal)]), SocialJson.Default.LinkedProvidersBody));
}
