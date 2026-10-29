using Cronos;
using TerraBase.Features.Management;
using TerraBase.Hosting;
using ZLogger;

namespace TerraBase.Features.Functions;

internal sealed class FunctionScheduler(SystemCatalog catalog, FunctionService functions, Configuration.TerraBaseOptions options, TimeProvider time,
    ILogger<FunctionScheduler> logger) : BackgroundService
{
    private readonly Dictionary<string, (string Cron, DateTimeOffset Next)> _next = [];
    private TaskCompletionSource _ticked = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public static bool IsValid(string cron) => Parse(cron) is not null;

    internal Task NextTickAsync() => Volatile.Read(ref _ticked).Task;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1), time);
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
        {
            try
            {
                await TickAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                logger.ZLogWarning(error, $"Function schedule pass failed");
            }

            Interlocked.Exchange(ref _ticked, new(TaskCreationOptions.RunContinuationsAsynchronously)).TrySetResult();
        }
    }

    private async Task TickAsync(CancellationToken cancellationToken)
    {
        var schedules = await catalog.ReadAsync<List<FunctionSchedule>?>(async transaction => await SystemCatalog.ListAllAsync(transaction,
            FunctionService.Schedules, TerraBaseJsonContext.Default.FunctionSchedule, cancellationToken).ConfigureAwait(false),
            static _ => null, cancellationToken).ConfigureAwait(false);
        if (schedules is null)
        {
            return;
        }

        var now = time.GetUtcNow();
        var mine = schedules.Where(schedule => schedule.Machine == options.MachineId).ToList();
        foreach (var gone in _next.Keys.Except(mine.Select(Key), StringComparer.Ordinal).ToList())
        {
            _ = _next.Remove(gone);
        }

        foreach (var schedule in mine)
        {
            var key = Key(schedule);
            if (Parse(schedule.Cron) is not { } expression)
            {
                continue;
            }

            if (!_next.TryGetValue(key, out var next) || next.Cron != schedule.Cron)
            {
                _next[key] = (schedule.Cron, expression.GetNextOccurrence(now, TimeZoneInfo.Utc) ?? DateTimeOffset.MaxValue);
                continue;
            }

            if (now < next.Next)
            {
                continue;
            }

            _next[key] = (schedule.Cron, expression.GetNextOccurrence(now, TimeZoneInfo.Utc) ?? DateTimeOffset.MaxValue);
            var eventJson = $$"""{"trigger":"cron","scheduledAt":"{{next.Next.UtcDateTime:yyyy-MM-ddTHH:mm:ssZ}}"}""";
            _ = await functions.RunAsync(schedule.Tenant, schedule.Name, "cron", eventJson, cancellationToken).ConfigureAwait(false);
        }
    }

    private static string Key(FunctionSchedule schedule) => FunctionService.ScheduleId(schedule.Tenant, schedule.Name);

    private static CronExpression? Parse(string cron)
    {
        try
        {
            return CronExpression.Parse(cron, CronFormat.Standard);
        }
        catch (CronFormatException)
        {
            return null;
        }
    }
}
