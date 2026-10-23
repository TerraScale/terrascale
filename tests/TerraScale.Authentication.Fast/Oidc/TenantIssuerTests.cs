using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using TerraScale.Authentication.Features.Oidc;
using TerraScale.Authentication.Store;
using TerraScale.Authentication.Tests;
using static TerraScale.Authentication.Tests.PlatformCalls;

namespace TerraScale.Authentication.Fast.Oidc;

/// <summary>One issuer per tenant on one origin: the platform's, and each existing project's discovery document and JWKS.</summary>
[ClassDataSource<TerraBaseFixture>(Shared = SharedType.PerTestSession)]
public sealed class TenantIssuerTests(TerraBaseFixture fixture)
{
    [Test]
    public async Task A_project_tenant_serves_its_own_issuer_discovery_and_keys()
    {
        await using var oidc = await OidcHarness.StartAsync(fixture);
        var (_, token) = await oidc.SignInUserAsync("alice@example.test");
        var project = await oidc.CreateProjectAsync(token, await oidc.CreateOrganizationAsync(token), "Production");
        var issuer = $"{OidcHarness.PublicOrigin}/{project}";

        var discovery = await oidc.Client.GetFromJsonAsync<JsonElement>($"/{project}/.well-known/openid-configuration");
        await Assert.That(discovery.GetProperty("issuer").GetString()).IsEqualTo(issuer);
        await Assert.That(discovery.GetProperty("jwks_uri").GetString()).IsEqualTo($"{issuer}/.well-known/jwks.json");
        await Assert.That(discovery.GetProperty("token_endpoint").GetString()).IsEqualTo($"{issuer}/oauth/token");

        // Its own key ring: no kid of the platform's.
        var keys = await KeyIdsAsync(oidc, project);
        var platformKeys = await KeyIdsAsync(oidc, OidcIssuer.Slug);
        await Assert.That(keys.Count).IsEqualTo(1);
        await Assert.That(platformKeys.Intersect(keys)).IsEmpty();
        await Assert.That(await KeyIdsAsync(oidc, project)).IsEquivalentTo(keys);

        // The platform issuer is unchanged.
        var platform = await oidc.Client.GetFromJsonAsync<JsonElement>($"/{OidcIssuer.Slug}/.well-known/openid-configuration");
        await Assert.That(platform.GetProperty("issuer").GetString()).IsEqualTo(OidcHarness.Issuer);
    }

    [Test]
    public async Task A_project_tenant_knows_none_of_the_platform_clients()
    {
        await using var oidc = await OidcHarness.StartAsync(fixture);
        var (_, token) = await oidc.SignInUserAsync("alice@example.test");
        var project = await oidc.CreateProjectAsync(token, await oidc.CreateOrganizationAsync(token), "Production");

        // The dashboard client exists only in the platform tenant, so the project's issuer neither authorizes it nor
        // gives it a token.
        using var tokens = await oidc.Client.PostAsync($"/{project}/oauth/token", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["code"] = "unknown",
            ["redirect_uri"] = OidcHarness.RedirectUri,
            ["code_verifier"] = OidcHarness.Pkce().Verifier,
            ["client_id"] = OidcIssuer.DashboardClientId,
            ["client_secret"] = OidcHarness.ClientSecret,
        }));
        var (_, challenge) = OidcHarness.Pkce();
        using var authorize = await oidc.Client.GetAsync(OidcHarness.AuthorizePath(challenge).Replace($"/{OidcIssuer.Slug}/", $"/{project}/", StringComparison.Ordinal));

        await Assert.That(await OidcHarness.ErrorAsync(tokens)).IsEqualTo("invalid_client");
        await Assert.That(authorize.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        await Assert.That(await authorize.Content.ReadAsStringAsync()).Contains("invalid_request");
    }

    [Test]
    public async Task Unknown_and_deleted_tenants_answer_404()
    {
        await using var oidc = await OidcHarness.StartAsync(fixture);
        var (_, token) = await oidc.SignInUserAsync("alice@example.test");
        var organization = await oidc.CreateOrganizationAsync(token);
        var project = await oidc.CreateProjectAsync(token, organization, "Production");
        await oidc.OkAsync(HttpMethod.Delete, $"{Organizations}/{organization}/projects/{project}", token, status: HttpStatusCode.NoContent);

        foreach (var tenant in new[] { project, AuthStore.NewId(), "other", project.ToLowerInvariant() })
        {
            using var discovery = await oidc.Client.GetAsync($"/{tenant}/.well-known/openid-configuration");
            using var jwks = await oidc.Client.GetAsync($"/{tenant}/.well-known/jwks.json");
            await Assert.That(discovery.StatusCode).IsEqualTo(HttpStatusCode.NotFound).Because(tenant);
            await Assert.That(jwks.StatusCode).IsEqualTo(HttpStatusCode.NotFound).Because(tenant);
        }
    }

    private static async Task<List<string>> KeyIdsAsync(OidcHarness oidc, string tenant) =>
        [.. (await oidc.Client.GetFromJsonAsync<JsonElement>($"/{tenant}/.well-known/jwks.json")).GetProperty("keys").EnumerateArray().Select(key => key.GetProperty("kid").GetString()!)];
}
