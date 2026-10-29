using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Time.Testing;
using TerraBase.Configuration;
using TerraBase.Engine;
using TerraBase.Features.Management;
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
