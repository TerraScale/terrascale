using System.Diagnostics.Metrics;
using TerraBase.Hosting;
using TerraBase.Tenancy;
using ZLogger;

namespace TerraBase.Features.Backup;

internal sealed class ShreddedDatabaseSweep(KeyCustody custody, KeyRevocationLocal local, TimeProvider time,
    IMeterFactory meters, ILogger<ShreddedDatabaseSweep> logger) : BackgroundService
{
    private string? _after;
    private readonly HashSet<string> _refused = [with(StringComparer.Ordinal)];
    private readonly Counter<long> _refusals = meters.Create(Telemetry.MeterName)
        .CreateCounter<long>("terrabase.keys.sweep_refused", description: "Protected databases whose shred records were refused by this process.");

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
                if (!_refused.Contains(database))
                {
                    if (ShredGuard.Refusal(tenant) is not null)
                    {
                        _ = _refused.Add(database);
                        _refusals.Add(1);
                        logger.ZLogWarning($"Refused a shred record for organization {organization:@Organization}, project {project:@Project}, database {database:@Database}: custody and local files stay untouched; an operator must inspect and clear the marker");
                    }
                    else if (!await local.RevokeAsync(tenant, token).ConfigureAwait(false))
                    {
                        return;
                    }
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
