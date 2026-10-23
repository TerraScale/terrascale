using System.Net;
using Bogus;
using Microsoft.Extensions.DependencyInjection;
using TerraBase.Engine;
using TerraBase.Engine.Turso;
using TerraBase.Fast.Documents;
using TerraBase.Fast.Management;
using TerraBase.Features.Management;
using TerraBase.Features.Qos;
using TerraBase.Tenancy;
using TerraBase.Testing;

namespace TerraBase.Fast.Qos;

public sealed class StorageQuotaTests
{
    [Test]
    public async Task Cold_quota_accounting_ignores_query_deadlines_but_foreground_queries_remain_limited()
    {
        var clock = new AccountingClock();
        await using var api = await DocumentApi.StartAsync(services: services =>
        {
            Configure(services);
            services.AddKeyedSingleton<TimeProvider>(EngineExtensions.QueryDeadlineClock, clock);
        });
        var original = DocumentApi.NewId();
        (await api.PutAsync(original, new { n = 1 })).Status.ShouldBe(HttpStatusCode.Created);
        var other = DocumentApi.NewId();
        var deleted = DocumentApi.NewId();
        var store = (await api.Server.Services.GetRequiredService<IStorageEngine>().OpenDatabaseAsync(await api.TenantOfAsync(), CancellationToken.None))!;
        await using (var transaction = await store.BeginTransactionAsync(write: true, CancellationToken.None))
        {
            await transaction.CreateCollectionAsync(new CollectionInfo("other", 0), CancellationToken.None);
            await transaction.PutAsync("other", other, """{"data":"1234567890"}"""u8.ToArray(), CancellationToken.None);
            await transaction.PutAsync("other", deleted, """{"data":"deleted history does not count"}"""u8.ToArray(), CancellationToken.None);
            await transaction.TombstoneAsync("other", deleted, CancellationToken.None);
            await transaction.CommitAsync(CancellationToken.None);
        }

        // Each request read-pool timestamp advances beyond both commercial and default safety deadlines.
        // The direct commit invalidated cached usage: cold accounting must total 7 + 21 bytes, not time out.
        clock.Enabled = true;
        // Background reconciliation must remain outside the request-only artificial deadline.
        await api.Server.Services.GetRequiredService<EngineManagementService>().ConvergeAsync(CancellationToken.None);
        await IsQuotaAsync(await api.PutAsync(DocumentApi.NewId(), new { n = 2 }));
        var foreground = await api.SendAsync(HttpMethod.Post, api.Query, new { limit = 1 });
        await Assert.That((foreground.Status, foreground.Code)).IsEqualTo((HttpStatusCode.BadRequest, "invalid_query"));
        (await api.SendAsync(HttpMethod.Delete, $"{api.Collections}/other/documents/by-key?id={other}")).Status.ShouldBe(HttpStatusCode.OK);
        await using (var transaction = await store.BeginTransactionAsync(write: true, CancellationToken.None))
        {
            await transaction.PutAsync("items", original, """{"n":3}"""u8.ToArray(), CancellationToken.None);
            await transaction.CommitAsync(CancellationToken.None);
        }

        // Reconstruct from storage again: old JSON retained in history and internal collection metadata are excluded.
        await Assert.That((await api.PutAsync(DocumentApi.NewId(), new { n = 4 })).Status).IsEqualTo(HttpStatusCode.Created);
    }

