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
    private static readonly string[] ReadOnly = ["documents:read"];
    private static readonly string[] ReadWrite = ["documents:read", "documents:write"];
    private static readonly string[] WithShapes = ["documents:read", "shapes:read"];
    private static readonly string[] Browse = ["databases:read", "documents:read"];

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
        await Assert.That(access.GetClaim("scope").Value.Split(' ')).IsEquivalentTo(["documents:read", "documents:write"]);
        await Assert.That(access.TryGetClaim("email", out _)).IsFalse();
        await Assert.That((access.ValidTo - access.IssuedAt).TotalSeconds).IsEqualTo(60);

        // The exchange is recorded as the key's last use, in its body and in the list.
        var used = (await oidc.OkAsync(HttpMethod.Get, $"{keys}/{id}", token)).GetProperty("last_used_at").GetDateTimeOffset();
        await Assert.That(used).IsLessThanOrEqualTo(DateTimeOffset.UtcNow.AddSeconds(5));
        await Assert.That((await oidc.OkAsync(HttpMethod.Get, keys, token)).GetProperty("items")[0].GetProperty("last_used_at").GetDateTimeOffset()).IsEqualTo(used);

        // The key string authenticates with client_secret_post too; scopes narrow but never widen.
        using (var posted = await oidc.PostTokenAsync(new() { ["grant_type"] = "client_credentials", ["client_id"] = id, ["client_secret"] = key, ["scope"] = "documents:read" }, null, null))
        {
            var narrowed = await oidc.ValidateAsync((await OidcHarness.JsonAsync(posted)).GetProperty("access_token").GetString()!, Audience);
            await Assert.That(narrowed.GetClaim("scope").Value).IsEqualTo("documents:read");
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
    public async Task An_organization_key_reaches_one_project_of_its_organization_per_token_and_only_admins_hold_one()
    {
        await using var oidc = await StartAsync();
        var (_, token) = await oidc.SignInUserAsync("alice@example.test");
        var (bob, bobToken) = await oidc.SignInUserAsync("bob@example.test");
        var organization = await oidc.CreateOrganizationAsync(token);
        var project = await oidc.CreateProjectAsync(token, organization, "Production");
        var other = await oidc.CreateProjectAsync(token, await oidc.CreateOrganizationAsync(token, "Elsewhere"), "Production");
        await oidc.AddMemberAsync(organization, bob.Id, "member");
        var keys = $"{Organizations}/{organization}/api-keys";

        await Assert.That(await oidc.FailAsync(HttpMethod.Post, keys, bobToken, new { name = "ci", scopes = ReadOnly })).IsEqualTo((403, "forbidden_role"));
        await Assert.That(await oidc.FailAsync(HttpMethod.Get, keys, bobToken)).IsEqualTo((403, "forbidden_role"));
        var database = await oidc.CreateDatabaseIdAsync("app");
        foreach (var invalid in new object[]
        {
            new { name = "ci", scopes = Array.Empty<string>() },
            new { name = "ci", scopes = new[] { "documents:read", "documents:read" } },
            new { name = "ci", scopes = new[] { "admin" } },
            new { name = "ci", scopes = ReadOnly, database },
            new { name = "ci", scopes = ReadOnly, expires_at = DateTimeOffset.UtcNow.AddMinutes(-1) },
            new { name = "", scopes = ReadOnly },
        })
        {
            await Assert.That(await oidc.FailAsync(HttpMethod.Post, keys, token, invalid)).IsEqualTo((400, "invalid_request"));
        }

        var key = (await oidc.OkAsync(HttpMethod.Post, keys, token, new { name = "ci", scopes = Browse }, HttpStatusCode.Created)).GetProperty("key").GetString()!;
        await oidc.OkAsync(HttpMethod.Post, $"{Organizations}/{organization}/projects/{project}/api-keys", token, new { name = "app", scopes = ReadOnly }, HttpStatusCode.Created);

        // Organization keys and project keys list apart.
        var organizationKeys = (await oidc.OkAsync(HttpMethod.Get, keys, token)).GetProperty("items");
        await Assert.That(organizationKeys.EnumerateArray().Select(item => item.GetProperty("id").GetString()!).ToArray()).IsEquivalentTo([key[..26]]);
        await Assert.That(organizationKeys[0].GetProperty("project_id").ValueKind).IsEqualTo(JsonValueKind.Null);
        var projectKeys = (await oidc.OkAsync(HttpMethod.Get, $"{Organizations}/{organization}/projects/{project}/api-keys", token)).GetProperty("items");
        await Assert.That(projectKeys.EnumerateArray().Select(item => item.GetProperty("name").GetString()!).ToArray()).IsEquivalentTo(["app"]);

        using (var organizationToken = await oidc.ApiKeyTokenAsync(key))
        {
            var access = await oidc.ValidateAsync((await OidcHarness.JsonAsync(organizationToken)).GetProperty("access_token").GetString()!, Audience);
            await Assert.That(access.GetClaim("org_id").Value).IsEqualTo(organization);
            await Assert.That(access.TryGetClaim("project_id", out _)).IsFalse();
        }

        using (var projectToken = await oidc.ApiKeyTokenAsync(key, new() { ["project"] = project }))
        {
            var access = await oidc.ValidateAsync((await OidcHarness.JsonAsync(projectToken)).GetProperty("access_token").GetString()!, Audience);
            await Assert.That(access.GetClaim("project_id").Value).IsEqualTo(project);
            await Assert.That(access.GetClaim("scope").Value.Split(' ')).IsEquivalentTo(["databases:read", "documents:read"]);
        }

        // Another organization's project and an unknown one answer alike.
        await Assert.That(await TokenErrorAsync(oidc.ApiKeyTokenAsync(key, new() { ["project"] = other }))).IsEqualTo((400, "invalid_target"));
        await Assert.That(await TokenErrorAsync(oidc.ApiKeyTokenAsync(key, new() { ["project"] = Guid.CreateVersion7().ToString("D") }))).IsEqualTo((400, "invalid_target"));

        // A project key outlives nothing: deleting its project revokes it.
        var projectKey = (await oidc.OkAsync(HttpMethod.Post, $"{Organizations}/{organization}/projects/{project}/api-keys", bobToken, new { name = "app", scopes = ReadOnly }, HttpStatusCode.Created)).GetProperty("key").GetString()!;
        await oidc.OkAsync(HttpMethod.Delete, $"{Organizations}/{organization}/projects/{project}", token, status: HttpStatusCode.NoContent);
        await Assert.That((await TokenErrorAsync(oidc.ApiKeyTokenAsync(projectKey))).Status).IsGreaterThanOrEqualTo(400);
        await Assert.That(await TokenErrorAsync(oidc.ApiKeyTokenAsync(key, new() { ["project"] = project }))).IsEqualTo((400, "invalid_target"));
    }

    [Test]
    public async Task Only_those_who_manage_a_key_read_rotate_or_revoke_it_and_a_refused_change_leaves_it_working()
    {
        await using var oidc = await StartAsync();
        var (_, token) = await oidc.SignInUserAsync("alice@example.test");
        var (bob, bobToken) = await oidc.SignInUserAsync("bob@example.test");
        var (dave, daveToken) = await oidc.SignInUserAsync("dave@example.test");
        var organization = await oidc.CreateOrganizationAsync(token);
        var elsewhere = await oidc.CreateOrganizationAsync(token, "Elsewhere");
        var production = await oidc.CreateProjectAsync(token, organization, "Production");
        var staging = await oidc.CreateProjectAsync(token, organization, "Staging");
        await oidc.AddMemberAsync(organization, bob.Id, "viewer");
        await oidc.GrantAsync(staging, dave.Id, "admin");
        var key = (await oidc.OkAsync(HttpMethod.Post, $"{Organizations}/{organization}/projects/{production}/api-keys", token, new { name = "app", scopes = ReadOnly }, HttpStatusCode.Created)).GetProperty("key").GetString()!;
        var id = key[..26];

        // A viewer of the key's project, an admin of another project, and the right caller on a route that does not own
        // the key: none reads, rotates or revokes it.
        var refused = new (string Token, string Route, (int, string) Answer)[]
        {
            (bobToken, $"{Organizations}/{organization}/projects/{production}/api-keys/{id}", (403, "forbidden_role")),
            (daveToken, $"{Organizations}/{organization}/projects/{production}/api-keys/{id}", (404, "not_found")),
            (daveToken, $"{Organizations}/{organization}/projects/{staging}/api-keys/{id}", (404, "not_found")),
            (token, $"{Organizations}/{organization}/projects/{staging}/api-keys/{id}", (404, "not_found")),
            (token, $"{Organizations}/{organization}/api-keys/{id}", (404, "not_found")),
            (token, $"{Organizations}/{elsewhere}/api-keys/{id}", (404, "not_found")),
        };
        foreach (var (caller, route, answer) in refused)
        {
            await Assert.That(await oidc.FailAsync(HttpMethod.Get, route, caller)).IsEqualTo(answer);
            await Assert.That(await oidc.FailAsync(HttpMethod.Post, $"{route}/rotate", caller)).IsEqualTo(answer);
            await Assert.That(await oidc.FailAsync(HttpMethod.Delete, route, caller)).IsEqualTo(answer);
        }

        await Assert.That((await TokenErrorAsync(oidc.ApiKeyTokenAsync(key))).Status).IsEqualTo(200);
        // Still there and never rotated.
        await Assert.That((await oidc.OkAsync(HttpMethod.Get, $"{Organizations}/{organization}/projects/{production}/api-keys/{id}", token)).GetProperty("previous_secret_expires_at").ValueKind).IsEqualTo(JsonValueKind.Null);
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
            await Assert.That(access.GetClaim("scope").Value.Split(' ')).IsEquivalentTo(["databases:read", "databases:write", "documents:read", "documents:write"]);
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
            await Assert.That(access.GetClaim("scope").Value.Split(' ')).IsEquivalentTo(["databases:read", "documents:read"]);
        }

        await Assert.That(await TokenErrorAsync(oidc.ExchangeAsync(bobToken, new() { ["organization"] = organization, ["project"] = first, ["scope"] = "documents:write" }))).IsEqualTo((400, "invalid_scope"));

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
        using (var authorize = await oidc.AuthorizeAsync(OidcHarness.AuthorizePath(challenge, scope: "openid documents:write"), await oidc.SignInAsync("bob@example.test")))
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

    [Test]
    public async Task Rotating_a_key_replaces_its_secret_and_keeps_the_key_identity()
    {
        await using var oidc = await StartAsync();
        var (_, token) = await oidc.SignInUserAsync("owner@example.test");
        var organization = await oidc.CreateOrganizationAsync(token);
        var project = await oidc.CreateProjectAsync(token, organization, "Production");
        var keys = $"{Organizations}/{organization}/projects/{project}/api-keys";
        var created = await oidc.OkAsync(HttpMethod.Post, keys, token, new { name = "backend", scopes = ReadOnly }, HttpStatusCode.Created);
        var original = created.GetProperty("key").GetString()!;
        var id = original[..26];
        await Assert.That((await TokenErrorAsync(oidc.ApiKeyTokenAsync(original))).Status).IsEqualTo(200);

        var rotated = await oidc.OkAsync(HttpMethod.Post, $"{keys}/{id}/rotate", token);
        var replacement = rotated.GetProperty("key").GetString()!;
        await Assert.That(replacement[..26]).IsEqualTo(id);
        await Assert.That(replacement).IsNotEqualTo(original);
        await Assert.That(await TokenErrorAsync(oidc.ApiKeyTokenAsync(original))).IsEqualTo((401, "invalid_client"));
        await Assert.That((await TokenErrorAsync(oidc.ApiKeyTokenAsync(replacement))).Status).IsEqualTo(200);
    }

    private Task<OidcHarness> StartAsync(TimeProvider? clock = null) => OidcHarness.StartAsync(fixture, values => values["access-token-audiences"] = Audience, clock: clock);

    private Task<AuthTestServer> StartReplicaAsync(OidcHarness oidc, ManualClock clock) =>
        OidcHarness.StartServerAsync(fixture, oidc.Tenant, oidc.Email, values =>
        {
            values["master-key"] = Convert.ToBase64String(oidc.Server.App.Services.GetRequiredService<AuthOptions>().MasterKey);
            values["access-token-audiences"] = Audience;
        }, clock: clock);

    private static HttpRequestMessage KeyTokenRequest(string key)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/terrascale/oauth/token") { Content = new FormUrlEncodedContent(new Dictionary<string, string> { ["grant_type"] = "client_credentials" }) };
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Basic", Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes($"{key[..26]}:{key}")));
        return request;
    }

    private static async Task<(int Status, string? Error)> TokenErrorAsync(Task<HttpResponseMessage> call)
    {
        using var response = await call;
        var body = await OidcHarness.JsonAsync(response);
        return ((int)response.StatusCode, body.TryGetProperty("error", out var error) ? error.GetString() : null);
    }

    [GeneratedRegex("^[0-7][0-9A-HJKMNP-TV-Z]{25}_[0-9a-hjkmnp-tv-z]{52}$")]
    private static partial Regex KeyString();
}
