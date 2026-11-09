using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.IdentityModel.JsonWebTokens;
using TerraScale.Authentication.Features.Oidc;
using TerraScale.Authentication.Features.Platform;
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

        var (_, tokens) = await oidc.CreateSessionAndRedeemAsync(admin);
        await Assert.That(Role(tokens)).IsNull();

        await SetRoleAsync(oidc.Database, admin.Id, "admin");
        using var promoted = await oidc.RefreshAsync(tokens.GetProperty("refresh_token").GetString()!);
        var promotedBody = await promoted.Content.ReadFromJsonAsync<JsonElement>();
        await Assert.That(Role(promotedBody)).IsEqualTo(OidcResults.PlatformRoleAdmin);

        await SetRoleAsync(oidc.Database, admin.Id, null);
        using var demoted = await oidc.RefreshAsync(promotedBody.GetProperty("refresh_token").GetString()!);
        await Assert.That(Role(await demoted.Content.ReadFromJsonAsync<JsonElement>())).IsNull();
    }

    // What an operator does through TerraBase's internal listener: edits the stored user.
    private static async Task SetRoleAsync(TerraBaseSqlDatabase database, string userId, string? role)
    {
        var updated = await database.ExecuteAsync("UPDATE users SET platform_role = ? WHERE id = ? AND instance_id = ?", [role, userId, OidcIssuer.InstanceId]);
        await Assert.That(updated.RowsAffected).IsEqualTo(1);
    }

    private static string? Role(JsonElement tokens) =>
        new JsonWebToken(tokens.GetProperty("access_token").GetString()!).TryGetPayloadValue<string>(OidcResults.PlatformRoleClaim, out var role) ? role : null;
}
