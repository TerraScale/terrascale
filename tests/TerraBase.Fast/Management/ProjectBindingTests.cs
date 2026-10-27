using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Bogus;
using Microsoft.Extensions.DependencyInjection;
using TerraBase.Engine;
using TerraBase.Primitives;
using TerraBase.Testing;

namespace TerraBase.Fast.Management;

public sealed class ProjectBindingTests
{
    private const string Scopes = "databases:read databases:write documents:read documents:write";

    [Test]
    public async Task Verification_without_response_memory_is_refused_before_forwarding_the_sign_in_token()
    {
        await using var api = await ManagementApi.StartAsync(settings => settings["memory-budget"] = "1048576");
        var database = await api.CreateManagedDatabaseAsync();
        var token = api.Issuer.Mint(api.User);
        api.Issuer.GrantProject(token, database.Organization, database.Project);
        using var owner = api.WithToken(token);
        await BindingStatusAsync(owner, database.Organization, database.Project, database.Organization, HttpStatusCode.ServiceUnavailable);
        await Assert.That(api.Issuer.ProjectFetches).IsEqualTo(0);
        using var jwt = api.WithToken(ProjectToken(api.Issuer, api.User, database.Project));
        await InaccessibleAsync(jwt, database);
        await Assert.That((await JsonAsync(api.Client.GetAsync(Uri(database.Path)))).GetProperty("id").GetString()).IsEqualTo(database.Id);
    }

