using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using TerraBase.Configuration;
using TerraBase.Features.Backup;
using TerraBase.Features.Management;
using TerraBase.Features.Management.Backups;
using TerraBase.Hosting;
using TerraBase.Tenancy;
using static TerraBase.Features.Management.SystemCatalog;

namespace TerraBase.Fast.Backup;

public sealed class PointInTimeLeaseTests
{
    [Test]
    public async Task A_held_point_in_time_restore_keeps_its_generation_through_retention_and_expires_after_release(CancellationToken cancellationToken)
    {
        var gate = new CopyGate();
        await using var host = await SqlBackupHost.StartAsync(cancellationToken, services =>
        {
            ((BackupSettings)services.Single(service => service.ServiceType == typeof(BackupSettings)).ImplementationInstance!).RetentionDays =
                new Dictionary<PlanTier, int> { [PlanTier.Hobby] = 1, [PlanTier.Starter] = 1, [PlanTier.Pro] = 1, [PlanTier.Team] = 1, [PlanTier.Enterprise] = 1 };
            gate.Decorate(services);
        });
        await host.OkAsync("CREATE TABLE items (id TEXT PRIMARY KEY)");
        await host.OkAsync("INSERT INTO items VALUES ('one')");
        await host.SnapshotAsync(cancellationToken);
        var shipped = host.S3.Objects.Keys.Where(key => key.StartsWith(host.File + "/", StringComparison.Ordinal)).ToList();
        await Assert.That(shipped.Count).IsGreaterThan(0);

        using var restore = await host.Management.PostAsJsonAsync(
            ManagementApi.Uri($"{host.Database.Path}/point-in-time/restore"), new { pointInTime = host.SnapshotAt, name = "held" }, cancellationToken);
        var operation = await restore.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
        await Assert.That(restore.StatusCode).IsEqualTo(HttpStatusCode.Accepted).Because(operation.GetRawText());
        var operationId = operation.GetProperty("operationId").GetString();
        await gate.Entered.WaitAsync(cancellationToken);

        host.Time.Advance(TimeSpan.FromDays(3));
        var catalog = host.Api.Server.Services.GetRequiredService<SystemCatalog>();
        await TerraBaseTestServer.EventuallyAsync(
            async token => await LeaseUntilAsync(catalog, host, operationId!, token).ConfigureAwait(false) is { } until && until > host.Time.GetUtcNow(),
            timeout: TimeSpan.FromSeconds(10));

        await host.ShipAsync(TimeSpan.Zero, cancellationToken);
        foreach (var key in shipped)
        {
            await Assert.That(host.S3.Objects.ContainsKey(key)).IsTrue();
        }

        var status = await StatusAsync(catalog, host, cancellationToken);
        await Assert.That(status?.UpdatedAt >= host.Time.GetUtcNow()).IsTrue();

        gate.Release();
        await TerraBaseTestServer.EventuallyAsync(async token =>
        {
            using var status = await host.Management.GetAsync(ManagementApi.Uri($"{host.Database.Path}/restores/{operationId}"), token);
            return status.StatusCode == HttpStatusCode.OK
                && (await status.Content.ReadFromJsonAsync<JsonElement>(token)).GetProperty("status").GetString() == "completed";
        }, timeout: TimeSpan.FromSeconds(30));
        await TerraBaseTestServer.EventuallyAsync(
            async token => await LeaseUntilAsync(catalog, host, operationId!, token).ConfigureAwait(false) is null, timeout: TimeSpan.FromSeconds(10));

        await host.ShipAsync(TimeSpan.FromHours(2), cancellationToken);
        await Assert.That(shipped.Where(key => !key.Contains("/generations/", StringComparison.Ordinal) && host.S3.Objects.ContainsKey(key))).IsEmpty();
    }

    private static async ValueTask<BackupStatusRecord?> StatusAsync(SystemCatalog catalog, SqlBackupHost host, CancellationToken cancellationToken)
    {
        var (kind, id) = ContinuousBackup.RecordKey(host.Tenant);
        return await catalog.ReadAsync(
            transaction => GetAsync(transaction, kind, id, TerraBaseJsonContext.Default.BackupStatusRecord, cancellationToken),
            static _ => (BackupStatusRecord?)null, cancellationToken);
    }

    private static async ValueTask<DateTimeOffset?> LeaseUntilAsync(SystemCatalog catalog, SqlBackupHost host, string operationId, CancellationToken cancellationToken)
    {
        var leases = await catalog.ReadAsync(
            transaction => SystemCatalog.ListAllAsync(
                transaction, BackupCatalog.GenerationLeases(host.Database.Organization), TerraBaseJsonContext.Default.RestoreGenerationLease, cancellationToken),
            static _ => new List<RestoreGenerationLease>(), cancellationToken);
        return leases.FirstOrDefault(lease => lease.OperationId == operationId)?.Until;
    }
}
