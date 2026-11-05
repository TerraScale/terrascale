using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.DependencyInjection;
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

    [Test]
    public async Task A_project_key_is_shown_once_and_exchanged_for_a_project_token_with_the_contract_claims()
    {
        await using var oidc = await StartAsync();
        var (alice, token) = await oidc.CreateUserTokenAsync(oidc.UniqueEmail("alice"));
        var organization = await oidc.CreateOrganizationAsync(token);
        var project = await oidc.CreateProjectAsync(token, organization, "Production");
        var keys = $"{Organizations}/{organization}/projects/{project}/api-keys";

        var database = await oidc.CreateDatabaseIdAsync(oidc.UniqueName("app"));
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

        await Assert.That(await TokenErrorAsync(oidc.PostTokenAsync(new() { ["grant_type"] = "client_credentials" }, id, key[..^1] + (key[^1] == 'a' ? 'b' : 'a')))).IsEqualTo((401, "invalid_client"));

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
        await using var oidc = await StartAsync();
        var (alice, aliceToken) = await oidc.CreateUserTokenAsync(oidc.UniqueEmail("alice"));
        var (bob, bobToken) = await oidc.CreateUserTokenAsync(oidc.UniqueEmail("bob"));
        var (dave, daveToken) = await oidc.CreateUserTokenAsync(oidc.UniqueEmail("dave"));
        var organization = await oidc.CreateOrganizationAsync(aliceToken);
        var first = await oidc.CreateProjectAsync(aliceToken, organization, "Production");
        var second = await oidc.CreateProjectAsync(aliceToken, organization, "Staging");
        await oidc.AddMemberAsync(organization, bob.Id, "viewer");
        await oidc.GrantAsync(second, dave.Id, "member");

        using (var exchanged = await oidc.ExchangeAsync(aliceToken, new() { ["organization"] = organization, ["project"] = first }))
        {
            await Assert.That(exchanged.StatusCode).IsEqualTo(HttpStatusCode.OK);
            var body = await OidcHarness.JsonAsync(exchanged);
            await Assert.That(body.GetProperty("issued_token_type").GetString()).IsEqualTo("urn:ietf:params:oauth:token-type:access_token");
            await Assert.That(body.TryGetProperty("refresh_token", out _)).IsFalse();
            var access = await oidc.ValidateAsync(body.GetProperty("access_token").GetString()!, Audience);
            await Assert.That(access.Subject).IsEqualTo(alice.Id);
            await Assert.That(access.GetClaim("email").Value).IsEqualTo(alice.Email);
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
            var access = await oidc.ValidateAsync((await OidcHarness.JsonAsync(viewer)).GetProperty("access_token").GetString()!, Audience);
            await Assert.That(access.TryGetClaim("project_id", out _)).IsFalse();
            await Assert.That(access.GetClaim("scope").Value.Split(' ')).IsEquivalentTo(["databases:read", "sql:read"]);
        }

        await Assert.That(await TokenErrorAsync(oidc.ExchangeAsync(bobToken, new() { ["organization"] = organization, ["project"] = first, ["scope"] = "sql:write" }))).IsEqualTo((400, "invalid_scope"));

        // A collaborator reaches only its granted project.
        await Assert.That((await TokenErrorAsync(oidc.ExchangeAsync(daveToken, new() { ["organization"] = organization, ["project"] = second }))).Status).IsEqualTo(200);
        await Assert.That(await TokenErrorAsync(oidc.ExchangeAsync(daveToken, new() { ["organization"] = organization, ["project"] = first }))).IsEqualTo((400, "invalid_target"));

        // After sign-out everywhere, the sign-in token can no longer be exchanged.
        await Assert.That(await oidc.Server.App.Services.GetRequiredService<TerraScale.Authentication.Features.Identity.Sessions>().RevokeAllAsync("terrascale", bob.Id)).IsTrue();
        await Assert.That(await TokenErrorAsync(oidc.ExchangeAsync(bobToken, new() { ["organization"] = organization }))).IsEqualTo((400, "invalid_grant"));

    }

    // The dashboard sends ts-auth the scope set below when a user creates a full-access key. Read from the dashboard's
    // own source, so a scope rename that leaves the two products disagreeing at one commit fails here.

    private Task<OidcHarness> StartAsync() => OidcHarness.StartSharedManagementAsync(fixture);

    private static async Task<(int Status, string? Error)> TokenErrorAsync(Task<HttpResponseMessage> call)
    {
        using var response = await call;
        var body = await OidcHarness.JsonAsync(response);
        return ((int)response.StatusCode, body.TryGetProperty("error", out var error) ? error.GetString() : null);
    }

    [GeneratedRegex(@"FullAccessScopes = \[(?<scopes>[^\]]+)\]")]
    private static partial Regex DashboardFullAccess();

    [GeneratedRegex("^[0-7][0-9A-HJKMNP-TV-Z]{25}_[0-9a-hjkmnp-tv-z]{52}$")]
    private static partial Regex KeyString();
}
