using System.Net;
using TerraScale.Authentication.Tests;
using static TerraScale.Authentication.Tests.PlatformCalls;

namespace TerraScale.Authentication.Fast.Management;

/// <summary>
/// API key and project IP allowlists: stored Canonical, changed by those who manage the key or project, and carried on
/// the key's tokens as <c>key_cidrs</c> and <c>project_cidrs</c> for TerraBase to enforce.
/// </summary>
[ClassDataSource<TerraBaseFixture>(Shared = SharedType.PerTestSession)]
public sealed class ApiKeyAllowlistTests(TerraBaseFixture fixture)
{
    private const string Audience = "https://api.terrascale.tech";
    private static readonly string[] ReadOnly = ["documents:read"];
    private static readonly string[] Invalid = ["10.0.0.0/33"];
    private static readonly string[] Submitted = ["203.0.113.77/24", "198.51.100.7", "::ffff:192.0.2.1", "2001:db8::1/48", "198.51.100.7/32"];
    private static readonly string[] Canonical = ["203.0.113.0/24", "198.51.100.7/32", "192.0.2.1/32", "2001:db8::/48"];
    private static readonly string[] Everywhere = ["0.0.0.0/0"];

    [Test]
    public async Task Key_and_project_allowlists_are_Canonical_and_ride_on_the_keys_next_token()
    {
        await using var oidc = await OidcHarness.StartAsync(fixture, values => values["access-token-audiences"] = Audience);
        var (_, token) = await oidc.SignInUserAsync("alice@example.test");
        var organization = await oidc.CreateOrganizationAsync(token);
        var project = await oidc.CreateProjectAsync(token, organization, "Production");
        var keys = $"{Organizations}/{organization}/projects/{project}/api-keys";

        await Assert.That(await oidc.FailAsync(HttpMethod.Post, keys, token, new { name = "backend", scopes = ReadOnly, allowed_cidrs = Invalid })).IsEqualTo((400, "invalid_request"));
        await Assert.That(await oidc.FailAsync(HttpMethod.Post, keys, token, new { name = "backend", scopes = ReadOnly, allowed_cidrs = Enumerable.Range(0, 33).Select(i => $"10.0.{i}.0/24").ToArray() })).IsEqualTo((400, "invalid_request"));
        var created = await oidc.OkAsync(HttpMethod.Post, keys, token,
            new { name = "backend", scopes = ReadOnly, allowed_cidrs = Submitted }, HttpStatusCode.Created);
        var key = created.GetProperty("key").GetString()!;
        var id = created.GetProperty("api_key").GetProperty("id").GetString()!;
        await Assert.That(Cidrs(created.GetProperty("api_key"))).IsEquivalentTo(Canonical);
        await Assert.That(Cidrs((await oidc.OkAsync(HttpMethod.Get, keys, token)).GetProperty("items")[0])).IsEquivalentTo(Canonical);

        var access = await TokenAsync(oidc, key);
        await Assert.That(access.GetClaim("key_cidrs").Value).IsEqualTo(string.Join(' ', Canonical));
        await Assert.That(access.TryGetClaim("project_cidrs", out _)).IsFalse();

        // The project's list joins the key's on the next token; emptying the key's drops its claim.
        var projectBody = await oidc.OkAsync(HttpMethod.Put, $"{Organizations}/{organization}/projects/{project}/allowed-cidrs", token, new { allowed_cidrs = Everywhere });
        await Assert.That(Cidrs(projectBody)).IsEquivalentTo(Everywhere);
        var cleared = await oidc.OkAsync(HttpMethod.Put, $"{keys}/{id}/allowed-cidrs", token, new { allowed_cidrs = Array.Empty<string>() });
        await Assert.That(Cidrs(cleared)).IsEmpty();
        access = await TokenAsync(oidc, key);
        await Assert.That(access.TryGetClaim("key_cidrs", out _)).IsFalse();
        await Assert.That(access.GetClaim("project_cidrs").Value).IsEqualTo("0.0.0.0/0");
        await Assert.That(await oidc.FailAsync(HttpMethod.Put, $"{keys}/{id}/allowed-cidrs", token, new { allowed_cidrs = (string[]?)null })).IsEqualTo((400, "invalid_request"));

        // Only a project admin changes the project's list; a viewer cannot change a key's.
        var (bob, bobToken) = await oidc.SignInUserAsync("bob@example.test");
        await oidc.AddMemberAsync(organization, bob.Id, "viewer");
        await Assert.That((await oidc.FailAsync(HttpMethod.Put, $"{Organizations}/{organization}/projects/{project}/allowed-cidrs", bobToken, new { allowed_cidrs = Array.Empty<string>() })).Status).IsEqualTo(403);
        await Assert.That((await oidc.FailAsync(HttpMethod.Put, $"{keys}/{id}/allowed-cidrs", bobToken, new { allowed_cidrs = Array.Empty<string>() })).Status).IsEqualTo(403);
    }

    private static string[] Cidrs(System.Text.Json.JsonElement body) =>
        [.. body.GetProperty("allowed_cidrs").EnumerateArray().Select(cidr => cidr.GetString()!)];

    private static async Task<Microsoft.IdentityModel.JsonWebTokens.JsonWebToken> TokenAsync(OidcHarness oidc, string key)
    {
        using var exchanged = await oidc.ApiKeyTokenAsync(key);
        await Assert.That(exchanged.StatusCode).IsEqualTo(HttpStatusCode.OK);
        return await oidc.ValidateAsync((await OidcHarness.JsonAsync(exchanged)).GetProperty("access_token").GetString()!, Audience);
    }
}
