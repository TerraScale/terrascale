using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using Bogus;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TerraScale.Authentication.Configuration;
using TerraScale.Authentication.Contracts;
using TerraScale.Authentication.Features.Identity;
using TerraScale.Authentication.Features.Platform;
using TerraScale.Authentication.Features.ProjectTenants;
using TerraScale.Authentication.Tests;

namespace TerraScale.Authentication.Fast.Management;

/// <summary>The project tenant management API (<c>/v1/{project}/…</c>) over real HTTP on the real TerraBase.</summary>
[ClassDataSource<TerraBaseFixture>(Shared = SharedType.PerTestSession)]
public sealed class ProjectTenantTests(TerraBaseFixture fixture)
{
    private static readonly string[] InsecureRedirect = ["http://app.example.test/cb"];
    private static readonly string[] CreateRedirect = ["https://app.example.test/callback"];
    private static readonly string[] UpdatedRedirects = ["https://app.example.test/changed", "http://localhost:3000/cb"];
    private static readonly string[] HostileRedirect = ["https://evil.example.test/callback"];

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
        var stored = await oidc.Database.ReadAsync(db => db.OidcApplications.SingleAsync(client => client.InstanceId == project && client.Id == clientId));
        await Assert.That(stored.ClientSecret).IsNotEqualTo(secret);
        await Assert.That(stored.InstanceId).IsEqualTo(project);
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

    [Test]
    public async Task Admins_update_a_clients_name_and_redirects_without_changing_its_client_id()
    {
        await using var oidc = await OidcHarness.StartSharedManagementAsync(fixture);
        var (_, token, _, root) = await this.ProjectAsync(oidc);
        var created = await oidc.OkAsync(HttpMethod.Post, $"{root}/clients", token,
            new { name = this.faker.Company.CompanyName(), type = "public", redirect_uris = CreateRedirect }, HttpStatusCode.Created);
        var clientId = created.GetProperty("client_id").GetString()!;

        var updated = await oidc.OkAsync(HttpMethod.Put, $"{root}/clients/{clientId}", token,
            new { name = "Renamed app", redirect_uris = UpdatedRedirects });
        await Assert.That(updated.GetProperty("client_id").GetString()).IsEqualTo(clientId);
        await Assert.That(updated.GetProperty("name").GetString()).IsEqualTo("Renamed app");
        await Assert.That(updated.GetProperty("redirect_uris").EnumerateArray().Select(uri => uri.GetString()!).ToArray())
            .IsEquivalentTo(UpdatedRedirects);
        await Assert.That(updated.TryGetProperty("client_secret", out _)).IsFalse();
        var listed = (await oidc.OkAsync(HttpMethod.Get, $"{root}/clients", token)).GetProperty("items");
        await Assert.That(listed[0].GetProperty("client_id").GetString()).IsEqualTo(clientId);
        await Assert.That(listed[0].GetProperty("redirect_uris").EnumerateArray().Select(uri => uri.GetString()!).ToArray())
            .IsEquivalentTo(UpdatedRedirects);
        await Assert.That(await oidc.FailAsync(HttpMethod.Put, $"{root}/clients/{clientId}", token,
            new { name = "Renamed app", redirect_uris = InsecureRedirect })).IsEqualTo((400, "invalid_request"));

        using var deleted = await oidc.CallAsync(HttpMethod.Delete, $"{root}/clients/{clientId}", token);
        await Assert.That(deleted.StatusCode).IsEqualTo(HttpStatusCode.NoContent);
    }

    [Test]
    public async Task Another_projects_admin_gets_404_updating_or_rotating_a_clients_secret()
    {
        await using var oidc = await OidcHarness.StartSharedManagementAsync(fixture);
        var (_, token, _, root) = await this.ProjectAsync(oidc);
        var created = await oidc.OkAsync(HttpMethod.Post, $"{root}/clients", token,
            new { name = this.faker.Company.CompanyName(), type = "confidential", redirect_uris = CreateRedirect }, HttpStatusCode.Created);
        var clientId = created.GetProperty("client_id").GetString()!;
        var (_, otherToken) = await oidc.CreateUserTokenAsync(oidc.UniqueEmail("person2"));

        using var renamed = await oidc.CallAsync(HttpMethod.Put, $"{root}/clients/{clientId}", otherToken,
            new { name = "Hostile rename", redirect_uris = HostileRedirect });
        await Assert.That(renamed.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
        using var rotated = await oidc.CallAsync(HttpMethod.Post, $"{root}/clients/{clientId}/secret/rotate", otherToken, new { grace_seconds = 60 });
        await Assert.That(rotated.StatusCode).IsEqualTo(HttpStatusCode.NotFound);

        using var deleted = await oidc.CallAsync(HttpMethod.Delete, $"{root}/clients/{clientId}", token);
        await Assert.That(deleted.StatusCode).IsEqualTo(HttpStatusCode.NoContent);
    }

    [Test]
    public async Task A_rotated_secret_keeps_working_only_through_its_grace()
    {
        var clock = new ManualClock(DateTimeOffset.UtcNow);
        await using var oidc = await OidcHarness.StartAsync(fixture, clock: clock);
        var (_, token, project, root) = await this.ProjectAsync(oidc);
        var created = await oidc.OkAsync(HttpMethod.Post, $"{root}/clients", token,
            new { name = this.faker.Company.CompanyName(), type = "confidential", redirect_uris = CreateRedirect }, HttpStatusCode.Created);
        var clientId = created.GetProperty("client_id").GetString()!;
        var first = created.GetProperty("client_secret").GetString()!;

        var rotated = await oidc.OkAsync(HttpMethod.Post, $"{root}/clients/{clientId}/secret/rotate", token, new { grace_seconds = 3600 });
        var second = rotated.GetProperty("client_secret").GetString()!;
        await Assert.That(second).IsNotEqualTo(first);
        await Assert.That(rotated.GetProperty("previous_secret_expires_at").GetDateTimeOffset()).IsEqualTo(clock.GetUtcNow().AddHours(1));

        var form = new Dictionary<string, string> { ["grant_type"] = "client_credentials" };
        await Assert.That((await oidc.PostTokenAsync(new Dictionary<string, string>(form), clientId, first, tenant: project)).StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        await Assert.That((await oidc.PostTokenAsync(new Dictionary<string, string>(form), clientId, second, tenant: project)).StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        using var refused = await oidc.PostTokenAsync(new Dictionary<string, string>(form), clientId, "wrong-secret", tenant: project);
        await Assert.That(refused.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);

        clock.Advance(TimeSpan.FromSeconds(3601));
        using var expired = await oidc.PostTokenAsync(new Dictionary<string, string>(form), clientId, first, tenant: project);
        await Assert.That(expired.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
        await Assert.That((await oidc.PostTokenAsync(new Dictionary<string, string>(form), clientId, second, tenant: project)).StatusCode).IsEqualTo(HttpStatusCode.BadRequest);

        using var deleted = await oidc.CallAsync(HttpMethod.Delete, $"{root}/clients/{clientId}", token);
        await Assert.That(deleted.StatusCode).IsEqualTo(HttpStatusCode.NoContent);
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
