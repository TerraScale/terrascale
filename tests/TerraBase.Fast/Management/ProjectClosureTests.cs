using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using TerraBase.Engine;
using TerraBase.Features.Management;
using TerraBase.Configuration;
using TerraBase.Hosting;
using TerraBase.Primitives;
using TerraBase.Testing;

namespace TerraBase.Fast.Management;

/// <summary>ts-auth's project closure handoff (docs/terrabase/project-closure.md) on one machine.</summary>
public sealed class ProjectClosureTests
{
    private const string AllScopes = "databases:read databases:write documents:read documents:write";
    private static readonly string[] ReadScope = ["documents:read"];

    [Test]
    public async Task A_never_enrolled_project_is_fenced_idempotently_and_never_enrolls_afterwards()
    {
        await using var api = await ManagementApi.StartAsync();
        var (project, organization, closure) = (EntityIds.New().ToString(), EntityIds.New().ToString(), EntityIds.New().ToString());

        var accepted = await CloseAsync(api.Server.Client, project, closure, organization, HttpStatusCode.OK);
        await Assert.That(accepted.GetProperty("status").GetString()).IsEqualTo("accepted");
        await Assert.That(accepted.GetProperty("pendingHolders").GetInt32()).IsEqualTo(0);
        // A lost answer is retried with the same closure and answers the same.
        await Assert.That((await CloseAsync(api.Server.Client, project, closure, organization, HttpStatusCode.OK)).GetProperty("closureId").GetString()).IsEqualTo(closure);
        await ErrorAssert.ErrorAsync(PutAsync(api.Server.Client, project, EntityIds.New().ToString(), organization), HttpStatusCode.Conflict, "closure_conflict",
            "the project is already closing under another closure id or organization");
        await ErrorAssert.ErrorAsync(PutAsync(api.Server.Client, project, closure, EntityIds.New().ToString()), HttpStatusCode.Conflict, "closure_conflict",
            "the project is already closing under another closure id or organization");
        await ErrorAssert.ErrorAsync(api.Server.Client.PutAsJsonAsync(ManagementApi.Uri($"/admin/projects/{project}/closure"), new { version = 2, closureId = closure, authorityOrganizationId = organization }),
            HttpStatusCode.BadRequest, "invalid_request", "version must be 1; project, closureId and authorityOrganizationId must be canonical uppercase ULIDs");

        // A signed project token can no longer enroll the project or create its first database.
        using var client = api.WithToken(Token(api, EntityIds.New().ToString(), project));
        using (var created = await client.PostAsJsonAsync(ManagementApi.Uri($"/v1/projects/{project}/databases"), new { name = "main" }))
        {
            await Assert.That(created.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
        }

        await Assert.That(await api.Server.Services.GetRequiredService<IProjectCatalog>().ProjectOrganizationAsync(project, CancellationToken.None) is string).IsFalse();
    }

    [Test]
    public async Task Only_the_internal_listener_with_the_peer_credential_serves_the_closure()
    {
        await using var api = await ManagementApi.StartAsync();
        var (project, organization, closure) = (EntityIds.New().ToString(), EntityIds.New().ToString(), EntityIds.New().ToString());
        using (var onClient = await PutAsync(api.WithToken(null), project, closure, organization))
        {
            await Assert.That(onClient.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
        }

        using var anonymous = new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false, UseCookies = false })
        {
            BaseAddress = api.Server.BaseAddress,
            DefaultRequestVersion = HttpVersion.Version20,
            DefaultVersionPolicy = HttpVersionPolicy.RequestVersionExact,
        };
        using (var uncredentialed = await PutAsync(anonymous, project, closure, organization))
        {
            await Assert.That(uncredentialed.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
        }

        // Neither wrote a fence: the project still enrolls.
        await Assert.That(await StoredAsync(api, project)).IsNull();
    }

    [Test]
    public async Task A_non_empty_or_foreign_project_is_refused_without_a_fence_and_keeps_its_databases()
    {
        await using var api = await ManagementApi.StartAsync();
        var (project, organization) = (EntityIds.New().ToString(), EntityIds.New().ToString());
        using var client = api.WithToken(Token(api, EntityIds.New().ToString(), project));
        using (var created = await client.PostAsJsonAsync(ManagementApi.Uri($"/v1/projects/{project}/databases"), new { name = "main" }))
        {
            await Assert.That(created.StatusCode).IsEqualTo(HttpStatusCode.Created);
        }

        await ErrorAssert.ErrorAsync(PutAsync(api.Server.Client, project, EntityIds.New().ToString(), organization), HttpStatusCode.Conflict, "not_empty", "delete what it contains first");
        await Assert.That(await StoredAsync(api, project)).IsNull();
        using (var listed = await client.GetAsync(ManagementApi.Uri($"/v1/projects/{project}/databases/main")))
        {
            await Assert.That(listed.StatusCode).IsEqualTo(HttpStatusCode.OK);
        }

        // Once its customer deletes the database, the closure is accepted and nothing is created again.
        using (var deleted = await client.DeleteAsync(ManagementApi.Uri($"/v1/projects/{project}/databases/main")))
        {
            await Assert.That(deleted.IsSuccessStatusCode).IsTrue();
        }

        await CloseAsync(api.Server.Client, project, EntityIds.New().ToString(), organization, HttpStatusCode.OK);
        using (var again = await client.PostAsJsonAsync(ManagementApi.Uri($"/v1/projects/{project}/databases"), new { name = "again" }))
        {
            await Assert.That(again.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
        }

        // A legacy binding names its ts-auth organization; another one cannot close it, and an unverified namespace never.
        var owned = await api.CreateManagedDatabaseAsync("owned");
        await api.VerifyProjectBindingAsync(owned.Organization, owned.Project);
        await ErrorAssert.ErrorAsync(PutAsync(api.Server.Client, owned.Project, EntityIds.New().ToString(), EntityIds.New().ToString()), HttpStatusCode.Conflict, "binding_conflict",
            "TerraBase binds this project id to another organization");
        await ErrorAssert.ErrorAsync(PutAsync(api.Server.Client, owned.Project, EntityIds.New().ToString(), owned.Organization), HttpStatusCode.Conflict, "not_empty",
            "delete what it contains first");
        var unbound = await api.CreateProjectAsync(await api.CreateOrganizationAsync("unbound"), "unbound");
        await ErrorAssert.ErrorAsync(PutAsync(api.Server.Client, unbound, EntityIds.New().ToString(), organization), HttpStatusCode.Conflict, "binding_conflict",
            "TerraBase binds this project id to another organization");
        await Assert.That(await StoredAsync(api, owned.Project)).IsNull();
        await Assert.That(await StoredAsync(api, unbound)).IsNull();
    }

    // A creation that prepared its files before the fence fails to register after it: the receipt holds.
    [Test]
    public async Task A_database_creation_in_flight_when_the_fence_lands_never_registers()
    {
        using var prepared = new SemaphoreSlim(0);
        using var resume = new SemaphoreSlim(0);
        string? held = null;
        await using var api = await ManagementApi.StartAsync(services: services => services.AddSingleton<DatabaseLifecycleCheckpoint>((step, storage) =>
        {
            if (step == "create.files" && storage.ProjectId == Volatile.Read(ref held))
            {
                prepared.Release();
                resume.Wait(TimeSpan.FromSeconds(30));
            }
        }));
        var (project, organization) = (EntityIds.New().ToString(), EntityIds.New().ToString());
        Volatile.Write(ref held, project);
        using var client = api.WithToken(Token(api, EntityIds.New().ToString(), project));
        var creating = client.PostAsJsonAsync(ManagementApi.Uri($"/v1/projects/{project}/databases"), new { name = "main" });
        await Assert.That(await prepared.WaitAsync(TimeSpan.FromSeconds(30))).IsTrue();

        await CloseAsync(api.Server.Client, project, EntityIds.New().ToString(), organization, HttpStatusCode.OK);
        resume.Release();
        using (var created = await creating)
        {
            await Assert.That(created.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
        }

        var catalog = await api.Server.Services.GetRequiredService<IStorageEngine>().OpenSystemDatabaseAsync(CancellationToken.None);
        await using var transaction = await catalog.BeginTransactionAsync(write: false, CancellationToken.None);
        await Assert.That((await transaction.ListAsync($"projects/{project}/databases", null, 10, 1024 * 1024, CancellationToken.None)).Documents).IsEmpty();
    }

    // Review R1: after this machine's empty receipt, neither a project API key (either route) nor a customer backup
    // destination with its sealed credentials can land, so acceptance stays true.
    [Test]
    public async Task A_fenced_project_takes_no_api_key_or_backup_destination_and_stays_accepted()
    {
        var s3 = new FakeS3();
        await using var api = await ManagementApi.StartAsync(s3.Configure, s3.Route);
        var organization = await api.CreateOrganizationAsync("closing");
        var project = await api.CreateProjectAsync(organization, "closing");
        await api.VerifyProjectBindingAsync(organization, project);
        var closure = EntityIds.New().ToString();
        await CloseAsync(api.Server.Client, project, closure, organization, HttpStatusCode.OK);

        var projectRoute = $"/v1/management/organizations/{organization}/projects/{project}";
        using (var key = await api.Client.PostAsJsonAsync(ManagementApi.Uri(projectRoute + "/api-keys"), new { name = "late", scopes = ReadScope }))
        {
            await Assert.That(key.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
        }

        // The operator route answers an unknown scope, a fenced project included, with its fixed 400.
        await ErrorAssert.ErrorAsync(api.Server.Client.PostAsJsonAsync(ManagementApi.Uri("/admin/api-keys"),
            new { name = "late", scopes = ReadScope, organizationId = organization, projectId = project }), HttpStatusCode.BadRequest, "invalid_request",
            "organizationId must name an organization, projectId one of its projects and databaseId (only with projectId) one of that project's databases");

        using (var destination = await api.Client.PutAsJsonAsync(ManagementApi.Uri(projectRoute + "/backup-destination"), new Dictionary<string, string>
        {
            ["endpoint"] = FakeS3.CustomerEndpoint,
            ["region"] = "test-region-1",
            ["bucket"] = FakeS3.CustomerBucket,
            ["accessKeyId"] = FakeS3.CustomerAccessKey,
            ["secretKey"] = FakeS3.CustomerSecretKey,
            ["mode"] = "customer",
        }))
        {
            await Assert.That(destination.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
        }

        var catalog = await api.Server.Services.GetRequiredService<IStorageEngine>().OpenSystemDatabaseAsync(CancellationToken.None);
        await using (var transaction = await catalog.BeginTransactionAsync(write: false, CancellationToken.None))
        {
            foreach (var collection in new[] { $"projects/{project}/api_keys", $"projects/{project}/backup_destination", $"backup_destination_history/{project}" })
            {
                await Assert.That((await transaction.ListAsync(collection, null, 10, 1024 * 1024, CancellationToken.None)).Documents).IsEmpty().Because(collection);
            }
        }

        await CloseAsync(api.Server.Client, project, closure, organization, HttpStatusCode.OK);
    }

    // Review R1: a receipt binds to its fence by closure id, not by comparing two machines' clocks. A fence recorded by a
    // machine whose clock ran ahead is still accepted once every holder confirmed it.
    [Test]
    public async Task Clock_skew_between_the_fencing_and_the_confirming_machine_never_strands_a_closure()
    {
        await using var api = await ManagementApi.StartAsync();
        var (project, organization, closure) = (EntityIds.New().ToString(), EntityIds.New().ToString(), EntityIds.New().ToString());
        var machine = api.Server.Services.GetRequiredService<TerraBaseOptions>().MachineId;
        var ahead = new ProjectClosureRecord(project, closure, organization, DateTimeOffset.UtcNow.AddHours(6), [machine]);
        var catalog = await api.Server.Services.GetRequiredService<IStorageEngine>().OpenSystemDatabaseAsync(CancellationToken.None);
        await using (var transaction = await catalog.BeginTransactionAsync(write: true, CancellationToken.None))
        {
            _ = await transaction.PutAsync(ProjectClosure.Collection, project,
                EntityPayload.WithoutId(JsonSerializer.SerializeToUtf8Bytes(ahead, TerraBaseJsonContext.Default.ProjectClosureRecord), project, nested: true), CancellationToken.None);
            _ = await transaction.CommitAsync(CancellationToken.None);
        }

        await CloseAsync(api.Server.Client, project, closure, organization, HttpStatusCode.OK);
    }

    private static async Task<JsonElement> CloseAsync(HttpClient client, string project, string closure, string organization, HttpStatusCode expected)
    {
        using var response = await PutAsync(client, project, closure, organization);
        await Assert.That(response.StatusCode).IsEqualTo(expected).Because(await response.Content.ReadAsStringAsync());
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static Task<HttpResponseMessage> PutAsync(HttpClient client, string project, string closure, string organization) =>
        client.PutAsJsonAsync(ManagementApi.Uri($"/admin/projects/{project}/closure"), new { version = 1, closureId = closure, authorityOrganizationId = organization });

    private static async Task<StoredDocument?> StoredAsync(ManagementApi api, string project)
    {
        var catalog = await api.Server.Services.GetRequiredService<IStorageEngine>().OpenSystemDatabaseAsync(CancellationToken.None);
        await using var transaction = await catalog.BeginTransactionAsync(write: false, CancellationToken.None);
        return await transaction.GetAsync("project_closures", project, CancellationToken.None);
    }

    private static string Token(ManagementApi api, string actor, string project) =>
        api.Issuer.Mint(actor, token =>
        {
            token.Claims["project_id"] = project;
            token.Claims["scope"] = AllScopes;
        });
}
