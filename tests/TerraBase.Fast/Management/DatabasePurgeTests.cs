using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Time.Testing;
using TerraBase.Configuration;
using TerraBase.Engine;
using TerraBase.Engine.Lattices;
using TerraBase.Engine.Turso;
using TerraBase.Features.Management;
using TerraBase.Features.Catalog;
using TerraBase.Hosting;
using TerraBase.Primitives;
using TerraBase.Tenancy;
using TerraBase.Testing;

namespace TerraBase.Fast.Management;

public sealed class DatabasePurgeTests
{
    [Test]
    public async Task Customer_deletion_fixes_deadlines_and_requires_its_local_absence_receipt()
    {
        await using var state = new PurgeData();
        await using var api = await state.StartAsync();
        var database = await api.CreateManagedDatabaseAsync();
        var tenant = Tenant(database);
        using var deleted = await api.Client.DeleteAsync(new Uri(database.Path, UriKind.Relative));
        await Assert.That(deleted.StatusCode).IsEqualTo(HttpStatusCode.NoContent);
        var catalog = api.Server.Services.GetRequiredService<SystemCatalog>();
        var record = (await catalog.ListDatabasePurgesAsync(null, 100, CancellationToken.None))!.Items.Single();
        var machine = api.Server.Services.GetRequiredService<TerraBaseOptions>().MachineId;
        await Assert.That(record.RequestedAt).IsEqualTo(state.Clock.GetUtcNow());
        await Assert.That(record.ActiveDueAt).IsEqualTo(record.RequestedAt.AddDays(30));
        await Assert.That(record.BackupDueAt).IsEqualTo(record.RequestedAt.AddDays(39));
        await Assert.That(record.ExpectedHolders).IsEquivalentTo([machine]);
        await PurgeData.WriteAsync(api, async transaction =>
        {
            _ = await transaction.DeleteAsync(DatabasePurge.Receipts(database.Id), machine, CancellationToken.None);
        });
        await Assert.That(await catalog.AreActiveCopiesAbsentAsync(tenant, CancellationToken.None)).IsFalse();
        await Assert.That(await catalog.MarkBackupVerifiedAsync(tenant, state.Clock.GetUtcNow(), CancellationToken.None)).IsFalse();
        await PurgeData.ConvergeAsync(api);
        var retried = (await catalog.ListDatabasePurgesAsync(null, 100, CancellationToken.None))!.Items.Single();
        await Assert.That(retried.RequestedAt).IsEqualTo(record.RequestedAt);
        await Assert.That(retried.ActiveDueAt).IsEqualTo(record.ActiveDueAt);
        await Assert.That(retried.BackupDueAt).IsEqualTo(record.BackupDueAt);
        await Assert.That(await catalog.AreActiveCopiesAbsentAsync(tenant, CancellationToken.None)).IsTrue();
        await Assert.That(await catalog.MarkBackupVerifiedAsync(tenant, state.Clock.GetUtcNow(), CancellationToken.None)).IsTrue();
    }

