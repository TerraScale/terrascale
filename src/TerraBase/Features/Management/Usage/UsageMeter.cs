using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using System.Globalization;
using TerraBase.Engine;
using TerraBase.Hosting;
using ZLogger;

namespace TerraBase.Features.Management.Usage;

internal readonly union UsageTotalsOutcome(IReadOnlyDictionary<string, long>, BackendUnavailable);

internal readonly union UsageMonthsOutcome(IReadOnlyList<string>, BackendUnavailable);

public static class UsageMetrics
{
    public const string Requests = "requests";

    public const string Reads = "reads";

    public const string Writes = "writes";

    public const string RowsRead = "rows_read";

    public const string RowsWritten = "rows_written";

    public const string StrongCommits = "strong_commits";

    public static IReadOnlyList<string> All { get; } = [Requests, Reads, Writes, RowsRead, RowsWritten, StrongCommits];
}

internal sealed class UsageMeter(SystemDatabase system, TimeProvider time, IMeterFactory meters, ILogger<UsageMeter> logger) : BackgroundService
{
    private static readonly TimeSpan FlushInterval = TimeSpan.FromSeconds(10);

    private readonly ConcurrentDictionary<(string Scope, string Month, string Metric), long> _pending = new();
    private readonly Counter<long> _recorded = meters.Create(Telemetry.MeterName).CreateCounter<long>("terrabase.usage.recorded");

    public static string DatabaseScope(Ulid databaseId) => string.Concat("db:", databaseId.ToString());

    public static string ProjectScope(Ulid projectId) => string.Concat("project:", projectId.ToString());

    public static string Month(DateTimeOffset at) => at.ToString("yyyy-MM", CultureInfo.InvariantCulture);

    public void Record(Ulid projectId, Ulid databaseId, string metric, long value)
    {
        if (value <= 0)
        {
            return;
        }

        var month = Month(time.GetUtcNow());
        _pending.AddOrUpdate((DatabaseScope(databaseId), month, metric), value, (_, current) => current + value);
        _pending.AddOrUpdate((ProjectScope(projectId), month, metric), value, (_, current) => current + value);
        _recorded.Add(value, new KeyValuePair<string, object?>("metric", metric));
    }

    public async ValueTask<UsageTotalsOutcome> ReadAsync(string scope, string month, CancellationToken cancellationToken) =>
        await system.QueryAsync(SqlBuild.Statement("SELECT metric, value FROM usage_counters WHERE scope = ? AND month = ?", SqlBuild.Text(scope), SqlBuild.Text(month)),
            cancellationToken) switch
        {
            IReadOnlyList<IReadOnlyList<SqlValue>> rows => Totals(rows, scope, month),
            BackendUnavailable unavailable => unavailable,
        };

    public async ValueTask<UsageMonthsOutcome> MonthsAsync(string scope, CancellationToken cancellationToken) =>
        await system.QueryAsync(SqlBuild.Statement("SELECT DISTINCT month FROM usage_counters WHERE scope = ? ORDER BY month DESC LIMIT 36", SqlBuild.Text(scope)),
            cancellationToken) switch
        {
            IReadOnlyList<IReadOnlyList<SqlValue>> rows => Months(rows),
            BackendUnavailable unavailable => unavailable,
        };

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(FlushInterval, time);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                await FlushAsync(stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken);
        await FlushAsync(cancellationToken);
    }

    private async Task FlushAsync(CancellationToken cancellationToken)
    {
        var drained = _pending.Keys.Select(key => (Key: key, Value: _pending.TryRemove(key, out var value) ? value : 0)).Where(static item => item.Value > 0).ToArray();
        if (drained.Length == 0)
        {
            return;
        }

        SqlStatement[] statements =
        [
            .. drained.Select(static item => SqlBuild.Statement(
                "INSERT INTO usage_counters (scope, month, metric, value) VALUES (?, ?, ?, ?) ON CONFLICT (scope, month, metric) DO UPDATE SET value = value + excluded.value",
                SqlBuild.Text(item.Key.Scope), SqlBuild.Text(item.Key.Month), SqlBuild.Text(item.Key.Metric), SqlBuild.Integer(item.Value))),
        ];
        string? failure;
        try
        {
            var written = await system.WriteAsync(statements, ConsistencyMode.Fast, cancellationToken);
            failure = written switch
            {
                SqlWriteResult => null,
                StateConflict conflict => conflict.Message,
                BackendUnavailable unavailable => unavailable.Message,
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            failure = "usage flush canceled";
        }

        if (failure is not null)
        {
            foreach (var (key, value) in drained)
            {
                _pending.AddOrUpdate(key, value, (_, current) => current + value);
            }

            logger.ZLogWarning($"Usage flush failed and is retried: {failure:@Reason}");
        }
    }

    private UsageTotalsOutcome Totals(IReadOnlyList<IReadOnlyList<SqlValue>> rows, string scope, string month)
    {
        Dictionary<string, long> totals = new(StringComparer.Ordinal);
        foreach (var metric in UsageMetrics.All)
        {
            totals[metric] = _pending.GetValueOrDefault((scope, month, metric));
        }

        foreach (var row in rows)
        {
            totals[row.TextAt(0)] = totals.GetValueOrDefault(row.TextAt(0)) + row.IntegerAt(1);
        }

        return totals;
    }

    private static UsageMonthsOutcome Months(IReadOnlyList<IReadOnlyList<SqlValue>> rows) => new([.. rows.Select(static row => row.TextAt(0))]);
}
