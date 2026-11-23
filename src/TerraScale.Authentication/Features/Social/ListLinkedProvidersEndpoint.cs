using System.Text.Json.Serialization;
using TerraScale.Authentication.Contracts;
using TerraScale.Authentication.Features.Platform;

namespace TerraScale.Authentication.Features.Social;

public sealed record LinkedProvidersBody([property: JsonPropertyName("providers")] IReadOnlyList<string> Providers);

public sealed class ListLinkedProvidersEndpoint(PlatformAuthentication authentication) : PlatformEndpoint(authentication)
{
    public override void Configure()
    {
        Get("/v1/terrascale/account/providers");
        this.Describe("account_linked_providers", "List my linked sign-in providers", "Requires the account's live sign-in token. Returns provider names only.", "Account", null,
            StatusCodes.Status200OK, typeof(LinkedProvidersBody), roleChecked: false, scoped: false);
    }

    protected override Task<IResult> HandleAsync(UserRecord caller, CancellationToken cancellationToken) => Task.FromResult<IResult>(
        TypedResults.Json(new LinkedProvidersBody([.. caller.LinkedProviders.Select(item => item.Provider).Distinct(StringComparer.Ordinal)]), PlatformSocialJson.Default.LinkedProvidersBody));
}
