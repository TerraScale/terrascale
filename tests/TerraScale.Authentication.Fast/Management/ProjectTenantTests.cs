using System.Globalization;
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
        var stored = await oidc.Server.App.Services.GetRequiredService<AuthStore>().GetAsync(AuthStore.OidcApplicationCollection, project, clientId);
        await Assert.That(stored!.Document.GetRawText()).DoesNotContain(secret);
        await Assert.That(stored.Document.GetProperty("instanceId").GetString()).IsEqualTo(project);
        await Assert.That(await oidc.FailAsync(HttpMethod.Post, $"{root}/clients", token, body with { redirect_uris = InsecureRedirect })).IsEqualTo((400, "invalid_request"));
        foreach (var invalid in new[]
        {
            "https://user@example.test/callback", "https://user:password@example.test/callback",
            "http://user@localhost:3000/cb", "http://user@127.0.0.1:3000/cb", "http://user@[::1]:3000/cb",
        })
        {
            await Assert.That(await oidc.FailAsync(HttpMethod.Post, $"{root}/clients", token,
                body with { redirect_uris = [invalid] })).IsEqualTo((400, "invalid_request"));
        }

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
    public async Task Admins_configure_social_providers_whose_secrets_are_sealed_and_never_answered()
    {
        await using var oidc = await OidcHarness.StartAsync(fixture);
        var (_, token, project, root) = await this.ProjectAsync(oidc);
        var (viewer, viewerToken) = await oidc.SignInUserAsync(this.Email(2));
        await Assert.That(await oidc.GrantAsync(project, viewer.Id, "viewer")).IsTrue();
        var secret = this.faker.Random.AlphaNumeric(40);

        // Every provider is listed with the callback to register, configured or not.
        var initial = (await oidc.OkAsync(HttpMethod.Get, $"{root}/settings", viewerToken)).GetProperty("providers");
        await Assert.That(initial.EnumerateArray().Select(item => item.GetProperty("provider").GetString()!).ToList()).IsEquivalentTo(["google", "github", "apple", "oidc"]);
        await Assert.That(initial[0].GetProperty("configured").GetBoolean()).IsFalse();
        await Assert.That(initial[0].GetProperty("callback_url").GetString()).IsEqualTo($"{OidcHarness.PublicOrigin}/ui/{project}/social/google/callback");

        await Assert.That(await oidc.FailAsync(HttpMethod.Put, $"{root}/providers/google", token, new { client_id = "id" })).IsEqualTo((400, "invalid_request"));
        await Assert.That(await oidc.FailAsync(HttpMethod.Put, $"{root}/providers/google", viewerToken, new { client_id = "id", client_secret = secret })).IsEqualTo((403, "forbidden_role"));
        await Assert.That(await oidc.FailAsync(HttpMethod.Put, $"{root}/providers/facebook", token, new { client_id = "id", client_secret = secret })).IsEqualTo((404, "not_found"));
        await Assert.That(await oidc.FailAsync(HttpMethod.Put, $"{root}/providers/oidc", token, new { client_id = "id", client_secret = secret, issuer = "http://idp.example.test" })).IsEqualTo((400, "invalid_request"));
        await Assert.That(await oidc.FailAsync(HttpMethod.Put, $"{root}/providers/apple", token, new { client_id = "tech.example.app", client_secret = "not a key", team_id = "ABCDE12345", key_id = "KEY1234567" })).IsEqualTo((400, "invalid_request"));

        var google = (await oidc.OkAsync(HttpMethod.Put, $"{root}/providers/google", token, new { client_id = "123.apps.example.test", client_secret = secret })).GetProperty("providers")[0];
        await Assert.That(google.GetProperty("configured").GetBoolean()).IsTrue();
        await Assert.That(google.GetProperty("client_id").GetString()).IsEqualTo("123.apps.example.test");
        await Assert.That(google.ToString()).DoesNotContain(secret);
        using var apple = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var configured = await oidc.OkAsync(HttpMethod.Put, $"{root}/providers/apple", token, new { client_id = "tech.example.app", client_secret = apple.ExportPkcs8PrivateKeyPem(), team_id = "ABCDE12345", key_id = "KEY1234567" });
        await Assert.That(configured.GetProperty("providers")[2].GetProperty("team_id").GetString()).IsEqualTo("ABCDE12345");

        // A change without client_secret keeps the sealed one; the stored document never holds it in clear.
        await oidc.OkAsync(HttpMethod.Put, $"{root}/providers/google", token, new { client_id = "456.apps.example.test" });
        var services = oidc.Server.App.Services;
        var stored = await services.GetRequiredService<AuthStore>().GetAsync(AuthStore.ProjectAuthSettingsCollection, project, project);
        await Assert.That(stored!.Document.GetRawText()).DoesNotContain(secret);
        var record = (await services.GetRequiredService<ProjectAuthSettings>().GetAsync(project)).Provider("google")!;
        await Assert.That(record.ClientId).IsEqualTo("456.apps.example.test");
        await Assert.That(SocialProviders.Open(services.GetRequiredService<AuthOptions>(), project, record)).IsEqualTo(secret);
        await Assert.That(() => SocialProviders.Open(services.GetRequiredService<AuthOptions>(), AuthStore.NewId(), record)).Throws<CryptographicException>();

        var removed = await oidc.OkAsync(HttpMethod.Delete, $"{root}/providers/google", token);
        await Assert.That(removed.GetProperty("providers")[0].GetProperty("configured").GetBoolean()).IsFalse();
        await Assert.That(removed.GetProperty("providers")[2].GetProperty("configured").GetBoolean()).IsTrue();
    }

    [Test]
    public async Task Project_branding_and_configuration_are_validated_persisted_and_isolated()
    {
        await using var oidc = await OidcHarness.StartAsync(fixture);
        var (_, token, project, root) = await this.ProjectAsync(oidc);
        var (viewer, viewerToken) = await oidc.SignInUserAsync(this.Email(2));
        await Assert.That(await oidc.GrantAsync(project, viewer.Id, "viewer")).IsTrue();
        var branding = new
        {
            name = "Paperwork",
            logo_url = "https://assets.example.test/logo.svg",
            theme_color = "#A1B2C3",
            title_size = 30,
            default_theme = "dark",
            background_color = "#151515",
            text_color = "#FAFAFA",
            footer_links = new[] { new { label = "Privacy", url = "https://example.test/privacy" } },
        };
        var configuration = new { redirect_urls = new[] { "https://app.example.test/callback" }, session_lifetime_seconds = 3600 };
        await Assert.That(await oidc.FailAsync(HttpMethod.Put, $"{root}/settings", viewerToken, new { branding })).IsEqualTo((403, "forbidden_role"));
        await oidc.OkAsync(HttpMethod.Put, $"{root}/settings", token, new { branding, configuration });
        // A separate read and unrelated methods update retain the committed theme and policy.
        var answer = await oidc.OkAsync(HttpMethod.Put, $"{root}/settings", token, new { methods = new { sign_up = false } });
        await Assert.That(answer.GetProperty("branding").GetProperty("title_size").GetInt32()).IsEqualTo(30);
        await Assert.That(answer.GetProperty("branding").GetProperty("text_color").GetString()).IsEqualTo("#fafafa");
        await Assert.That(answer.GetProperty("configuration").GetProperty("session_lifetime_seconds").GetInt32()).IsEqualTo(3600);
        await Assert.That((await oidc.OkAsync(HttpMethod.Get, $"{root}/settings", viewerToken)).GetProperty("branding").GetProperty("footer_links")[0].GetProperty("label").GetString()).IsEqualTo("Privacy");
        foreach (var invalid in new object[]
        {
            new { branding = branding with { title_size = 19 } },
            new { branding = branding with { default_theme = "invalid" } },
            new { branding = branding with { theme_color = "#123456\n" } },
            new { branding = branding with { logo_url = "javascript:alert(1)" } },
            new { branding = new { email_message = new string('x', 501) } },
            new { branding = new { email_message = "unsafe\rcontrol" } },
            new { branding = branding with { footer_links = [new { label = "Unsafe", url = "javascript:alert(1)" }] } },
            new { configuration = configuration with { session_lifetime_seconds = 299 } },
            new { configuration = configuration with { redirect_urls = ["https://example.test/cb#fragment"] } },
            new { configuration = configuration with { redirect_urls = ["https://user:password@example.test/cb"] } },
        })
        {
            await Assert.That(await oidc.FailAsync(HttpMethod.Put, $"{root}/settings", token, invalid)).IsEqualTo((400, "invalid_request"));
        }

        var other = await oidc.CreateProjectAsync(token, await oidc.CreateOrganizationAsync(token, this.faker.Company.CompanyName()), this.faker.Commerce.ProductName());
        var untouched = await oidc.OkAsync(HttpMethod.Get, $"/v1/{other}/settings", token);
        await Assert.That(untouched.GetProperty("branding").GetProperty("title_size").ValueKind).IsEqualTo(System.Text.Json.JsonValueKind.Null);
        await Assert.That(untouched.GetProperty("configuration").GetProperty("session_lifetime_seconds").GetInt32()).IsEqualTo(1209600);
        await Assert.That((await oidc.Server.App.Services.GetRequiredService<ProjectAuthSettings>().GetAsync(project)).TitleSize).IsEqualTo(30);
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
