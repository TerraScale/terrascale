using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using OpenIddict.Abstractions;
using static OpenIddict.Abstractions.OpenIddictConstants;
using TerraScale.Authentication.Contracts;
using TerraScale.Authentication.Features.Identity;
using TerraScale.Authentication.Features.Oidc;
using TerraScale.Authentication.Features.SigningKeys;
using TerraScale.Authentication.Security;
using TerraScale.Authentication.Store;
using TerraScale.Authentication.Tests;
using TerraScale.DotNet.TerraBase;

namespace TerraScale.Authentication.Fast.Oidc;

[ClassDataSource<TerraBaseFixture>(Shared = SharedType.PerTestSession)]
public sealed class OidcTokenTests(TerraBaseFixture fixture)
{
    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task Refresh_replay_ends_only_the_compromised_grant_across_warm_and_fresh_instances(bool publicClient)
    {
        await using var oidc = await OidcHarness.StartAsync(fixture);
        await using var replica = await oidc.StartReplicaAsync();
        await oidc.CreateUserAsync();
        var session = await oidc.SignInAsync("alice@example.test");
        var tokens = await IssueTokensAsync(oidc, session, publicClient);
        var first = tokens.GetProperty("refresh_token").GetString()!;
        var parent = await CurrentTokenAsync(oidc, TokenTypeIdentifiers.RefreshToken);

        using var rotated = await RefreshAsync(oidc, first, publicClient);
        await Assert.That(rotated.StatusCode).IsEqualTo(HttpStatusCode.OK);
        var second = await OidcHarness.JsonAsync(rotated);
        var newest = second.GetProperty("refresh_token").GetString()!;
        await Assert.That(newest).IsNotEqualTo(first);
        await AssertLineageAsync(oidc, parent, await CurrentTokenAsync(oidc, TokenTypeIdentifiers.RefreshToken));
        using var rotatedAgain = await RefreshAsync(oidc, newest, publicClient);
        await Assert.That(rotatedAgain.StatusCode).IsEqualTo(HttpStatusCode.OK);
        var third = await OidcHarness.JsonAsync(rotatedAgain);
        var unrelated = await IssueTokensAsync(oidc, session, publicClient);
        await AssertAccessWorksAsync(oidc, third, replica.Client);
        await AssertAccessWorksAsync(oidc, unrelated, replica.Client);

        // Neither a different authenticated client nor failed client authentication may end this grant.
        using var wrongClient = await RefreshAsync(oidc, first, !publicClient);
        await Assert.That(wrongClient.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        await Assert.That(await OidcHarness.ErrorAsync(wrongClient)).IsEqualTo("invalid_grant");
        using var wrongSecret = await oidc.TokenAsync(new() { ["grant_type"] = "refresh_token", ["refresh_token"] = first }, secret: "wrong-secret");
        await Assert.That(wrongSecret.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
        await Assert.That(await OidcHarness.ErrorAsync(wrongSecret)).IsEqualTo("invalid_client");
        await AssertAccessWorksAsync(oidc, third, replica.Client);

        using var replay = await RefreshAsync(oidc, first, publicClient, replica.Client);
        await Assert.That(replay.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        var error = await OidcHarness.JsonAsync(replay);
        await Assert.That(error.GetProperty("error").GetString()).IsEqualTo("invalid_grant");
        await Assert.That(error.GetProperty("error_description").GetString()).Contains("This sign-in has ended");
        await Assert.That(error.GetProperty("error_description").GetString()).DoesNotContain("latest credential");

        await using var fresh = await oidc.StartReplicaAsync();
        foreach (var client in new[] { oidc.Client, replica.Client, fresh.Client })
        {
            foreach (var generation in new[] { tokens, second, third })
            {
                using var userInfo = await oidc.UserInfoAsync(generation.GetProperty("access_token").GetString()!, client);
                await Assert.That(userInfo.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
                using var refresh = await RefreshAsync(oidc, generation.GetProperty("refresh_token").GetString()!, publicClient, client);
                await Assert.That(refresh.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
                await Assert.That(await OidcHarness.ErrorAsync(refresh)).IsEqualTo("invalid_grant");
            }

            await AssertAccessWorksAsync(oidc, unrelated, client);
        }

        using var unrelatedRefresh = await RefreshAsync(oidc, unrelated.GetProperty("refresh_token").GetString()!, publicClient, fresh.Client);
        await Assert.That(unrelatedRefresh.StatusCode).IsEqualTo(HttpStatusCode.OK);
    }

    [Test]
    public async Task Concurrent_redemptions_issue_once_and_refresh_replay_ends_the_grant()
    {
        await using var oidc = await OidcHarness.StartAsync(fixture);
        await using var replica = await oidc.StartReplicaAsync();
        await oidc.CreateUserAsync();
        var session = await oidc.SignInAsync("alice@example.test");
        var (verifier, challenge) = OidcHarness.Pkce();
        var code = await oidc.CodeAsync(session, challenge);
        var parent = await CurrentTokenAsync(oidc, TokenTypeIdentifiers.Private.AuthorizationCode);

        var codeRace = await Race(index => oidc.RedeemCodeAsync(code, verifier, client: index % 2 == 0 ? oidc.Client : replica.Client));
        await Assert.That(Outcomes(codeRace)).IsEqualTo(Expected);
        var codeWinner = JsonSerializer.Deserialize<JsonElement>(codeRace.Single(result => result.Status == HttpStatusCode.OK).Body);
        await AssertAccessWorksAsync(oidc, codeWinner, replica.Client);
        foreach (var loser in codeRace.Where(result => result.Status != HttpStatusCode.OK))
        {
            await AssertConflictNoticeAsync(JsonSerializer.Deserialize<JsonElement>(loser.Body));
        }
        var refreshParent = await CurrentTokenAsync(oidc, TokenTypeIdentifiers.RefreshToken);
        await AssertLineageAsync(oidc, parent, refreshParent);
        var refreshToken = codeWinner.GetProperty("refresh_token").GetString()!;
        var refreshRace = await Race(index => oidc.RefreshAsync(refreshToken, index % 2 == 0 ? oidc.Client : replica.Client));
        await Assert.That(Outcomes(refreshRace)).IsEqualTo(Expected);
        var winner = JsonSerializer.Deserialize<JsonElement>(refreshRace.Single(result => result.Status == HttpStatusCode.OK).Body);
        foreach (var loser in refreshRace.Where(result => result.Status != HttpStatusCode.OK))
        {
            await Assert.That(JsonSerializer.Deserialize<JsonElement>(loser.Body).GetProperty("error").GetString()).IsEqualTo("invalid_grant");
        }
        await AssertLineageAsync(oidc, refreshParent, await CurrentTokenAsync(oidc, TokenTypeIdentifiers.RefreshToken));
        using var afterRace = await oidc.RefreshAsync(winner.GetProperty("refresh_token").GetString()!, replica.Client);
        await Assert.That(afterRace.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        await Assert.That(await OidcHarness.ErrorAsync(afterRace)).IsEqualTo("invalid_grant");
        using var userInfo = await oidc.UserInfoAsync(winner.GetProperty("access_token").GetString()!, replica.Client);
        await Assert.That(userInfo.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
    }

    [Test]
    public async Task Code_replay_rejects_the_parent_and_preserves_the_winning_descendants()
    {
        await using var oidc = await OidcHarness.StartAsync(fixture);
        await oidc.CreateUserAsync();
        var session = await oidc.SignInAsync("alice@example.test");
        var (verifier, challenge) = OidcHarness.Pkce();
        var code = await oidc.CodeAsync(session, challenge);
        var parent = await CurrentTokenAsync(oidc, TokenTypeIdentifiers.Private.AuthorizationCode);

        using var first = await oidc.RedeemCodeAsync(code, verifier);
        await Assert.That(first.StatusCode).IsEqualTo(HttpStatusCode.OK);
        var tokens = await OidcHarness.JsonAsync(first);
        var refreshParent = await CurrentTokenAsync(oidc, TokenTypeIdentifiers.RefreshToken);
        await AssertLineageAsync(oidc, parent, refreshParent);

        using var replay = await oidc.RedeemCodeAsync(code, verifier);
        await AssertReplayRejectedAsync(replay);
        await AssertAccessWorksAsync(oidc, tokens);
        using var refresh = await oidc.RefreshAsync(tokens.GetProperty("refresh_token").GetString()!);
        await Assert.That(refresh.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await AssertAccessWorksAsync(oidc, await OidcHarness.JsonAsync(refresh));
        await AssertLineageAsync(oidc, refreshParent, await CurrentTokenAsync(oidc, TokenTypeIdentifiers.RefreshToken));
        await AssertAccessWorksAsync(oidc, tokens);
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
        foreach (var stored in await KeyDocumentsAsync(oidc))
        {
            var record = JsonNode.Parse(stored.Document.GetRawText())!;
            record["secretBox"] = "unavailable";
            await OverwriteAsync(oidc, stored, record);
        }

        var before = await KeyDocumentsAsync(oidc);
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

        await Assert.That((await KeyDocumentsAsync(oidc)).Select(document => document.Version)).IsEquivalentTo(before.Select(document => document.Version));
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

        // A storage attacker replaces only X/Y, retaining the real owner, kid and metadata MAC.
        var stored = (await KeyDocumentsAsync(oidc)).Single(document => document.Document.GetProperty("id").GetString() == genuine.Kid);
        var record = JsonNode.Parse(stored.Document.GetRawText())!;
        record["x"] = attackerPublic.X;
        record["y"] = attackerPublic.Y;
        var before = await OverwriteAsync(oidc, stored, record);
        foreach (var client in new[] { oidc.Client, replica.Client })
        {
            using var jwks = await client.GetAsync($"/{OidcIssuer.Slug}/.well-known/jwks.json");
            await Assert.That(jwks.StatusCode).IsEqualTo(HttpStatusCode.InternalServerError);
            await Assert.That(await OidcHarness.EnvelopeCodeAsync(jwks)).IsEqualTo("internal_error");
            await Assert.That(await jwks.Content.ReadAsStringAsync()).DoesNotContain("\"kid\"");
            using var userInfo = await oidc.UserInfoAsync(forged, client);
            await Assert.That(userInfo.StatusCode).IsEqualTo(HttpStatusCode.InternalServerError);
            await Assert.That(userInfo.Headers.CacheControl?.NoStore).IsTrue();
            await Assert.That(userInfo.Headers.Pragma.Any(header => header.Name == "no-cache")).IsTrue();
            await Assert.That(await OidcHarness.EnvelopeCodeAsync(userInfo)).IsEqualTo("internal_error");
            using var organizations = await oidc.CallAsync(HttpMethod.Get, PlatformCalls.Organizations, forged, client: client);
            await Assert.That(organizations.StatusCode).IsEqualTo(HttpStatusCode.InternalServerError);
            await Assert.That(await OidcHarness.EnvelopeCodeAsync(organizations)).IsEqualTo("internal_error");
        }

        await Assert.That((await KeyDocumentsAsync(oidc)).Single().Version).IsEqualTo(before);
    }

    [Test]
    public async Task Invalid_public_key_metadata_fails_closed_without_rewriting_the_key()
    {
        await using var oidc = await OidcHarness.StartAsync(fixture);
        var (_, _, tokens) = await oidc.SignInAndRedeemAsync();
        var stored = (await KeyDocumentsAsync(oidc)).Single();
        var record = JsonNode.Parse(stored.Document.GetRawText())!;
        record["x"] = "not-a-coordinate";
        var before = await OverwriteAsync(oidc, stored, record);
        using var jwks = await oidc.Client.GetAsync($"/{OidcIssuer.Slug}/.well-known/jwks.json");
        await Assert.That(jwks.StatusCode).IsEqualTo(HttpStatusCode.InternalServerError);
        await Assert.That(await jwks.Content.ReadAsStringAsync()).DoesNotContain("\"kid\"");
        using var userInfo = await oidc.UserInfoAsync(tokens.GetProperty("access_token").GetString()!);
        await Assert.That(userInfo.IsSuccessStatusCode).IsFalse();
        await Assert.That(userInfo.Headers.CacheControl?.NoStore).IsTrue();
        await Assert.That(userInfo.Headers.Pragma.Any(header => header.Name == "no-cache")).IsTrue();
        using var organizations = await oidc.CallAsync(HttpMethod.Get, PlatformCalls.Organizations, tokens.GetProperty("access_token").GetString()!);
        await Assert.That(organizations.IsSuccessStatusCode).IsFalse();
        using var refused = await oidc.RefreshAsync(tokens.GetProperty("refresh_token").GetString()!);
        await Assert.That(refused.IsSuccessStatusCode).IsFalse();
        await Assert.That(await refused.Content.ReadAsStringAsync()).DoesNotContain("access_token");
        await Assert.That((await KeyDocumentsAsync(oidc)).Single(document => document.Id == stored.Id).Version).IsEqualTo(before);
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

    private static Task<HttpResponseMessage> RefreshAsync(OidcHarness oidc, string token, bool publicClient, HttpClient? client = null) =>
        publicClient ? (client ?? oidc.Client).PostAsync($"/{OidcIssuer.Slug}/oauth/token", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = token,
            ["client_id"] = OidcIssuer.CliClientId,
        })) : oidc.RefreshAsync(token, client);

    private static async Task<JsonElement> IssueTokensAsync(OidcHarness oidc, string session, bool publicClient)
    {
        if (!publicClient)
        {
            var (verifier, challenge) = OidcHarness.Pkce();
            using var redeemed = await oidc.RedeemCodeAsync(await oidc.CodeAsync(session, challenge), verifier);
            await Assert.That(redeemed.StatusCode).IsEqualTo(HttpStatusCode.OK);
            return await OidcHarness.JsonAsync(redeemed);
        }

        using var issued = await oidc.Client.PostAsync($"/{OidcIssuer.Slug}/oauth/device", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["client_id"] = OidcIssuer.CliClientId,
            ["scope"] = "openid email offline_access",
        }));
        await Assert.That(issued.StatusCode).IsEqualTo(HttpStatusCode.OK);
        var device = await OidcHarness.JsonAsync(issued);
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/ui/{OidcIssuer.Slug}/device")
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["user_code"] = device.GetProperty("user_code").GetString()!,
                ["decision"] = "approve",
            }),
        };
        request.Headers.Add("Origin", OidcHarness.PublicOrigin);
        request.Headers.Add("Cookie", $"tsauth_session={session}");
        using var approved = await oidc.Client.SendAsync(request);
        await Assert.That(approved.StatusCode).IsEqualTo(HttpStatusCode.OK);
        using var polled = await oidc.Client.PostAsync($"/{OidcIssuer.Slug}/oauth/token", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "urn:ietf:params:oauth:grant-type:device_code",
            ["device_code"] = device.GetProperty("device_code").GetString()!,
            ["client_id"] = OidcIssuer.CliClientId,
        }));
        await Assert.That(polled.StatusCode).IsEqualTo(HttpStatusCode.OK);
        return await OidcHarness.JsonAsync(polled);
    }

