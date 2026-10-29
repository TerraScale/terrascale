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