    [Test]
    public async Task Receipts_bind_to_the_purge_they_verified_not_to_any_node_clock()
    {
        await using var state = new PurgeData();
        await using var api = await state.StartAsync();
        var database = await api.CreateManagedDatabaseAsync();
        var tenant = Tenant(database);
        using var deleted = await api.Client.DeleteAsync(new Uri(database.Path, UriKind.Relative));
        await Assert.That(deleted.StatusCode).IsEqualTo(HttpStatusCode.NoContent);
        var catalog = api.Server.Services.GetRequiredService<SystemCatalog>();
        var record = (await catalog.ListDatabasePurgesAsync(null, 100, CancellationToken.None))!.Items.Single();
        var machine = api.Server.Services.GetRequiredService<TerraBaseOptions>().MachineId;

        await PutReceiptAsync(machine, record.RequestedAt.AddHours(-1), record.RequestedAt);
        await Assert.That(await catalog.AreActiveCopiesAbsentAsync(tenant, CancellationToken.None)).IsTrue();

        await Assert.That(await catalog.MarkBackupVerifiedAsync(tenant, record.RequestedAt.AddHours(-1), CancellationToken.None)).IsTrue();

        await PutReceiptAsync(machine, record.RequestedAt.AddHours(1), null);
        await Assert.That(await catalog.AreActiveCopiesAbsentAsync(tenant, CancellationToken.None)).IsFalse();
        await PutReceiptAsync(machine, record.RequestedAt.AddHours(1), record.RequestedAt.AddTicks(1));
        await Assert.That(await catalog.AreActiveCopiesAbsentAsync(tenant, CancellationToken.None)).IsFalse();

        var skewed = record.RequestedAt.AddHours(-1);
        await PutReceiptAsync(machine, skewed, record.RequestedAt);
        await PurgeData.ConvergeAsync(api);
        await Assert.That((await ReceiptAsync(machine))!.VerifiedAt).IsEqualTo(skewed);

        await PutReceiptAsync(machine, record.RequestedAt.AddHours(1), null);
        await PurgeData.ConvergeAsync(api);
        await Assert.That((await ReceiptAsync(machine))!.Authority).IsEqualTo(record.RequestedAt);

        async Task PutReceiptAsync(string holder, DateTimeOffset verifiedAt, DateTimeOffset? authority)
        {
            await PurgeData.WriteAsync(api, transaction => SystemCatalog.PutAsync(transaction, DatabasePurge.Receipts(database.Id), holder,
                new DatabasePurgeReceipt(tenant, holder, verifiedAt, Authority: authority), TerraBaseJsonContext.Default.DatabasePurgeReceipt, CancellationToken.None));
        }

        async Task<DatabasePurgeReceipt?> ReceiptAsync(string holder)
        {
            var store = await api.Server.Services.GetRequiredService<IStorageEngine>().OpenSystemDatabaseAsync(CancellationToken.None);
            await using var read = await store.BeginTransactionAsync(write: false, CancellationToken.None);
            return await SystemCatalog.GetAsync(read, DatabasePurge.Receipts(database.Id), holder, TerraBaseJsonContext.Default.DatabasePurgeReceipt, CancellationToken.None);
        }
    }

    [Test]
    public async Task Unreadable_purge_records_never_stop_the_listing_or_complete()
    {
        await using var state = new PurgeData();
        await using var api = await state.StartAsync();
        var database = await api.CreateManagedDatabaseAsync();
        using var deleted = await api.Client.DeleteAsync(new Uri(database.Path, UriKind.Relative));
        await Assert.That(deleted.StatusCode).IsEqualTo(HttpStatusCode.NoContent);

        var (scalar, repeated) = ("00000000000000000000000001", "00000000000000000000000002");
        var store = await api.Server.Services.GetRequiredService<IStorageEngine>().OpenSystemDatabaseAsync(CancellationToken.None);
        await using (var join = await store.BeginDurableTransactionAsync(CancellationToken.None))
        {
            foreach (var (id, json) in new[]
            {
                (scalar, "null"),
                (repeated, """{"database":42,"formatVersion":1}"""),
            })
            {
                _ = await join.PutAsync(DatabasePurge.Collection, id, System.Text.Encoding.UTF8.GetBytes(json), CancellationToken.None);
            }

            _ = await join.CommitAsync(CancellationToken.None);
        }

        var catalog = api.Server.Services.GetRequiredService<SystemCatalog>();
        var all = (await catalog.ListDatabasePurgesAsync(null, 100, CancellationToken.None))!;
        await Assert.That(all.Items.Select(record => record.Database.DatabaseId!)).IsEquivalentTo([database.Id]);

        var first = (await catalog.ListDatabasePurgesAsync(null, 1, CancellationToken.None))!;
        await Assert.That(first.Items).IsEmpty();
        await Assert.That(first.Next).IsEqualTo(scalar);
        var second = (await catalog.ListDatabasePurgesAsync(first.Next, 1, CancellationToken.None))!;
        await Assert.That(second.Items).IsEmpty();
        await Assert.That(second.Next).IsEqualTo(repeated);
        var third = (await catalog.ListDatabasePurgesAsync(second.Next, 1, CancellationToken.None))!;
        await Assert.That(third.Items.Single().Database.DatabaseId).IsEqualTo(database.Id);
    }