    private static async Task AssertReplayRejectedAsync(HttpResponseMessage response)
    {
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        await AssertConflictNoticeAsync(await OidcHarness.JsonAsync(response));
    }

    private static async Task AssertConflictNoticeAsync(JsonElement error)
    {
        await Assert.That(error.GetProperty("error").GetString()).IsEqualTo("invalid_grant");
        await Assert.That(error.GetProperty("error_description").GetString()).Contains("another region");
        await Assert.That(error.GetProperty("error_description").GetString()).Contains("Sign in again or use the latest credential");
        foreach (var token in new[] { "access_token", "refresh_token", "id_token" })
        {
            await Assert.That(error.TryGetProperty(token, out _)).IsFalse();
        }
    }

    private static async Task AssertAccessWorksAsync(OidcHarness oidc, JsonElement tokens, HttpClient? client = null)
    {
        using var response = await oidc.UserInfoAsync(tokens.GetProperty("access_token").GetString()!, client);
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
    }

    private static async Task<JsonElement> CurrentTokenAsync(OidcHarness oidc, string type) =>
        (await oidc.Database.ListDocumentsAsync(AuthStore.OidcTokenCollection)).Items.Single(document =>
            document.Document.GetProperty("type").GetString() == type && document.Document.GetProperty("status").GetString() == "valid").Document;

