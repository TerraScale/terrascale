using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Extensions.DependencyInjection;
using OpenIddict.Abstractions;
using OpenIddict.Server;
using TerraScale.Authentication.Configuration;
using TerraScale.Authentication.Features.Oidc;
using TerraScale.Authentication.Features.Platform;
using TerraScale.Authentication.Features.SignIn;
using TerraScale.Authentication.Store;
using TerraScale.Authentication.Tests;
using static TerraScale.Authentication.Tests.PlatformCalls;

namespace TerraScale.Authentication.Fast.Management;

/// <summary>
/// API keys and the token grants of the platform contract (TS-598): an API key's client credentials and a user's token
/// exchange, each for an organization or project token, over real HTTP on the real TerraBase.
/// </summary>
[ClassDataSource<TerraBaseFixture>(Shared = SharedType.PerTestSession)]
public sealed partial class ApiKeyTests(TerraBaseFixture fixture)
{
    private const string Audience = "https://api.terrascale.tech";
    private static readonly string[] ReadWrite = ["sql:read", "sql:write"];
    private static readonly string[] WithShapes = ["sql:read", "shapes:read"];

    [Test]
    public async Task A_project_key_is_shown_once_and_exchanged_for_a_project_token_with_the_contract_claims()
    {
        await using var oidc = await StartAsync();
        var (alice, token) = await oidc.SignInUserAsync("alice@example.test");
        var organization = await oidc.CreateOrganizationAsync(token);
        var project = await oidc.CreateProjectAsync(token, organization, "Production");
        var keys = $"{Organizations}/{organization}/projects/{project}/api-keys";

        await Assert.That(await oidc.FailAsync(HttpMethod.Post, keys, token, new { name = "backend", scopes = ReadWrite, database = "app\n" })).IsEqualTo((400, "invalid_request"));
        // TerraBase dropped shapes (R2c-1), so ts-auth no longer issues their scope.
        await Assert.That(await oidc.FailAsync(HttpMethod.Post, keys, token, new { name = "backend", scopes = WithShapes })).IsEqualTo((400, "invalid_request"));
        await Assert.That(await oidc.FailAsync(HttpMethod.Post, keys, token, new { name = "backend", scopes = ReadWrite, database = "app" })).IsEqualTo((400, "invalid_request"));
        var database = await oidc.CreateDatabaseIdAsync("app");
        var created = await oidc.OkAsync(HttpMethod.Post, keys, token, new { name = "backend", scopes = ReadWrite, database }, HttpStatusCode.Created);
        var key = created.GetProperty("key").GetString()!;
        var id = created.GetProperty("api_key").GetProperty("id").GetString()!;
        await Assert.That(KeyString().IsMatch(key)).IsTrue();
        await Assert.That(key[..26]).IsEqualTo(id);
        await Assert.That(created.GetProperty("api_key").GetProperty("project_id").GetString()).IsEqualTo(project);
        await Assert.That(created.GetProperty("api_key").GetProperty("created_by").GetString()).IsEqualTo(alice.Id);

        // Never shown again.
        var listed = await oidc.OkAsync(HttpMethod.Get, keys, token);
        await Assert.That(listed.GetProperty("items").GetArrayLength()).IsEqualTo(1);
        await Assert.That(listed.ToString()).DoesNotContain(key[27..]);
        await Assert.That(listed.GetProperty("items")[0].GetProperty("last_used_at").ValueKind).IsEqualTo(JsonValueKind.Null);
        await Assert.That((await oidc.OkAsync(HttpMethod.Get, $"{keys}/{id}", token)).GetProperty("database").GetString()).IsEqualTo(database);

        using var exchanged = await oidc.ApiKeyTokenAsync(key);
        await Assert.That(exchanged.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(exchanged.Headers.CacheControl!.NoStore).IsTrue();
        var body = await OidcHarness.JsonAsync(exchanged);
        await Assert.That(body.GetProperty("expires_in").GetInt32()).IsBetween(50, 60);
        await Assert.That(body.TryGetProperty("refresh_token", out _)).IsFalse();
        await Assert.That(body.TryGetProperty("id_token", out _)).IsFalse();
        var access = await oidc.ValidateAsync(body.GetProperty("access_token").GetString()!, Audience);
        await Assert.That(access.Alg).IsEqualTo("ES256");
        await Assert.That(access.Subject).IsEqualTo(id);
        await Assert.That(access.GetClaim("client_id").Value).IsEqualTo(id);
        await Assert.That(access.Audiences.ToArray()).IsEquivalentTo([Audience]);
        await Assert.That(access.GetClaim("org_id").Value).IsEqualTo(organization);
        await Assert.That(access.GetClaim("project_id").Value).IsEqualTo(project);
        await Assert.That(access.GetClaim("database").Value).IsEqualTo(database);
        await Assert.That(access.GetClaim("scope").Value.Split(' ')).IsEquivalentTo(["sql:read", "sql:write"]);
        await Assert.That(access.TryGetClaim("email", out _)).IsFalse();
        await Assert.That((access.ValidTo - access.IssuedAt).TotalSeconds).IsEqualTo(60);

        // The exchange is recorded as the key's last use, in its body and in the list.
        var used = (await oidc.OkAsync(HttpMethod.Get, $"{keys}/{id}", token)).GetProperty("last_used_at").GetDateTimeOffset();
        await Assert.That(used).IsLessThanOrEqualTo(DateTimeOffset.UtcNow.AddSeconds(5));
        await Assert.That((await oidc.OkAsync(HttpMethod.Get, keys, token)).GetProperty("items")[0].GetProperty("last_used_at").GetDateTimeOffset()).IsEqualTo(used);

        // The key string authenticates with client_secret_post too; scopes narrow but never widen.
        using (var posted = await oidc.PostTokenAsync(new() { ["grant_type"] = "client_credentials", ["client_id"] = id, ["client_secret"] = key, ["scope"] = "sql:read" }, null, null))
        {
            var narrowed = await oidc.ValidateAsync((await OidcHarness.JsonAsync(posted)).GetProperty("access_token").GetString()!, Audience);
            await Assert.That(narrowed.GetClaim("scope").Value).IsEqualTo("sql:read");
        }

        await Assert.That(await TokenErrorAsync(oidc.ApiKeyTokenAsync(key, new() { ["scope"] = "databases:write" }))).IsEqualTo((400, "invalid_scope"));
        await Assert.That(await TokenErrorAsync(oidc.ApiKeyTokenAsync(key, new() { ["project"] = Guid.CreateVersion7().ToString("D") }))).IsEqualTo((400, "invalid_target"));
        await Assert.That(await TokenErrorAsync(oidc.PostTokenAsync(new() { ["grant_type"] = "client_credentials" }, id, key[..^1] + (key[^1] == 'a' ? 'b' : 'a')))).IsEqualTo((401, "invalid_client"));
        await Assert.That(await TokenErrorAsync(oidc.PostTokenAsync(new() { ["grant_type"] = "client_credentials" }, "tsk_00000000000000000000", key))).IsEqualTo((401, "invalid_client"));
        // A key has only the client-credentials grant.
        await Assert.That((await TokenErrorAsync(oidc.PostTokenAsync(new() { ["grant_type"] = "authorization_code", ["code"] = "x", ["redirect_uri"] = OidcHarness.RedirectUri, ["code_verifier"] = "x" }, id, key))).Status).IsEqualTo(400);

        // A key's token never opens the management API.
        await Assert.That(await oidc.FailAsync(HttpMethod.Get, Organizations, body.GetProperty("access_token").GetString()!)).IsEqualTo((401, "unauthorized"));

        // Revocation stops the next exchange at once.
        await oidc.OkAsync(HttpMethod.Delete, $"{keys}/{id}", token, status: HttpStatusCode.NoContent);
        await Assert.That(await TokenErrorAsync(oidc.ApiKeyTokenAsync(key))).IsEqualTo((401, "invalid_client"));
        await Assert.That((await oidc.OkAsync(HttpMethod.Get, keys, token)).GetProperty("items").GetArrayLength()).IsEqualTo(0);
        await Assert.That(await oidc.FailAsync(HttpMethod.Get, $"{keys}/{id}", token)).IsEqualTo((404, "not_found"));
    }

    [Test]
    public async Task Users_exchange_their_sign_in_for_organization_and_project_tokens_their_role_allows()
    {
        var clock = new ManualClock(DateTimeOffset.UtcNow);
        await using var oidc = await StartAsync(clock);
        var (alice, _, tokens) = await oidc.SignInAndRedeemAsync("alice@example.test");
        var aliceToken = tokens.GetProperty("access_token").GetString()!;
        var (bob, bobToken) = await oidc.SignInUserAsync("bob@example.test");
        var (dave, daveToken) = await oidc.SignInUserAsync("dave@example.test");
        var organization = await oidc.CreateOrganizationAsync(aliceToken);
        var first = await oidc.CreateProjectAsync(aliceToken, organization, "Production");
        var second = await oidc.CreateProjectAsync(aliceToken, organization, "Staging");
        await oidc.AddMemberAsync(organization, bob.Id, "viewer");
        await oidc.GrantAsync(second, dave.Id, "member");

        using (var discovery = await oidc.Client.GetAsync("/terrascale/.well-known/openid-configuration"))
        {
            var grants = (await OidcHarness.JsonAsync(discovery)).GetProperty("grant_types_supported").EnumerateArray().Select(grant => grant.GetString()).ToArray();
            await Assert.That(grants).Contains("urn:ietf:params:oauth:grant-type:token-exchange");
        }

        using (var exchanged = await oidc.ExchangeAsync(aliceToken, new() { ["organization"] = organization, ["project"] = first }))
        {
            await Assert.That(exchanged.StatusCode).IsEqualTo(HttpStatusCode.OK);
            var body = await OidcHarness.JsonAsync(exchanged);
            await Assert.That(body.GetProperty("issued_token_type").GetString()).IsEqualTo("urn:ietf:params:oauth:token-type:access_token");
            await Assert.That(body.TryGetProperty("refresh_token", out _)).IsFalse();
            var access = await oidc.ValidateAsync(body.GetProperty("access_token").GetString()!, Audience, clock);
            await Assert.That(access.Subject).IsEqualTo(alice.Id);
            await Assert.That(access.GetClaim("email").Value).IsEqualTo("alice@example.test");
            await Assert.That(access.GetClaim("org_id").Value).IsEqualTo(organization);
            await Assert.That(access.GetClaim("project_id").Value).IsEqualTo(first);
            await Assert.That(access.GetClaim("scope").Value.Split(' ')).IsEquivalentTo(["databases:read", "databases:write", "sql:read", "sql:write"]);
            await Assert.That(access.Audiences.ToArray()).IsEquivalentTo([Audience]);
            await Assert.That((access.ValidTo - access.IssuedAt).TotalSeconds).IsLessThanOrEqualTo(60);

            // Organization and project tokens never open the management API.
            await Assert.That(await oidc.FailAsync(HttpMethod.Get, Organizations, body.GetProperty("access_token").GetString()!)).IsEqualTo((401, "unauthorized"));

            // Nor can they be exchanged again.
            await Assert.That(await TokenErrorAsync(oidc.ExchangeAsync(body.GetProperty("access_token").GetString()!, new() { ["organization"] = organization }))).IsEqualTo((400, "invalid_grant"));
        }

        // A viewer gets read scopes and no more.
        using (var viewer = await oidc.ExchangeAsync(bobToken, new() { ["organization"] = organization }))
        {
            var access = await oidc.ValidateAsync((await OidcHarness.JsonAsync(viewer)).GetProperty("access_token").GetString()!, Audience, clock);
            await Assert.That(access.TryGetClaim("project_id", out _)).IsFalse();
            await Assert.That(access.GetClaim("scope").Value.Split(' ')).IsEquivalentTo(["databases:read", "sql:read"]);
        }

        await Assert.That(await TokenErrorAsync(oidc.ExchangeAsync(bobToken, new() { ["organization"] = organization, ["project"] = first, ["scope"] = "sql:write" }))).IsEqualTo((400, "invalid_scope"));

        // A collaborator reaches only its project, and has no organization token.
        await Assert.That((await TokenErrorAsync(oidc.ExchangeAsync(daveToken, new() { ["organization"] = organization, ["project"] = second }))).Status).IsEqualTo(200);
        foreach (var target in new[] { new Dictionary<string, string> { ["organization"] = organization }, new() { ["organization"] = organization, ["project"] = first }, new() { ["organization"] = Guid.CreateVersion7().ToString("D") } })
        {
            await Assert.That(await TokenErrorAsync(oidc.ExchangeAsync(daveToken, target))).IsEqualTo((400, "invalid_target"));
        }

        await Assert.That(await TokenErrorAsync(oidc.ExchangeAsync(aliceToken, []))).IsEqualTo((400, "invalid_request"));
        await Assert.That((await TokenErrorAsync(oidc.ExchangeAsync(tokens.GetProperty("id_token").GetString()!, new() { ["organization"] = organization }))).Status).IsEqualTo(400);

        // A sign-in never carries TerraBase or billing scopes, although the dashboard holds their permissions.
        clock.Advance(HostedAuth.MailCooldown);
        var (_, challenge) = OidcHarness.Pkce();
        using (var authorize = await oidc.AuthorizeAsync(OidcHarness.AuthorizePath(challenge, scope: "openid sql:write"), await oidc.SignInAsync("bob@example.test")))
        {
            await Assert.That(OidcHarness.Query(authorize.Headers.Location!)["error"]).IsEqualTo("invalid_scope");
        }

        // After sign-out everywhere, the sign-in token can no longer be exchanged.
        await Assert.That(await oidc.Server.App.Services.GetRequiredService<TerraScale.Authentication.Features.Identity.Sessions>().RevokeAllAsync("terrascale", bob.Id)).IsTrue();
        await Assert.That(await TokenErrorAsync(oidc.ExchangeAsync(bobToken, new() { ["organization"] = organization }))).IsEqualTo((400, "invalid_grant"));

        // A revoked sign-in can no longer be exchanged, on any replica, whatever it exchanged before.
        await using var replica = await oidc.StartReplicaAsync(values => values["access-token-audiences"] = Audience, clock);
        await Assert.That((await TokenErrorAsync(oidc.ExchangeAsync(aliceToken, new() { ["organization"] = organization }, replica.Client))).Status).IsEqualTo(200);
        using (var revoked = await oidc.RevokeAsync(tokens.GetProperty("refresh_token").GetString()!))
        {
            await Assert.That(revoked.StatusCode).IsEqualTo(HttpStatusCode.OK);
        }

        await Assert.That(await TokenErrorAsync(oidc.ExchangeAsync(aliceToken, new() { ["organization"] = organization }, replica.Client))).IsEqualTo((400, "invalid_grant"));
        await Assert.That(await TokenErrorAsync(oidc.ExchangeAsync(aliceToken, new() { ["organization"] = organization }))).IsEqualTo((400, "invalid_grant"));
    }

    private Task<OidcHarness> StartAsync(TimeProvider? clock = null) => OidcHarness.StartAsync(fixture, values => values["access-token-audiences"] = Audience, clock: clock);

    private static async Task<(int Status, string? Error)> TokenErrorAsync(Task<HttpResponseMessage> call)
    {
        using var response = await call;
        var body = await OidcHarness.JsonAsync(response);
        return ((int)response.StatusCode, body.TryGetProperty("error", out var error) ? error.GetString() : null);
    }

    [GeneratedRegex("^[0-7][0-9A-HJKMNP-TV-Z]{25}_[0-9a-hjkmnp-tv-z]{52}$")]
    private static partial Regex KeyString();
}