    [Test]
    public async Task Restart_and_collected_tombstones_keep_authority_and_forbid_reusing_a_deleted_ulid()
    {
        await using var state = new PurgeData();
        ManagedDatabaseRef database;
        ManagedDatabaseRef neighbor;
        var armed = false;
        await using (var api = await state.StartAsync(services => services.AddSingleton<DatabaseLifecycleCheckpoint>((step, _) =>
        {
            if (armed && step == "delete.intent")
            {
                throw new IOException("injected interruption after committed deletion");
            }
        })))
        {
            database = await api.CreateManagedDatabaseAsync();
            neighbor = await api.CreateManagedDatabaseAsync(database.Organization, database.Project, "neighbor");
            armed = true;
            using var deleted = await api.Client.DeleteAsync(new Uri(database.Path, UriKind.Relative));
            await Assert.That(deleted.StatusCode).IsEqualTo(HttpStatusCode.InternalServerError);
            armed = false;
            await PurgeData.WriteAsync(api, async transaction => { _ = await transaction.DeleteAsync("pending_database_cleanup", database.Id, CancellationToken.None); });
            await api.Server.Services.GetRequiredService<IStorageEngine>().CompleteDatabaseIntentAsync(Tenant(database), CancellationToken.None);
            var store = await api.Server.Services.GetRequiredService<TursoStorageEngine>().OpenSystemDatabaseAsync(CancellationToken.None);
            await using (var gc = (TursoStorageTransaction)await store.BeginTransactionAsync(write: true, CancellationToken.None))
            {
                _ = await gc.CollectHistoryAsync(new Hlc((ulong)state.Clock.GetUtcNow().AddMinutes(1).ToUnixTimeMilliseconds() << 16), CancellationToken.None);
                await gc.CommitAsync(CancellationToken.None);
            }
            await using var read = await store.BeginTransactionAsync(write: false, CancellationToken.None);
            await Assert.That(await read.GetVersionAsync($"projects/{database.Project}/databases", database.Id, CancellationToken.None)).IsNull();
            await Assert.That(await read.GetAsync(DatabasePurge.Collection, database.Id, CancellationToken.None)).IsNotNull();
            await Assert.That(await api.Server.Services.GetRequiredService<ICatalogGate>().TryAnchorAsync(CancellationToken.None)).IsTrue();
        }

        await using var restarted = await state.StartAsync();
        await Assert.That(restarted.Server.Services.GetRequiredService<ICatalogGate>().IsOpen).IsTrue();
        await Assert.That(File.Exists(state.Path(database))).IsTrue();
        await PurgeData.ConvergeAsync(restarted);
        await Assert.That(File.Exists(state.Path(database))).IsFalse();
        await Assert.That(await restarted.Server.Services.GetRequiredService<SystemCatalog>().AreActiveCopiesAbsentAsync(Tenant(database), CancellationToken.None)).IsTrue();
        using var retained = await restarted.Client.GetAsync(new Uri(neighbor.Path, UriKind.Relative));
        await Assert.That(retained.StatusCode).IsEqualTo(HttpStatusCode.OK);
        var reused = await restarted.Server.Services.GetRequiredService<ITenantDirectory>().AdoptDatabaseAsync(
            Tenant(database), "reuse", new DatabasePlacement("gru-1", [], 1), CancellationToken.None);
        await Assert.That(reused is TerraBase.Errors.Conflict).IsTrue();
        await Assert.That(File.Exists(state.Path(database))).IsFalse();
    }

