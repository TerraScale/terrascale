using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.IdentityModel.JsonWebTokens;
using TerraScale.Authentication.Features.Oidc;
using TerraScale.Authentication.Features.Platform;
using TerraScale.Authentication.Store;
using TerraScale.Authentication.Tests;
using TerraScale.DotNet.TerraBase;

namespace TerraScale.Authentication.Slow.Oidc;

/// <summary>
/// A platform admin's access token carries platform_role=admin, which TerraBase's explore API requires. The role is read from
/// the stored user at every sign-in and refresh, and nothing but the stored field or being the platform owner grants it.
/// </summary>
[ClassDataSource<TerraBaseFixture>(Shared = SharedType.PerTestSession)]
public sealed class PlatformRoleTests(TerraBaseFixture fixture)
{

    [Test]
    public async Task Any_other_stored_role_grants_nothing()
    {
        await using var oidc = await OidcHarness.StartAsync(fixture);
        var user = await oidc.CreateUserAsync("viewer@example.test");
        await SetRoleAsync(oidc.Database, user.Id, "viewer");

        await Assert.That(Role(await SignInAsync(oidc, "viewer@example.test"))).IsNull();
    }

    private static async Task<JsonElement> SignInAsync(OidcHarness oidc, string address)
    {
        var session = await oidc.SignInAsync(address);
        var (verifier, challenge) = OidcHarness.Pkce();
        using var response = await oidc.RedeemCodeAsync(await oidc.CodeAsync(session, challenge), verifier);
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    // What an operator does through TerraBase's internal listener: edits the stored user.
    private static async Task SetRoleAsync(TerraBaseDatabase database, string userId, string? role)
    {
        var id = AuthStore.NaturalKeyId(OidcIssuer.InstanceId, AuthStore.UserCollection, userId);
        var stored = (await database.GetDocumentAsync(AuthStore.UserCollection, id))!;
        var user = JsonNode.Parse(stored.Document.GetRawText())!.AsObject();
        user["platformRole"] = role;
        await database.PutDocumentAsync(AuthStore.UserCollection, id, JsonSerializer.SerializeToElement(user), TerraBasePrecondition.Matches(stored.Version));
    }

    private static string? Role(JsonElement tokens) =>
        new JsonWebToken(tokens.GetProperty("access_token").GetString()!).TryGetPayloadValue<string>(OidcResults.PlatformRoleClaim, out var role) ? role : null;
}
