using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using TerraBase.Engine;
using TerraBase.Errors;
using TerraBase.Features.Management;
using TerraBase.Features.Management.Backups;
using TerraBase.Hosting;
using static TerraBase.Features.Management.SystemCatalog;

namespace TerraBase.Fast.Backup;

public sealed class BackupLeaseTests
{
    [Test]
    public async Task A_sweep_keeps_a_snapshot_a_running_restore_copies_and_deletes_it_once_the_restore_settles(CancellationToken cancellationToken)
    {
        var gate = new CopyGate();
        await using var host = await SqlBackupHost.StartAsync(cancellationToken, gate.Decorate);
        await host.OkAsync("CREATE TABLE items (id TEXT PRIMARY KEY)");
        using var created = await host.Management.PostAsync(ManagementApi.Uri($"{host.Database.Path}/backups"), content: null, cancellationToken);
        var backupId = (await created.Content.ReadFromJsonAsync<JsonElement>(cancellationToken)).GetProperty("id").GetString();
        using var policy = await host.Management.PutAsJsonAsync(
            ManagementApi.Uri($"{host.Database.Path}/backup-policy"), new { retentionDays = 1 }, cancellationToken);
        await Assert.That(created.StatusCode).IsEqualTo(HttpStatusCode.Created);
        await Assert.That(policy.StatusCode).IsEqualTo(HttpStatusCode.OK);

        var catalog = host.Api.Server.Services.GetRequiredService<SystemCatalog>();
        await AgePastRetentionAsync(host, catalog, backupId!, cancellationToken);

        using var restored = await host.Management.PostAsJsonAsync(
            ManagementApi.Uri($"{host.Database.Path}/backups/{backupId}/restore"), new { name = "leased" }, cancellationToken);
        var operation = await restored.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
        await Assert.That(restored.StatusCode).IsEqualTo(HttpStatusCode.Accepted);
        var operationId = operation.GetProperty("operationId").GetString();
        await Assert.That(operation.GetProperty("status").GetString()).IsEqualTo("running");
        await gate.Entered.WaitAsync(cancellationToken);

        using var second = await host.Management.PostAsJsonAsync(
            ManagementApi.Uri($"{host.Database.Path}/backups/{backupId}/restore"), new { name = "also-leased" }, cancellationToken);
        await Assert.That(second.StatusCode).IsEqualTo(HttpStatusCode.Accepted);
        var secondId = (await second.Content.ReadFromJsonAsync<JsonElement>(cancellationToken)).GetProperty("operationId").GetString();
        await TerraBaseTestServer.EventuallyAsync(async token => (await LeasesAsync(catalog, host, backupId!, token)).Count == 2, timeout: TimeSpan.FromSeconds(30));

        var backups = host.Api.Server.Services.GetRequiredService<IManagedBackupService>();
        await backups.RunScheduleAsync(cancellationToken);
        var held = await RecordAsync(catalog, host, backupId!, cancellationToken);
        await Assert.That(held?.State).IsEqualTo(BackupState.Registered);
        await Assert.That((await LeasesAsync(catalog, host, backupId!, cancellationToken)).Select(lease => lease.OperationId))
            .IsEquivalentTo([operationId, secondId]);

        gate.Release();
        foreach (var id in new[] { operationId, secondId })
        {
            await TerraBaseTestServer.EventuallyAsync(async token =>
            {
                using var status = await host.Management.GetAsync(ManagementApi.Uri($"{host.Database.Path}/restores/{id}"), token);
                return status.StatusCode == HttpStatusCode.OK
                    && (await status.Content.ReadFromJsonAsync<JsonElement>(token)).GetProperty("status").GetString() == "completed";
            }, timeout: TimeSpan.FromSeconds(30));
        }

        var released = await RecordAsync(catalog, host, backupId!, cancellationToken);
        await Assert.That(released?.State).IsEqualTo(BackupState.Registered);
        await Assert.That(await LeasesAsync(catalog, host, backupId!, cancellationToken)).IsEmpty();

        await backups.RunScheduleAsync(cancellationToken);
        await Assert.That(await RecordAsync(catalog, host, backupId!, cancellationToken)).IsNull();
    }

    private static async ValueTask<BackupRecord?> RecordAsync(SystemCatalog catalog, SqlBackupHost host, string backupId, CancellationToken cancellationToken) =>
        await catalog.ReadAsync(
            transaction => GetAsync(transaction, BackupCatalog.Records(host.Database.Organization), backupId, ManagementJsonContext.Default.BackupRecord, cancellationToken),
            _ => (BackupRecord?)null, cancellationToken);

    private static async ValueTask<List<BackupLease>> LeasesAsync(SystemCatalog catalog, SqlBackupHost host, string backupId, CancellationToken cancellationToken)
    {
        var leases = await catalog.ReadAsync(
            transaction => SystemCatalog.ListAllAsync(
                transaction, BackupCatalog.Leases(host.Database.Organization), ManagementJsonContext.Default.BackupLease, cancellationToken),
            static _ => new List<BackupLease>(), cancellationToken);
        return [.. leases.Where(lease => lease.BackupId == backupId)];
    }

    private static async ValueTask AgePastRetentionAsync(SqlBackupHost host, SystemCatalog catalog, string backupId, CancellationToken cancellationToken)
    {
        var record = await RecordAsync(catalog, host, backupId, cancellationToken);
        var aged = record! with { CompletedAt = record!.CompletedAt!.Value.AddDays(-2) };
        var retention = BackupCatalog.Retention(host.Database.Organization, host.Database.Id);
        var order = record.Order!;
        var current = record.CompletedAt.Value;
        _ = await catalog.WriteAsync<CatalogCheck>(async transaction =>
        {
            await transaction.DeleteAsync(retention, current.UtcTicks.ToString("D19") + order, cancellationToken).ConfigureAwait(false);
            await PutAsync(transaction, retention, aged.CompletedAt!.Value.UtcTicks.ToString("D19") + order, aged.Id, TerraBaseJsonContext.Default.String, cancellationToken).ConfigureAwait(false);
            await PutAsync(transaction, BackupCatalog.Records(aged.OrganizationId), aged.Id, aged, ManagementJsonContext.Default.BackupRecord, cancellationToken).ConfigureAwait(false);
            return new Done();
        }, static error => error, cancellationToken);
    }
}
