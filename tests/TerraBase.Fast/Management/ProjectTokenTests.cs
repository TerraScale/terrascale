using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Bogus;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;
using TerraBase.Features.Management;
using TerraBase.Features.Qos;
using TerraBase.Primitives;
using TerraBase.Tenancy;
using TerraBase.Testing;

namespace TerraBase.Fast.Management;

public sealed class ProjectTokenTests
{
    private const string AllScopes = "databases:read databases:write documents:read documents:write";

    [Test]
    [Arguments("GET", "")]
    [Arguments("DELETE", "")]
    [Arguments("POST", "/branches")]
    public async Task A_signed_project_database_lookup_failure_after_route_resolution_remains_opaque(string method, string suffix)
    {
        var refuseLookup = false;
        var resolvedRoutes = 0;
        var refusedLookups = 0;
        var mutations = 0;
        await using var api = await ManagementApi.StartAsync(services: services =>
        {
            var registration = services.Single(service => service.ServiceType == typeof(IManagementService));
            services.Remove(registration);
            services.AddSingleton(provider => UsageApiTests.SlowManagement.Wrap(
                (IManagementService)registration.ImplementationFactory!(provider),
                operation =>
                {
                    if (!Volatile.Read(ref refuseLookup))
                    {
                        return;
                    }

                    if (operation == nameof(IManagementService.FindDatabaseRecordAsync))
                    {
                        Interlocked.Increment(ref resolvedRoutes);
                    }
                    if (operation == nameof(IManagementService.GetDatabaseAsync))
                    {
                        Interlocked.Increment(ref refusedLookups);
                    }
                    if (operation is nameof(IManagementService.DeleteDatabaseAsync) or nameof(IManagementService.BranchDatabaseAsync))
                    {
                        Interlocked.Increment(ref mutations);
                    }
                },
                operation => Volatile.Read(ref refuseLookup) && operation == nameof(IManagementService.GetDatabaseAsync)
                    ? new ValueTask<GetDatabaseResult>(ManagementErrors.NotFound)
                    : null));
        });
        var database = await api.CreateManagedDatabaseAsync("first");
        await api.VerifyProjectBindingAsync(database.Organization, database.Project);
        using var data = await api.CreateClientAsync(database.Organization, "documents:read", "documents:write");
        await ManagementApi.CreateCollectionAsync(data, database, "people");
        using var created = await data.PostAsJsonAsync(Uri(database.Data + "/collections/people/documents"), new { name = "Preserved" });
        created.EnsureSuccessStatusCode();
        var id = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString();
        using var restricted = api.WithToken(Token(api, api.User, database.Project, AllScopes, database: database.Id));
        Volatile.Write(ref refuseLookup, true);
        using var request = new HttpRequestMessage(new HttpMethod(method), Uri(database.Data + suffix))
        {
            Content = method == "POST" ? JsonContent.Create(new { name = "copy" }) : null,
        };
        await ErrorAssert.ErrorAsync(restricted.SendAsync(request), HttpStatusCode.NotFound, "unknown_route");
        await Assert.That(Volatile.Read(ref resolvedRoutes)).IsGreaterThan(0);
        await Assert.That(Volatile.Read(ref refusedLookups)).IsEqualTo(1);
        await Assert.That(Volatile.Read(ref mutations)).IsEqualTo(0);

        Volatile.Write(ref refuseLookup, false);
        var listed = await api.Client.GetFromJsonAsync<JsonElement>(Uri(database.ProjectPath + "/databases"));
        await Assert.That(listed.GetProperty("items").EnumerateArray().Select(item => item.GetProperty("id").GetString()!))
            .IsEquivalentTo([database.Id]);
        var preserved = await data.GetFromJsonAsync<JsonElement>(Uri(database.Data + "/collections/people/documents/by-key?id=" + System.Uri.EscapeDataString(id!)));
        await Assert.That(preserved.GetProperty("document").GetProperty("name").GetString()).IsEqualTo("Preserved");
    }

