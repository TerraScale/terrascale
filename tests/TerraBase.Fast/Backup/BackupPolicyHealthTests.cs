using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Time.Testing;
using TerraBase.Engine;
using TerraBase.Engine.Turso;
using TerraBase.Features.Management.Backups;
using TerraBase.Primitives;
using TerraBase.Tenancy;
using TerraBase.Testing;

namespace TerraBase.Fast.Backup;

public sealed class BackupPolicyHealthTests
{
    [Test]
    public async Task A_scheduled_backup_success_shows_lastSucceededAt_and_a_future_nextDueAt()
    {
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        await using var api = await ManagementApi.StartAsync(time: time, services: WithoutScheduler);
        var database = await api.CreateManagedDatabaseAsync();
        using var client = api.WithToken(api.ProjectToken(database.Project, "databases:read databases:write"));
        var policy = $"/v1/projects/{database.Project}/databases/{database.Id}/backup-policy";
        using var set = await client.PutAsJsonAsync(ManagementApi.Uri(policy), new { fullBackupCron = "* * * * *", retentionDays = 7 });
        await Assert.That(set.StatusCode).IsEqualTo(HttpStatusCode.OK);

        time.Advance(TimeSpan.FromSeconds(90));
        await api.Server.Services.GetRequiredService<IManagedBackupService>().RunScheduleAsync(TestContext.Current!.Execution.CancellationToken);

        using var read = await client.GetAsync(ManagementApi.Uri(policy));
        await Assert.That(read.StatusCode).IsEqualTo(HttpStatusCode.OK);
        var status = await read.Content.ReadFromJsonAsync<JsonElement>();
        await Assert.That(status.GetProperty("lastSucceededAt").GetString()).IsNotNull();
        await Assert.That(status.GetProperty("lastFailedAt").GetString()).IsNull();
        await Assert.That(status.GetProperty("lastError").GetString()).IsNull();
        var succeeded = DateTimeOffset.Parse(status.GetProperty("lastSucceededAt").GetString()!);
        var nextDue = DateTimeOffset.Parse(status.GetProperty("nextDueAt").GetString()!);
        await Assert.That(nextDue).IsGreaterThan(succeeded);
    }

    [Test]
    public async Task A_failed_scheduled_attempt_shows_lastFailedAt_and_the_error_code()
    {
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        await using var api = await ManagementApi.StartAsync(time: time, services: services =>
        {
            WithoutScheduler(services);
            services.Replace(ServiceDescriptor.Singleton<IStorageEngine>(provider => new FailingCopyEngine(provider.GetRequiredService<TursoStorageEngine>())));
        });
        var database = await api.CreateManagedDatabaseAsync();
        using var client = api.WithToken(api.ProjectToken(database.Project, "databases:read databases:write"));
        var policy = $"/v1/projects/{database.Project}/databases/{database.Id}/backup-policy";
        using var set = await client.PutAsJsonAsync(ManagementApi.Uri(policy), new { fullBackupCron = "* * * * *", retentionDays = 7 });
        await Assert.That(set.StatusCode).IsEqualTo(HttpStatusCode.OK);

        time.Advance(TimeSpan.FromSeconds(90));
        await api.Server.Services.GetRequiredService<IManagedBackupService>().RunScheduleAsync(TestContext.Current!.Execution.CancellationToken);

        using var read = await client.GetAsync(ManagementApi.Uri(policy));
        await Assert.That(read.StatusCode).IsEqualTo(HttpStatusCode.OK);
        var status = await read.Content.ReadFromJsonAsync<JsonElement>();
        await Assert.That(status.GetProperty("lastFailedAt").GetString()).IsNotNull();
        await Assert.That(status.GetProperty("lastSucceededAt").GetString()).IsNull();
        await Assert.That(status.GetProperty("lastError").GetProperty("code").GetString()).IsEqualTo("snapshot_failed");
        await Assert.That(status.GetProperty("lastError").GetProperty("message").GetString()).IsNotNull();
    }

