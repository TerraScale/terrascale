using System.Net;
using System.Text.Json;
using TerraScale.Authentication.Tests;

namespace TerraScale.Authentication.Fast.Management;

/// <summary>
/// Organization single sign-on connections against an in-process OpenID Connect provider and DNS resolver: an owner
/// connects the provider and proves the email domain by DNS; only owners change it, and a verified domain stays theirs.
/// </summary>
[ClassDataSource<TerraBaseFixture>(Shared = SharedType.PerTestSession)]
public sealed class OrganizationSsoTests(TerraBaseFixture fixture)
{
    private const string Domain = "acme.test";

    [Test]
    public async Task Only_owners_change_the_connection_and_a_verified_domain_cannot_be_taken()
    {
        using var idp = new FakeSsoIdp();
        await using var oidc = await OidcHarness.StartAsync(fixture, services: idp.Route);
        var (organization, token) = await ConnectAsync(oidc, idp);
        var (_, other) = await oidc.SignInUserAsync("mallory@other.test");
        var otherOrganization = await oidc.CreateOrganizationAsync(other);

        await Assert.That((await oidc.FailAsync(HttpMethod.Put, $"{PlatformCalls.Organizations}/{otherOrganization}/sso", other, Request())).Status).IsEqualTo(409);
        await Assert.That((await oidc.FailAsync(HttpMethod.Get, $"{PlatformCalls.Organizations}/{organization}/sso", other)).Status).IsEqualTo(404);
        await Assert.That((await oidc.FailAsync(HttpMethod.Put, $"{PlatformCalls.Organizations}/{organization}/sso", token, Request(issuer: "https://idp.example.test/unknown"))).Code).IsEqualTo("invalid_request");

        await oidc.OkAsync(HttpMethod.Delete, $"{PlatformCalls.Organizations}/{organization}/sso", token, status: HttpStatusCode.NoContent);
        await Assert.That((await oidc.FailAsync(HttpMethod.Get, $"{PlatformCalls.Organizations}/{organization}/sso", token)).Status).IsEqualTo(404);
        await Assert.That((await oidc.FailAsync(HttpMethod.Put, $"{PlatformCalls.Organizations}/{otherOrganization}/sso", other, new { domain = Domain, oidc = new { issuer = FakeSsoIdp.Issuer, client_id = "c" } })).Code).IsEqualTo("invalid_request");
    }

    private static object Request(string issuer = FakeSsoIdp.Issuer) => new
    {
        domain = Domain,
        default_role = "viewer",
        oidc = new { issuer, client_id = FakeSsoIdp.ClientId, client_secret = FakeSsoIdp.ClientSecret },
    };

    // The owner's organization with a connection whose domain the DNS proves, after a refused first verification.
    private static async Task<(string Organization, string Token)> ConnectAsync(OidcHarness oidc, FakeSsoIdp idp)
    {
        var (_, token) = await oidc.SignInUserAsync("owner@acme.test");
        var organization = await oidc.CreateOrganizationAsync(token);
        var path = $"{PlatformCalls.Organizations}/{organization}/sso";
        var connection = await oidc.OkAsync(HttpMethod.Put, path, token, Request());
        await Assert.That(connection.GetProperty("domain_verified").GetBoolean()).IsFalse();
        await Assert.That(connection.GetProperty("oidc").GetProperty("client_secret_set").GetBoolean()).IsTrue();
        await Assert.That(connection.GetRawText().Contains(FakeSsoIdp.ClientSecret, StringComparison.Ordinal)).IsFalse();
        await Assert.That((await oidc.FailAsync(HttpMethod.Post, path + "/verify", token)).Code).IsEqualTo("domain_unverified");

        var record = connection.GetProperty("verification");
        idp.Txt[record.GetProperty("name").GetString()!] = record.GetProperty("value").GetString()!;
        var verified = await oidc.OkAsync(HttpMethod.Post, path + "/verify", token);
        await Assert.That(verified.GetProperty("domain_verified").GetBoolean()).IsTrue();
        return (organization, token);
    }
}