    [Test]
    public async Task A_stale_live_catalog_row_cannot_serve_restore_adopt_or_place_a_purged_database()
    {
        await using var state = new PurgeData();
        await using var api = await state.StartAsync();
        var database = await api.CreateManagedDatabaseAsync();
        var tenant = Tenant(database);
        using var deleted = await api.Client.DeleteAsync(new Uri(database.Path, UriKind.Relative));
        deleted.EnsureSuccessStatusCode();
        await PurgeData.WriteAsync(api, async transaction =>
        {
            await SystemCatalog.PutAsync(transaction, $"projects/{database.Project}/databases", database.Id,
                new ManagedDatabase(database.Id, database.Project, database.Name, null, state.Clock.GetUtcNow(), "elsewhere"), TerraBaseJsonContext.Default.ManagedDatabase, CancellationToken.None);
            await SystemCatalog.PutAsync(transaction, $"projects/{database.Project}/database_names", database.Name, database.Id, TerraBaseJsonContext.Default.String, CancellationToken.None);
        });
        var directory = api.Server.Services.GetRequiredService<ITenantDirectory>();
        await Assert.That(await directory.RegisteredAsync(tenant, CancellationToken.None) is false).IsTrue();
        await Assert.That(await directory.ReplicatesAsync(tenant, CancellationToken.None) is false).IsTrue();
        using var read = await api.Client.GetAsync(new Uri(database.Path, UriKind.Relative));
        await Assert.That(read.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
        var management = api.Server.Services.GetRequiredService<EngineManagementService>();
        await Assert.That(await management.AdoptDatabaseAsync(tenant, "recovered", new DatabasePlacement("local", [], 1), CancellationToken.None) is TerraBase.Errors.Conflict).IsTrue();
        await Assert.That(await management.UpdatePlacementAsync(tenant, new DatabasePlacement("local", [], 99), CancellationToken.None) is PlacementUpdate { Updated: false }).IsTrue();
        using var project = api.WithToken(api.Issuer.Mint(api.User, token =>
        {
            token.Claims["project_id"] = database.Project;
            token.Claims["scope"] = "databases:read databases:write";
        }));
        using var branch = await project.PostAsJsonAsync(new Uri(database.Data + "/branches", UriKind.Relative), new { name = "restored" });
        await Assert.That(branch.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
    }

    [Test]
    public async Task Purge_does_not_wait_for_ordinary_gc_and_does_not_delete_unknown_files()
    {
        await using var state = new PurgeData();
        await using var api = await state.StartAsync();
        var database = await api.CreateManagedDatabaseAsync();
        var engine = api.Server.Services.GetRequiredService<IStorageEngine>();
        var unknown = Tenant(database) with { DatabaseId = EntityIds.New().ToString() };
        await Assert.That(await engine.CreateDatabaseAsync(unknown, CancellationToken.None)).IsTrue();
        var system = await api.Server.Services.GetRequiredService<TursoStorageEngine>().OpenSystemDatabaseAsync(CancellationToken.None);
        using var deleted = await api.Client.DeleteAsync(new Uri(database.Path, UriKind.Relative));
        deleted.EnsureSuccessStatusCode();
        await PurgeData.ConvergeAsync(api);
        await Assert.That(File.Exists(state.Path(database))).IsFalse();
        await Assert.That(await engine.OpenDatabaseAsync(unknown, CancellationToken.None)).IsNotNull();
        await using var read = await system.BeginTransactionAsync(write: false, CancellationToken.None);
        await Assert.That(await read.GetVersionAsync($"projects/{database.Project}/databases", database.Id, CancellationToken.None)).IsNotNull();
    }

    [Test]
    public async Task Authority_arriving_while_forgetting_a_replica_waits_for_verified_reader_closure()
    {
        await using var state = new PurgeData();
        var hook = new UsageCommitHook();
        var clearing = new TaskCompletionSource<UsageCommitHook.Gate>(TaskCreationOptions.RunContinuationsAsynchronously);
        var armed = false;
        await using var api = await state.StartAsync(services =>
        {
            hook.Install(services);
            services.AddSingleton<DatabaseLifecycleCheckpoint>((step, _) =>
            {
                if (armed && step == "cleanup.clearing")
                {
                    armed = false;
                    clearing.SetResult(hook.PauseWrite());
                }
            });
        });
        var database = await api.CreateManagedDatabaseAsync();
        var tenant = Tenant(database);
        var engine = api.Server.Services.GetRequiredService<IStorageEngine>();
        var store = (await engine.OpenDatabaseAsync(tenant, CancellationToken.None))!;
        var catalog = api.Server.Services.GetRequiredService<SystemCatalog>();
        var system = await engine.OpenSystemDatabaseAsync(CancellationToken.None);
        await using (var reader = await store.BeginTransactionAsync(write: false, CancellationToken.None))
        {
            _ = await reader.GetAsync("items", "held-reader", CancellationToken.None);
            await PurgeData.WriteAsync(api, transaction => SystemCatalog.PutAsync(transaction, $"projects/{database.Project}/databases", database.Id,
                new ManagedDatabase(database.Id, database.Project, database.Name, null, state.Clock.GetUtcNow(), "elsewhere"), TerraBaseJsonContext.Default.ManagedDatabase, CancellationToken.None));
            armed = true;
            var forgetting = api.Server.Services.GetRequiredService<EngineManagementService>().ForgetDatabaseAsync(tenant, CancellationToken.None).AsTask();
            using (var gate = await clearing.Task.WaitAsync(TestContext.Current!.Execution.CancellationToken))
            {
                await gate.Entered.Task.WaitAsync(TestContext.Current.Execution.CancellationToken);
                await Assert.That(File.Exists(state.Path(database))).IsFalse();
                await Assert.That(await engine.VerifyDatabaseErasureAsync(tenant, CancellationToken.None)).IsFalse();
                var now = state.Clock.GetUtcNow();
                await PurgeData.WriteAsync(api, transaction => SystemCatalog.PutAsync(transaction, DatabasePurge.Collection, database.Id,
                    new DatabasePurgeRecord(tenant, now, now.AddDays(30), now.AddDays(39), null, ["local"]), TerraBaseJsonContext.Default.DatabasePurgeRecord, CancellationToken.None));
            }

            await Assert.That(await forgetting is DatabaseForgotten { Forgotten: true }).IsTrue();
            await AssertPendingAsync();
            await PurgeData.ConvergeAsync(api);
            await AssertPendingAsync();
        }

        await PurgeData.ConvergeAsync(api);
        await Assert.That(await engine.VerifyDatabaseErasureAsync(tenant, CancellationToken.None)).IsTrue();
        await Assert.That(await catalog.AreActiveCopiesAbsentAsync(tenant, CancellationToken.None)).IsTrue();
        await using var completed = await system.BeginTransactionAsync(write: false, CancellationToken.None);
        await Assert.That(await completed.GetAsync("pending_database_cleanup", database.Id, CancellationToken.None)).IsNull();
        await Assert.That(await completed.GetAsync(DatabasePurge.Receipts(database.Id), "local", CancellationToken.None)).IsNotNull();

        async Task AssertPendingAsync()
        {
            await Assert.That(await catalog.AreActiveCopiesAbsentAsync(tenant, CancellationToken.None)).IsFalse();
            await using var read = await system.BeginTransactionAsync(write: false, CancellationToken.None);
            await Assert.That(await read.GetAsync("pending_database_cleanup", database.Id, CancellationToken.None)).IsNotNull();
            await Assert.That(await read.GetAsync(DatabasePurge.Receipts(database.Id), "local", CancellationToken.None)).IsNull();
        }
    }

    [Test]
    public async Task Forgetting_a_replica_does_not_create_customer_purge_authority()
    {
        await using var state = new PurgeData();
        await using var api = await state.StartAsync();
        var database = await api.CreateManagedDatabaseAsync();
        await PurgeData.WriteAsync(api, transaction => SystemCatalog.PutAsync(transaction, $"projects/{database.Project}/databases", database.Id,
            new ManagedDatabase(database.Id, database.Project, database.Name, null, state.Clock.GetUtcNow(), "elsewhere"), TerraBaseJsonContext.Default.ManagedDatabase, CancellationToken.None));
        await Assert.That(await api.Server.Services.GetRequiredService<EngineManagementService>().ForgetDatabaseAsync(Tenant(database), CancellationToken.None) is DatabaseForgotten { Forgotten: true }).IsTrue();
        var page = await api.Server.Services.GetRequiredService<SystemCatalog>().ListDatabasePurgesAsync(null, 100, CancellationToken.None);
        await Assert.That(page!.Items).IsEmpty();
    }

    private static TenantContext Tenant(ManagedDatabaseRef database) => new(database.Organization, PlanTier.Hobby, database.Project, database.Id);

    private sealed class PurgeData : IAsyncDisposable
    {
        private readonly string _directory = Directory.CreateTempSubdirectory("terrabase-purge-").FullName;
        private readonly FakeS3 _s3 = new();
        public string User { get; } = EntityIds.New().ToString();
        public FakeTimeProvider Clock { get; } = new(DateTimeOffset.UtcNow);

        public Task<ManagementApi> StartAsync(Action<IServiceCollection>? configure = null) => ManagementApi.StartAsync(settings =>
        {
            _s3.Configure(settings);
            settings["data-directory"] = _directory;
        }, services =>
        {
            ((BackupSettings)services.Single(service => service.ServiceType == typeof(BackupSettings)).ImplementationInstance!).RetentionDays =
                new Dictionary<PlanTier, int> { [PlanTier.Hobby] = 9, [PlanTier.Starter] = 3, [PlanTier.Pro] = 12, [PlanTier.Team] = 14, [PlanTier.Enterprise] = 33 };
            _s3.Route(services);
            ManualBackupHosting.Configure(services);
            var convergence = services.Single(descriptor => descriptor.ServiceType == typeof(IHostedService) && descriptor.ImplementationType == typeof(CatalogConvergence));
            services.Remove(convergence);
            configure?.Invoke(services);
        }, time: Clock, userId: User);

        public string Path(ManagedDatabaseRef database) => System.IO.Path.Combine(_directory, database.Organization, database.Project, database.Id + ".db");

        public static async Task WriteAsync(ManagementApi api, Func<IStorageTransaction, ValueTask> write)
        {
            var store = await api.Server.Services.GetRequiredService<IStorageEngine>().OpenSystemDatabaseAsync(CancellationToken.None);
            await using var transaction = await store.BeginTransactionAsync(write: true, CancellationToken.None);
            await write(transaction);
            await transaction.CommitAsync(CancellationToken.None);
        }

        public static Task ConvergeAsync(ManagementApi api) => api.Server.Services.GetRequiredService<EngineManagementService>().ConvergeAsync(CancellationToken.None).AsTask();

        public ValueTask DisposeAsync()
        {
            Directory.Delete(_directory, recursive: true);
            return ValueTask.CompletedTask;
        }
    }
}
