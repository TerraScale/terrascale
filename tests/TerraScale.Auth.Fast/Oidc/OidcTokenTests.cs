using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using TerraScale.Auth.Features.Identity;
using TerraScale.Auth.Features.Oidc;
using TerraScale.Auth.Features.SigningKeys;
using TerraScale.Auth.Store;
using TerraScale.Auth.Tests;
using TerraScale.DotNet.TerraBase;

namespace TerraScale.Auth.Fast.Oidc;

[ClassDataSource<TerraBaseFixture>(Shared = SharedType.PerTestSession)]
public sealed class OidcTokenTests(TerraBaseFixture fixture)
{
    [Test]
    public async Task Refresh_tokens_rotate_and_a_replayed_one_revokes_the_whole_family()
    {
        await using var oidc = await OidcHarness.StartAsync(fixture);
        var (_, _, tokens) = await oidc.SignInAndRedeemAsync();
        var first = tokens.GetProperty("refresh_token").GetString()!;

        using var rotated = await oidc.RefreshAsync(first);
        await Assert.That(rotated.StatusCode).IsEqualTo(HttpStatusCode.OK);
        var second = await OidcHarness.JsonAsync(rotated);
        var newest = second.GetProperty("refresh_token").GetString()!;
        await Assert.That(newest).IsNotEqualTo(first);
        using (var works = await oidc.UserInfoAsync(second.GetProperty("access_token").GetString()!))
        {
            await Assert.That(works.StatusCode).IsEqualTo(HttpStatusCode.OK);
        }

        using var replay = await oidc.RefreshAsync(first);
        await Assert.That(replay.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        await Assert.That(await OidcHarness.ErrorAsync(replay)).IsEqualTo("invalid_grant");

        using var afterReplay = await oidc.RefreshAsync(newest);
        await Assert.That(afterReplay.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        await Assert.That(await OidcHarness.ErrorAsync(afterReplay)).IsEqualTo("invalid_grant");
        using var revoked = await oidc.UserInfoAsync(second.GetProperty("access_token").GetString()!);
        await Assert.That(revoked.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
    }

    [Test]
    public async Task Concurrent_redemptions_across_replicas_succeed_exactly_once()
    {
        await using var oidc = await OidcHarness.StartAsync(fixture);
        await using var replica = await oidc.StartReplicaAsync();
        await oidc.CreateUserAsync();
        var session = await oidc.SignInAsync("alice@example.test");
        var (verifier, challenge) = OidcHarness.Pkce();
        var code = await oidc.CodeAsync(session, challenge);

        var codeRace = await Race(index => oidc.RedeemCodeAsync(code, verifier, client: index % 2 == 0 ? oidc.Client : replica.Client));
        await Assert.That(Outcomes(codeRace)).IsEqualTo(Expected);

        // A fresh family for the refresh race: the code race above may already have revoked its own.
        var (fresh, freshChallenge) = OidcHarness.Pkce();
        using var redeemed = await oidc.RedeemCodeAsync(await oidc.CodeAsync(session, freshChallenge), fresh);
        var refreshToken = (await OidcHarness.JsonAsync(redeemed)).GetProperty("refresh_token").GetString()!;

        var refreshRace = await Race(index => oidc.RefreshAsync(refreshToken, index % 2 == 0 ? oidc.Client : replica.Client));
        await Assert.That(Outcomes(refreshRace)).IsEqualTo(Expected);

        // The losers count as reuse, so the winner's new refresh token dies with the family.
        var winner = JsonSerializer.Deserialize<JsonElement>(refreshRace.Single(result => result.Status == HttpStatusCode.OK).Body).GetProperty("refresh_token").GetString()!;
        using var afterRace = await oidc.RefreshAsync(winner);
        await Assert.That(afterRace.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        await Assert.That(await OidcHarness.ErrorAsync(afterRace)).IsEqualTo("invalid_grant");
    }

    [Test]
    public async Task A_replayed_code_fails_and_revokes_the_refresh_token_it_issued()
    {
        await using var oidc = await OidcHarness.StartAsync(fixture);
        await oidc.CreateUserAsync();
        var session = await oidc.SignInAsync("alice@example.test");
        var (verifier, challenge) = OidcHarness.Pkce();
        var code = await oidc.CodeAsync(session, challenge);

        using var first = await oidc.RedeemCodeAsync(code, verifier);
        await Assert.That(first.StatusCode).IsEqualTo(HttpStatusCode.OK);
        var refreshToken = (await OidcHarness.JsonAsync(first)).GetProperty("refresh_token").GetString()!;

        using var replay = await oidc.RedeemCodeAsync(code, verifier);
        await Assert.That(replay.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        await Assert.That(await OidcHarness.ErrorAsync(replay)).IsEqualTo("invalid_grant");
        using var refresh = await oidc.RefreshAsync(refreshToken);
        await Assert.That(refresh.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        await Assert.That(await OidcHarness.ErrorAsync(refresh)).IsEqualTo("invalid_grant");
    }

    [Test]
    public async Task Signing_out_everywhere_kills_outstanding_refresh_tokens_and_codes()
    {
        await using var oidc = await OidcHarness.StartAsync(fixture);
        var (user, session, tokens) = await oidc.SignInAndRedeemAsync();
        var (verifier, challenge) = OidcHarness.Pkce();
        var code = await oidc.CodeAsync(session, challenge);

        await Assert.That(await oidc.Server.App.Services.GetRequiredService<Sessions>().RevokeAllAsync(OidcIssuer.InstanceId, user.Id)).IsTrue();

        using var refresh = await oidc.RefreshAsync(tokens.GetProperty("refresh_token").GetString()!);
        await Assert.That(refresh.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        await Assert.That(await OidcHarness.ErrorAsync(refresh)).IsEqualTo("invalid_grant");
        using var redeem = await oidc.RedeemCodeAsync(code, verifier);
        await Assert.That(redeem.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        await Assert.That(await OidcHarness.ErrorAsync(redeem)).IsEqualTo("invalid_grant");
    }

    [Test]
    public async Task A_key_rotation_reaches_every_replica_and_earlier_tokens_keep_validating()
    {
        await using var oidc = await OidcHarness.StartAsync(fixture);
        await using var replica = await oidc.StartReplicaAsync();
        var (_, _, tokens) = await oidc.SignInAndRedeemAsync();
        var oldIdToken = tokens.GetProperty("id_token").GetString()!;
        var oldKid = new JsonWebToken(oldIdToken).Kid;

        using var rotated = await oidc.Server.App.Services.GetRequiredService<KeyRing>().RotateAsync(OidcIssuer.InstanceId, CancellationToken.None);
        var keys = new JsonWebKeySet(await replica.Client.GetStringAsync($"/{OidcIssuer.Slug}/.well-known/jwks.json"));
        await Assert.That(keys.Keys.Select(key => key.Kid).ToArray()).IsEquivalentTo([oldKid, rotated.KeyId]);

        using var refreshed = await oidc.RefreshAsync(tokens.GetProperty("refresh_token").GetString()!, replica.Client);
        await Assert.That(refreshed.StatusCode).IsEqualTo(HttpStatusCode.OK);
        var newKid = new JsonWebToken((await OidcHarness.JsonAsync(refreshed)).GetProperty("id_token").GetString()!).Kid;
        await Assert.That(newKid).IsEqualTo(rotated.KeyId);

        var validation = await new JsonWebTokenHandler().ValidateTokenAsync(oldIdToken, new TokenValidationParameters
        {
            ValidIssuer = OidcHarness.Issuer,
            ValidAudience = OidcIssuer.DashboardClientId,
            ValidAlgorithms = [SecurityAlgorithms.EcdsaSha256],
            IssuerSigningKeys = keys.GetSigningKeys(),
        });
        await Assert.That(validation.IsValid).IsTrue();
        using var userInfo = await oidc.UserInfoAsync(tokens.GetProperty("access_token").GetString()!, replica.Client);
        await Assert.That(userInfo.StatusCode).IsEqualTo(HttpStatusCode.OK);
    }

    [Test]
    public async Task A_key_ring_out_of_creation_order_fails_closed_instead_of_publishing_or_signing()
    {
        await using var oidc = await OidcHarness.StartAsync(fixture);
        var (_, _, tokens) = await oidc.SignInAndRedeemAsync();
        using var rotated = await oidc.Server.App.Services.GetRequiredService<KeyRing>().RotateAsync(OidcIssuer.InstanceId, CancellationToken.None);

        // Reorder the stored ring, as a tampered or foreign writer could; the sealed keys themselves are untouched.
        var id = AuthStore.NaturalKeyId(OidcIssuer.InstanceId, KeyRing.Collection, "ring");
        var stored = (await oidc.Database.GetDocumentAsync(KeyRing.Collection, id))!;
        var ring = JsonNode.Parse(stored.Document.GetRawText())!.AsObject();
        ring["keys"] = new JsonArray([.. ring["keys"]!.AsArray().Reverse().Select(key => key!.DeepClone())]);
        await oidc.Database.PutDocumentAsync(KeyRing.Collection, id, JsonSerializer.SerializeToElement(ring), TerraBasePrecondition.Matches(stored.Version));

        using var jwks = await oidc.Client.GetAsync($"/{OidcIssuer.Slug}/.well-known/jwks.json");
        await Assert.That(jwks.StatusCode).IsEqualTo(HttpStatusCode.InternalServerError);
        await Assert.That(await jwks.Content.ReadAsStringAsync()).DoesNotContain("\"kid\"");
        using var refreshed = await oidc.RefreshAsync(tokens.GetProperty("refresh_token").GetString()!);
        await Assert.That(refreshed.IsSuccessStatusCode).IsFalse();
        await Assert.That(await refreshed.Content.ReadAsStringAsync()).DoesNotContain("access_token");
    }

    [Test]
    public async Task Storage_never_holds_a_code_or_refresh_token_the_client_received()
    {
        await using var oidc = await OidcHarness.StartAsync(fixture);
        await oidc.CreateUserAsync();
        var session = await oidc.SignInAsync("alice@example.test");
        var (verifier, challenge) = OidcHarness.Pkce();
        var code = await oidc.CodeAsync(session, challenge);
        using var redeemed = await oidc.RedeemCodeAsync(code, verifier);
        var first = (await OidcHarness.JsonAsync(redeemed)).GetProperty("refresh_token").GetString()!;
        using var refreshed = await oidc.RefreshAsync(first);
        var second = (await OidcHarness.JsonAsync(refreshed)).GetProperty("refresh_token").GetString()!;

        var stored = new List<string>();
        foreach (var collection in new[] { AuthStore.OidcTokenCollection, AuthStore.OidcAuthorizationCollection, AuthStore.UniqueCollection })
        {
            stored.AddRange((await oidc.Database.ListDocumentsAsync(collection, 1000)).Items.Select(item => item.Document.GetRawText()));
        }

        // The code and both refresh tokens have entries; none holds the secret the client received.
        await Assert.That(stored.Count).IsGreaterThanOrEqualTo(3);
        foreach (var secret in new[] { code, first, second })
        {
            await Assert.That(stored.Any(document => document.Contains(secret, StringComparison.Ordinal))).IsFalse();
        }
    }

    // Descending, so an unexpected status leads the failure message.
    private const string Expected = "400 invalid_grant | 400 invalid_grant | 400 invalid_grant | 400 invalid_grant | 400 invalid_grant | 400 invalid_grant | 400 invalid_grant | 200";

    private static string Outcomes((HttpStatusCode Status, string? Error, string Body)[] race) =>
        string.Join(" | ", race.Select(result => $"{(int)result.Status}{(result.Error is null ? string.Empty : " " + result.Error)}").OrderDescending(StringComparer.Ordinal));

    private static async Task<(HttpStatusCode Status, string? Error, string Body)[]> Race(Func<int, Task<HttpResponseMessage>> send)
    {
        using var start = new ManualResetEventSlim();
        var attempts = Enumerable.Range(0, 8).Select(index => Task.Run(async () =>
        {
            start.Wait();
            using var response = await send(index);
            var body = await response.Content.ReadAsStringAsync();
            // A non-OAuth body (for example the 500 error envelope) is kept whole so a failure shows it.
            var error = response.StatusCode == HttpStatusCode.OK ? null
                : JsonSerializer.Deserialize<JsonElement>(body).TryGetProperty("error", out var code) && code.ValueKind == JsonValueKind.String ? code.GetString() : body;
            return (response.StatusCode, error, body);
        })).ToArray();
        start.Set();
        return await Task.WhenAll(attempts);
    }
}