    [Test]
    public async Task Project_token_requests_reuse_the_project_lookup_while_the_catalog_is_unchanged()
    {
        await using var api = await ManagementApi.StartAsync();
        var project = EntityIds.New().ToString();
        using var client = api.WithToken(Token(api, EntityIds.New().ToString(), project, AllScopes));
        using (var created = await client.PostAsJsonAsync(Uri($"/v1/projects/{project}/databases"), new { name = "main" }))
        {
            await Assert.That(created.StatusCode).IsEqualTo(HttpStatusCode.Created);
        }

        var collections = Uri($"/v1/projects/{project}/databases/main/collections");
        (await client.GetAsync(collections)).EnsureSuccessStatusCode();
        var before = await PointReadsAsync(api);
        for (var request = 0; request < 50; request++)
        {
            using var listed = await client.GetAsync(collections);
            listed.EnsureSuccessStatusCode();
        }

        // Uncached, admission and authorization each read the project's organization and binding: about 8 per request, 400 here.
        // Background catalog commits may each add one shared re-read.
        await Assert.That(await PointReadsAsync(api) - before).IsLessThan(100d);
    }

    private static async Task<double> PointReadsAsync(ManagementApi api) =>
        (await api.Server.Client.GetStringAsync(Uri("/metrics"))).Split('\n')
            .Where(line => line.StartsWith("terrabase_storage_reads_total{", StringComparison.Ordinal) && line.Contains("path=\"point\"", StringComparison.Ordinal))
            .Sum(line => double.Parse(line.Split(' ')[^1], System.Globalization.CultureInfo.InvariantCulture));

    // Usage, plans and allowances key on the organization ts-auth signs into the project token, so every project of one
    // ts-auth organization lands in it; an organization a legacy commit created with that id keeps its own storage.
    [Test]
    public async Task Enrolled_projects_land_in_the_ts_auth_organization_the_token_signs()
    {
        await using var api = await ManagementApi.StartAsync();
        var actor = EntityIds.New().ToString();
        var organization = EntityIds.New().ToString();
        var catalog = api.Server.Services.GetRequiredService<IProjectCatalog>();
        foreach (var project in new[] { EntityIds.New().ToString(), EntityIds.New().ToString() })
        {
            using var client = api.WithToken(Token(api, actor, project, AllScopes, edit: token => token.Claims["org_id"] = organization));
            using (var created = await client.PostAsJsonAsync(Uri($"/v1/projects/{project}/databases"), new { name = "main" }))
            {
                await Assert.That(created.StatusCode).IsEqualTo(HttpStatusCode.Created);
            }

            await Assert.That(await catalog.ProjectOrganizationAsync(project, CancellationToken.None) is string found && found == organization).IsTrue();
        }

        var owned = await api.CreateOrganizationAsync("Legacy " + EntityIds.New().ToString());
        var squatter = EntityIds.New().ToString();
        using var squatting = api.WithToken(Token(api, actor, squatter, AllScopes, edit: token => token.Claims["org_id"] = owned));
        await Assert.That(await StatusAsync(squatting.PostAsJsonAsync(Uri($"/v1/projects/{squatter}/databases"), new { name = "main" })))
            .IsEqualTo(HttpStatusCode.NotFound);
        await Assert.That(await catalog.ProjectOrganizationAsync(squatter, CancellationToken.None) is not string).IsTrue();
    }

