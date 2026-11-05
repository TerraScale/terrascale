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
public sealed class OidcTokenReplicaTests(TerraBaseFixture fixture)
{
    [Test]
    public async Task Concurrent_redemptions_issue_once_and_refresh_replay_ends_the_grant()
    {
        await using var oidc = await OidcHarness.StartAsync(fixture);
        await using var replica = await oidc.StartReplicaAsync();
        var user = await oidc.CreateUserAsync();
        var session = (await oidc.Server.App.Services.GetRequiredService<Sessions>().CreateAsync(user)).Token;
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
    public async Task A_key_rotation_reaches_every_replica_and_earlier_tokens_keep_validating()
    {
        await using var oidc = await OidcHarness.StartAsync(fixture, services: services => services.AddSingleton(new KeyRingRefresh(TimeSpan.Zero)));
        await using var replica = await oidc.StartReplicaAsync(services: services => services.AddSingleton(new KeyRingRefresh(TimeSpan.Zero)));
        var user = await oidc.CreateUserAsync();
        var (_, tokens) = await oidc.CreateSessionAndRedeemAsync(user);
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
        (await oidc.Database.RowsAsync(AuthStore.OidcTokenTable)).Single(rowData =>
            rowData.Body.GetProperty("type").GetString() == type && rowData.Body.GetProperty("status").GetString() == "valid").Body;

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
}
