using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.IdentityModel.JsonWebTokens;
using TerraScale.Authentication.Features.Oidc;
using TerraScale.Authentication.Features.Platform;
using TerraScale.Authentication.Store;
using TerraScale.Authentication.Tests;
using TerraScale.DotNet.TerraBase;

namespace TerraScale.Authentication.Fast.Oidc;

/// <summary>
/// A platform admin's access token carries platform_role=admin, which TerraBase's explore API requires. The role is read from
/// the stored user at every sign-in and refresh, and nothing but the stored field or being the platform owner grants it.
/// </summary>
[ClassDataSource<TerraBaseFixture>(Shared = SharedType.PerTestSession)]
public sealed class PlatformRoleTests(TerraBaseFixture fixture)
{
    [Test]
    public async Task Only_a_stored_platform_admin_gets_the_claim_and_losing_the_role_removes_it_at_refresh()
    {
        await using var oidc = await OidcHarness.StartAsync(fixture);
        var admin = await oidc.CreateUserAsync("ops@example.test");
        await SetRoleAsync(oidc.Database, admin.Id, "admin");

        var adminTokens = await SignInAsync(oidc, "ops@example.test");
        var (_, _, userTokens) = await oidc.SignInAndRedeemAsync("someone@example.test");

        await Assert.That(Role(adminTokens)).IsEqualTo(OidcResults.PlatformRoleAdmin);
        await Assert.That(Role(userTokens)).IsNull();

        await SetRoleAsync(oidc.Database, admin.Id, null);
        using var refreshed = await oidc.RefreshAsync(adminTokens.GetProperty("refresh_token").GetString()!);
        await Assert.That(Role(await refreshed.Content.ReadFromJsonAsync<JsonElement>())).IsNull();
    }

    [Test]
    public async Task The_platform_owner_gets_the_claim_without_a_stored_role_and_no_other_email_does()
    {
        await using var oidc = await OidcHarness.StartAsync(fixture);
        await oidc.CreateUserAsync(PlatformBootstrap.OwnerEmail);
        await oidc.CreateUserAsync("mariogk@example.test");

        await Assert.That(Role(await SignInAsync(oidc, PlatformBootstrap.OwnerEmail))).IsEqualTo(OidcResults.PlatformRoleAdmin);
        await Assert.That(Role(await SignInAsync(oidc, "mariogk@example.test"))).IsNull();
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
