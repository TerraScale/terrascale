using System.Text.Json;
using Cronos;
using TerraBase.Features.Management;
using TerraBase.Features.Webhooks;
using TerraBase.Hosting;
using TerraBase.Primitives;
using ZLogger;

namespace TerraBase.Features.Functions;

/// <summary>
/// Delivers change webhooks addressed to <c>terrabase-function:{project}/{database}/{databaseId}/{function}</c> in process. A
/// delivery whose body names another project or database than the URL and the function's own tenant is refused, so no tenant can
/// aim a webhook at another's function.
/// </summary>
internal sealed class FunctionTriggerReceiver(IServiceProvider services) : IWebhookLocalReceiver
{
    public const string Scheme = "terrabase-function:";

    public bool Accepts(string url) => url.StartsWith(Scheme, StringComparison.Ordinal);

    public async ValueTask<WebhookAttempt> ReceiveAsync(string url, ReadOnlyMemory<byte> body, CancellationToken cancellationToken)
    {
        var parts = url[Scheme.Length..].Split('/');
        var payload = JsonSerializer.Deserialize(body.Span, TerraBaseJsonContext.Default.WebhookPayload);
        if (parts.Length != 4 || payload is null || payload.ProjectId != parts[0] || payload.Database != parts[1] || !EntityIds.IsValid(parts[2]))
        {
            return new(null, "target_rejected");
        }

        // Resolved per delivery: the webhook transport is built before the function service, which depends on webhooks.
        return await services.GetRequiredService<FunctionService>().ReceiveAsync(payload, parts[2], parts[3], cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>Runs this machine's cron functions; a run missed while the machine was down is skipped, not replayed.</summary>
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
            _ = await functions.RunAsync(schedule.Tenant, schedule.Name, "cron", eventJson, readOnlyCollection: null, cancellationToken).ConfigureAwait(false);
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