    // An enrolled project's organization has no owner to set its plan; the internal listener's operator sizes it, and
    // admission applies the new plan at once. Projects bound to an owned organization keep the owner route.
    [Test]
    public async Task The_operator_sets_an_enrolled_projects_plan_and_admission_applies_it()
    {
        await using var api = await ManagementApi.StartAsync();
        var project = EntityIds.New().ToString();
        using var client = api.WithToken(Token(api, EntityIds.New().ToString(), project, AllScopes));
        using (var created = await client.PostAsJsonAsync(Uri($"/v1/projects/{project}/databases"), new { name = "main" }))
        {
            await Assert.That(created.StatusCode).IsEqualTo(HttpStatusCode.Created);
        }

        if (await api.Server.Services.GetRequiredService<IProjectCatalog>().ProjectOrganizationAsync(project, CancellationToken.None) is not string organization)
        {
            throw new InvalidOperationException("the project did not enroll");
        }

        var admission = api.Server.Services.GetRequiredService<IQosAdmission>();
        var hobby = new TenantContext(organization, PlanTier.Hobby);
        List<IDisposable> held = [.. Enumerable.Range(0, QosPolicy.For(PlanTier.Hobby).Concurrency)
            .Select(_ => admission.TryEnter(hobby, out _) ?? throw new InvalidOperationException("fixture admission failed"))];
        try
        {
            var collections = Uri($"/v1/projects/{project}/databases/main/collections");
            await ErrorAssert.ErrorAsync(client.GetAsync(collections), HttpStatusCode.TooManyRequests, "tenant_busy", "organization request budget exceeded");
            using (var onClient = await client.PutAsJsonAsync(Uri($"/admin/projects/{project}/plan"), new { plan = "enterprise" }))
            {
                await Assert.That(onClient.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
            }

            await ErrorAssert.ErrorAsync(api.Server.Client.PutAsJsonAsync(Uri($"/admin/projects/{project}/plan"), new { plan = "unlimited" }),
                HttpStatusCode.BadRequest, "invalid_request", "plan must be hobby, starter, pro, team or enterprise");
            using (var set = await api.Server.Client.PutAsJsonAsync(Uri($"/admin/projects/{project}/plan"), new { plan = "enterprise" }))
            {
                await Assert.That(set.StatusCode).IsEqualTo(HttpStatusCode.OK);
                await Assert.That((await set.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()).IsEqualTo(organization);
            }

            using var admitted = await client.GetAsync(collections);
            await Assert.That(admitted.StatusCode).IsEqualTo(HttpStatusCode.OK);
        }
        finally
        {
            foreach (var lease in held)
            {
                lease.Dispose();
            }
        }

        var owned = await api.CreateManagedDatabaseAsync("owned");
        await api.VerifyProjectBindingAsync(owned.Organization, owned.Project);
        await ErrorAssert.ErrorAsync(api.Server.Client.PutAsJsonAsync(Uri($"/admin/projects/{owned.Project}/plan"), new { plan = "enterprise" }),
            HttpStatusCode.NotFound, "not_found", "the resource does not exist");
        await ErrorAssert.ErrorAsync(api.Server.Client.PutAsJsonAsync(Uri($"/admin/projects/{EntityIds.New()}/plan"), new { plan = "enterprise" }),
            HttpStatusCode.NotFound, "not_found", "the resource does not exist");
    }

    [Test]
    [Arguments("/v1/projects/", "/databases")]
    [Arguments("/v1/projects/", "/databases/")]
    [Arguments("/V1/Projects/", "/DataBases")]
    [Arguments("/V1/Projects/", "/DataBases/")]
    public async Task Database_creation_bootstraps_a_fresh_project_for_every_matched_route_spelling(string prefix, string suffix)
    {
        var faker = new Faker();
        await using var api = await ManagementApi.StartAsync();
        var project = EntityIds.New().ToString();
        using var client = api.WithToken(Token(api, EntityIds.New().ToString(), project, AllScopes));

        using var created = await client.PostAsJsonAsync(Uri(prefix + project + suffix), new { name = "main" });
        await Assert.That(created.StatusCode).IsEqualTo(HttpStatusCode.Created);
        var info = await created.Content.ReadFromJsonAsync<JsonElement>();
        await Assert.That(info.GetProperty("projectId").GetString()).IsEqualTo(project);
        var listed = await client.GetFromJsonAsync<JsonElement>(Uri($"/v1/projects/{project}/databases"));
        await Assert.That(listed.GetProperty("items").GetArrayLength()).IsEqualTo(1);
        await Assert.That(listed.GetProperty("items")[0].GetProperty("id").GetString()).IsEqualTo(info.GetProperty("id").GetString());
    }

    [Test]
    [Arguments("GET", "/databases")]
    [Arguments("POST", "/databases/main/branches")]
    [Arguments("POST", "/databases/main/collections")]
    [Arguments("POST", "/saved-queries")]
    public async Task Operations_other_than_database_creation_cannot_bootstrap_a_fresh_project(string method, string suffix)
    {
        var faker = new Faker();
        await using var api = await ManagementApi.StartAsync();
        var project = EntityIds.New().ToString();
        using var client = api.WithToken(Token(api, EntityIds.New().ToString(), project, AllScopes));
        using var request = new HttpRequestMessage(new HttpMethod(method), Uri($"/v1/projects/{project}" + suffix))
        {
            Content = method == "POST" ? JsonContent.Create(new { name = "other" }) : null,
        };

        await Assert.That(await StatusAsync(client.SendAsync(request))).IsEqualTo(HttpStatusCode.NotFound);
        await Assert.That(await StatusAsync(client.GetAsync(Uri($"/v1/projects/{project}/databases")))).IsEqualTo(HttpStatusCode.NotFound);
    }

    [Test]
    public async Task A_project_JWT_creates_and_manages_database_documents_without_organization_membership()
    {
        var faker = new Faker();
        await using var api = await ManagementApi.StartAsync();
        var project = EntityIds.New().ToString();
        using var client = api.WithToken(Token(api, EntityIds.New().ToString(), project, AllScopes));
        var databases = $"/v1/projects/{project}/databases";
        var database = databases + "/main";
        var collection = database + "/collections/people";
        var document = collection + "/documents/by-key?id=" + EntityIds.New();
        var name = faker.Name.FullName();

        using var created = await client.PostAsJsonAsync(Uri(databases), new { name = "main" });
        await Assert.That(created.StatusCode).IsEqualTo(HttpStatusCode.Created);
        await Assert.That(created.Headers.Location?.OriginalString).IsEqualTo(database);
        var info = await created.Content.ReadFromJsonAsync<JsonElement>();
        await Assert.That(info.GetProperty("projectId").GetString()).IsEqualTo(project);
        await Assert.That((await client.GetFromJsonAsync<JsonElement>(Uri(database))).GetProperty("id").GetString())
            .IsEqualTo(info.GetProperty("id").GetString());

        await Assert.That(await StatusAsync(client.PostAsJsonAsync(Uri(database + "/collections"), new { name = "people" })))
            .IsEqualTo(HttpStatusCode.Created);
        await Assert.That(await StatusAsync(client.PutAsJsonAsync(Uri(document), new { name, count = 1 }))).IsEqualTo(HttpStatusCode.Created);
        var read = await client.GetFromJsonAsync<JsonElement>(Uri(document));
        await Assert.That(read.GetProperty("document").GetProperty("name").GetString()).IsEqualTo(name);
        using var patch = JsonContent.Create(new { count = 2 }, mediaType: new MediaTypeHeaderValue("application/merge-patch+json"));
        await Assert.That(await StatusAsync(client.PatchAsync(Uri(document), patch))).IsEqualTo(HttpStatusCode.OK);

        using var query = await client.PostAsJsonAsync(Uri(collection + "/query"), new { filter = new { eq = new object[] { "/count", 2 } } });
        query.EnsureSuccessStatusCode();
        await Assert.That((await query.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("items").GetArrayLength()).IsEqualTo(1);
        var transactionId = EntityIds.New().ToString();
        using var transaction = await client.PostAsJsonAsync(Uri(database + "/transactions"), new
        {
            ops = new object[] { new { op = "create", collection = "people", id = transactionId, document = new { name, count = 3 } } },
        });
        await Assert.That(transaction.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That((await client.GetFromJsonAsync<JsonElement>(Uri(collection + "/documents/by-key?id=" + transactionId)))
            .GetProperty("document").GetProperty("count").GetInt32()).IsEqualTo(3);
        await Assert.That(await StatusAsync(client.DeleteAsync(Uri(document)))).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(await StatusAsync(client.GetAsync(Uri(document)))).IsEqualTo(HttpStatusCode.NotFound);
    }

    [Test]
    public async Task A_project_JWT_preserves_legacy_database_data_and_records_its_subject_as_the_actor()
    {
        var faker = new Faker();
        await using var api = await ManagementApi.StartAsync();
        var database = await api.CreateManagedDatabaseAsync();
        await api.VerifyProjectBindingAsync(database.Organization, database.Project);
        using var legacy = await api.CreateClientAsync(database.Organization, "documents:read", "documents:write");
        await ManagementApi.CreateCollectionAsync(legacy, database, "people");
        var document = ManagementApi.DocumentPath(database, "people", "existing");
        var name = faker.Name.FullName();
        using var written = await legacy.PutAsJsonAsync(Uri(document), new { name });
        written.EnsureSuccessStatusCode();

        var actor = EntityIds.New().ToString();
        using var jwt = api.WithToken(Token(api, actor, database.Project, AllScopes, edit: token => token.Claims["org_id"] = EntityIds.New().ToString()));
        jwt.DefaultRequestHeaders.Add("X-TerraBase-User-Id", api.User);
        using var read = await jwt.GetAsync(Uri(document));
        read.EnsureSuccessStatusCode();
        await Assert.That(read.Headers.ETag?.Tag).IsEqualTo(written.Headers.ETag?.Tag);
        await Assert.That((await read.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("document").GetProperty("name").GetString()).IsEqualTo(name);
        var canonical = await jwt.GetFromJsonAsync<JsonElement>(Uri(database.Data));
        await Assert.That(canonical.GetProperty("id").GetString()).IsEqualTo(database.Id);

        await Assert.That(await StatusAsync(jwt.PostAsJsonAsync(Uri(database.Data + "/branches"), new { name = "copy" }))).IsEqualTo(HttpStatusCode.Created);
        var copied = await jwt.GetFromJsonAsync<JsonElement>(Uri($"/v1/projects/{database.Project}/databases/copy/collections/people/documents/by-key?id={ManagementApi.DocumentId("existing")}"));
        await Assert.That(copied.GetProperty("document").GetProperty("name").GetString()).IsEqualTo(name);
        var audit = await api.Client.GetFromJsonAsync<JsonElement>(Uri($"/v1/management/organizations/{database.Organization}/audit-events"));
        await Assert.That(audit.GetProperty("items").EnumerateArray().Any(item => item.GetProperty("actor").GetString() == actor)).IsTrue();

        await Assert.That((await api.Client.GetFromJsonAsync<JsonElement>(Uri(database.Path))).GetProperty("id").GetString()).IsEqualTo(database.Id);
        await Assert.That((await legacy.GetFromJsonAsync<JsonElement>(Uri(document))).GetProperty("document").GetProperty("name").GetString()).IsEqualTo(name);
        using var nonmember = api.As(actor);
        await Assert.That(await StatusAsync(nonmember.GetAsync(Uri(database.ProjectPath)))).IsEqualTo(HttpStatusCode.NotFound);
    }

    [Test]
    public async Task Project_claims_are_checked_before_scope_and_each_operation_requires_its_scope()
    {
        var faker = new Faker();
        await using var api = await ManagementApi.StartAsync();
        var database = await api.CreateManagedDatabaseAsync();
        await api.VerifyProjectBindingAsync(database.Organization, database.Project);
        var actor = EntityIds.New().ToString();
        using var wrongProject = api.WithToken(Token(api, actor, EntityIds.New().ToString(), scope: null));
        using var noProject = api.WithToken(Token(api, actor, project: null, scope: AllScopes));
        using var noScope = api.WithToken(Token(api, actor, database.Project, scope: null));
        using var readOnly = api.WithToken(Token(api, actor, database.Project, "databases:read documents:read"));

        await Assert.That(await StatusAsync(wrongProject.GetAsync(Uri(database.Data)))).IsEqualTo(HttpStatusCode.NotFound);
        await Assert.That(await StatusAsync(noProject.GetAsync(Uri(database.Data)))).IsEqualTo(HttpStatusCode.NotFound);
        await Assert.That(await StatusAsync(wrongProject.PostAsJsonAsync(Uri(database.Data + "/collections"), new { name = "people" })))
            .IsEqualTo(HttpStatusCode.NotFound);
        await ErrorAssert.ErrorAsync(noScope.GetAsync(Uri(database.Data)), HttpStatusCode.Forbidden, "forbidden_scope");
        await ErrorAssert.ErrorAsync(readOnly.PostAsJsonAsync(Uri($"/v1/projects/{database.Project}/databases"), new { name = "other" }),
            HttpStatusCode.Forbidden, "forbidden_scope");
        await ErrorAssert.ErrorAsync(readOnly.PostAsJsonAsync(Uri(database.Data + "/collections"), new { name = "people" }),
            HttpStatusCode.Forbidden, "forbidden_scope");
        var listed = await readOnly.GetFromJsonAsync<JsonElement>(Uri($"/v1/projects/{database.Project}/databases"));
        await Assert.That(listed.GetProperty("items").GetArrayLength()).IsEqualTo(1);
    }

    [Test]
    public async Task Invalid_signature_lifetime_issuer_audience_and_non_ES256_project_tokens_are_rejected()
    {
        var faker = new Faker();
        await using var api = await ManagementApi.StartAsync();
        var project = EntityIds.New().ToString();
        var actor = EntityIds.New().ToString();
        var now = DateTime.UtcNow;
        var rsa = api.Issuer.AddKey(SecurityAlgorithms.RsaSha256);
        Action<SecurityTokenDescriptor>[] edits =
        [
            token => (token.IssuedAt, token.NotBefore, token.Expires) = (now.AddMinutes(-10), now.AddMinutes(-10), now.AddMinutes(-2)),
            token => token.Issuer = "https://auth.test/customer-project",
            token => token.Audience = "https://other.test",
            token => token.SigningCredentials = rsa,
            token => token.Claims["project_id"] = 123,
            token => token.Claims["project_id"] = null!,
            token => token.Claims["project_id"] = "00112233-4455-6677-8899-aabbccddeeff",
            token => token.Claims["project_id"] = "01arz3ndektsv4rrffq69g5fav",
            token => token.Claims["project_id"] = "01ARZ3NDEKTSV4RRFFQ69G5FAI",
            token => token.Claims["project_id"] = "81ARZ3NDEKTSV4RRFFQ69G5FAV",
            token => token.Claims["database"] = 123,
            token => token.Claims["database"] = null!,
            token => token.Claims["scope"] = 123,
            token => token.Claims["scope"] = null!,
        ];
        var tokens = edits.Select(edit => Token(api, actor, project, AllScopes, edit: edit)).ToList();
        var valid = Token(api, actor, project, AllScopes);
        tokens.Add(valid[..^2] + (valid[^2] == 'A' ? "B" : "A") + valid[^1]);
        foreach (var token in tokens)
        {
            using var client = api.WithToken(token);
            await Assert.That(await StatusAsync(client.PostAsJsonAsync(Uri($"/v1/projects/{project}/databases"), new { name = "main" })))
                .IsEqualTo(HttpStatusCode.Unauthorized);
        }

        using var accepted = api.WithToken(valid);
        await Assert.That(await StatusAsync(accepted.PostAsJsonAsync(Uri($"/v1/projects/{project}/databases"), new { name = "main" })))
            .IsEqualTo(HttpStatusCode.Created);
    }

    [Test]
    public async Task A_database_name_claim_is_rejected_even_when_the_database_exists()
    {
        await using var api = await ManagementApi.StartAsync();
        var database = await api.CreateManagedDatabaseAsync("first");
        await api.VerifyProjectBindingAsync(database.Organization, database.Project);
        using var restricted = api.WithToken(Token(api, api.User, database.Project, AllScopes, database: database.Name));
        foreach (var reference in new[] { database.Name, database.Id })
        {
            using var refused = await restricted.GetAsync(Uri($"/v1/projects/{database.Project}/databases/{reference}"));
            await Assert.That(refused.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
        }
    }

    [Test]
    public async Task A_database_restricted_JWT_filters_project_list_and_usage_and_cannot_read_delete_create_branch_or_restore_another_database()
    {
        var faker = new Faker();
        await using var api = await ManagementApi.StartAsync();
        var first = await api.CreateManagedDatabaseAsync("first");
        await api.VerifyProjectBindingAsync(first.Organization, first.Project);
        var second = await api.CreateManagedDatabaseAsync(first.Organization, first.Project, "second");
        using var legacy = await api.CreateClientAsync(first.Organization, "documents:read", "documents:write");
        foreach (var database in new[] { first, second })
        {
            await ManagementApi.CreateCollectionAsync(legacy, database, "people");
            using var written = await legacy.PutAsJsonAsync(Uri(ManagementApi.DocumentPath(database, "people", "existing")), new { name = faker.Name.FullName() });
            written.EnsureSuccessStatusCode();
        }

        using var backupResponse = await api.Client.PostAsync(Uri(first.Path + "/backups"), content: null);
        backupResponse.EnsureSuccessStatusCode();
        var backup = (await backupResponse.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString();
        using var restricted = api.WithToken(Token(api, EntityIds.New().ToString(), first.Project, AllScopes, database: first.Id));
        var databases = $"/v1/projects/{first.Project}/databases";
        var listed = await restricted.GetFromJsonAsync<JsonElement>(Uri(databases));
        await Assert.That(listed.GetProperty("items").EnumerateArray().Select(item => item.GetProperty("name").GetString()!))
            .IsEquivalentTo(["first"]);
        var usage = await restricted.GetFromJsonAsync<JsonElement>(Uri($"/v1/projects/{first.Project}/usage"));
        await Assert.That(usage.GetProperty("databases").GetInt64()).IsEqualTo(1);
        await Assert.That(usage.GetProperty("documents").GetInt64()).IsEqualTo(1);
        await Assert.That(usage.GetProperty("items").EnumerateArray().Select(item => item.GetProperty("databaseId").GetString()!))
            .IsEquivalentTo([first.Id]);
        await Assert.That(await StatusAsync(restricted.GetAsync(Uri(first.Data)))).IsEqualTo(HttpStatusCode.OK);
        await ErrorAssert.ErrorAsync(restricted.GetAsync(Uri(second.Data)), HttpStatusCode.NotFound,
            "unknown_database", "the database does not exist");
        await ErrorAssert.ErrorAsync(restricted.DeleteAsync(Uri(second.Data)), HttpStatusCode.NotFound,
            "unknown_database", "the database does not exist");
        await ErrorAssert.ErrorAsync(restricted.PostAsJsonAsync(Uri(second.Data + "/branches"), new { name = "second-copy" }),
            HttpStatusCode.NotFound, "unknown_database", "the database does not exist");
        await Assert.That(await StatusAsync(restricted.GetAsync(Uri(ManagementApi.DocumentPath(second, "people", "existing")))))
            .IsEqualTo(HttpStatusCode.NotFound);
        await Assert.That(await StatusAsync(restricted.PostAsJsonAsync(Uri(databases), new { name = "created" }))).IsEqualTo(HttpStatusCode.NotFound);
        await ErrorAssert.ErrorAsync(restricted.PostAsJsonAsync(Uri(first.Data + "/branches"), new { name = "branched" }),
            HttpStatusCode.NotFound, "unknown_route");
        await Assert.That(await StatusAsync(restricted.PostAsJsonAsync(Uri(first.Data + $"/backups/{backup}/restore"), new { name = "restored" })))
            .IsEqualTo(HttpStatusCode.NotFound);

        using var unrestricted = api.WithToken(Token(api, EntityIds.New().ToString(), first.Project, AllScopes));
        var unchanged = await unrestricted.GetFromJsonAsync<JsonElement>(Uri(databases));
        await Assert.That(unchanged.GetProperty("items").GetArrayLength()).IsEqualTo(2);
        await Assert.That(unchanged.GetProperty("items").EnumerateArray().Select(item => item.GetProperty("id").GetString()!))
            .IsEquivalentTo([first.Id, second.Id]);
    }

    [Test]
    public async Task A_project_JWT_of_a_legacy_owner_cannot_regain_membership_rights_through_hidden_management_routes()
    {
        await using var api = await ManagementApi.StartAsync();
        var first = await api.CreateManagedDatabaseAsync("first");
        await api.VerifyProjectBindingAsync(first.Organization, first.Project);
        var second = await api.CreateManagedDatabaseAsync(first.Organization, first.Project, "second");
        var otherProject = await api.CreateProjectAsync(first.Organization, "Other project");
        var foreign = await api.CreateManagedDatabaseAsync(first.Organization, otherProject, "foreign");
        using var scopedOwner = api.WithToken(Token(api, api.User, first.Project, "databases:read documents:read", database: first.Id));

        await Assert.That(await StatusAsync(scopedOwner.GetAsync(Uri(first.Data)))).IsEqualTo(HttpStatusCode.OK);
        foreach (var database in new[] { first, second, foreign })
        {
            await Assert.That(await StatusAsync(scopedOwner.GetAsync(Uri(database.Path)))).IsEqualTo(HttpStatusCode.NotFound);
            await Assert.That(await StatusAsync(scopedOwner.DeleteAsync(Uri(database.Path)))).IsEqualTo(HttpStatusCode.NotFound);
        }

        await Assert.That(await StatusAsync(scopedOwner.GetAsync(Uri(foreign.ProjectPath)))).IsEqualTo(HttpStatusCode.NotFound);
        await Assert.That(await StatusAsync(scopedOwner.PostAsJsonAsync(Uri(foreign.ProjectPath + "/databases"), new { name = "injected" })))
            .IsEqualTo(HttpStatusCode.NotFound);
        await Assert.That(await StatusAsync(scopedOwner.PostAsJsonAsync(Uri(first.ProjectPath + "/databases"), new { name = "injected" })))
            .IsEqualTo(HttpStatusCode.NotFound);
        await Assert.That(await StatusAsync(scopedOwner.PatchAsJsonAsync(Uri($"/v1/management/organizations/{first.Organization}"), new { name = "Changed" })))
            .IsEqualTo(HttpStatusCode.NotFound);

        foreach (var database in new[] { first, second, foreign })
        {
            await Assert.That((await api.Client.GetFromJsonAsync<JsonElement>(Uri(database.Path))).GetProperty("id").GetString())
                .IsEqualTo(database.Id);
        }

        await Assert.That((await api.Client.GetFromJsonAsync<JsonElement>(Uri(first.ProjectPath + "/databases"))).GetProperty("items").GetArrayLength())
            .IsEqualTo(2);
        await Assert.That((await api.Client.GetFromJsonAsync<JsonElement>(Uri(foreign.ProjectPath + "/databases"))).GetProperty("items").GetArrayLength())
            .IsEqualTo(1);
    }

    [Test]
    public async Task A_project_JWT_cannot_promote_its_subject_through_a_pending_ownership_transfer()
    {
        await using var api = await ManagementApi.StartAsync();
        var database = await api.CreateManagedDatabaseAsync();
        await api.VerifyProjectBindingAsync(database.Organization, database.Project);
        var nominee = await api.AddMemberAsync(api.Client, database.Organization, "member");
        var organization = $"/v1/management/organizations/{database.Organization}";
        var members = Uri(organization + "/members");
        var before = await api.Client.GetFromJsonAsync<JsonElement>(members);
        var membership = before.GetProperty("items").EnumerateArray().Single(item => item.GetProperty("userId").GetString() == nominee);
        using var offered = await api.Client.PostAsJsonAsync(Uri(organization + "/ownership-transfer"), new { userId = nominee });
        offered.EnsureSuccessStatusCode();

        using var project = api.WithToken(Token(api, nominee, database.Project, AllScopes));
        await Assert.That(await StatusAsync(project.GetAsync(Uri(organization + "/ownership-transfer")))).IsEqualTo(HttpStatusCode.NotFound);
        await Assert.That(await StatusAsync(project.PostAsync(Uri(organization + "/ownership-transfer/confirm"), null)))
            .IsEqualTo(HttpStatusCode.NotFound);
        await Assert.That(await StatusAsync(project.PutAsJsonAsync(Uri($"{organization}/members/{nominee}/role"), new { role = "owner" })))
            .IsEqualTo(HttpStatusCode.NotFound);
        var unchanged = await api.Client.GetFromJsonAsync<JsonElement>(members);
        await Assert.That(unchanged.GetProperty("items").EnumerateArray().Single(item => item.GetProperty("userId").GetString() == nominee)
            .GetProperty("role").GetString()).IsEqualTo("member");

        using var signedIn = api.As(nominee);
        using var confirmed = await signedIn.PostAsync(Uri(organization + "/ownership-transfer/confirm"), null);
        confirmed.EnsureSuccessStatusCode();
        var owner = await confirmed.Content.ReadFromJsonAsync<JsonElement>();
        await Assert.That(owner.GetProperty("role").GetString()).IsEqualTo("owner");
        await Assert.That(owner.GetProperty("joinedAt").GetDateTimeOffset()).IsEqualTo(membership.GetProperty("joinedAt").GetDateTimeOffset());
    }

    private static string Token(ManagementApi api, string actor, string? project, string? scope, string? database = null, Action<SecurityTokenDescriptor>? edit = null) =>
        api.Issuer.Mint(actor, token =>
        {
            if (project is not null)
            {
                token.Claims["project_id"] = project;
            }

            if (scope is not null)
            {
                token.Claims["scope"] = scope;
            }

            if (database is not null)
            {
                token.Claims["database"] = database;
            }

            edit?.Invoke(token);
        });

    private static async Task<HttpStatusCode> StatusAsync(Task<HttpResponseMessage> sending)
    {
        using var response = await sending;
        return response.StatusCode;
    }

    private static Uri Uri(string path) => ManagementApi.Uri(path);
}
