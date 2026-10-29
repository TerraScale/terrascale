using System.Diagnostics.Metrics;
using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using TerraBase.Engine;
using TerraBase.Features.Management;
using TerraBase.Configuration;
using TerraBase.Hosting;
using TerraBase.Primitives;
using TerraBase.Testing;
using TerraBase.Tenancy;

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
        await ErrorAssert.ErrorAsync(api.Server.Client.PutAsJsonAsync(ManagementApi.Uri($"/admin/projects/{project}/closure"), new { version = 3, closureId = closure, authorityOrganizationId = organization }),
            HttpStatusCode.BadRequest, "invalid_request", "version must be 1 or 2; project, closureId and authorityOrganizationId must be canonical uppercase ULIDs");

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

    // Review R1: after this machine's empty receipt, neither an operator project key nor a customer backup
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

        using var response = await PutAsync(api.Server.Client, project, closure, organization);
        var detail = await response.Content.ReadAsStringAsync();
        if (response.StatusCode != HttpStatusCode.OK)
        {
            var receipt = await ReceiptAsync(api, project);
            await using var read = await catalog.BeginTransactionAsync(write: false, CancellationToken.None);
            var holders = await EngineManagementService.CatalogHoldersAsync(read, machine, CancellationToken.None);
            var fence = await SystemCatalog.GetAsync(read, ProjectClosure.Collection, project,
                TerraBaseJsonContext.Default.ProjectClosureRecord, CancellationToken.None);
            detail += $"; holders={string.Join(',', holders)}; fence={JsonSerializer.Serialize(fence, TerraBaseJsonContext.Default.ProjectClosureRecord)}; own receipt={JsonSerializer.Serialize(receipt, TerraBaseJsonContext.Default.ProjectClosureReceipt)}";
        }
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK).Because(detail);
    }

    [Test]
    public async Task Account_erasure_revokes_existing_project_keys_and_the_current_backup_destination()
    {
        var s3 = new FakeS3();
        await using var api = await ManagementApi.StartAsync(s3.Configure, s3.Route);
        var database = await api.CreateManagedDatabaseAsync();
        await api.VerifyProjectBindingAsync(database.Organization, database.Project);
        var route = $"/v1/management/organizations/{database.Organization}/projects/{database.Project}";
        using var issued = await api.Server.Client.PostAsJsonAsync(ManagementApi.Uri("/admin/api-keys"),
            new { name = "existing", scopes = ReadScope, organizationId = database.Organization, projectId = database.Project });
        await Assert.That(issued.StatusCode).IsEqualTo(HttpStatusCode.Created);
        var key = await issued.Content.ReadFromJsonAsync<JsonElement>();
        using var data = api.DataClient(key.GetProperty("secret").GetString());
        var collections = ManagementApi.Uri(database.Data + "/collections");
        using var before = await data.GetAsync(collections);
        await Assert.That(before.StatusCode).IsEqualTo(HttpStatusCode.OK);
        using var destination = await api.Client.PutAsJsonAsync(ManagementApi.Uri(route + "/backup-destination"), new Dictionary<string, string>
        {
            ["endpoint"] = FakeS3.CustomerEndpoint,
            ["region"] = "test-region-1",
            ["bucket"] = FakeS3.CustomerBucket,
            ["accessKeyId"] = FakeS3.CustomerAccessKey,
            ["secretKey"] = FakeS3.CustomerSecretKey,
            ["mode"] = "customer",
        });
        await Assert.That(destination.IsSuccessStatusCode).IsTrue().Because(await destination.Content.ReadAsStringAsync());
        var closure = EntityIds.New().ToString();
        await TerraBaseTestServer.EventuallyAsync(async token =>
        {
            using var response = await PutAsync(api.Server.Client, database.Project, closure, database.Organization, 2, token);
            await Assert.That(response.StatusCode is HttpStatusCode.OK or HttpStatusCode.Accepted).IsTrue();
            return response.StatusCode == HttpStatusCode.OK;
        }, timeout: TimeSpan.FromSeconds(60));
        using var revoked = await data.GetAsync(collections);
        await Assert.That(revoked.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
        var catalog = await api.Server.Services.GetRequiredService<IStorageEngine>().OpenSystemDatabaseAsync(CancellationToken.None);
        await using (var transaction = await catalog.BeginTransactionAsync(write: false, CancellationToken.None))
        {
            await Assert.That(await transaction.GetAsync("api_keys", key.GetProperty("id").GetString()!, CancellationToken.None)).IsNull();
            foreach (var collection in new[] { $"projects/{database.Project}/api_keys", $"projects/{database.Project}/backup_destination" })
            {
                await Assert.That((await transaction.ListAsync(collection, null, 10, 1024 * 1024, CancellationToken.None)).Documents).IsEmpty().Because(collection);
            }
        }

        await CloseAsync(api.Server.Client, database.Project, closure, database.Organization, HttpStatusCode.OK, 2);
    }

    [Test]
    [Arguments(false, false)]
    [Arguments(true, false)]
    [Arguments(false, true)]
    public async Task Both_closure_contracts_require_current_erasure_evidence_and_holder_local_prior_purge_scans(bool priorPurge, bool legacyReceipt)
    {
        await using var api = await ManagementApi.StartAsync();
        var (project, organization, closure, holder) = (EntityIds.New().ToString(), EntityIds.New().ToString(), EntityIds.New().ToString(), EntityIds.New().ToString());
        var machine = api.Server.Services.GetRequiredService<TerraBaseOptions>().MachineId;
        var fence = new ProjectClosureRecord(project, closure, organization, DateTimeOffset.UtcNow, [machine, holder]);
        var receipt = new ProjectClosureReceipt(project, closure, holder, true, DateTimeOffset.UtcNow, EvidenceVersion: legacyReceipt ? 0 : 2);
        var storage = new TenantContext(organization, PlanTier.Hobby, project, EntityIds.New().ToString());
        var catalog = await api.Server.Services.GetRequiredService<IStorageEngine>().OpenSystemDatabaseAsync(CancellationToken.None);
        await using (var transaction = await catalog.BeginTransactionAsync(write: true, CancellationToken.None))
        {
            _ = await transaction.PutAsync(ProjectClosure.Collection, project,
                EntityPayload.WithoutId(JsonSerializer.SerializeToUtf8Bytes(fence, TerraBaseJsonContext.Default.ProjectClosureRecord), project, nested: true), CancellationToken.None);
            _ = await transaction.PutAsync(ProjectClosure.Receipts(project), holder,
                EntityPayload.WithoutId(JsonSerializer.SerializeToUtf8Bytes(receipt, TerraBaseJsonContext.Default.ProjectClosureReceipt), holder, nested: true), CancellationToken.None);
            if (priorPurge)
            {
                // A deletion authority predates the per-project index; a peer has already completed its own scan.
                var now = DateTimeOffset.UtcNow;
                var purge = new DatabasePurgeRecord(storage, now, now, now, now, [holder]);
                _ = await transaction.PutAsync(DatabasePurge.Collection, storage.DatabaseId!,
                    EntityPayload.WithoutId(JsonSerializer.SerializeToUtf8Bytes(purge, TerraBaseJsonContext.Default.DatabasePurgeRecord), storage.DatabaseId!, nested: true), CancellationToken.None);
                _ = await transaction.PutAsync(ProjectClosure.Scans(project), holder,
                    JsonSerializer.SerializeToUtf8Bytes("complete", TerraBaseJsonContext.Default.String), CancellationToken.None);
            }

            _ = await transaction.CommitAsync(CancellationToken.None);
        }

        var initial = await CloseAsync(api.Server.Client, project, closure, organization,
            priorPurge || legacyReceipt ? HttpStatusCode.Accepted : HttpStatusCode.OK);
        await Assert.That(initial.GetProperty("pendingHolders").GetInt32()).IsEqualTo(priorPurge || legacyReceipt ? 1 : 0);
        var upgraded = await CloseAsync(api.Server.Client, project, closure, organization, HttpStatusCode.Accepted, 2);
        await Assert.That(upgraded.GetProperty("pendingHolders").GetInt32()).IsEqualTo(priorPurge ? 2 : 1);
        await Assert.That(EntityPayload.Read((await StoredAsync(api, project))!, TerraBaseJsonContext.Default.ProjectClosureRecord)!.FormatVersion).IsEqualTo(2);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task A_replicated_fence_never_authorizes_erasure_of_an_unverified_or_conflicting_local_binding(bool verified)
    {
        await using var api = await ManagementApi.StartAsync();
        var database = await api.CreateManagedDatabaseAsync();
        if (verified)
        {
            await api.VerifyProjectBindingAsync(database.Organization, database.Project);
        }

        var fence = new ProjectClosureRecord(database.Project, EntityIds.New().ToString(), EntityIds.New().ToString(), DateTimeOffset.UtcNow, [], FormatVersion: 2);
        var catalog = await api.Server.Services.GetRequiredService<IStorageEngine>().OpenSystemDatabaseAsync(CancellationToken.None);
        await using (var transaction = await catalog.BeginTransactionAsync(write: true, CancellationToken.None))
        {
            _ = await transaction.PutAsync(ProjectClosure.Collection, database.Project,
                EntityPayload.WithoutId(JsonSerializer.SerializeToUtf8Bytes(fence, TerraBaseJsonContext.Default.ProjectClosureRecord), database.Project, nested: true), CancellationToken.None);
            _ = await transaction.CommitAsync(CancellationToken.None);
        }

        await ErrorAssert.ErrorAsync(PutAsync(api.Server.Client, database.Project, fence.ClosureId, fence.AuthorityOrganizationId, 2),
            HttpStatusCode.Conflict, "binding_conflict", "TerraBase binds this project id to another organization");
        await ((EngineManagementService)api.Server.Services.GetRequiredService<IManagementService>()).ConvergeAsync(CancellationToken.None);
        using var retained = await api.Client.GetAsync(ManagementApi.Uri(database.Path));
        await Assert.That(retained.StatusCode).IsEqualTo(HttpStatusCode.OK);
    }

    // Every pass converges every closure ever accepted. Written under SYSTEM's writer, settled ones held it ~30 times a second
    // on every node (2026-10-27); now they are read, and one that has something to erase again is still written.
    [Test]
    [Arguments(1)]
    [Arguments(2)]
    public async Task A_settled_closure_converges_without_the_catalog_writer_until_it_has_something_to_erase(int version)
    {
        await using var api = await ManagementApi.StartAsync(services: services => services.Remove(services.Single(descriptor =>
            descriptor.ServiceType == typeof(IHostedService) && descriptor.ImplementationType == typeof(CatalogConvergence))));
        var (project, organization, closure) = (EntityIds.New().ToString(), EntityIds.New().ToString(), EntityIds.New().ToString());
        await CloseAsync(api.Server.Client, project, closure, organization, HttpStatusCode.OK, version);

        var meter = api.Server.Services.GetRequiredService<IMeterFactory>().Create(Telemetry.MeterName);
        var holds = 0;
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, observer) =>
        {
            if (instrument.Name == "terrabase.storage.writer_held" && ReferenceEquals(instrument.Meter, meter))
            {
                observer.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<double>((_, _, tags, _) =>
        {
            foreach (var tag in tags)
            {
                if (tag is { Key: "at", Value: string at } && at.StartsWith("EngineManagementService.Closure.cs:", StringComparison.Ordinal))
                {
                    Interlocked.Increment(ref holds);
                }
            }
        });
        listener.Start();

        var catalog = (EngineManagementService)api.Server.Services.GetRequiredService<IManagementService>();
        await catalog.ConvergeAsync(CancellationToken.None);
        await catalog.ConvergeAsync(CancellationToken.None);
        await Assert.That(Volatile.Read(ref holds)).IsEqualTo(0);
        await Assert.That((await ReceiptAsync(api, project))!.Empty).IsTrue();

        // A purge to verify again: the next pass writes, and the receipt no longer proves the project empty.
        var system = await api.Server.Services.GetRequiredService<IStorageEngine>().OpenSystemDatabaseAsync(CancellationToken.None);
        await using (var transaction = await system.BeginTransactionAsync(write: true, CancellationToken.None))
        {
            var storage = new TenantContext(organization, PlanTier.Hobby, project, EntityIds.New().ToString());
            await SystemCatalog.PutAsync(transaction, ProjectClosure.Purges(project), storage.DatabaseId!, storage, TerraBaseJsonContext.Default.TenantContext, CancellationToken.None);
            _ = await transaction.CommitAsync(CancellationToken.None);
        }

        await catalog.ConvergeAsync(CancellationToken.None);
        await Assert.That(Volatile.Read(ref holds)).IsGreaterThan(0);
        await Assert.That((await ReceiptAsync(api, project))!.Empty).IsFalse();
    }

    private static async Task<ProjectClosureReceipt?> ReceiptAsync(ManagementApi api, string project)
    {
        var catalog = await api.Server.Services.GetRequiredService<IStorageEngine>().OpenSystemDatabaseAsync(CancellationToken.None);
        await using var transaction = await catalog.BeginTransactionAsync(write: false, CancellationToken.None);
        return await SystemCatalog.GetAsync(transaction, ProjectClosure.Receipts(project), api.Server.Services.GetRequiredService<TerraBaseOptions>().MachineId,
            TerraBaseJsonContext.Default.ProjectClosureReceipt, CancellationToken.None);
    }

    private static async Task<JsonElement> CloseAsync(HttpClient client, string project, string closure, string organization, HttpStatusCode expected, int version = 1)
    {
        using var response = await PutAsync(client, project, closure, organization, version);
        await Assert.That(response.StatusCode).IsEqualTo(expected).Because(await response.Content.ReadAsStringAsync());
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static Task<HttpResponseMessage> PutAsync(HttpClient client, string project, string closure, string organization, int version = 1, CancellationToken cancellationToken = default) =>
        client.PutAsJsonAsync(ManagementApi.Uri($"/admin/projects/{project}/closure"), new { version, closureId = closure, authorityOrganizationId = organization }, cancellationToken);

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
