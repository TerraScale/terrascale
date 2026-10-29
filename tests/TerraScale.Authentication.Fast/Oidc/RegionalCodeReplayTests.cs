using System.Net;
using Microsoft.Extensions.DependencyInjection;
using OpenIddict.Abstractions;
using TerraScale.Authentication.Features.Oidc;
using TerraScale.Authentication.Store;
using TerraScale.Authentication.Tests;

namespace TerraScale.Authentication.Fast.Oidc;

public sealed class RegionalCodeReplayTests
{
    [Test]
    public async Task A_code_replayed_in_another_region_after_its_redemption_lost_ancestry_ends_the_grant_in_every_region()
    {
        await using var regions = await RegionalTerraBaseFixture.StartAsync();
        await using var gru = await OidcHarness.StartAsync(regions, "gru");
        await using var sjc = await OidcHarness.StartAsync(regions, "sjc");
        var nodes = new[] { gru, sjc };
        var stores = nodes.Select(node => node.Server.App.Services.GetRequiredService<AuthStore>()).ToArray();
        await gru.CreateUserAsync();
        var session = await gru.SignInAsync("alice@example.test");
        var (verifier, challenge) = OidcHarness.Pkce();
        var code = await gru.CodeAsync(session, challenge);
        using var redeemed = await gru.RedeemCodeAsync(code, verifier);
        await Assert.That(redeemed.StatusCode).IsEqualTo(HttpStatusCode.OK);
        var tokens = await OidcHarness.JsonAsync(redeemed);
        var entry = (await gru.Database.ListDocumentsAsync(AuthStore.OidcTokenCollection)).Items
            .Single(item => item.Document.GetProperty("type").GetString() == OpenIddictConstants.TokenTypeIdentifiers.Private.AuthorizationCode).Document;
        var codeId = entry.GetProperty("id").GetString()!;
        var authorizationId = entry.GetProperty("authorizationId").GetString()!;

        // A competing redemption's winner claim arrives through real replication: this redemption's branch lost.
        await Assert.That(await ReadinessWait.UntilAsync(async () =>
            await stores[1].FindUniqueAsync(OidcIssuer.InstanceId, "credential:oidc", codeId) is not null, TimeSpan.FromSeconds(10))).IsTrue();
        var claim = (await stores[1].FindUniqueAsync(OidcIssuer.InstanceId, "credential:oidc", codeId))!.Value;
        var winner = AuthStore.NewId();
        await Assert.That(await stores[1].TransactAsync([AuthStore.Reclaim(OidcIssuer.InstanceId, "credential:oidc", codeId, winner, claim.Version)])).IsNull();
        foreach (var store in stores)
        {
            await Assert.That(await ReadinessWait.UntilAsync(async () =>
                await store.FindUniqueOwnerAsync(OidcIssuer.InstanceId, "credential:oidc", codeId) == winner
                && (await store.GetAsync(AuthStore.OidcTokenCollection, OidcIssuer.InstanceId, codeId))?.Document.GetProperty("status").GetString() == "redeemed",
                TimeSpan.FromSeconds(10))).IsTrue();
        }

        // The stored redemption still identifies a genuine replay, which ends the shared grant in both regions.
        using (var replay = await sjc.RedeemCodeAsync(code, verifier))
        {
            await Assert.That(replay.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
            await Assert.That((await OidcHarness.JsonAsync(replay)).GetProperty("error_description").GetString()).IsEqualTo(AuthorizationCodeReplay.Notice);
        }

        foreach (var (node, store) in nodes.Zip(stores))
        {
            await Assert.That(await ReadinessWait.UntilAsync(async () =>
                (await store.GetAsync(AuthStore.OidcAuthorizationCollection, OidcIssuer.InstanceId, authorizationId))?.Document.GetProperty("status").GetString() == "revoked",
                TimeSpan.FromSeconds(10))).IsTrue();
            using var userInfo = await node.UserInfoAsync(tokens.GetProperty("access_token").GetString()!);
            await Assert.That(userInfo.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
            using var refresh = await node.RefreshAsync(tokens.GetProperty("refresh_token").GetString()!);
            await Assert.That(refresh.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        }
    }
}
