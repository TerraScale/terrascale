using System.Net;
using System.Text.Json;
using Bogus;
using TerraScale.Authentication.Features.Oidc;
using TerraScale.Authentication.Features.Platform;
using TerraScale.Authentication.Tests;

namespace TerraScale.Authentication.Fast.Oidc;

/// <summary>Discovery describes each tenant's supported protocol over real HTTP and TerraBase persistence.</summary>
[ClassDataSource<TerraBaseFixture>(Shared = SharedType.PerTestSession)]
public sealed class ProjectDiscoveryTests(TerraBaseFixture fixture)
{
    [Test]
    public async Task Project_metadata_excludes_platform_only_capabilities_without_changing_platform_metadata()
    {
        await using var oidc = await OidcHarness.StartAsync(fixture);
        var faker = new Faker();
        var (_, token) = await oidc.SignInUserAsync(faker.Internet.Email(provider: "example.test"));
        var organization = await oidc.CreateOrganizationAsync(token, faker.Company.CompanyName());
        var project = await oidc.CreateProjectAsync(token, organization, faker.Commerce.ProductName());
        var platformBefore = await DiscoveryAsync(oidc, OidcIssuer.Slug);
        var metadata = await DiscoveryAsync(oidc, project);
        var issuer = $"{OidcHarness.PublicOrigin}/{project}";

        await Assert.That(metadata.GetProperty("issuer").GetString()).IsEqualTo(issuer);
        await Assert.That(Values(metadata, "grant_types_supported")).IsEquivalentTo(["authorization_code", "refresh_token"]);
        await Assert.That(Values(metadata, "scopes_supported")).IsEquivalentTo(["openid", "email", "profile", "offline_access"]);
        await Assert.That(Values(metadata, "code_challenge_methods_supported")).IsEquivalentTo(["S256"]);
        await Assert.That(metadata.TryGetProperty("device_authorization_endpoint", out _)).IsFalse();
        await Assert.That(metadata.TryGetProperty("device_authorization_endpoint_auth_methods_supported", out _)).IsFalse();
        foreach (var (field, path) in new (string, string)[]
        {
            ("authorization_endpoint", "oauth/authorize"), ("token_endpoint", "oauth/token"),
            ("userinfo_endpoint", "oauth/userinfo"), ("revocation_endpoint", "oauth/revoke"),
            ("end_session_endpoint", "oauth/logout"), ("jwks_uri", ".well-known/jwks.json"),
        })
        {
            await Assert.That(metadata.GetProperty(field).GetString()).IsEqualTo($"{issuer}/{path}");
        }

        // Reading project metadata must not mutate the shared OpenIddict server options.
        var platformAfter = await DiscoveryAsync(oidc, OidcIssuer.Slug);
        await Assert.That(Values(platformAfter, "grant_types_supported")).IsEquivalentTo([
            "authorization_code", "refresh_token", "client_credentials", "urn:ietf:params:oauth:grant-type:device_code",
            "urn:ietf:params:oauth:grant-type:token-exchange", GitHubActionsGrant.GrantType,
        ]);
        await Assert.That(platformAfter.GetProperty("device_authorization_endpoint").GetString()).IsEqualTo($"{OidcHarness.Issuer}/oauth/device");
        await Assert.That(Values(platformAfter, "device_authorization_endpoint_auth_methods_supported")).IsNotEmpty();
        await Assert.That(platformAfter.ToString()).IsEqualTo(platformBefore.ToString());
    }

    private static string[] Values(JsonElement metadata, string field) =>
        [.. metadata.GetProperty(field).EnumerateArray().Select(value => value.GetString()!)];

    private static async Task<JsonElement> DiscoveryAsync(OidcHarness oidc, string tenant)
    {
        using var response = await oidc.Client.GetAsync($"/{tenant}/.well-known/openid-configuration");
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        return await OidcHarness.JsonAsync(response);
    }
}
