using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using TerraBase.Configuration;
using TerraBase.Engine;
using TerraBase.Features.Management;
using TerraBase.Hosting;
using TerraBase.Primitives;
using TerraBase.Tenancy;
using TerraBase.Testing;

namespace TerraBase.Fast.Management;

public sealed partial class DatabaseRecoveryTests
{
    [Test]
    [Arguments("creation", false)]
    [Arguments("pending", false)]
    [Arguments("purge", false)]
    [Arguments("registered", false)]
    [Arguments("creation", true)]
    [Arguments("pending", true)]
    [Arguments("purge", true)]
    [Arguments("registered", true)]
    public async Task Owned_files_are_not_recorded_and_becoming_owned_drops_first_seen(string protection, bool previouslyOrphan, CancellationToken token)
    {
        await using var api = await ManagementApi.StartAsync(services: ManualAudit);
        var registered = await api.CreateManagedDatabaseAsync();
        var orphan = new TenantContext(registered.Organization, PlanTier.Hobby, registered.Project, EntityIds.New().ToString());
        var engine = api.Server.Services.GetRequiredService<IStorageEngine>();
        if (protection == "creation" && !previouslyOrphan)
        {
            await engine.CreateDatabaseWithIntentAsync(orphan, null, _ => ValueTask.FromResult(true), token);
        }
        else
        {
            await engine.CreateDatabaseAsync(orphan, token);
        }
        if (previouslyOrphan)
        {
            await AuditAsync(api, token);
            await Assert.That(await ObservationAsync(api, orphan)).IsNotNull();
        }
        if (protection == "creation" && previouslyOrphan)
        {
            await engine.RecordDatabaseDeleteIntentAsync(orphan, _ => ValueTask.FromResult(true), token);
        }
        var system = await engine.OpenSystemDatabaseAsync(token);
        await using (var transaction = await system.BeginTransactionAsync(write: true, token))
        {
            if (protection == "pending")
            {
                await SystemCatalog.PutAsync(transaction, "pending_database_cleanup", orphan.DatabaseId!, orphan, TerraBaseJsonContext.Default.TenantContext, token);
            }
            if (protection == "purge")
            {
                await SystemCatalog.PutAsync(transaction, DatabasePurge.Collection, orphan.DatabaseId!, new DatabasePurgeRecord(orphan, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddDays(1), DateTimeOffset.UtcNow.AddDays(30), null, ["test-node"]), TerraBaseJsonContext.Default.DatabasePurgeRecord, token);
            }
            if (protection == "registered")
            {
                await SystemCatalog.PutAsync(transaction, $"projects/{orphan.ProjectId}/databases", orphan.DatabaseId!, new ManagedDatabase(orphan.DatabaseId!, orphan.ProjectId!, "adopted", null, DateTimeOffset.UtcNow), TerraBaseJsonContext.Default.ManagedDatabase, token);
            }
            await transaction.CommitAsync(token);
        }
        await AuditAsync(api, token);
        await Assert.That(await ObservationAsync(api, orphan)).IsNull();
        await Assert.That(File.Exists(Path.Combine(api.Server.DataDirectory, orphan.OrganizationId, orphan.ProjectId!, orphan.DatabaseId + ".db"))).IsTrue();
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task An_orphan_past_its_grace_period_is_deleted_unless_it_was_registered(bool registerFirst, CancellationToken token)
    {
        await using var api = await ManagementApi.StartAsync(services: ManualAudit);
        var registered = await api.CreateManagedDatabaseAsync();
        var orphan = new TenantContext(registered.Organization, PlanTier.Hobby, registered.Project, EntityIds.New().ToString());
        var engine = api.Server.Services.GetRequiredService<IStorageEngine>();
        await engine.CreateDatabaseAsync(orphan, token);
        var path = Path.Combine(api.Server.DataDirectory, orphan.OrganizationId, orphan.ProjectId!, orphan.DatabaseId + ".db");
        await AuditAsync(api, token);
        await Assert.That(File.Exists(path)).IsTrue();
        var seen = await ObservationAsync(api, orphan);
        var system = await engine.OpenSystemDatabaseAsync(token);
        await using (var transaction = await system.BeginTransactionAsync(write: true, token))
        {
            await SystemCatalog.PutAsync(transaction, "orphan_database_files/" + api.Server.Services.GetRequiredService<TerraBaseOptions>().MachineId,
                $"{orphan.OrganizationId}:{orphan.ProjectId}:{orphan.DatabaseId}", seen! with { FirstSeen = DateTimeOffset.UtcNow.AddHours(-25) },
                TerraBaseJsonContext.Default.OrphanDatabaseFile, token);
            if (registerFirst)
            {
                await SystemCatalog.PutAsync(transaction, $"projects/{orphan.ProjectId}/databases", orphan.DatabaseId!, new ManagedDatabase(orphan.DatabaseId!, orphan.ProjectId!, "adopted", null, DateTimeOffset.UtcNow), TerraBaseJsonContext.Default.ManagedDatabase, token);
            }
            await transaction.CommitAsync(token);
        }
        await AuditAsync(api, token);
        await Assert.That(File.Exists(path)).IsEqualTo(registerFirst);
        await Assert.That(await ObservationAsync(api, orphan)).IsNull();
        await Assert.That(await CatalogAsync(api, "pending_database_cleanup", orphan.DatabaseId!)).IsNull();
        await Assert.That(File.Exists(Path.Combine(api.Server.DataDirectory, registered.Organization, registered.Project, registered.Id + ".db"))).IsTrue();
    }

    // Backup frames and native temporary files filled every node's log with one warning per file per pass (2026-10-27).
    [Test]
    public async Task Scratch_files_are_not_audited_and_an_orphan_file_is_named_once(CancellationToken token)
    {
        var logs = new CapturedLogs();
        await using var api = await ManagementApi.StartAsync(services: services =>
        {
            ManualAudit(services);
            services.AddSingleton<ILoggerProvider>(logs);
        });
        string[] scratch = [.. new[] { ".tmp", ".backup", ".import", ".exports" }.Select(name =>
            Path.Combine(api.Server.DataDirectory, name, EntityIds.New() + ".frames"))];
        foreach (var path in scratch)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllBytesAsync(path, [1], token);
        }

        var registered = await api.CreateManagedDatabaseAsync();
        var orphan = new TenantContext(registered.Organization, PlanTier.Hobby, registered.Project, EntityIds.New().ToString());
        var engine = api.Server.Services.GetRequiredService<IStorageEngine>();
        await engine.CreateDatabaseAsync(orphan, token);
        await AuditAsync(api, token);
        await AuditAsync(api, token);

        await Assert.That(engine.EnumerateDatabaseFiles(token).Select(file => file.Path).Intersect(scratch)).IsEmpty();
        var main = Path.Combine(api.Server.DataDirectory, orphan.OrganizationId, orphan.ProjectId!, orphan.DatabaseId + ".db");
        string[] files = [.. logs.Messages.Where(message => message.StartsWith("Warning: Preserved database file without a catalog registration: ", StringComparison.Ordinal))];
        await Assert.That(files).IsEquivalentTo(["Warning: Preserved database file without a catalog registration: " + main]);
        await Assert.That(logs.Messages.Count(message => message.StartsWith("Warning: Preserved database without a catalog registration: " + orphan.DatabaseId + ",",
            StringComparison.Ordinal))).IsEqualTo(2);
    }