    [Test]
    public async Task A_legacy_project_registered_with_a_victims_UUID_cannot_capture_the_victims_JWT_data()
    {
        var faker = new Faker();
        await using var api = await ManagementApi.StartAsync();
        var organization = await api.CreateOrganizationAsync(faker.Company.CompanyName());
        var project = EntityIds.New().ToString();
        var operation = new { op = "create_project", organization, id = project, name = faker.Company.CompanyName() };
        using (var denied = await api.Client.PostAsJsonAsync(Uri("/v1/management/commits"), new
        {
            id = EntityIds.New().ToString(),
            ops = new[] { operation },
        }))
        {
            await Assert.That(denied.StatusCode).IsEqualTo(HttpStatusCode.Forbidden);
        }

        using (var absent = await api.Client.GetAsync(Uri(ProjectPath(organization, project))))
        {
            await Assert.That(absent.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
        }

        // The service seeds the chosen ID supported by pre-upgrade public commits, without inventing a verified binding.
        using (var service = api.Service(api.User))
        using (var seeded = await service.PostAsJsonAsync(Uri("/v1/management/commits"), new
        {
            id = EntityIds.New().ToString(),
            ops = new[] { operation },
        }))
        {
            await Assert.That(seeded.StatusCode).IsEqualTo(HttpStatusCode.OK);
        }

        var database = await api.CreateManagedDatabaseAsync(organization, project, "main");
        using var legacy = await api.CreateClientAsync(organization, "documents:read", "documents:write");
        await ManagementApi.CreateCollectionAsync(legacy, database, "people");
        var document = ManagementApi.DocumentPath(database, "people", "saved");
        var name = faker.Name.FullName();
        using var written = await legacy.PutAsJsonAsync(Uri(document), new { name });
        await Assert.That(written.StatusCode).IsEqualTo(HttpStatusCode.Created);

        var authorityOrganization = EntityIds.New().ToString();
        var victim = EntityIds.New().ToString();
        var victimSignIn = api.Issuer.Mint(victim);
        api.Issuer.GrantProject(victimSignIn, authorityOrganization, project);
        using var victimOwner = api.WithToken(victimSignIn);
        using var attackerOwner = api.WithToken(api.Issuer.Mint(api.User));
        await BindingStatusAsync(attackerOwner, organization, project, authorityOrganization, HttpStatusCode.NotFound);
        await BindingStatusAsync(victimOwner, organization, project, authorityOrganization, HttpStatusCode.NotFound);
        await Assert.That(api.Issuer.ProjectFetches).IsEqualTo(1);

        using var jwt = api.WithToken(ProjectToken(api.Issuer, victim, project));
        await InaccessibleAsync(jwt, database);
        await StatusAsync(jwt.GetAsync(Uri(document)), HttpStatusCode.NotFound);
        await StatusAsync(jwt.PutAsJsonAsync(Uri(document), new { name = "captured" }), HttpStatusCode.NotFound);
        await Assert.That((await JsonAsync(legacy.GetAsync(Uri(document)))).GetProperty("document").GetProperty("name").GetString()).IsEqualTo(name);
        await Assert.That((await JsonAsync(api.Client.GetAsync(Uri(database.Path)))).GetProperty("id").GetString()).IsEqualTo(database.Id);
        await Assert.That((await JsonAsync(api.Client.GetAsync(Uri(database.ProjectPath + "/databases")))).GetProperty("items").GetArrayLength()).IsEqualTo(1);
    }

    [Test]
    [Arguments(8 * 1024 * 1024)]
    [Arguments(16 * 1024 * 1024)]
    [Arguments(48 * 1024 * 1024)]
    public async Task Only_a_legacy_owner_with_live_ts_auth_admin_authority_verifies_existing_data_without_rewriting_it(int memoryBudget)
    {
        var faker = new Faker();
        await using var api = await ManagementApi.StartAsync(settings => settings["memory-budget"] = memoryBudget.ToString(CultureInfo.InvariantCulture));
        var database = await api.CreateManagedDatabaseAsync();
        using var legacy = await api.CreateClientAsync(database.Organization, "documents:read", "documents:write");
        await ManagementApi.CreateCollectionAsync(legacy, database, "people");
        var document = ManagementApi.DocumentPath(database, "people", "saved");
        var name = faker.Name.FullName();
        using var written = await legacy.PutAsJsonAsync(Uri(document), new { name });
        await Assert.That(written.StatusCode).IsEqualTo(HttpStatusCode.Created);
        using var jwt = api.WithToken(ProjectToken(api.Issuer, EntityIds.New().ToString(), database.Project));
        await InaccessibleAsync(jwt, database);

        // A separate ts-auth organization proves the binding does not depend on equal legacy/auth organization IDs.
        var authorityOrganization = EntityIds.New().ToString();
        var signIn = api.Issuer.Mint(api.User);
        api.Issuer.GrantProject(signIn, authorityOrganization, database.Project);
        using var owner = api.WithToken(signIn);
        await BindingStatusAsync(owner, database.Organization, database.Project, authorityOrganization, HttpStatusCode.NoContent);
        await BindingStatusAsync(owner, database.Organization, database.Project, authorityOrganization, HttpStatusCode.NoContent);
        await Assert.That(api.Issuer.ProjectFetches).IsEqualTo(2);
        using var read = await jwt.GetAsync(Uri(document));
        await Assert.That(read.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(read.Headers.ETag?.Tag).IsEqualTo(written.Headers.ETag?.Tag);
        await Assert.That((await read.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("document").GetProperty("name").GetString()).IsEqualTo(name);
        await Assert.That((await JsonAsync(jwt.GetAsync(Uri(database.Data)))).GetProperty("id").GetString()).IsEqualTo(database.Id);
        await Assert.That((await JsonAsync(legacy.GetAsync(Uri(document)))).GetProperty("document").GetProperty("name").GetString()).IsEqualTo(name);
        await Assert.That((await JsonAsync(api.Client.GetAsync(Uri(database.Path)))).GetProperty("id").GetString()).IsEqualTo(database.Id);

        var otherAuthorityOrganization = EntityIds.New().ToString();
        api.Issuer.GrantProject(signIn, otherAuthorityOrganization, database.Project);
        await BindingStatusAsync(owner, database.Organization, database.Project, otherAuthorityOrganization, HttpStatusCode.Conflict);
        await StatusAsync(jwt.GetAsync(Uri(document)), HttpStatusCode.OK);
    }

    [Test]
    public async Task A_query_organization_naming_another_authority_does_not_replace_the_body_organization()
    {
        await using var api = await ManagementApi.StartAsync();
        var database = await api.CreateManagedDatabaseAsync();
        var authorityOrganization = EntityIds.New().ToString();
        var signIn = api.Issuer.Mint(api.User);
        api.Issuer.GrantProject(signIn, authorityOrganization, database.Project);
        using var owner = api.WithToken(signIn);
        var query = "/binding?organization=" + EntityIds.New();
        await StatusAsync(owner.PostAsJsonAsync(Uri(ProjectPath(database.Organization, database.Project) + query), new { organization = authorityOrganization }), HttpStatusCode.NoContent);
        await Assert.That(api.Issuer.ProjectFetches).IsEqualTo(1);
    }

    [Test]
    public async Task A_query_organization_does_not_substitute_for_a_missing_body_organization()
    {
        await using var api = await ManagementApi.StartAsync();
        var database = await api.CreateManagedDatabaseAsync();
        var authorityOrganization = EntityIds.New().ToString();
        var signIn = api.Issuer.Mint(api.User);
        api.Issuer.GrantProject(signIn, authorityOrganization, database.Project);
        using var owner = api.WithToken(signIn);
        var query = "/binding?organization=" + authorityOrganization;
        await StatusAsync(owner.PostAsJsonAsync(Uri(ProjectPath(database.Organization, database.Project) + query), new { }), HttpStatusCode.BadRequest);
        await Assert.That(api.Issuer.ProjectFetches).IsEqualTo(0);
    }

    [Test]
    [Arguments("malformed")]
    [Arguments("untyped")]
    public async Task An_unreadable_binding_body_answers_the_organization_validation_error_without_contacting_ts_auth(string body)
    {
        await using var api = await ManagementApi.StartAsync();
        var database = await api.CreateManagedDatabaseAsync();
        var signIn = api.Issuer.Mint(api.User);
        api.Issuer.GrantProject(signIn, database.Organization, database.Project);
        using var owner = api.WithToken(signIn);
        using var content = body == "malformed"
            ? new StringContent("{", Encoding.UTF8, "application/json")
            : new ByteArrayContent("{}"u8.ToArray());
        await StatusAsync(owner.PostAsync(Uri(ProjectPath(database.Organization, database.Project) + "/binding"), content), HttpStatusCode.BadRequest);
        await Assert.That(api.Issuer.ProjectFetches).IsEqualTo(0);
    }

    [Test]
    [Arguments("viewer")]
    [Arguments("member")]
    [Arguments("admin")]
    public async Task Ts_auth_project_admin_authority_does_not_replace_legacy_storage_ownership(string role)
    {
        await using var api = await ManagementApi.StartAsync();
        var database = await api.CreateManagedDatabaseAsync();
        var member = await api.AddMemberAsync(api.Client, database.Organization, role);
        var signIn = api.Issuer.Mint(member);
        api.Issuer.GrantProject(signIn, database.Organization, database.Project);
        using var caller = api.WithToken(signIn);

        await BindingStatusAsync(caller, database.Organization, database.Project, database.Organization, HttpStatusCode.NotFound);
        await Assert.That(api.Issuer.ProjectFetches).IsEqualTo(0);
        await Assert.That((await JsonAsync(caller.GetAsync(Uri(database.Path)))).GetProperty("id").GetString()).IsEqualTo(database.Id);
        using var jwt = api.WithToken(ProjectToken(api.Issuer, member, database.Project));
        await InaccessibleAsync(jwt, database);
    }

    [Test]
    [Arguments("role", HttpStatusCode.NotFound)]
    [Arguments("project", HttpStatusCode.NotFound)]
    [Arguments("organization", HttpStatusCode.NotFound)]
    [Arguments("unauthorized", HttpStatusCode.NotFound)]
    [Arguments("forbidden", HttpStatusCode.NotFound)]
    [Arguments("missing", HttpStatusCode.NotFound)]
    [Arguments("server", HttpStatusCode.ServiceUnavailable)]
    [Arguments("unavailable", HttpStatusCode.ServiceUnavailable)]
    [Arguments("oversized", HttpStatusCode.ServiceUnavailable)]
    public async Task Denied_mismatched_or_unavailable_ts_auth_authority_never_trusts_the_legacy_mapping(string response, HttpStatusCode expected)
    {
        var faker = new Faker();
        await using var api = await ManagementApi.StartAsync();
        var database = await api.CreateManagedDatabaseAsync();
        var authorityOrganization = EntityIds.New().ToString();
        var authority = new TestIssuer.ProjectAuthority(database.Project, authorityOrganization);
        authority = response switch
        {
            "role" => authority with { Role = "viewer" },
            "project" => authority with { Id = EntityIds.New().ToString() },
            "organization" => authority with { OrganizationId = EntityIds.New().ToString() },
            "unauthorized" => authority with { Status = HttpStatusCode.Unauthorized },
            "forbidden" => authority with { Status = HttpStatusCode.Forbidden },
            "missing" => authority with { Status = HttpStatusCode.NotFound },
            "server" => authority with { Status = HttpStatusCode.ServiceUnavailable },
            "unavailable" => authority with { Available = false },
            "oversized" => authority with { Role = new string('x', 64 * 1024) },
            _ => throw new ArgumentOutOfRangeException(nameof(response)),
        };
        var signIn = api.Issuer.Mint(api.User);
        api.Issuer.GrantProject(signIn, authorityOrganization, database.Project, authority);
        using var owner = api.WithToken(signIn);
        await BindingStatusAsync(owner, database.Organization, database.Project, authorityOrganization, expected);
        await Assert.That(api.Issuer.ProjectFetches).IsEqualTo(1);
        using var jwt = api.WithToken(ProjectToken(api.Issuer, api.User, database.Project));
        await InaccessibleAsync(jwt, database);
        await Assert.That((await JsonAsync(api.Client.GetAsync(Uri(database.Path)))).GetProperty("id").GetString()).IsEqualTo(database.Id);
    }

    [Test]
    [Arguments("issuer", HttpStatusCode.Unauthorized)]
    [Arguments("audience", HttpStatusCode.Unauthorized)]
    [Arguments("signature", HttpStatusCode.Unauthorized)]
    [Arguments("project", HttpStatusCode.NotFound)]
    public async Task Invalid_sign_in_tokens_and_restricted_project_tokens_cannot_verify_bindings(string failure, HttpStatusCode expected)
    {
        var faker = new Faker();
        await using var api = await ManagementApi.StartAsync();
        var database = await api.CreateManagedDatabaseAsync();
        var authorityOrganization = EntityIds.New().ToString();
        var unpublished = api.Issuer.AddKey(Microsoft.IdentityModel.Tokens.SecurityAlgorithms.EcdsaSha256, publish: false);
        var token = api.Issuer.Mint(api.User, descriptor =>
        {
            switch (failure)
            {
                case "issuer": descriptor.Issuer = "https://auth.test/other"; break;
                case "audience": descriptor.Audience = "https://other.test"; break;
                case "signature": descriptor.SigningCredentials = unpublished; break;
                case "project":
                    descriptor.Claims["project_id"] = database.Project;
                    descriptor.Claims["database"] = EntityIds.New().ToString();
                    descriptor.Claims["scope"] = Scopes;
                    break;
                default: throw new ArgumentOutOfRangeException(nameof(failure));
            }
        });
        api.Issuer.GrantProject(token, authorityOrganization, database.Project);
        using var caller = api.WithToken(token);
        await BindingStatusAsync(caller, database.Organization, database.Project, authorityOrganization, expected);
        // The operator's user header means nothing on the public listener.
        using var publicService = api.WithToken(null);
        publicService.DefaultRequestHeaders.Add("X-TerraBase-User-Id", api.User);
        await BindingStatusAsync(publicService, database.Organization, database.Project, authorityOrganization, HttpStatusCode.Unauthorized);
        using var adminService = api.Service(api.User);
        // Binding needs the user's own sign-in token; the operator cannot verify one.
        await BindingStatusAsync(adminService, database.Organization, database.Project, authorityOrganization, HttpStatusCode.Unauthorized);
        await Assert.That(api.Issuer.ProjectFetches).IsEqualTo(0);
        using var jwt = api.WithToken(ProjectToken(api.Issuer, api.User, database.Project));
        await InaccessibleAsync(jwt, database);
    }

    [Test]
    public async Task Project_authority_redirects_are_refused_by_the_live_TLS_transport_without_forwarding_the_bearer()
    {
        var faker = new Faker();
        using var issuer = new TestIssuer();
        using var tls = new ClientTls();
        await using var authorityServer = await issuer.ListenAsync();
        await using var server = await TerraBaseTestServer.StartAsync(settings =>
        {
            tls.Apply(settings);
            TestIssuer.Configure(settings);
        }, authorityServer.Route);
        var user = EntityIds.New().ToString();
        var token = issuer.Mint(user);
        using var owner = tls.CreateClient(server.ClientAddress!, token);
        var organization = (await JsonAsync(owner.PostAsJsonAsync(Uri("/v1/management/organizations"), new { name = faker.Company.CompanyName() }),
            HttpStatusCode.Created)).GetProperty("id").GetString()!;
        var project = (await JsonAsync(owner.PostAsJsonAsync(Uri($"/v1/management/organizations/{organization}/projects"), new { name = "Legacy" }),
            HttpStatusCode.Created)).GetProperty("id").GetString()!;
        var id = (await JsonAsync(owner.PostAsJsonAsync(Uri(ProjectPath(organization, project) + "/databases"), new { name = "main" }),
            HttpStatusCode.Created)).GetProperty("id").GetString()!;
        var database = new ManagedDatabaseRef(organization, project, id, "main");
        var authorityOrganization = EntityIds.New().ToString();
        var redirectedProject = EntityIds.New().ToString();
        var redirect = new Uri($"https://auth.test/v1/terrascale/organizations/{authorityOrganization}/projects/{redirectedProject}");
        issuer.GrantProject(token, authorityOrganization, project,
            new TestIssuer.ProjectAuthority(project, authorityOrganization, Status: HttpStatusCode.Found, Redirect: redirect));
        issuer.GrantProject(token, authorityOrganization, redirectedProject, new TestIssuer.ProjectAuthority(project, authorityOrganization));

        await BindingStatusAsync(owner, organization, project, authorityOrganization, HttpStatusCode.NotFound);
        await Assert.That(issuer.ProjectFetches).IsEqualTo(1);
        using var jwt = tls.CreateClient(server.ClientAddress!, ProjectToken(issuer, user, project));
        await InaccessibleAsync(jwt, database);
        await Assert.That((await JsonAsync(owner.GetAsync(Uri(database.Path)))).GetProperty("id").GetString()).IsEqualTo(database.Id);
    }

    [Test]
    [Arguments("valid")]
    [Arguments("issuer")]
    [Arguments("version")]
    [Arguments("organization")]
    public async Task Restart_trusts_only_a_binding_with_the_configured_issuer_supported_version_and_storage_organization(string persisted)
    {
        var directory = Directory.CreateTempSubdirectory("terrabase-project-binding-").FullName;
        using var issuer = new TestIssuer();
        var faker = new Faker();
        var user = EntityIds.New().ToString();
        var signIn = issuer.Mint(user);
        ManagedDatabaseRef database;
        string document;
        string? etag;
        var name = faker.Name.FullName();
        try
        {
            await using (var api = await ManagementApi.StartAsync(settings => settings["data-directory"] = directory, issuer.Route))
            {
                using var owner = api.WithToken(signIn);
                var organization = await api.CreateOrganizationAsync(faker.Company.CompanyName(), owner);
                var project = await api.CreateProjectAsync(organization, faker.Company.CompanyName(), owner);
                database = new ManagedDatabaseRef(organization, project, await api.CreateDatabaseAsync(organization, project, "main", owner), "main");
                using var legacy = await api.CreateClientAsync(organization, "documents:read", "documents:write");
                await ManagementApi.CreateCollectionAsync(legacy, database, "people");
                document = ManagementApi.DocumentPath(database, "people", "saved");
                using var written = await legacy.PutAsJsonAsync(Uri(document), new { name });
                await Assert.That(written.StatusCode).IsEqualTo(HttpStatusCode.Created);
                etag = written.Headers.ETag?.Tag;
                issuer.GrantProject(signIn, organization, project);
                await BindingStatusAsync(owner, organization, project, organization, HttpStatusCode.NoContent);
                if (persisted != "valid")
                {
                    // Model an incompatible persisted record at the same storage boundary used by catalog upgrade fixtures.
                    var system = await api.Server.Services.GetRequiredService<IStorageEngine>().OpenSystemDatabaseAsync(CancellationToken.None);
                    await using var transaction = await system.BeginTransactionAsync(write: true, CancellationToken.None);
                    var collection = $"projects/{project}/binding";
                    var stored = await transaction.GetAsync(collection, "ts_auth", CancellationToken.None);
                    var binding = JsonNode.Parse(stored!.Json.Span)!;
                    switch (persisted)
                    {
                        case "issuer": binding["issuer"] = "https://auth.test/other"; break;
                        case "version": binding["version"] = 2; break;
                        case "organization": binding["organizationId"] = EntityIds.New().ToString(); break;
                        default: throw new ArgumentOutOfRangeException(nameof(persisted));
                    }

                    await transaction.PutAsync(collection, "ts_auth", Encoding.UTF8.GetBytes(binding.ToJsonString()), CancellationToken.None);
                    await transaction.CommitAsync(CancellationToken.None);
                }
            }

            await using var restarted = await ManagementApi.StartAsync(settings => settings["data-directory"] = directory, issuer.Route);
            using var jwt = restarted.WithToken(ProjectToken(issuer, EntityIds.New().ToString(), database.Project));
            if (persisted == "valid")
            {
                using var read = await jwt.GetAsync(Uri(document));
                await Assert.That(read.StatusCode).IsEqualTo(HttpStatusCode.OK);
                await Assert.That(read.Headers.ETag?.Tag).IsEqualTo(etag);
                await Assert.That((await read.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("document").GetProperty("name").GetString()).IsEqualTo(name);
                await Assert.That((await JsonAsync(jwt.GetAsync(Uri(database.Data)))).GetProperty("id").GetString()).IsEqualTo(database.Id);
            }
            else
            {
                await InaccessibleAsync(jwt, database);
                await StatusAsync(jwt.GetAsync(Uri(document)), HttpStatusCode.NotFound);
            }

            using var legacyOwner = restarted.WithToken(issuer.Mint(user));
            await Assert.That((await JsonAsync(legacyOwner.GetAsync(Uri(database.Path)))).GetProperty("id").GetString()).IsEqualTo(database.Id);
            using var legacyReader = await restarted.CreateClientAsync(database.Organization, "documents:read");
            using var legacyRead = await legacyReader.GetAsync(Uri(document));
            await Assert.That(legacyRead.StatusCode).IsEqualTo(HttpStatusCode.OK);
            await Assert.That(legacyRead.Headers.ETag?.Tag).IsEqualTo(etag);
            await Assert.That((await legacyRead.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("document").GetProperty("name").GetString()).IsEqualTo(name);
            await Assert.That(issuer.ProjectFetches).IsEqualTo(1);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static string ProjectToken(TestIssuer issuer, string user, string project) => issuer.Mint(user, token =>
    {
        token.Claims["project_id"] = project;
        token.Claims["scope"] = Scopes;
    });

    private static async Task InaccessibleAsync(HttpClient jwt, ManagedDatabaseRef database)
    {
        await StatusAsync(jwt.GetAsync(Uri(database.Data)), HttpStatusCode.NotFound);
        await StatusAsync(jwt.PostAsJsonAsync(Uri($"/v1/projects/{database.Project}/databases"), new { name = "captured" }), HttpStatusCode.NotFound);
    }

    private static Task BindingStatusAsync(HttpClient caller, string organization, string project, string authorityOrganization, HttpStatusCode expected) =>
        StatusAsync(caller.PostAsJsonAsync(Uri(ProjectPath(organization, project) + "/binding"), new { organization = authorityOrganization }), expected);

    private static async Task StatusAsync(Task<HttpResponseMessage> sending, HttpStatusCode expected)
    {
        using var response = await sending;
        await Assert.That(response.StatusCode).IsEqualTo(expected).Because(await response.Content.ReadAsStringAsync());
    }

    private static async Task<JsonElement> JsonAsync(Task<HttpResponseMessage> sending, HttpStatusCode expected = HttpStatusCode.OK)
    {
        using var response = await sending;
        await Assert.That(response.StatusCode).IsEqualTo(expected).Because(await response.Content.ReadAsStringAsync());
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static string ProjectPath(string organization, string project) => $"/v1/management/organizations/{organization}/projects/{project}";

    private static Uri Uri(string path) => ManagementApi.Uri(path);
}