    private static async Task AssertLineageAsync(OidcHarness oidc, JsonElement parent, JsonElement child)
    {
        await Assert.That(child.GetProperty("authorizationId").GetString()).IsEqualTo(parent.GetProperty("authorizationId").GetString());
        var inherited = parent.GetProperty("bindings").EnumerateArray().Select(binding => binding.GetRawText()).ToArray();
        var bindings = child.GetProperty("bindings").EnumerateArray().ToArray();
        await Assert.That(bindings.Length).IsEqualTo(inherited.Length + 1);
        await Assert.That(inherited.All(binding => bindings.Any(candidate => candidate.GetRawText() == binding))).IsTrue();
        var added = bindings.Single(binding => !inherited.Contains(binding.GetRawText(), StringComparer.Ordinal));
        await Assert.That(added.GetProperty("kind").GetString()).IsEqualTo("credential:oidc");
        await Assert.That(added.GetProperty("key").GetString()).IsEqualTo(parent.GetProperty("id").GetString());
        var store = oidc.Server.App.Services.GetRequiredService<AuthStore>();
        await Assert.That(await store.FindUniqueOwnerAsync(OidcIssuer.InstanceId, "credential:oidc", parent.GetProperty("id").GetString()!))
            .IsEqualTo(added.GetProperty("ownerId").GetString());
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

    // The instance's stored signing key documents, read directly from TerraBase.
    private static async Task<List<TerraBaseDocument>> KeyDocumentsAsync(OidcHarness oidc, string instance = OidcIssuer.InstanceId) =>
        [.. (await oidc.Database.ListDocumentsAsync(AuthStore.SigningKeyCollection)).Items.Where(document => document.Document.GetProperty("instanceId").GetString() == instance)];

    // Overwrites a stored key as a storage writer could; returns the new version.
    private static async Task<string> OverwriteAsync(OidcHarness oidc, TerraBaseDocument stored, JsonNode record) =>
        (await oidc.Database.PutDocumentAsync(AuthStore.SigningKeyCollection, stored.Id, JsonSerializer.SerializeToElement(record), TerraBasePrecondition.Matches(stored.Version))).Version;
}
