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

namespace TerraScale.Authentication.Slow.Management;

/// <summary>The project tenant management API (<c>/v1/{project}/…</c>) over real HTTP on the real TerraBase.</summary>
[ClassDataSource<TerraBaseFixture>(Shared = SharedType.PerTestSession)]
public sealed class ProjectTenantTests(TerraBaseFixture fixture)
{

    private readonly Faker faker = new();

    [Test]
    public async Task Served_project_schemas_match_snake_case_HTTP_bodies_and_camel_case_is_rejected()
    {
        await using var oidc = await OidcHarness.StartAsync(fixture);
        var (_, token, project, root) = await this.ProjectAsync(oidc);
        using var response = await oidc.Client.GetAsync("/openapi/v1.json");
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        var document = JsonSerializer.Deserialize<JsonElement>(await response.Content.ReadAsStringAsync());
        var schemas = document.GetProperty("components").GetProperty("schemas");
        foreach (var (name, fields) in new (string, string)[]
        {
            ("CreateClientRequest", "name type redirect_uris"),
            ("ClientBody", "client_id name type redirect_uris created_at client_secret"),
            ("ClientPageBody", "items next"),
            ("SettingsBody", "project_id issuer discovery_url sign_in_url methods branding counts providers configuration"),
            ("UpdateSettingsRequest", "methods branding configuration"),
            ("MethodsBody", "passcode password sign_up"),
            ("MethodsPatch", "passcode password sign_up"),
            ("BrandingBody", "name logo_url theme_color title_size default_theme footer_links background_color text_color email_message"),
            ("FooterLinkBody", "label url"),
            ("ConfigurationBody", "redirect_urls session_lifetime_seconds"),
            ("CountsBody", "users clients"),
            ("ProviderBody", "provider configured client_id issuer team_id key_id callback_url"),
            ("SetProviderRequest", "client_id client_secret issuer team_id key_id"),
            ("ProjectUserBody", "id email created_at disabled"),
            ("ProjectUserPageBody", "items next"),
            ("ProjectSessionBody", "id user_id email created_at expires_at"),
            ("ProjectSessionPageBody", "items next"),
            ("DirectoryPageBody", "items next created_before"),
            ("DirectoryUserBody", "id email email_verified created_at disabled linked_providers"),
            ("DirectoryProviderBody", "provider client_id subject"),
            // Other auth wire conventions must stay intact.
            ("EmailAdmissionResponse", "status sendBy"),
            ("TokenBody", "access_token token_type expires_in scope id_token refresh_token"),
        })
        {
            var schema = schemas.GetProperty(name);
            await Assert.That(schema.GetProperty("properties").EnumerateObject().Select(property => property.Name).ToArray())
                .IsEquivalentTo(fields.Split(' '));
            if (schema.TryGetProperty("required", out var required))
            {
                await Assert.That(required.EnumerateArray().All(field => schema.GetProperty("properties").TryGetProperty(field.GetString()!, out _))).IsTrue();
            }
        }

        var paths = document.GetProperty("paths");
        var create = paths.GetProperty("/v1/{project}/clients").GetProperty("post");
        var request = new { name = "Customer app", type = "confidential", redirect_uris = new[] { "https://app.example.test/callback" } };
        await SchemaMatchesAsync(schemas, create.GetProperty("requestBody").GetProperty("content").GetProperty("application/json").GetProperty("schema"),
            JsonSerializer.SerializeToElement(request));
        var client = await oidc.OkAsync(HttpMethod.Post, $"{root}/clients", token, request, HttpStatusCode.Created);
        await SchemaMatchesAsync(schemas, ResponseSchema(create, "201"), client);
        await Assert.That(client.GetProperty("redirect_uris")[0].GetString()).IsEqualTo(request.redirect_uris[0]);
        await Assert.That(client.GetProperty("client_secret").GetString()).IsNotNull();
        await Assert.That(await oidc.FailAsync(HttpMethod.Post, $"{root}/clients", token,
            new { request.name, request.type, request.redirect_uris, redirectUris = request.redirect_uris })).IsEqualTo((400, "invalid_request"));

        // A renamed parent (linked_providers) must not leave its nested provider schema in camelCase.
        await oidc.Server.App.Services.GetRequiredService<Users>().FromProviderAsync(project,
            new LinkedProvider("oidc", "https://idp.example.test", "public-client", "verified-subject"), this.Email(2), true, default);
        var directory = await oidc.OkAsync(HttpMethod.Get, $"{root}/users/export", token);
        await Assert.That(directory.GetProperty("items").GetArrayLength()).IsEqualTo(1);
        await Assert.That(directory.ToString()).DoesNotContain("\"issuer\"");
        await Assert.That(directory.GetProperty("items")[0].GetProperty("linked_providers")[0].GetProperty("client_id").GetString()).IsEqualTo("public-client");
        await SchemaMatchesAsync(schemas, ResponseSchema(paths.GetProperty("/v1/{project}/users/export").GetProperty("get"), "200"), directory);

        var update = new
        {
            methods = new { sign_up = false },
            branding = new { logo_url = "https://assets.example.test/logo.svg", theme_color = "#a1b2c3", footer_links = new[] { new { label = "Privacy", url = "https://example.test/privacy" } } },
            configuration = new { redirect_urls = request.redirect_uris, session_lifetime_seconds = 3600 },
        };
        var settingsPath = paths.GetProperty("/v1/{project}/settings");
        await SchemaMatchesAsync(schemas, settingsPath.GetProperty("put").GetProperty("requestBody").GetProperty("content").GetProperty("application/json").GetProperty("schema"),
            JsonSerializer.SerializeToElement(update));
        var changed = await oidc.OkAsync(HttpMethod.Put, $"{root}/settings", token, update);
        await SchemaMatchesAsync(schemas, ResponseSchema(settingsPath.GetProperty("put"), "200"), changed);
        var settings = await oidc.OkAsync(HttpMethod.Get, $"{root}/settings", token);
        await SchemaMatchesAsync(schemas, ResponseSchema(settingsPath.GetProperty("get"), "200"), settings);
        await Assert.That(settings.GetProperty("methods").GetProperty("sign_up").GetBoolean()).IsFalse();
        await Assert.That(settings.GetProperty("branding").GetProperty("logo_url").GetString()).IsEqualTo(update.branding.logo_url);
        await Assert.That(settings.GetProperty("configuration").GetProperty("session_lifetime_seconds").GetInt32()).IsEqualTo(3600);
        foreach (var invalid in new object[]
        {
            new { methods = new { signUp = true } },
            new { branding = new { logoUrl = update.branding.logo_url } },
            new { configuration = new { redirect_urls = request.redirect_uris, sessionLifetimeSeconds = 3600 } },
        })
        {
            await Assert.That(await oidc.FailAsync(HttpMethod.Put, $"{root}/settings", token, invalid)).IsEqualTo((400, "invalid_request"));
        }
    }

