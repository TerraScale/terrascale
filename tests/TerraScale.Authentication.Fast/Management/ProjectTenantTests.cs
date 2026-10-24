using System.Globalization;
using System.Net;
using Bogus;
using Microsoft.Extensions.DependencyInjection;
using TerraScale.Authentication.Contracts;
using TerraScale.Authentication.Features.Identity;
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
    public async Task Viewers_read_admins_disable_and_outsiders_see_nothing()
    {
        await using var oidc = await OidcHarness.StartAsync(fixture);
        var (owner, ownerToken, project, root) = await this.ProjectAsync(oidc);
        var (viewer, viewerToken) = await oidc.SignInUserAsync(this.Email(2));
        var (_, outsiderToken) = await oidc.SignInUserAsync(this.Email(3));
        await Assert.That(await oidc.GrantAsync(project, viewer.Id, "viewer")).IsTrue();
        var users = oidc.Server.App.Services.GetRequiredService<Users>();
        var sessions = oidc.Server.App.Services.GetRequiredService<Sessions>();
        var endUser = (await users.CreateAsync(project, this.Email(4)))!;
        var session = await sessions.CreateAsync(endUser);

        var found = await oidc.OkAsync(HttpMethod.Get, $"{root}/users?q={Uri.EscapeDataString(endUser.Email[..3])}", viewerToken);
        await Assert.That(found.GetProperty("items").EnumerateArray().Select(item => item.GetProperty("id").GetString()!).ToList()).IsEquivalentTo([endUser.Id]);
        await Assert.That((await oidc.OkAsync(HttpMethod.Get, $"{root}/users?q=no-such-address", viewerToken)).GetProperty("items").GetArrayLength()).IsEqualTo(0);
        var live = await oidc.OkAsync(HttpMethod.Get, $"{root}/sessions?user={endUser.Id}", viewerToken);
        await Assert.That(live.GetProperty("items")[0].GetProperty("email").GetString()).IsEqualTo(endUser.Email);

        // The platform's own users are not the tenant's; a viewer cannot write; an outsider and an unknown project get 404.
        await Assert.That(await oidc.FailAsync(HttpMethod.Get, $"{root}/users/{owner.Id}", viewerToken)).IsEqualTo((404, "not_found"));
        await Assert.That(await oidc.FailAsync(HttpMethod.Post, $"{root}/users/{endUser.Id}/disable", viewerToken)).IsEqualTo((403, "forbidden_role"));
        await Assert.That(await oidc.FailAsync(HttpMethod.Get, $"{root}/users", outsiderToken)).IsEqualTo((404, "not_found"));
        await Assert.That(await oidc.FailAsync(HttpMethod.Get, $"/v1/{AuthStore.NewId()}/users", ownerToken)).IsEqualTo((404, "not_found"));

        // Disabling ends the live session at once and refuses new ones; enabling lets the user sign in again.
        var disabled = await oidc.OkAsync(HttpMethod.Post, $"{root}/users/{endUser.Id}/disable", ownerToken);
        await Assert.That(disabled.GetProperty("disabled").GetBoolean()).IsTrue();
        await Assert.That(await sessions.ResolveAsync(project, session.Token)).IsNull();
        var current = (await users.GetAsync(project, endUser.Id))!;
        await Assert.That(async () => await sessions.CreateAsync(current)).Throws<ApiException>();
        await Assert.That((await oidc.OkAsync(HttpMethod.Get, $"{root}/sessions", viewerToken)).GetProperty("items").GetArrayLength()).IsEqualTo(0);
        await Assert.That((await oidc.OkAsync(HttpMethod.Post, $"{root}/users/{endUser.Id}/enable", ownerToken)).GetProperty("disabled").GetBoolean()).IsFalse();
        var renewed = await sessions.CreateAsync((await users.GetAsync(project, endUser.Id))!);

        // Session ids are canonical ULIDs; an admin revokes one by id and it stops resolving.
        var sessionId = (await oidc.OkAsync(HttpMethod.Get, $"{root}/sessions", viewerToken)).GetProperty("items")[0].GetProperty("id").GetString()!;
        await Assert.That(TerraScale.DotNet.TerraScaleIds.IsValid(sessionId)).IsTrue();
        using var revoked = await oidc.CallAsync(HttpMethod.Delete, $"{root}/sessions/{sessionId}", ownerToken);
        await Assert.That(revoked.StatusCode).IsEqualTo(HttpStatusCode.NoContent);
        await Assert.That(await sessions.ResolveAsync(project, renewed.Token)).IsNull();
    }

    [Test]
    public async Task Admins_register_clients_whose_secret_is_answered_once_and_change_settings()
    {
        await using var oidc = await OidcHarness.StartAsync(fixture);
        var (_, token, project, root) = await this.ProjectAsync(oidc);
        var body = new { name = this.faker.Company.CompanyName(), type = "confidential", redirect_uris = new[] { "https://app.example.test/callback", "http://localhost:3000/cb" } };
        var created = await oidc.OkAsync(HttpMethod.Post, $"{root}/clients", token, body, HttpStatusCode.Created);
        var clientId = created.GetProperty("client_id").GetString()!;
        var secret = created.GetProperty("client_secret").GetString()!;
        await Assert.That(secret.Length).IsEqualTo(48);

        // Listed without its secret, and stored only as OpenIddict's hash, in the project's instance.
        var listed = (await oidc.OkAsync(HttpMethod.Get, $"{root}/clients", token)).GetProperty("items");
        await Assert.That(listed.GetArrayLength()).IsEqualTo(1);
        await Assert.That(listed[0].TryGetProperty("client_secret", out _)).IsFalse();
        var stored = await oidc.Server.App.Services.GetRequiredService<AuthStore>().GetAsync(AuthStore.OidcApplicationCollection, project, clientId);
        await Assert.That(stored!.Document.GetRawText()).DoesNotContain(secret);
        await Assert.That(stored.Document.GetProperty("instanceId").GetString()).IsEqualTo(project);
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

    private string Email(int index) =>
        this.faker.Internet.Email(provider: "example.test", uniqueSuffix: index.ToString(CultureInfo.InvariantCulture)).ToLowerInvariant();

    // An owner signed in, with its sign-in token, an organization and a project.
    private async Task<(UserRecord Owner, string Token, string Project, string Root)> ProjectAsync(OidcHarness oidc)
    {
        var (owner, token) = await oidc.SignInUserAsync(this.Email(1));
        var organization = await oidc.CreateOrganizationAsync(token, this.faker.Company.CompanyName());
        var project = await oidc.CreateProjectAsync(token, organization, this.faker.Commerce.ProductName());
        return (owner, token, project, $"/v1/{project}");
    }
}
