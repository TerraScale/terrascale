using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using TerraScale.Authentication.Features.Identity;
using TerraScale.Authentication.Features.Oidc;
using TerraScale.Authentication.Store;
using TerraScale.Authentication.Tests;

namespace TerraScale.Authentication.Fast.Oidc;

[ClassDataSource<TerraBaseFixture>(Shared = SharedType.PerTestSession)]
public sealed class OidcPruneTests(TerraBaseFixture fixture)
{
    [Test]
    public async Task Pruning_keeps_revoked_refresh_replay_evidence_until_it_expires_and_then_deletes_it()
    {
        var clock = new ManualClock(DateTimeOffset.UtcNow);
        await using var oidc = await OidcHarness.StartAsync(fixture, clock: clock);
        var user = await oidc.CreateUserAsync(oidc.UniqueEmail("prune"));
        var (_, tokens) = await oidc.CreateSessionAndRedeemAsync(user);
        var consumed = tokens.GetProperty("refresh_token").GetString()!;

        using var rotated = await oidc.RefreshAsync(consumed);
        await Assert.That(rotated.StatusCode).IsEqualTo(HttpStatusCode.OK);
        var latest = JsonSerializer.Deserialize<JsonElement>(await rotated.Content.ReadAsStringAsync()).GetProperty("refresh_token").GetString()!;

        using var replay = await oidc.RefreshAsync(consumed);
        var replayBody = JsonSerializer.Deserialize<JsonElement>(await replay.Content.ReadAsStringAsync());
        await Assert.That(replay.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        await Assert.That(replayBody.GetProperty("error").GetString()).IsEqualTo("invalid_grant");

        clock.Advance(OidcIssuer.TokenLifetime + TimeSpan.FromMinutes(1));
        var store = oidc.Server.App.Services.GetRequiredService<AuthStore>();
        var tokenStore = oidc.Server.App.Services.GetRequiredService<OidcTokenStore>();
        var authorizationStore = oidc.Server.App.Services.GetRequiredService<OidcAuthorizationStore>();
        await tokenStore.PruneAsync(500);
        await authorizationStore.PruneAsync(500);

        var refusedTask = oidc.RefreshAsync(latest);
        var replayedAgainTask = oidc.RefreshAsync(consumed);
        var rowsTask = RowsAsync(store, AuthStore.OidcTokenTable, user.Id);
        await Task.WhenAll(refusedTask, replayedAgainTask, rowsTask);
        using var refused = await refusedTask;
        using var replayedAgain = await replayedAgainTask;
        var refusedBody = JsonSerializer.Deserialize<JsonElement>(await refused.Content.ReadAsStringAsync());
        await Assert.That(refused.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        await Assert.That(refusedBody.GetProperty("error").GetString()).IsEqualTo("invalid_grant");
        var replayedBody = JsonSerializer.Deserialize<JsonElement>(await replayedAgain.Content.ReadAsStringAsync());
        await Assert.That(replayedAgain.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        await Assert.That(replayedBody.GetProperty("error").GetString()).IsEqualTo("invalid_grant");
        await Assert.That(replayedBody.GetProperty("error_description").GetString()).IsEqualTo(CredentialLineage.ReplayNotice);
        await Assert.That(await rowsTask).IsGreaterThan(0);

        clock.Advance(Sessions.Lifetime);
        await tokenStore.PruneAsync(500);
        await authorizationStore.PruneAsync(500);

        await Assert.That(await RowsAsync(store, AuthStore.OidcTokenTable, user.Id)).IsEqualTo(0);
        await Assert.That(await RowsAsync(store, AuthStore.OidcAuthorizationTable, user.Id)).IsEqualTo(0);
    }

    private static async Task<int> RowsAsync(AuthStore store, string table, string subject)
    {
        var filter = new JsonObject { ["eq"] = new JsonArray("/subject", subject) };
        using var document = JsonDocument.Parse(filter.ToJsonString());
        return (await store.QueryAsync(table, document.RootElement, 10, CancellationToken.None)).Count;
    }
}