    [Test]
    public async Task Finite_quota_writes_wait_outside_the_engine_batch_and_cannot_share_the_last_bytes()
    {
        var hook = new UsageCommitHook(storageLimit: plan => plan == PlanTier.Enterprise ? long.MaxValue : 32);
        await using var api = await DocumentApi.StartAsync(services: hook.InstallQuota);
        var engine = api.Server.Services.GetRequiredService<TursoStorageEngine>();
        var store = (TursoDatabaseStore)(await engine.OpenDatabaseAsync(await api.TenantOfAsync(), CancellationToken.None))!;
        var held = await store.BeginTransactionAsync(write: true, CancellationToken.None);
        Task<ApiResponse> primer;
        Task<ApiResponse>[] contenders;
        try
        {
            primer = api.PutAsync(DocumentApi.NewId(), new { n = 0 }); // Seven bytes of the 32-byte fixture ceiling.
            await TerraBaseTestServer.EventuallyAsync(_ => Task.FromResult(store.BatchedWrites == 1));
            var entered = 0;
            var arrived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            hook.BeforeDocumentQuota = (_, _) =>
            {
                if (Interlocked.Increment(ref entered) == 2)
                {
                    arrived.TrySetResult();
                }

                return ValueTask.CompletedTask;
            };
            contenders = [api.PutAsync(DocumentApi.NewId(), Data(10)), api.PutAsync(DocumentApi.NewId(), Data(10))];
            await arrived.Task.WaitAsync(TimeSpan.FromSeconds(30));
            // Finite admission holds through commit: other writes wait before entering the engine's queue.
            await Assert.That(store.QueuedWrites).IsEqualTo(0);
        }
        finally
        {
            await held.DisposeAsync();
        }

        var first = await primer;
        await Assert.That(first.Status).IsEqualTo(HttpStatusCode.Created);
        var results = await Task.WhenAll(contenders);
        await Assert.That(results.Count(result => result.Status == HttpStatusCode.Created)).IsEqualTo(1);
        await IsQuotaAsync(results.Single(result => result.Status != HttpStatusCode.Created));
        var accepted = results.Single(result => result.Status == HttpStatusCode.Created);
        await Assert.That(accepted.Json.GetProperty("offset").GetString()).IsNotEqualTo(first.Json.GetProperty("offset").GetString());
    }

    [Test]
    public async Task Hobby_can_delete_and_replace_large_enterprise_documents_without_materializing_the_old_body()
    {
        await using var api = await DocumentApi.StartAsync(settings => settings["memory-budget"] = "4194304", Configure);
        var management = api.Server.Services.GetRequiredService<IManagementService>();
        await management.SetPlanAsync(api.Tenant.User, api.Tenant.OrganizationId, PlanTier.Enterprise, CancellationToken.None);
        var deleted = DocumentApi.NewId();
        var replaced = DocumentApi.NewId();
        var large = Data(128 * 1024); // Reading this body reserves more than Hobby's 256 KiB memory share.
        (await api.PutAsync(deleted, large)).Status.ShouldBe(HttpStatusCode.Created);
        (await api.PutAsync(replaced, large)).Status.ShouldBe(HttpStatusCode.Created);
        await management.SetPlanAsync(api.Tenant.User, api.Tenant.OrganizationId, PlanTier.Hobby, CancellationToken.None);
        var removed = await api.SendAsync(HttpMethod.Delete, api.Document(deleted));
        await Assert.That(removed.Status).IsEqualTo(HttpStatusCode.OK);
        var reduced = await api.PutAsync(replaced, new { n = 1 });
        await Assert.That(reduced.Status).IsEqualTo(HttpStatusCode.OK);
        await Assert.That((await api.GetAsync(replaced)).Json.GetProperty("document").GetProperty("n").GetInt32()).IsEqualTo(1);
    }

    [Test]
    public async Task Hobby_can_drop_a_collection_holding_a_large_enterprise_document()
    {
        await using var api = await DocumentApi.StartAsync(settings => settings["memory-budget"] = "4194304", Configure);
        var management = api.Server.Services.GetRequiredService<IManagementService>();
        await management.SetPlanAsync(api.Tenant.User, api.Tenant.OrganizationId, PlanTier.Enterprise, CancellationToken.None);
        // The drop admits 16 times this document's stored bytes, far past Hobby's 256 KiB memory share.
        (await api.PutAsync(DocumentApi.NewId(), Data(64 * 1024))).Status.ShouldBe(HttpStatusCode.Created);
        await management.SetPlanAsync(api.Tenant.User, api.Tenant.OrganizationId, PlanTier.Hobby, CancellationToken.None);
        var collection = $"{api.Collections}/{DocumentApi.Collection}";
        await Assert.That((await api.SendAsync(HttpMethod.Delete, collection)).Status).IsEqualTo(HttpStatusCode.OK);
        await Assert.That((await api.SendAsync(HttpMethod.Get, collection)).Status).IsEqualTo(HttpStatusCode.NotFound);
    }

