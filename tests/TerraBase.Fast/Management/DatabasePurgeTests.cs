using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Time.Testing;
using TerraBase.Configuration;
using TerraBase.Engine;
using TerraBase.Features.Management;
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
        using var deleted = await api.ProjectClient(database.Project).DeleteAsync(new Uri(database.Path, UriKind.Relative));
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
        await PurgeData.RunMaintenanceAsync(api);
        var retried = (await catalog.ListDatabasePurgesAsync(null, 100, CancellationToken.None))!.Items.Single();
        await Assert.That(retried.RequestedAt).IsEqualTo(record.RequestedAt);
        await Assert.That(retried.ActiveDueAt).IsEqualTo(record.ActiveDueAt);
        await Assert.That(retried.BackupDueAt).IsEqualTo(record.BackupDueAt);
        await Assert.That(await catalog.AreActiveCopiesAbsentAsync(tenant, CancellationToken.None)).IsTrue();
        await Assert.That(await catalog.MarkBackupVerifiedAsync(tenant, state.Clock.GetUtcNow(), CancellationToken.None)).IsTrue();
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
            var convergence = services.Single(descriptor => descriptor.ServiceType == typeof(IHostedService) && descriptor.ImplementationType == typeof(CatalogMaintenance));
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

        public static Task RunMaintenanceAsync(ManagementApi api) => api.Server.Services.GetRequiredService<EngineManagementService>().RunMaintenanceAsync(CancellationToken.None).AsTask();

        public ValueTask DisposeAsync()
        {
            Directory.Delete(_directory, recursive: true);
            return ValueTask.CompletedTask;
        }
    }
}
