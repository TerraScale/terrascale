using TerraBase.Tenancy;
using ZLogger;

namespace TerraBase.Features.Backup;

// The unsnapshotted custody tombstone is deletion authority even when a region's catalog was offline at DELETE.
// Each pass scans at most 128 keys. Every node, including regional hold partners, consumes the same authority.
internal sealed class ShreddedDatabaseSweep(KeyCustody custody, KeyRevocationLocal local, TimeProvider time,
    ILogger<ShreddedDatabaseSweep> logger) : BackgroundService
{
    private string? _after;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(2), time);
        do
        {
            await Task.Yield();
            try
            {
                await RunOnceAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (Exception) when (!stoppingToken.IsCancellationRequested)
            {
                logger.ZLogWarning($"Deleted database custody discovery is unavailable; its durable cursor retries");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
    }

    internal async Task RunOnceAsync(CancellationToken token)
    {
        var count = 0;
        await foreach (var item in custody.Store.ListAsync("v1/keys/", _after, token).ConfigureAwait(false))
        {
            if (item.Key.Split('/') is ["v1", "keys", var organization, var project, var database, "shredded"]
                && Ulid.TryParse(organization, out _) && Ulid.TryParse(project, out _) && Ulid.TryParse(database, out _))
            {
                var tenant = new TenantContext(organization, PlanTier.Hobby, project, database);
                var scope = BackupLayout.KeyScope(tenant);
                var record = await ShredRecords.ReadAsync(custody.Store, scope, token).ConfigureAwait(false);
                if (record is not null)
                {
                    custody.Revocations.Forget(scope);
                }
                if (record is null || !await local.RevokeAsync(tenant, token).ConfigureAwait(false))
                {
                    return; // Never advance past authority the catalog could not durably accept.
                }
            }
            _after = item.Key;
            if (++count == 128)
            {
                return;
            }
        }
        _after = null;
    }
}