    [Test]
    public async Task Storage_is_aggregated_across_projects_but_other_organizations_have_their_own_quota()
    {
        await using var api = await StartAsync();
        var full = Data(21); // {"data":"..."} is exactly 32 UTF-8 bytes.
        (await api.PutAsync(DocumentApi.NewId(), full)).Status.ShouldBe(HttpStatusCode.Created);
        using var owner = api.Management.As(api.Tenant.User);
        var project = await api.Management.CreateProjectAsync(api.Tenant.OrganizationId, "Second", owner);
        var other = $"/v1/projects/{project}/databases/other";
        await CreateCollectionAsync(api, other);
        await IsQuotaAsync(await api.SendAsync(HttpMethod.Put, $"{other}/collections/items/documents/by-key?id={DocumentApi.NewId()}", new { n = 1 }));

        using var unrelated = await api.Management.CreateTenantAsync("documents:read", "documents:write");
        var separate = $"/v1/projects/{unrelated.ProjectId}/databases/other";
        (await DocumentApi.SendAsync(unrelated.Data, HttpMethod.Put, separate)).Status.ShouldBe(HttpStatusCode.Created);
        (await DocumentApi.SendAsync(unrelated.Data, HttpMethod.Post, separate + "/collections", JsonContent.Create(new { name = "items" })))
            .Status.ShouldBe(HttpStatusCode.Created);
        var allowed = await DocumentApi.SendAsync(unrelated.Data, HttpMethod.Put, separate + "/collections/items/documents/by-key?id=" + DocumentApi.NewId(), JsonContent.Create(full));
        await Assert.That(allowed.Status).IsEqualTo(HttpStatusCode.Created);
    }

    [Test]
    public async Task Concurrent_writes_to_different_databases_cannot_both_take_the_last_bytes()
    {
        await using var api = await StartAsync();
        await CreateCollectionAsync(api, api.DatabasePath("second"));
        var firstId = DocumentApi.NewId();
        var secondId = DocumentApi.NewId();
        var second = api.DatabasePath("second") + "/collections/items/documents/by-key?id=" + secondId;
        var results = await Task.WhenAll(api.PutAsync(firstId, Data(10)), api.SendAsync(HttpMethod.Put, second, Data(10)));
        await Assert.That(results.Count(result => result.Status == HttpStatusCode.Created)).IsEqualTo(1);
        await IsQuotaAsync(results.Single(result => result.Status != HttpStatusCode.Created));
        var reads = new[] { await api.GetAsync(firstId), await api.SendAsync(HttpMethod.Get, second) };
        await Assert.That(reads.Count(result => result.Status == HttpStatusCode.OK)).IsEqualTo(1);
    }

    [Test]
    public async Task Quota_refusals_roll_back_the_batch_and_net_reducing_batches_still_commit()
    {
        await using var api = await StartAsync();
        var original = DocumentApi.NewId();
        var fresh = DocumentApi.NewId();
        var before = await api.PutAsync(original, Data(21));
        var refused = await api.TransactAsync(new { op = "put", collection = "items", id = fresh, document = new { n = 1 } });
        await IsQuotaAsync(refused);
        await Assert.That(refused.Op).IsEqualTo(0);
        await Assert.That((await api.GetAsync(fresh)).Status).IsEqualTo(HttpStatusCode.NotFound);
        await Assert.That((await api.GetAsync(original)).Version).IsEqualTo(before.Version);
        var reduced = await api.TransactAsync(
            new { op = "put", collection = "items", id = fresh, document = new { n = 1 } },
            new { op = "delete", collection = "items", id = original });
        await Assert.That(reduced.Status).IsEqualTo(HttpStatusCode.OK);
        await Assert.That((await api.GetAsync(fresh)).Status).IsEqualTo(HttpStatusCode.OK);
        await Assert.That((await api.PutAsync(DocumentApi.NewId(), Data(10))).Status).IsEqualTo(HttpStatusCode.Created);
    }

    [Test]
    public async Task A_downgrade_allows_equal_puts_reducing_patches_and_deletes_but_refuses_growth()
    {
        await using var api = await StartAsync();
        var management = api.Server.Services.GetRequiredService<IManagementService>();
        // Fixture setup uses the real catalog; the production plan endpoint is owned by trusted billing.
        await management.SetPlanAsync(api.Tenant.User, api.Tenant.OrganizationId, PlanTier.Pro, CancellationToken.None);
        var id = DocumentApi.NewId();
        var large = Data(30);
        (await api.PutAsync(id, large)).Status.ShouldBe(HttpStatusCode.Created);
        await management.SetPlanAsync(api.Tenant.User, api.Tenant.OrganizationId, PlanTier.Hobby, CancellationToken.None);
        await IsQuotaAsync(await api.PutAsync(DocumentApi.NewId(), new { n = 1 }));
        await Assert.That((await api.PutAsync(id, large)).Status).IsEqualTo(HttpStatusCode.OK);
        var patch = await api.SendRawAsync(HttpMethod.Patch, api.Document(id), """{"data":"x"}""", "application/merge-patch+json");
        await Assert.That(patch.Status).IsEqualTo(HttpStatusCode.OK);
        await IsQuotaAsync(await api.PutAsync(DocumentApi.NewId(), Data(10)));
        await Assert.That((await api.SendAsync(HttpMethod.Delete, api.Document(id))).Status).IsEqualTo(HttpStatusCode.OK);
        await Assert.That((await api.PutAsync(DocumentApi.NewId(), Data(21))).Status).IsEqualTo(HttpStatusCode.Created);
    }

