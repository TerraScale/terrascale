using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using Bogus;
using Microsoft.Extensions.DependencyInjection;
using TerraScale.Authentication.Configuration;
using TerraScale.Authentication.Contracts;
using TerraScale.Authentication.Features.Identity;
using TerraScale.Authentication.Features.Platform;
using TerraScale.Authentication.Features.ProjectTenants;
using TerraScale.Authentication.Store;
using TerraScale.Authentication.Tests;

namespace TerraScale.Authentication.Fast.Management;

/// <summary>The project tenant management API (<c>/v1/{project}/…</c>) over real HTTP on the real TerraBase.</summary>
[ClassDataSource<TerraBaseFixture>(Shared = SharedType.PerTestSession)]
public sealed class ProjectTenantTests(TerraBaseFixture fixture)
{
    private static readonly string[] InsecureRedirect = ["http://app.example.test/cb"];

    private readonly Faker faker = new();

    [Test]
    public async Task Admins_register_clients_whose_secret_is_answered_once_and_change_settings()
    {
        await using var oidc = await OidcHarness.StartSharedManagementAsync(fixture);
        var (_, token, project, root) = await this.ProjectAsync(oidc);
        var body = new { name = this.faker.Company.CompanyName(), type = "confidential", redirect_uris = new[] { "https://app.example.test/callback", "http://localhost:3000/cb", "http://127.0.0.1:3000/cb", "http://[::1]:3000/cb" } };
        var created = await oidc.OkAsync(HttpMethod.Post, $"{root}/clients", token, body, HttpStatusCode.Created);
        await Assert.That(created.GetProperty("redirect_uris").EnumerateArray().Select(uri => uri.GetString()!).ToArray()).IsEquivalentTo(body.redirect_uris);
        var clientId = created.GetProperty("client_id").GetString()!;
        var secret = created.GetProperty("client_secret").GetString()!;
        await Assert.That(secret.Length).IsEqualTo(48);

        // Listed without its secret, and stored only as OpenIddict's hash, in the project's instance.
        var listed = (await oidc.OkAsync(HttpMethod.Get, $"{root}/clients", token)).GetProperty("items");
        await Assert.That(listed.GetArrayLength()).IsEqualTo(1);
        await Assert.That(listed[0].TryGetProperty("client_secret", out _)).IsFalse();
        var stored = await oidc.Server.App.Services.GetRequiredService<AuthStore>().GetAsync(AuthStore.OidcApplicationTable, project, clientId);
        await Assert.That(stored!.Body.GetRawText()).DoesNotContain(secret);
        await Assert.That(stored.Body.GetProperty("instanceId").GetString()).IsEqualTo(project);
        await Assert.That(await oidc.FailAsync(HttpMethod.Post, $"{root}/clients", token, body with { redirect_uris = InsecureRedirect })).IsEqualTo((400, "invalid_request"));
        var settings = await oidc.OkAsync(HttpMethod.Get, $"{root}/settings", token);
        await Assert.That(settings.GetProperty("methods").GetProperty("sign_up").GetBoolean()).IsTrue();
        await Assert.That(settings.GetProperty("counts").GetProperty("clients").GetInt64()).IsEqualTo(1);
        await Assert.That(await oidc.FailAsync(HttpMethod.Put, $"{root}/settings", token, new { methods = new { passcode = false, password = false } })).IsEqualTo((400, "invalid_request"));
        var changed = await oidc.OkAsync(HttpMethod.Put, $"{root}/settings", token, new { methods = new { sign_up = false }, branding = new { name = "Acme", logo_url = (string?)null, theme_color = "#A1B2C3" } });
        await Assert.That(changed.GetProperty("methods").GetProperty("sign_up").GetBoolean()).IsFalse();
        await Assert.That(changed.GetProperty("branding").GetProperty("theme_color").GetString()).IsEqualTo("#a1b2c3");

        using var deleted = await oidc.CallAsync(HttpMethod.Delete, $"{root}/clients/{clientId}", token);
        await Assert.That(deleted.StatusCode).IsEqualTo(HttpStatusCode.NoContent);
        await Assert.That((await oidc.OkAsync(HttpMethod.Get, $"{root}/clients", token)).GetProperty("items").GetArrayLength()).IsEqualTo(0);
    }

    // An owner signed in, with its sign-in token, an organization and a project.
    private async Task<(UserRecord Owner, string Token, string Project, string Root)> ProjectAsync(OidcHarness oidc)
    {
        var (owner, token) = await oidc.CreateUserTokenAsync(oidc.UniqueEmail("person1"));
        var organization = await oidc.CreateOrganizationAsync(token, this.faker.Company.CompanyName());
        var project = await oidc.CreateProjectAsync(token, organization, this.faker.Commerce.ProductName());
        return (owner, token, project, $"/v1/{project}");
    }
}
