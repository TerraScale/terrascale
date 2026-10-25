using System.Collections.Concurrent;
using System.Globalization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using TerraBase.Configuration;
using TerraBase.Engine;
using TerraBase.Features.Management;
using TerraBase.Hosting;
using TerraBase.Primitives;
using TerraBase.Tenancy;
using TerraBase.Testing;

namespace TerraBase.Fast.Management;

public sealed class OrphanDatabaseFileTests
{
    [Test]
    public async Task Orphans_are_logged_and_measured_then_closed_and_deleted_only_after_a_grace_period_that_survives_restart(CancellationToken token)
    {
        var directory = Directory.CreateTempSubdirectory("terrabase-orphan-sweep-").FullName;
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var logs = new Logs();
        TenantContext orphan;
        ManagedDatabaseRef registered;
        DateTimeOffset firstSeen;
        try
        {
            await using (var api = await StartAsync(directory, time, logs))
            {
                registered = await api.CreateManagedDatabaseAsync();
                orphan = new TenantContext(registered.Organization, PlanTier.Hobby, registered.Project, EntityIds.New().ToString());
                await api.Server.Services.GetRequiredService<IStorageEngine>().CreateDatabaseAsync(orphan, token);
                await AuditAsync(api, token);
                firstSeen = (await ObservationAsync(api, orphan, token))!.FirstSeen;
                await Assert.That(logs.Messages.Any(message => message.Contains("first seen", StringComparison.Ordinal) && message.Contains(orphan.DatabaseId!, StringComparison.Ordinal))).IsTrue();
                var metrics = await api.Server.Client.GetStringAsync(ManagementApi.Uri("/metrics"), token);
                await Assert.That(Sample(metrics, "files")).IsGreaterThanOrEqualTo(2);
                await Assert.That(Sample(metrics, "bytes")).IsGreaterThan(0);
                time.Advance(TimeSpan.FromHours(23));
                await AuditAsync(api, token);
                await Assert.That(File.Exists(PathFor(orphan))).IsTrue();
            }
            await using var restarted = await StartAsync(directory, time, logs);
            await Assert.That((await ObservationAsync(restarted, orphan, token))!.FirstSeen).IsEqualTo(firstSeen);
            var opened = await restarted.Server.Services.GetRequiredService<IStorageEngine>().OpenDatabaseAsync(orphan, token);
            time.Advance(TimeSpan.FromHours(2));
            await AuditAsync(restarted, token);
            await Assert.That(opened!.IsClosed).IsTrue();
            await Assert.That(File.Exists(PathFor(orphan))).IsFalse();
            await Assert.That(File.Exists(PathFor(orphan) + ".key")).IsFalse();
            await Assert.That(File.Exists(Path.Combine(directory, registered.Organization, registered.Project, registered.Id + ".db"))).IsTrue();
            await Assert.That(await ObservationAsync(restarted, orphan, token)).IsNull();
            var after = await restarted.Server.Client.GetStringAsync(ManagementApi.Uri("/metrics"), token);
            await Assert.That(Sample(after, "files")).IsEqualTo(0);
            await Assert.That(Sample(after, "bytes")).IsEqualTo(0);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
        string PathFor(TenantContext database)
        {
            return Path.Combine(directory, database.OrganizationId, database.ProjectId!, database.DatabaseId + ".db");
        }
    }

    [Test]
    [Arguments("creation")]
    [Arguments("pending")]
    [Arguments("purge")]
    [Arguments("unsupported")]
    public async Task Owned_or_unverifiable_files_are_preserved_after_the_grace_period(string protection, CancellationToken token)
    {
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        await using var api = await ManagementApi.StartAsync(services: ManualAudit, time: time);
        var registered = await api.CreateManagedDatabaseAsync();
        var orphan = new TenantContext(registered.Organization, PlanTier.Hobby, registered.Project, EntityIds.New().ToString());
        var engine = api.Server.Services.GetRequiredService<IStorageEngine>();
        if (protection == "creation")
        {
            await engine.CreateDatabaseWithIntentAsync(orphan, null, _ => ValueTask.FromResult(true), token);
        }
        else
        {
            await engine.CreateDatabaseAsync(orphan, token);
        }
        await AuditAsync(api, token);
        var system = await engine.OpenSystemDatabaseAsync(token);
        await using (var transaction = await system.BeginTransactionAsync(write: true, token))
        {
            if (protection == "pending")
            {
                await SystemCatalog.PutAsync(transaction, "pending_database_cleanup", orphan.DatabaseId!, orphan, TerraBaseJsonContext.Default.TenantContext, token);
            }
            else if (protection == "purge")
            {
                await SystemCatalog.PutAsync(transaction, DatabasePurge.Collection, orphan.DatabaseId!,
                    new DatabasePurgeRecord(orphan, time.GetUtcNow(), time.GetUtcNow().AddDays(1), time.GetUtcNow().AddDays(30), null, ["test-node"]),
                    TerraBaseJsonContext.Default.DatabasePurgeRecord, token);
            }
            else if (protection == "unsupported")
            {
                await transaction.PutAsync("catalog", "version", "\"99\""u8.ToArray(), token);
            }
            await transaction.CommitAsync(token);
        }
        time.Advance(TimeSpan.FromHours(25));
        await AuditAsync(api, token);
        await Assert.That(File.Exists(Path.Combine(api.Server.DataDirectory, orphan.OrganizationId, orphan.ProjectId!, orphan.DatabaseId + ".db"))).IsTrue();
    }

    private static Task<ManagementApi> StartAsync(string directory, TimeProvider time, Logs logs) =>
        ManagementApi.StartAsync(settings => settings["data-directory"] = directory, services =>
        {
            ManualAudit(services);
            services.AddSingleton<ILoggerProvider>(logs);
        }, time);

    private static void ManualAudit(IServiceCollection services) => services.Remove(services.Single(descriptor =>
        descriptor.ServiceType == typeof(IHostedService) && descriptor.ImplementationType == typeof(CatalogConvergence)));

    private static ValueTask AuditAsync(ManagementApi api, CancellationToken token) =>
        api.Server.Services.GetRequiredService<EngineManagementService>().AuditFilesAsync(token);

    private static double Sample(string metrics, string kind) => double.Parse(metrics.Split('\n')
        .Single(line => line.StartsWith("terrabase_storage_unknown_" + kind + "{", StringComparison.Ordinal)).Split(' ')[1], CultureInfo.InvariantCulture);

    private static async Task<OrphanDatabaseFile?> ObservationAsync(ManagementApi api, TenantContext database, CancellationToken token)
    {
        var system = await api.Server.Services.GetRequiredService<IStorageEngine>().OpenSystemDatabaseAsync(token);
        await using var transaction = await system.BeginTransactionAsync(write: false, token);
        return await SystemCatalog.GetAsync(transaction, "orphan_database_files/" + api.Server.Services.GetRequiredService<TerraBaseOptions>().MachineId,
            $"{database.OrganizationId}:{database.ProjectId}:{database.DatabaseId}", TerraBaseJsonContext.Default.OrphanDatabaseFile, token);
    }

    private sealed class Logs : ILoggerProvider, ILogger
    {
        public ConcurrentQueue<string> Messages { get; } = new();
        public ILogger CreateLogger(string categoryName) => this;
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) => Messages.Enqueue(formatter(state, exception));
        public void Dispose() { }
    }
}
