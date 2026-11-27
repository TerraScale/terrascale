using System.Globalization;
using ZLogger;

namespace TerraScale.Platform.Features.Functions;

public sealed class FunctionScheduler(
    PlatformStore store,
    FunctionService functions,
    FunctionInvoker invoker,
    PlatformReadiness readiness,
    TimeProvider timeProvider,
    ILogger<FunctionScheduler> logger) : BackgroundService
{
    private const int BatchSize = 32;
    private const int Parallelism = 4;
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan IdleInterval = TimeSpan.FromSeconds(15);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var backoff = Interval;
        var wait = Interval;
        var idle = false;
        while (true)
        {
            await (idle ? functions.Scheduled.WaitAsync(wait, timeProvider, stoppingToken) : Task.Delay(wait, timeProvider, stoppingToken));
            functions.Scheduled.Clear();
            if (!readiness.SchemaReady)
            {
                (backoff, wait, idle) = (Interval, Interval, false);
                continue;
            }

            var pass = new SchedulePass(true, null);
            try
            {
                pass = await TickAsync(stoppingToken);
            }
            catch (Exception error) when (!stoppingToken.IsCancellationRequested)
            {
                logger.ZLogWarning(error, $"Function schedule pass failed");
            }

            idle = !pass.Fired;
            backoff = idle ? IdleWake.Backoff(backoff, IdleInterval) : Interval;
            wait = idle && pass.NextIn is { } nextIn && nextIn < backoff ? nextIn : backoff;
        }
    }

    private async Task<SchedulePass> TickAsync(CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var nowMs = now.UnixMs();
        var upcomingOutcome = await store.ReadAsync(
            SqlValues.Statement(
                "SELECT f.database_id, f.name, f.organization_id, f.project_id, f.database_name, f.cron, f.cron_next FROM platform_functions f "
                + $"WHERE f.cron IS NOT NULL AND f.cron_next IS NOT NULL AND {FunctionService.Open} ORDER BY f.cron_next LIMIT ?",
                SqlValues.Number(BatchSize)),
            cancellationToken);
        var upcoming = upcomingOutcome switch
        {
            SqlExecuteResponse { Results: var results } => results[0].RowsOf().Select(Schedule).ToArray(),
            PlatformConflict conflict => Skip(conflict.Message),
            PlatformUnavailable unavailable => Skip(unavailable.Message),
        };
        DueSchedule[] due = [.. upcoming.Where(schedule => schedule.Due <= nowMs)];
        await Parallel.ForEachAsync(
            due,
            new ParallelOptions { MaxDegreeOfParallelism = Parallelism, CancellationToken = cancellationToken },
            async (schedule, token) => await FireAsync(schedule, now, token));
        return new SchedulePass(
            due.Length > 0,
            upcoming.FirstOrDefault(schedule => schedule.Due > nowMs) is { } next ? next.Due.FromUnixMs() - now : null);
    }

    private async ValueTask FireAsync(DueSchedule schedule, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var next = FunctionService.NextRun(schedule.Cron, now);
        var project = SqlValues.Text(schedule.Target.Project.ToString());
        var organization = SqlValues.Text(schedule.Target.Organization.ToString());
        var database = SqlValues.Text(schedule.Target.Database.ToString());
        var function = SqlValues.Text(schedule.Name);
        var due = SqlValues.Number(schedule.Due);
        var fence = FunctionService.FenceCronClaim(project, organization, database, function, due, now);
        var claim = SqlValues.Statement(
            "UPDATE platform_functions SET cron_next = ? WHERE database_id = ? AND name = ? AND cron_next = ? "
            + "AND NOT EXISTS (SELECT 1 FROM platform_closed_file_projects c WHERE c.project_id = platform_functions.project_id)",
            SqlValues.OptionalNumber(next),
            database,
            function,
            due);
        var claimedOutcome = await store.ExecuteAsync([fence, claim], SqlContext.Strong, cancellationToken);
        var claimed = claimedOutcome switch
        {
            SqlExecuteResponse { Results: var results } => results[1].RowsAffected == 1,
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

    private sealed record SchedulePass(bool Fired, TimeSpan? NextIn);
}