    [Test]
    public async Task Accounting_refreshes_after_a_direct_engine_commit_and_collection_deletion()
    {
        await using var api = await StartAsync();
        var id = DocumentApi.NewId();
        (await api.PutAsync(id, new { n = 1 })).Status.ShouldBe(HttpStatusCode.Created);
        var store = (await api.Server.Services.GetRequiredService<IStorageEngine>().OpenDatabaseAsync(await api.TenantOfAsync(), CancellationToken.None))!;
        // Replication and maintenance can commit outside admission. CommitSequence invalidates their cached usage.
        await using (var transaction = await store.BeginTransactionAsync(write: true, CancellationToken.None))
        {
            await transaction.PutAsync("items", id, """{"data":"123456789012345678901"}"""u8.ToArray(), CancellationToken.None);
            await transaction.CommitAsync(CancellationToken.None);
        }

        await IsQuotaAsync(await api.PutAsync(DocumentApi.NewId(), new { n = 1 }));
        (await api.SendAsync(HttpMethod.Delete, api.Collections + "/items")).Status.ShouldBe(HttpStatusCode.OK);
        (await api.SendAsync(HttpMethod.Post, api.Collections, new { name = "items" })).Status.ShouldBe(HttpStatusCode.Created);
        await Assert.That((await api.PutAsync(DocumentApi.NewId(), Data(21))).Status).IsEqualTo(HttpStatusCode.Created);
    }

    [Test]
    public async Task Restart_reconstructs_usage_from_committed_documents_without_a_quota_ledger()
    {
        var directory = Directory.CreateTempSubdirectory("terrabase-quota-").FullName;
        try
        {
            string key;
            string path;
            await using (var api = await DocumentApi.StartAsync(settings => settings["data-directory"] = directory, Configure))
            {
                (await api.PutAsync(DocumentApi.NewId(), Data(21))).Status.ShouldBe(HttpStatusCode.Created);
                key = api.Client.DefaultRequestHeaders.Authorization!.Parameter!;
                path = api.Document(DocumentApi.NewId());
            }

            await using var restarted = await ManagementApi.StartAsync(settings => settings["data-directory"] = directory, Configure);
            using var client = restarted.DataClient(key);
            await IsQuotaAsync(await DocumentApi.SendAsync(client, HttpMethod.Put, path, JsonContent.Create(new { n = 1 })));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static Task<DocumentApi> StartAsync() => DocumentApi.StartAsync(services: Configure);

    private static void Configure(IServiceCollection services) => services.AddSingleton<IStorageQuota>(provider =>
        new StorageQuota(provider.GetRequiredService<IStorageEngine>(), provider.GetRequiredService<ITenantDirectory>(),
            plan => plan switch { PlanTier.Hobby => 32, PlanTier.Pro => 128, _ => long.MaxValue }));

    private static object Data(int length) => new { data = new Faker().Random.AlphaNumeric(length) };

    private sealed class AccountingClock : TimeProvider
    {
        private long _timestamp;
        public bool Enabled { get; set; }
        public override long TimestampFrequency => 1000;
        // Background catalog queries must not consume this request-only artificial deadline.
        public override long GetTimestamp() => QosContext.Current is null ? 0
            : Enabled && Thread.CurrentThread.Name == "terrabase-turso"
            ? Interlocked.Add(ref _timestamp, 1_000_000) : Volatile.Read(ref _timestamp);
    }

    private static async Task CreateCollectionAsync(DocumentApi api, string database)
    {
        (await api.SendAsync(HttpMethod.Put, database)).Status.ShouldBe(HttpStatusCode.Created);
        (await api.SendAsync(HttpMethod.Post, database + "/collections", new { name = "items" })).Status.ShouldBe(HttpStatusCode.Created);
    }

    private static async Task IsQuotaAsync(ApiResponse response) =>
        await Assert.That((response.Status, response.Code)).IsEqualTo((HttpStatusCode.TooManyRequests, "storage_quota"));
}