    [Test]
    public async Task Directory_export_is_bounded_resumable_admin_only_and_audited_without_credentials()
    {
        await using var oidc = await OidcHarness.StartAsync(fixture);
        var (owner, token, project, root) = await this.ProjectAsync(oidc);
        var (viewer, viewerToken) = await oidc.SignInUserAsync(this.Email(2));
        var (_, outsiderToken) = await oidc.SignInUserAsync(this.Email(3));
        await Assert.That(await oidc.GrantAsync(project, viewer.Id, "viewer")).IsTrue();
        var users = oidc.Server.App.Services.GetRequiredService<Users>();
        var linked = (await users.FromProviderAsync(project, new LinkedProvider("oidc", "https://idp.example.test", "public-client", "verified-subject"), this.Email(4), true, default))!;
        var formula = (await users.CreateAsync(project, "+formula@example.test", "credential-must-never-leak", default))!;
        var third = (await users.CreateAsync(project, this.Email(5)))!;
        var first = await oidc.OkAsync(HttpMethod.Get, $"{root}/users/export?limit=1", token);
        var next = first.GetProperty("next").GetString()!;
        var resumed = await oidc.OkAsync(HttpMethod.Get, $"{root}/users/export?limit=1&after={Uri.EscapeDataString(next)}", token);
        var replayed = await oidc.OkAsync(HttpMethod.Get, $"{root}/users/export?limit=1&after={Uri.EscapeDataString(next)}", token);
        await Assert.That(replayed.ToString()).IsEqualTo(resumed.ToString());
        // A new account after the first page's cutoff never joins this export.
        await users.CreateAsync(project, this.Email(6));
        var last = await oidc.OkAsync(HttpMethod.Get, $"{root}/users/export?limit=1&after={Uri.EscapeDataString(resumed.GetProperty("next").GetString()!)}", token);
        await Assert.That(last.GetProperty("next").ValueKind).IsEqualTo(JsonValueKind.Null);
        var records = new[] { first, resumed, last }.Select(page => page.GetProperty("items")[0]).ToArray();
        await Assert.That(records.Select(item => item.GetProperty("id").GetString()!).ToList()).IsEquivalentTo([linked.Id, formula.Id, third.Id]);
        foreach (var record in records)
        {
            await Assert.That(record.EnumerateObject().Select(property => property.Name).ToList()).IsEquivalentTo(["id", "email", "email_verified", "created_at", "disabled", "linked_providers"]);
            await Assert.That(record.GetProperty("email_verified").GetBoolean()).IsTrue();
        }

        var identity = records.Single(record => record.GetProperty("id").GetString() == linked.Id).GetProperty("linked_providers")[0];
        await Assert.That(identity.GetProperty("subject").GetString()).IsEqualTo("verified-subject");
        await Assert.That(identity.EnumerateObject().Select(property => property.Name).ToList()).IsEquivalentTo(["provider", "client_id", "subject"]);
        using var csv = await oidc.CallAsync(HttpMethod.Get, $"{root}/users/export?format=csv&limit=1", token);
        await Assert.That(csv.Content.Headers.ContentType!.MediaType).IsEqualTo("text/csv");
        await Assert.That(csv.Headers.Contains("X-Export-Next")).IsTrue();
        using var allCsv = await oidc.CallAsync(HttpMethod.Get, $"{root}/users/export?format=csv", token);
        var text = await allCsv.Content.ReadAsStringAsync();
        await Assert.That(text).Contains("\"'+formula@example.test\"");
        await Assert.That(text).DoesNotContain("credential-must-never-leak");
        await Assert.That(allCsv.Headers.CacheControl!.NoStore).IsTrue();
        await Assert.That(await oidc.FailAsync(HttpMethod.Get, $"{root}/users/export", viewerToken)).IsEqualTo((403, "forbidden_role"));
        await Assert.That(await oidc.FailAsync(HttpMethod.Get, $"{root}/users/export", outsiderToken)).IsEqualTo((404, "not_found"));
        await Assert.That(await oidc.FailAsync(HttpMethod.Get, "/v1/terrascale/users/export", token)).IsEqualTo((404, "not_found"));
        await Assert.That(await oidc.FailAsync(HttpMethod.Get, $"{root}/users/export?after=invalid", token)).IsEqualTo((400, "invalid_request"));
        await Assert.That(await oidc.FailAsync(HttpMethod.Get, $"{root}/users/export?format=xml", token)).IsEqualTo((400, "invalid_request"));
        await Assert.That(await oidc.FailAsync(HttpMethod.Get, $"{root}/users/export?limit=101", token)).IsEqualTo((400, "invalid_request"));
        var other = await oidc.CreateProjectAsync(token, await oidc.CreateOrganizationAsync(token, this.faker.Company.CompanyName()), this.faker.Commerce.ProductName());
        await Assert.That(await oidc.FailAsync(HttpMethod.Get, $"/v1/{other}/users/export?after={Uri.EscapeDataString(next)}", token)).IsEqualTo((400, "invalid_request"));
        var organization = (await oidc.Server.App.Services.GetRequiredService<PlatformDirectory>().ProjectAsync(project, default))!.Value.OrganizationId;
        var audit = await oidc.OkAsync(HttpMethod.Get, $"/v1/terrascale/organizations/{organization}/audit-events", token);
        await Assert.That(audit.GetProperty("items").EnumerateArray().Any(item => item.GetProperty("action").GetString() == "project.users_exported"
            && item.GetProperty("actor").GetString() == owner.Id && item.GetProperty("target").GetString() == project)).IsTrue();
    }

