using TerraBase.Configuration;
using ZLogger;

namespace TerraBase.Engine;

internal sealed class TenantMemoryReclaimerService(TenantDatabases tenants, TerraBaseOptions options, TimeProvider time, ILogger<TenantMemoryReclaimerService> logger) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan ReaderIdle = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan WriterIdle = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan ColdTenantIdle = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan PressureReaderIdle = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan PressureTenantIdle = TimeSpan.FromSeconds(30);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval, time);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            var reclaimed = UnderPressure() ? await ReclaimUnderPressureAsync() : await ReclaimIdleAsync();
            if (reclaimed.Evicted > 0 || reclaimed.Trimmed > 0)
            {
                logger.ZLogInformation($"Reclaimed tenant memory: trimmed {reclaimed.Trimmed:@Trimmed} idle resources, evicted {reclaimed.Evicted:@Evicted} idle sessions; working set {Environment.WorkingSet:@WorkingSet} bytes, budget {options.MemoryBudget:@MemoryBudget} bytes");
            }
        }
    }

    private bool UnderPressure() => Environment.WorkingSet > options.MemoryBudget;

    private async ValueTask<Reclaimed> ReclaimIdleAsync()
    {
        var trimmed = tenants.TrimIdle(ReaderIdle, WriterIdle);
        var evicted = 0;
        foreach (var databaseId in tenants.IdleDatabaseIds(ColdTenantIdle))
        {
            if (await tenants.EvictIdleAsync(databaseId, ColdTenantIdle))
            {
                evicted++;
            }
        }

        return new Reclaimed(trimmed, evicted);
    }

    private async ValueTask<Reclaimed> ReclaimUnderPressureAsync()
    {
        var trimmed = tenants.TrimIdle(PressureReaderIdle, TimeSpan.Zero);
        var evicted = 0;
        foreach (var databaseId in tenants.IdleDatabaseIds(PressureTenantIdle))
        {
            if (!UnderPressure())
            {
                break;
            }

            if (await tenants.EvictIdleAsync(databaseId, PressureTenantIdle))
            {
                evicted++;
            }
        }

        return new Reclaimed(trimmed, evicted);
    }

    private readonly record struct Reclaimed(int Trimmed, int Evicted);
}
