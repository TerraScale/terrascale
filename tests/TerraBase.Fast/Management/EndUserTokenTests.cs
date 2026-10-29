using System.Net;
using Microsoft.IdentityModel.Tokens;
using TerraBase.Primitives;
using TerraBase.Testing;
using static TerraBase.Testing.ManagementApi;

namespace TerraBase.Fast.Management;

public sealed class EndUserTokenTests
{
    private static readonly string[] AdminRoles = ["admin"];

    [Test]
    public async Task Expired_and_tampered_end_user_tokens_are_denied_after_a_valid_token_warms_the_cache()
    {
        await using var api = await ManagementApi.StartAsync();
        var database = await api.CreateManagedDatabaseAsync("main");
        var documents = Uri(database.Data + "/collections/items/documents");
        using (var project = api.WithToken(api.Issuer.Mint(api.User, token =>
        {
            token.Claims["project_id"] = database.Project;
            token.Claims["scope"] = "databases:read documents:write";
        })))
        {
            await CreateCollectionAsync(project, database, "items");
        }

        var valid = Token(api, database.Project);
        using var authorized = api.WithToken(valid);
        using var before = await authorized.GetAsync(documents);
        await Assert.That(before.StatusCode).IsEqualTo(HttpStatusCode.Forbidden);

        var now = DateTime.UtcNow;
        var expired = Token(api, database.Project, token =>
        {
            token.IssuedAt = now.AddMinutes(-20);
            token.NotBefore = now.AddMinutes(-20);
            token.Expires = now.AddMinutes(-2);
        });
        var signature = valid.LastIndexOf('.') + 1;
        var tampered = string.Concat(valid.AsSpan(0, signature), valid[signature] == 'A' ? "B" : "A", valid.AsSpan(signature + 1));
        foreach (var rejected in new[] { expired, tampered })
        {
            using var client = api.WithToken(rejected);
            using var response = await client.GetAsync(documents);
            await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
            await Assert.That(response.Headers.ETag).IsNull();
        }

        using var after = await authorized.GetAsync(documents);
        await Assert.That(after.StatusCode).IsEqualTo(HttpStatusCode.Forbidden);
    }

    [Test]
    public async Task End_users_cannot_use_SQL_management_or_collection_metadata_even_with_an_admin_role()
    {
        await using var api = await ManagementApi.StartAsync();
        var project = EntityIds.New().ToString();
        using var client = api.WithToken(Token(api, project));
        foreach (var path in new[]
        {
            "/v1/management/organizations", $"/v1/projects/{project}/databases",
            $"/v1/projects/{project}/databases/main/collections",
            $"/v1/projects/{project}/databases/main/collections/items/schema",
            $"/v1/projects/{project}/databases/main/query-shapes",
        })
        {
            using var response = await client.GetAsync(Uri(path));
            await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Forbidden).Because(path);
        }

        using var sql = await client.PostAsJsonAsync(Uri($"/v1/projects/{project}/databases/main/sql"), new { sql = "SELECT 1" });
        await Assert.That(sql.StatusCode).IsEqualTo(HttpStatusCode.Forbidden);
    }

    [Test]
    public async Task End_user_linkage_kind_roles_and_guest_status_fail_closed()
    {
        await using var api = await ManagementApi.StartAsync();
        var project = EntityIds.New().ToString();
        Action<SecurityTokenDescriptor>[] invalid =
        [
            token => token.Claims["instance_id"] = EntityIds.New().ToString(),
            token => token.Claims.Remove("instance_id"),
            token => token.Claims.Remove("token_kind"),
            token => token.Claims["token_kind"] = "project",
            token => token.Claims["project_id"] = new[] { project },
            token => token.Claims["roles"] = "admin",
            token => token.Claims["roles"] = new object[] { "reader", 7 },
            token => token.Claims["roles"] = new[] { "" },
            token => token.Claims["scope"] = "databases:write documents:write",
            token => token.Claims["database"] = EntityIds.New().ToString(),
            token => token.Claims["platform_role"] = "admin",
            token => token.Claims["is_anonymous"] = true,
            token => token.Claims["is_anonymous"] = "false",
            token => token.Claims["roles"] = new[] { "reader\n" },
            token => token.Issuer = "https://untrusted.test/terrascale",
            token => token.Audience = "https://untrusted.test",
            token => token.TokenType = "JWT",
        ];
        foreach (var edit in invalid)
        {
            using var client = api.WithToken(Token(api, project, edit));
            using var response = await client.GetAsync(Uri($"/v1/projects/{project}/databases/main/collections/items/documents"));
            await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
        }

        using var anonymous = api.WithToken(null);
        using var denied = await anonymous.GetAsync(Uri($"/v1/projects/{project}/databases/main/collections/items/documents"));
        await Assert.That(denied.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
    }

    [Test]
    public async Task Valid_end_users_stay_in_one_project_and_cannot_enroll_a_project()
    {
        await using var api = await ManagementApi.StartAsync();
        var project = EntityIds.New().ToString();
        using var client = api.WithToken(Token(api, project));
        using var other = await client.GetAsync(Uri($"/v1/projects/{EntityIds.New()}/databases/main/collections/items/documents"));
        await Assert.That(other.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
        using var create = await client.PostAsJsonAsync(Uri($"/v1/projects/{project}/databases"), new { name = "main" });
        await Assert.That(create.StatusCode).IsEqualTo(HttpStatusCode.Forbidden);
        using var missing = await client.GetAsync(Uri($"/v1/projects/{project}/databases/main/collections/items/documents"));
        await Assert.That(missing.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
    }

    [Test]
    public async Task An_existing_project_grant_still_works_while_an_end_user_without_rules_is_denied_on_repeated_requests()
    {
        await using var api = await ManagementApi.StartAsync();
        var database = await api.CreateManagedDatabaseAsync("main");
        using var project = api.WithToken(api.Issuer.Mint(api.User, token =>
        {
            token.Claims["project_id"] = database.Project;
            token.Claims["scope"] = "databases:read documents:read documents:write";
        }));
        await CreateCollectionAsync(project, database, "items");
        var documents = Uri(database.Data + "/collections/items/documents");
        using var created = await project.PostAsJsonAsync(documents, new { userId = api.User });
        await Assert.That(created.StatusCode).IsEqualTo(HttpStatusCode.Created);
        using var listed = await project.GetAsync(documents);
        await Assert.That(listed.StatusCode).IsEqualTo(HttpStatusCode.OK);

        using var user = api.WithToken(Token(api, database.Project));
        for (var attempt = 0; attempt < 2; attempt++)
        {
            using var denied = await user.GetAsync(documents);
            await Assert.That(denied.StatusCode).IsEqualTo(HttpStatusCode.Forbidden);
        }
    }

    private static string Token(ManagementApi api, string project, Action<SecurityTokenDescriptor>? edit = null) =>
        api.Issuer.Mint(EntityIds.New().ToString(), token =>
        {
            token.Claims["token_kind"] = "end_user";
            token.Claims["project_id"] = project;
            token.Claims["instance_id"] = project;
            token.Claims["roles"] = AdminRoles;
            edit?.Invoke(token);
        });
}
