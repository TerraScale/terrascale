using System.Net;
using System.Text.Json;
using TerraScale.Authentication.Features.Oidc;
using TerraScale.Authentication.Tests;
using static TerraScale.Authentication.Tests.PlatformCalls;

namespace TerraScale.Authentication.Fast.Oidc;

/// <summary>Authorization-code replay ends the grant the code issued, and only a request able to redeem the code can trigger it.</summary>
[ClassDataSource<TerraBaseFixture>(Shared = SharedType.PerTestSession)]
public sealed class OidcCodeReplayTests(TerraBaseFixture fixture)
{
    [Test]
    public async Task A_genuine_code_replay_ends_only_its_grant_and_requests_that_cannot_redeem_the_code_change_nothing()
    {
        await using var oidc = await OidcHarness.StartAsync(fixture);
        await using var replica = await oidc.StartReplicaAsync();
        await oidc.CreateUserAsync();
        var session = await oidc.SignInAsync("alice@example.test");
        var unrelated = await RedeemAsync(oidc, session);
        var (verifier, challenge) = OidcHarness.Pkce();
        var code = await oidc.CodeAsync(session, challenge);
        using var redeemed = await oidc.RedeemCodeAsync(code, verifier);
        await Assert.That(redeemed.StatusCode).IsEqualTo(HttpStatusCode.OK);
        var tokens = await OidcHarness.JsonAsync(redeemed);

        // A wrong verifier, redirect URI, client or client secret cannot use the code, so it ends nothing.
        var refused = new[]
        {
            await oidc.RedeemCodeAsync(code, OidcHarness.Pkce().Verifier),
            await oidc.RedeemCodeAsync(code, verifier, OidcHarness.LoopbackRedirectUri),
            await oidc.PostTokenAsync(Form(code, verifier, OidcIssuer.CliClientId), null, null),
            await oidc.TokenAsync(Form(code, verifier), secret: "wrong-secret"),
        };
        foreach (var response in refused)
        {
            using (response)
            {
                await Assert.That(response.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.Unauthorized).IsTrue();
                await Assert.That(await response.Content.ReadAsStringAsync()).DoesNotContain("access_token");
            }
        }

        await AccessAsync(oidc, tokens, HttpStatusCode.OK, oidc.Client, replica.Client);
        using (var replay = await oidc.RedeemCodeAsync(code, verifier, client: replica.Client))
        {
            await Assert.That(replay.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
            var error = await OidcHarness.JsonAsync(replay);
            await Assert.That(error.GetProperty("error").GetString()).IsEqualTo("invalid_grant");
            await Assert.That(error.GetProperty("error_description").GetString()).IsEqualTo(AuthorizationCodeReplay.Notice);
        }

        await AccessAsync(oidc, tokens, HttpStatusCode.Unauthorized, oidc.Client, replica.Client);
        await RefusedRefreshAsync(oidc, tokens, oidc.Client, replica.Client);
        await AccessAsync(oidc, unrelated, HttpStatusCode.OK, oidc.Client, replica.Client);
        using var unrelatedRefresh = await oidc.RefreshAsync(unrelated.GetProperty("refresh_token").GetString()!, replica.Client);
        await Assert.That(unrelatedRefresh.StatusCode).IsEqualTo(HttpStatusCode.OK);
    }

    [Test]
    public async Task Concurrent_redemptions_of_one_code_across_replicas_issue_once_and_end_that_grant()
    {
        await using var oidc = await OidcHarness.StartAsync(fixture);
        await using var replica = await oidc.StartReplicaAsync();
        await oidc.CreateUserAsync();
        var session = await oidc.SignInAsync("alice@example.test");
        var unrelated = await RedeemAsync(oidc, session);
        var (verifier, challenge) = OidcHarness.Pkce();
        var code = await oidc.CodeAsync(session, challenge);

        using var start = new ManualResetEventSlim();
        var attempts = Enumerable.Range(0, 8).Select(index => Task.Run(async () =>
        {
            start.Wait();
            using var response = await oidc.RedeemCodeAsync(code, verifier, client: index % 2 == 0 ? oidc.Client : replica.Client);
            return (response.StatusCode, Body: await response.Content.ReadAsStringAsync());
        })).ToArray();
        start.Set();
        var race = await Task.WhenAll(attempts);

        // One redemption issues tokens; every other one is a replay of a used code and ends the grant, winner included.
        var winners = race.Where(result => result.StatusCode == HttpStatusCode.OK).ToArray();
        await Assert.That(winners.Length).IsEqualTo(1);
        foreach (var loser in race.Where(result => result.StatusCode != HttpStatusCode.OK))
        {
            await Assert.That(loser.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
            await Assert.That(JsonSerializer.Deserialize<JsonElement>(loser.Body).GetProperty("error").GetString()).IsEqualTo("invalid_grant");
        }

        var tokens = JsonSerializer.Deserialize<JsonElement>(winners[0].Body);
        await AccessAsync(oidc, tokens, HttpStatusCode.Unauthorized, oidc.Client, replica.Client);
        await RefusedRefreshAsync(oidc, tokens, oidc.Client, replica.Client);
        await AccessAsync(oidc, unrelated, HttpStatusCode.OK, oidc.Client, replica.Client);
    }

    private static Dictionary<string, string> Form(string code, string verifier, string? clientId = null)
    {
        var form = new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code", ["code"] = code, ["redirect_uri"] = OidcHarness.RedirectUri, ["code_verifier"] = verifier,
        };
        if (clientId is not null)
        {
            form["client_id"] = clientId;
        }

        return form;
    }

    private static async Task<JsonElement> RedeemAsync(OidcHarness oidc, string session)
    {
        var (verifier, challenge) = OidcHarness.Pkce();
        using var response = await oidc.RedeemCodeAsync(await oidc.CodeAsync(session, challenge), verifier);
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        return await OidcHarness.JsonAsync(response);
    }

    private static async Task AccessAsync(OidcHarness oidc, JsonElement tokens, HttpStatusCode expected, params HttpClient[] clients)
    {
        foreach (var client in clients)
        {
            using var response = await oidc.UserInfoAsync(tokens.GetProperty("access_token").GetString()!, client);
            await Assert.That(response.StatusCode).IsEqualTo(expected);
        }
    }

    private static async Task RefusedRefreshAsync(OidcHarness oidc, JsonElement tokens, params HttpClient[] clients)
    {
        foreach (var client in clients)
        {
            using var response = await oidc.RefreshAsync(tokens.GetProperty("refresh_token").GetString()!, client);
            await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
            await Assert.That(await OidcHarness.ErrorAsync(response)).IsEqualTo("invalid_grant");
        }
    }
}
