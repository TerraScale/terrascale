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
        await using var oidc = await OidcHarness.StartSharedManagementAsync(fixture);
        var admin = await oidc.CreateUserAsync(oidc.UniqueEmail("ops"));
        await SetRoleAsync(oidc.Database, admin.Id, "admin");

        var (_, adminTokens) = await oidc.CreateSessionAndRedeemAsync(admin);
        var user = await oidc.CreateUserAsync(oidc.UniqueEmail("someone"));
        var (_, userTokens) = await oidc.CreateSessionAndRedeemAsync(user);

        await Assert.That(Role(adminTokens)).IsEqualTo(OidcResults.PlatformRoleAdmin);
        await Assert.That(Role(userTokens)).IsNull();

        await SetRoleAsync(oidc.Database, admin.Id, null);
        using var refreshed = await oidc.RefreshAsync(adminTokens.GetProperty("refresh_token").GetString()!);
        await Assert.That(Role(await refreshed.Content.ReadFromJsonAsync<JsonElement>())).IsNull();
    }

    // What an operator does through TerraBase's internal listener: edits the stored user.
    private static async Task SetRoleAsync(TerraBaseSqlDatabase database, string userId, string? role)
    {
        var id = AuthStore.NaturalKeyId(OidcIssuer.InstanceId, AuthStore.UserTable, userId);
        var stored = (await database.RowAsync(AuthStore.UserTable, id))!;
        var user = JsonNode.Parse(stored.Body.GetRawText())!.AsObject();
        user["platformRole"] = role;
        await database.OverwriteAsync(AuthStore.UserTable, id, JsonSerializer.SerializeToElement(user), stored.Version);
    }

    private static string? Role(JsonElement tokens) =>
        new JsonWebToken(tokens.GetProperty("access_token").GetString()!).TryGetPayloadValue<string>(OidcResults.PlatformRoleClaim, out var role) ? role : null;
}
