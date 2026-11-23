using System.Globalization;
using ZLogger;

namespace TerraScale.Platform.Features.Functions;

public sealed class FunctionScheduler(
    PlatformStore store,
    FunctionInvoker invoker,
    PlatformReadiness readiness,
    TimeProvider timeProvider,
    ILogger<FunctionScheduler> logger) : BackgroundService
{
    private const int BatchSize = 32;
    private const int Parallelism = 4;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1), timeProvider);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            if (!readiness.SchemaReady)
            {
                continue;
            }

            try
            {
                await TickAsync(stoppingToken);
            }
            catch (Exception error) when (!stoppingToken.IsCancellationRequested)
            {
                logger.ZLogWarning(error, $"Function schedule pass failed");
            }
        }
    }

    private async Task TickAsync(CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var dueOutcome = await store.ReadAsync(
            SqlValues.Statement(
                "SELECT database_id, name, organization_id, project_id, database_name, cron, cron_next FROM platform_functions "
                + "WHERE cron IS NOT NULL AND cron_next IS NOT NULL AND cron_next <= ? ORDER BY cron_next LIMIT ?",
                SqlValues.Number(now.UnixMs()),
                SqlValues.Number(BatchSize)),
            cancellationToken);
        var due = dueOutcome switch
        {
            SqlExecuteResponse { Results: var results } => results[0].RowsOf().Select(Schedule).ToArray(),
            PlatformConflict conflict => Skip(conflict.Message),
            PlatformUnavailable unavailable => Skip(unavailable.Message),
        };
        await Parallel.ForEachAsync(
            due,
            new ParallelOptions { MaxDegreeOfParallelism = Parallelism, CancellationToken = cancellationToken },
            async (schedule, token) => await FireAsync(schedule, now, token));
    }

    private async ValueTask FireAsync(DueSchedule schedule, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var next = FunctionService.NextRun(schedule.Cron, now);
        var claim = SqlValues.Statement(
            "UPDATE platform_functions SET cron_next = ? WHERE database_id = ? AND name = ? AND cron_next = ?",
            SqlValues.OptionalNumber(next),
            SqlValues.Text(schedule.Target.Database.ToString()),
            SqlValues.Text(schedule.Name),
            SqlValues.Number(schedule.Due));
        var claimedOutcome = await store.ExecuteAsync([claim], SqlContext.Strong, cancellationToken);
        var claimed = claimedOutcome switch
        {
            SqlExecuteResponse { Results: var results } => results[0].RowsAffected == 1,
            PlatformConflict => false,
            PlatformUnavailable => false,
        };
        if (!claimed)
        {
            return;
        }

        var scheduledAt = schedule.Due.FromUnixMs().UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);
        var eventJson = $$"""{"trigger":"cron","scheduledAt":"{{scheduledAt}}"}""";
        var outcomeOutcome = await invoker.InvokeAsync(schedule.Target, schedule.Name, "cron", eventJson, "", null, cancellationToken);
        var outcome = outcomeOutcome switch
        {
            FunctionAnswer answer => $"HTTP {answer.Status}",
            PlatformNotFound missing => missing.Message ?? "the function no longer exists",
            PlatformUnavailable unavailable => unavailable.Message,
        };
        logger.ZLogInformation($"Scheduled function {schedule.Name} on {schedule.Target.Database} ran: {outcome}");
    }

    private DueSchedule[] Skip(string message)
    {
        logger.ZLogWarning($"Function schedules are unreadable: {message}");
        return [];
    }

    private static DueSchedule Schedule(SqlRow row) =>
        new(
            new FunctionTarget(Ulid.Parse(row.Text(2)), Ulid.Parse(row.Text(3)), Ulid.Parse(row.Text(0)), row.Text(4)),
            row.Text(1),
            row.Text(5),
            row.Number(6));

    private sealed record DueSchedule(FunctionTarget Target, string Name, string Cron, long Due);
}
