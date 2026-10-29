using System.Net;
using System.Text.Json;
using TerraScale.Authentication.Features.SignIn;
using TerraScale.Authentication.Tests;

namespace TerraScale.Authentication.Slow.Management;

/// <summary>
/// Organization single sign-on against an in-process OpenID Connect provider and DNS resolver: an owner connects the
/// provider and proves the email domain by DNS; then the domain's accounts sign in only through the provider and join
/// with the default role, owners keep email-code sign-in, every broken callback is refused, and only owners change it.
/// </summary>
[ClassDataSource<TerraBaseFixture>(Shared = SharedType.PerTestSession)]
public sealed class OrganizationSsoTests(TerraBaseFixture fixture)
{
    private const string Domain = "acme.test";

    [Test]
    public async Task Broken_callbacks_are_refused()
    {
        using var idp = new FakeSsoIdp();
        await using var oidc = await OidcHarness.StartAsync(fixture, services: idp.Route);
        await ConnectAsync(oidc, idp);

        foreach (var claims in new Dictionary<string, object?>[] { new() { ["nonce"] = "other" }, new() { ["email"] = "eve@evil.test" },
            new() { ["email_verified"] = false }, new() { ["aud"] = "another-client" }, new() { ["iss"] = "https://idp.example.test/realms/other" } })
        {
            using var refused = await SignInAsync(oidc, idp, "bob@acme.test", claims);
            await Assert.That(refused.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
            await Assert.That(OidcHarness.SessionCookie(refused)).IsNull();
        }

        using var noCookie = await SignInAsync(oidc, idp, "bob@acme.test", cookie: false);
        await Assert.That(noCookie.StatusCode).IsEqualTo(HttpStatusCode.Forbidden);
    }

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
        using var email = await oidc.PostFormAsync("/ui/terrascale/sign-in", new() { ["email"] = "bob@acme.test" });
        await Assert.That((await email.Content.ReadAsStringAsync()).Contains("Check your email", StringComparison.Ordinal)).IsTrue();
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

    // Starts an SSO sign-in, lets the provider sign <paramref name="email"/> in, and follows its redirect back.
    private static async Task<HttpResponseMessage> SignInAsync(OidcHarness oidc, FakeSsoIdp idp, string email, IReadOnlyDictionary<string, object?>? claims = null, bool cookie = true)
    {
        using var start = await oidc.Client.GetAsync($"/ui/terrascale/sso?email={Uri.EscapeDataString(email)}");
        await Assert.That(start.StatusCode).IsEqualTo(HttpStatusCode.Redirect);
        var location = start.Headers.Location!;
        await Assert.That(location.AbsoluteUri.StartsWith(FakeSsoIdp.Issuer + "/protocol/openid-connect/auth?", StringComparison.Ordinal)).IsTrue();
        var state = start.Headers.GetValues("Set-Cookie").Select(value => value.Split(';')[0]).Single(pair => pair.StartsWith("tsauth_sso=", StringComparison.Ordinal));
        var (code, returned) = idp.Authorize(location, email, claims);
        using var callback = new HttpRequestMessage(HttpMethod.Get, $"/ui/terrascale/sso/callback?code={code}&state={Uri.EscapeDataString(returned)}");
        if (cookie)
        {
            callback.Headers.Add("Cookie", state);
        }

        return await oidc.Client.SendAsync(callback);
    }
}