    private static void ManualAudit(IServiceCollection services) => services.Remove(services.Single(descriptor =>
        descriptor.ServiceType == typeof(IHostedService) && descriptor.ImplementationType == typeof(CatalogConvergence)));
    private static ValueTask AuditAsync(ManagementApi api, CancellationToken token) => api.Server.Services.GetRequiredService<EngineManagementService>().AuditFilesAsync(token);
    private static async Task<OrphanDatabaseFile?> ObservationAsync(ManagementApi api, TenantContext database) =>
        JsonSerializer.Deserialize(await CatalogAsync(api, "orphan_database_files/" + api.Server.Services.GetRequiredService<TerraBaseOptions>().MachineId,
            $"{database.OrganizationId}:{database.ProjectId}:{database.DatabaseId}") ?? "null", TerraBaseJsonContext.Default.OrphanDatabaseFile);

    private static async Task AssertFileLoggedAsync(ManagementApi api, CapturedLogs logs, string path)
    {
        var database = api.Server.Services.GetRequiredService<IStorageEngine>().EnumerateDatabaseFiles().Single(file => file.Path == path).Database;
        var prefix = database is null ? "Warning: Preserved database file without a catalog registration: " + path
            : "Warning: Preserved database without a catalog registration: " + database.DatabaseId + ",";
        await Assert.That(logs.Messages.Count(message => message.StartsWith(prefix, StringComparison.Ordinal)
            || database is not null && message.StartsWith("Warning: Database file audit could not read the current catalog", StringComparison.Ordinal))).IsEqualTo(1);
    }
}
