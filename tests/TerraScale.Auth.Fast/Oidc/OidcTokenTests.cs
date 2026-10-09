using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using TerraScale.Auth.Features.Identity;
using TerraScale.Auth.Features.Oidc;
using TerraScale.Auth.Features.SigningKeys;
using TerraScale.Auth.Security;
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
    public async Task A_replica_starting_on_an_unverifiable_ring_replaces_it_so_only_new_sign_ins_validate()
    {
        await using var oidc = await OidcHarness.StartAsync(fixture);
        var (_, _, stale) = await oidc.SignInAndRedeemAsync();
        var staleAccess = stale.GetProperty("access_token").GetString()!;
        var id = OidcIssuer.InstanceId;
        var stored = oidc.Vault.Current(id);
        var ring = JsonNode.Parse(stored.Document.GetRawText())!;
        // Metadata this master key cannot authenticate, as after a lost key or an earlier format.
        ring["keys"]![0]!["publicMetadataMac"] = new string('0', 64);
        oidc.Vault.Put(id, JsonSerializer.SerializeToElement(ring));

        await using var replica = await oidc.StartReplicaAsync();
        var keys = new JsonWebKeySet(await replica.Client.GetStringAsync($"/{OidcIssuer.Slug}/.well-known/jwks.json"));
        var fresh = keys.Keys.Single().Kid;
        await Assert.That(fresh).IsNotEqualTo(new JsonWebToken(staleAccess).Kid);
        foreach (var client in new[] { oidc.Client, replica.Client })
        {
            using var userInfo = await oidc.UserInfoAsync(staleAccess, client);
            await Assert.That(userInfo.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
            using var organizations = await oidc.CallAsync(HttpMethod.Get, PlatformCalls.Organizations, staleAccess, client: client);
            await Assert.That(organizations.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
        }

        // Signing in again issues tokens under the fresh key that every replica accepts.
        var (_, _, tokens) = await oidc.SignInAndRedeemAsync("bob@example.test");
        var access = tokens.GetProperty("access_token").GetString()!;
        await Assert.That(new JsonWebToken(access).Kid).IsEqualTo(fresh);
        using var accepted = await oidc.UserInfoAsync(access, replica.Client);
        await Assert.That(accepted.StatusCode).IsEqualTo(HttpStatusCode.OK);
    }

    [Test]
    public async Task Public_key_requests_keep_working_across_replicas_when_private_material_is_unavailable()
    {
        await using var oidc = await OidcHarness.StartAsync(fixture);
        await using var replica = await oidc.StartReplicaAsync();
        var (user, _, older) = await oidc.SignInAndRedeemAsync();
        using var rotated = await oidc.Server.App.Services.GetRequiredService<KeyRing>().RotateAsync(OidcIssuer.InstanceId, CancellationToken.None);
        using var refreshed = await oidc.RefreshAsync(older.GetProperty("refresh_token").GetString()!, replica.Client);
        await Assert.That(refreshed.StatusCode).IsEqualTo(HttpStatusCode.OK);
        var newer = await OidcHarness.JsonAsync(refreshed);
        await Assert.That(new JsonWebToken(newer.GetProperty("id_token").GetString()!).Kid).IsEqualTo(rotated.KeyId);
        var expectedJwks = await oidc.Client.GetStringAsync($"/{OidcIssuer.Slug}/.well-known/jwks.json");
        await Assert.That(new JsonWebKeySet(expectedJwks).Keys.Select(key => key.Kid).ToArray())
            .IsEquivalentTo([new JsonWebToken(older.GetProperty("id_token").GetString()!).Kid, rotated.KeyId]);

        // Make every private key unreadable: public requests must use persisted public material only.
        var id = OidcIssuer.InstanceId;
        var stored = oidc.Vault.Current(id);
        var ring = JsonNode.Parse(stored.Document.GetRawText())!;
        foreach (var record in ring["keys"]!.AsArray())
        {
            record!["secretBox"] = "unavailable";
        }

        oidc.Vault.Put(id, JsonSerializer.SerializeToElement(ring));
        var before = oidc.Vault.Current(id);
        foreach (var client in new[] { oidc.Client, replica.Client })
        {
            var jwks = await client.GetStringAsync($"/{OidcIssuer.Slug}/.well-known/jwks.json");
            await Assert.That(jwks).IsEqualTo(expectedJwks);
            var keys = new JsonWebKeySet(jwks);
            await Assert.That(keys.Keys.All(key => !key.HasPrivateKey)).IsTrue();
            await Assert.That(jwks).DoesNotContain("secretBox");
            foreach (var tokens in new[] { older, newer })
            {
                var validation = await new JsonWebTokenHandler().ValidateTokenAsync(tokens.GetProperty("id_token").GetString()!, new TokenValidationParameters
                {
                    ValidIssuer = OidcHarness.Issuer,
                    ValidAudience = OidcIssuer.DashboardClientId,
                    ValidTypes = ["JWT"],
                    ValidAlgorithms = [SecurityAlgorithms.EcdsaSha256],
                    IssuerSigningKeys = keys.GetSigningKeys(),
                    ClockSkew = TimeSpan.Zero,
                });
                await Assert.That(validation.IsValid).IsTrue();
                using var userInfo = await oidc.UserInfoAsync(tokens.GetProperty("access_token").GetString()!, client);
                await Assert.That(userInfo.StatusCode).IsEqualTo(HttpStatusCode.OK);
                await Assert.That((await OidcHarness.JsonAsync(userInfo)).GetProperty("sub").GetString()).IsEqualTo(user.Id);
                using var organizations = await oidc.CallAsync(HttpMethod.Get, PlatformCalls.Organizations, tokens.GetProperty("access_token").GetString()!, client: client);
                await Assert.That(organizations.StatusCode).IsEqualTo(HttpStatusCode.OK);
                await Assert.That((await OidcHarness.JsonAsync(organizations)).GetProperty("items").GetArrayLength()).IsEqualTo(0);
            }
        }

        var afterPublicRequests = oidc.Vault.Current(id);
        await Assert.That(afterPublicRequests.Version).IsEqualTo(before.Version);
        await Assert.That(afterPublicRequests.Document.GetRawText()).IsEqualTo(before.Document.GetRawText());
        using var refused = await oidc.RefreshAsync(newer.GetProperty("refresh_token").GetString()!, replica.Client);
        await Assert.That(refused.StatusCode).IsEqualTo(HttpStatusCode.InternalServerError);
        var body = await OidcHarness.JsonAsync(refused);
        await Assert.That(body.GetProperty("error").GetProperty("code").GetString()).IsEqualTo("internal_error");
        await Assert.That(body.TryGetProperty("access_token", out _)).IsFalse();
        await Assert.That(body.TryGetProperty("id_token", out _)).IsFalse();
        await Assert.That(body.TryGetProperty("refresh_token", out _)).IsFalse();
    }

    [Test]
    public async Task Substituting_a_valid_public_point_cannot_publish_or_authenticate_attacker_signed_tokens()
    {
        await using var oidc = await OidcHarness.StartAsync(fixture);
        await using var replica = await oidc.StartReplicaAsync();
        var (user, _, tokens) = await oidc.SignInAndRedeemAsync();
        var genuine = new JsonWebToken(tokens.GetProperty("access_token").GetString()!);
        using var attacker = Es256Key.Generate(genuine.Kid);
        var attackerPublic = attacker.PublicJwk();
        var handler = new JsonWebTokenHandler { SetDefaultTimesOnTokenCreation = false };
        // Preserve all token-entry, authorization and user claims; only its signing key changes.
        var forged = handler.CreateToken(Base64UrlEncoder.Decode(genuine.EncodedPayload),
            new SigningCredentials(attacker.SecurityKey, SecurityAlgorithms.EcdsaSha256),
            new Dictionary<string, object> { ["typ"] = genuine.Typ });
        await Assert.That(new JsonWebToken(forged).EncodedPayload).IsEqualTo(genuine.EncodedPayload);
        await Assert.That(new JsonWebToken(forged).Kid).IsEqualTo(genuine.Kid);
        var validation = await handler.ValidateTokenAsync(forged, new TokenValidationParameters
        {
            ValidIssuer = OidcHarness.Issuer,
            ValidAudience = OidcHarness.Issuer,
            ValidTypes = ["at+jwt"],
            ValidAlgorithms = [SecurityAlgorithms.EcdsaSha256],
            IssuerSigningKey = attackerPublic,
            IgnoreTrailingSlashWhenValidatingAudience = false,
            ClockSkew = TimeSpan.Zero,
        });
        await Assert.That(validation.IsValid).IsTrue();
        await Assert.That(validation.Claims["sub"]).IsEqualTo(user.Id);

        var id = OidcIssuer.InstanceId;
        var stored = oidc.Vault.Current(id);
        var ring = JsonNode.Parse(stored.Document.GetRawText())!;
        // A storage attacker replaces only X/Y, retaining the real owner, kid and metadata MAC.
        var record = ring["keys"]!.AsArray().Single(key => key!["id"]!.GetValue<string>() == genuine.Kid)!;
        record["x"] = attackerPublic.X;
        record["y"] = attackerPublic.Y;
        oidc.Vault.Put(id, JsonSerializer.SerializeToElement(ring));
        var before = oidc.Vault.Current(id);
        foreach (var client in new[] { oidc.Client, replica.Client })
        {
            using var jwks = await client.GetAsync($"/{OidcIssuer.Slug}/.well-known/jwks.json");
            await Assert.That(jwks.StatusCode).IsEqualTo(HttpStatusCode.InternalServerError);
            await Assert.That(await OidcHarness.EnvelopeCodeAsync(jwks)).IsEqualTo("internal_error");
            await Assert.That(await jwks.Content.ReadAsStringAsync()).DoesNotContain("\"kid\"");
            using var userInfo = await oidc.UserInfoAsync(forged, client);
            await Assert.That(userInfo.StatusCode).IsEqualTo(HttpStatusCode.InternalServerError);
            await Assert.That(await OidcHarness.EnvelopeCodeAsync(userInfo)).IsEqualTo("internal_error");
            using var organizations = await oidc.CallAsync(HttpMethod.Get, PlatformCalls.Organizations, forged, client: client);
            await Assert.That(organizations.StatusCode).IsEqualTo(HttpStatusCode.InternalServerError);
            await Assert.That(await OidcHarness.EnvelopeCodeAsync(organizations)).IsEqualTo("internal_error");
        }

        var after = oidc.Vault.Current(id);
        await Assert.That(after.Version).IsEqualTo(before.Version);
        await Assert.That(after.Document.GetRawText()).IsEqualTo(before.Document.GetRawText());
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task Invalid_public_key_metadata_fails_closed_without_rewriting_the_ring(bool copiedForeignRing)
    {
        await using var oidc = await OidcHarness.StartAsync(fixture);
        var (_, _, tokens) = await oidc.SignInAndRedeemAsync();
        var id = OidcIssuer.InstanceId;
        var stored = oidc.Vault.Current(id);
        var ring = JsonNode.Parse(stored.Document.GetRawText())!;
        if (copiedForeignRing)
        {
            const string otherInstance = "other-tenant";
            var foreignKeys = await oidc.Server.App.Services.GetRequiredService<KeyRing>().GetActiveAsync(otherInstance, CancellationToken.None);
            foreach (var key in foreignKeys)
            {
                key.Dispose();
            }

            var foreignId = otherInstance;
            ring = JsonNode.Parse(oidc.Vault.Current(foreignId).Document.GetRawText())!;
        }
        else
        {
            ring["keys"]![0]!["x"] = "not-a-coordinate";
        }

        oidc.Vault.Put(id, JsonSerializer.SerializeToElement(ring));
        var before = oidc.Vault.Current(id);
        using var jwks = await oidc.Client.GetAsync($"/{OidcIssuer.Slug}/.well-known/jwks.json");
        await Assert.That(jwks.StatusCode).IsEqualTo(HttpStatusCode.InternalServerError);
        await Assert.That(await jwks.Content.ReadAsStringAsync()).DoesNotContain("\"kid\"");
        using var userInfo = await oidc.UserInfoAsync(tokens.GetProperty("access_token").GetString()!);
        await Assert.That(userInfo.IsSuccessStatusCode).IsFalse();
        using var organizations = await oidc.CallAsync(HttpMethod.Get, PlatformCalls.Organizations, tokens.GetProperty("access_token").GetString()!);
        await Assert.That(organizations.IsSuccessStatusCode).IsFalse();
        using var refused = await oidc.RefreshAsync(tokens.GetProperty("refresh_token").GetString()!);
        await Assert.That(refused.IsSuccessStatusCode).IsFalse();
        await Assert.That(await refused.Content.ReadAsStringAsync()).DoesNotContain("access_token");
        var after = oidc.Vault.Current(id);
        await Assert.That(after.Version).IsEqualTo(before.Version);
        await Assert.That(after.Document.GetRawText()).IsEqualTo(before.Document.GetRawText());
    }

    [Test]
    public async Task A_key_ring_out_of_creation_order_fails_closed_instead_of_publishing_or_signing()
    {
        await using var oidc = await OidcHarness.StartAsync(fixture);
        var (_, _, tokens) = await oidc.SignInAndRedeemAsync();
        using var rotated = await oidc.Server.App.Services.GetRequiredService<KeyRing>().RotateAsync(OidcIssuer.InstanceId, CancellationToken.None);

        // Reorder the stored ring, as a tampered or foreign writer could; the sealed keys themselves are untouched.
        var id = OidcIssuer.InstanceId;
        var stored = oidc.Vault.Current(id);
        var ring = JsonNode.Parse(stored.Document.GetRawText())!.AsObject();
        ring["keys"] = new JsonArray([.. ring["keys"]!.AsArray().Reverse().Select(key => key!.DeepClone())]);
        oidc.Vault.Put(id, JsonSerializer.SerializeToElement(ring));

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