    private static JsonElement ResponseSchema(JsonElement operation, string status) =>
        operation.GetProperty("responses").GetProperty(status).GetProperty("content").GetProperty("application/json").GetProperty("schema");

    // Follow the served references, including nullable nested DTOs and array elements.
    private static async Task SchemaMatchesAsync(JsonElement schemas, JsonElement schema, JsonElement body)
    {
        if (body.ValueKind == JsonValueKind.Null)
        {
            return;
        }

        if (schema.TryGetProperty("$ref", out var reference))
        {
            schema = schemas.GetProperty(reference.GetString()!.Split('/')[^1]);
        }

        if (schema.TryGetProperty("oneOf", out var alternatives))
        {
            await SchemaMatchesAsync(schemas, alternatives.EnumerateArray().Single(item => !item.TryGetProperty("type", out var type) || type.GetString() != "null"), body);
            return;
        }

        if (body.ValueKind == JsonValueKind.Object)
        {
            var properties = schema.GetProperty("properties");
            foreach (var property in body.EnumerateObject())
            {
                await Assert.That(properties.TryGetProperty(property.Name, out _)).IsTrue();
                await SchemaMatchesAsync(schemas, properties.GetProperty(property.Name), property.Value);
            }
        }
        else if (body.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in body.EnumerateArray())
            {
                await SchemaMatchesAsync(schemas, schema.GetProperty("items"), item);
            }
        }
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