    private static void WithoutScheduler(IServiceCollection services)
    {
        var scheduler = services.Single(descriptor => descriptor.ServiceType == typeof(IHostedService)
            && descriptor.ImplementationType == typeof(BackupScheduler));
        services.Remove(scheduler);
    }

    private sealed class FailingCopyEngine(IStorageEngine engine) : IStorageEngine
    {
        public string Name => engine.Name;

        public StoragePressure StoragePressure => engine.StoragePressure;

        public long CommittedWrites => engine.CommittedWrites;

        public long? IoAdmittedCount => engine.IoAdmittedCount;

        public bool SystemDatabaseExists => engine.SystemDatabaseExists;

        public event EventHandler<IDatabaseStore>? DatabaseDeleted
        {
            add => engine.DatabaseDeleted += value;
            remove => engine.DatabaseDeleted -= value;
        }

        public ValueTask RefreshStoragePressureAsync(CancellationToken cancellationToken) => engine.RefreshStoragePressureAsync(cancellationToken);

        public ValueTask<bool> CreateDatabaseWithIntentAsync(
            TenantContext database, TenantContext? source, Func<CancellationToken, ValueTask<bool>> recordIntent, CancellationToken cancellationToken) =>
            engine.CreateDatabaseWithIntentAsync(database, source, recordIntent, cancellationToken);

        public ValueTask CompleteDatabaseIntentAsync(TenantContext database, CancellationToken cancellationToken) =>
            engine.CompleteDatabaseIntentAsync(database, cancellationToken);

        public ValueTask<bool> RecordDatabaseDeleteIntentAsync(
            TenantContext database, Func<CancellationToken, ValueTask<bool>> recordIntent, CancellationToken cancellationToken) =>
            engine.RecordDatabaseDeleteIntentAsync(database, recordIntent, cancellationToken);

        public ValueTask<bool> IsReadyAsync(CancellationToken cancellationToken) => engine.IsReadyAsync(cancellationToken);

        public ValueTask<bool> CreateDatabaseAsync(TenantContext database, CancellationToken cancellationToken) =>
            engine.CreateDatabaseAsync(database, cancellationToken);

        public ValueTask<IDatabaseStore?> OpenDatabaseAsync(TenantContext database, CancellationToken cancellationToken) =>
            engine.OpenDatabaseAsync(database, cancellationToken);

        public IAsyncEnumerable<TenantContext> ListDatabasesAsync(CancellationToken cancellationToken) => engine.ListDatabasesAsync(cancellationToken);

        public ValueTask<bool> CopyDatabaseAsync(TenantContext source, TenantContext target, CancellationToken cancellationToken) =>
            throw new IOException("simulated copy failure");

        public ValueTask<bool> DeleteDatabaseAsync(TenantContext database, CancellationToken cancellationToken, bool retainIntentOwnership = false, Ulid? incarnation = null) =>
            engine.DeleteDatabaseAsync(database, cancellationToken, retainIntentOwnership, incarnation);

        public ValueTask<bool> VerifyDatabaseErasureAsync(TenantContext database, CancellationToken cancellationToken) =>
            engine.VerifyDatabaseErasureAsync(database, cancellationToken);

        public IEnumerable<StoredDatabaseFile> EnumerateDatabaseFiles(CancellationToken cancellationToken = default) =>
            engine.EnumerateDatabaseFiles(cancellationToken);

        public ValueTask<long?> GetDatabaseSizeAsync(TenantContext database, CancellationToken cancellationToken) =>
            engine.GetDatabaseSizeAsync(database, cancellationToken);

        public ValueTask<IDatabaseStore> OpenSystemDatabaseAsync(CancellationToken cancellationToken) =>
            engine.OpenSystemDatabaseAsync(cancellationToken);

        public ValueTask DisposeAsync() => engine.DisposeAsync();
    }
}
