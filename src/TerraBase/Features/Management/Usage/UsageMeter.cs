using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using System.Globalization;
using TerraBase.Engine;
using TerraBase.Hosting;
using ZLogger;

namespace TerraBase.Features.Management.Usage;

internal readonly union UsageTotalsOutcome(IReadOnlyDictionary<string, long>, BackendUnavailable);

internal readonly union UsageMonthsOutcome(IReadOnlyList<string>, BackendUnavailable);

internal readonly union OrganizationCountersOutcome(OrganizationCounters, BackendUnavailable);

internal sealed record OrganizationCounters(IReadOnlyList<ProjectCounters> Projects, bool Truncated, bool Settled);

internal sealed record ProjectCounters(Ulid ProjectId, IReadOnlyDictionary<string, long> Metrics);

public static class UsageMetrics
{
    public const string Requests = "requests";

    public const string Reads = "reads";

    public const string Writes = "writes";

    public const string RowsRead = "rows_read";

    public const string RowsWritten = "rows_written";

    public const string StrongCommits = "strong_commits";

    public const string StorageByteHours = "storage_byte_hours";

    public static IReadOnlyList<string> All { get; } = [Requests, Reads, Writes, RowsRead, RowsWritten, StrongCommits, StorageByteHours];
}

internal sealed class UsageMeter(SystemDatabase system, TimeProvider time, IMeterFactory meters, ILogger<UsageMeter> logger) : BackgroundService
{
    private static readonly TimeSpan FlushInterval = TimeSpan.FromSeconds(10);

    private const int OrganizationProjectLimit = 1000;

    private readonly ConcurrentDictionary<(string Scope, string Month, string Metric), long> _pending = new();
    private readonly Dictionary<(string Scope, string Month, string Metric), long> _inFlight = [];
    private readonly Dictionary<string, long> _moves = new(StringComparer.Ordinal);
    private readonly Lock _gate = new();
    private readonly Counter<long> _recorded = meters.Create(Telemetry.MeterName).CreateCounter<long>("terrabase.usage.recorded");
    private volatile bool _flushFailing;

    public bool FlushFailing => _flushFailing;

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

    public async ValueTask<OrganizationCountersOutcome> OrganizationAsync(Ulid organizationId, string month, CancellationToken cancellationToken)
    {
        var before = Unflushed(month);
        var outcome = await system.QueryAsync(SqlBuild.Statement(
            "SELECT p.id, u.metric, u.value FROM (SELECT id FROM projects WHERE organization_id = ?1 ORDER BY id LIMIT ?2) AS p "
            + "LEFT JOIN usage_counters AS u ON u.scope = 'project:' || p.id AND u.month = ?3 ORDER BY p.id",
            SqlBuild.Id(organizationId), SqlBuild.Integer(OrganizationProjectLimit + 1), SqlBuild.Text(month)), cancellationToken);
        return outcome switch
        {
            IReadOnlyList<IReadOnlyList<SqlValue>> rows => Organization(rows, before, Settled(before, month)),
            BackendUnavailable unavailable => unavailable,
        };
    }

    public async ValueTask<UsageTotalsOutcome> ReadAsync(string scope, string month, CancellationToken cancellationToken)
    {
        var before = Unflushed(month);
        var outcome = await system.QueryAsync(SqlBuild.Statement("SELECT metric, value FROM usage_counters WHERE scope = ? AND month = ?", SqlBuild.Text(scope), SqlBuild.Text(month)),
            cancellationToken);
        return outcome switch
        {
            IReadOnlyList<IReadOnlyList<SqlValue>> rows => (IReadOnlyDictionary<string, long>)Totals(rows.Select(static row => (row.TextAt(0), row.IntegerAt(1))), before, scope),
            BackendUnavailable unavailable => unavailable,
        };
    }

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
        var drained = Drain();
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

        lock (_gate)
        {
            foreach (var (key, value) in drained)
            {
                if (failure is not null)
                {
                    _pending.AddOrUpdate(key, value, (_, current) => current + value);
                }

                _inFlight.Remove(key);
            }
        }

        _flushFailing = failure is not null;
        if (failure is not null)
        {
            logger.ZLogWarning($"Usage flush failed and is retried: {failure:@Reason}");
        }
    }

    private KeyValuePair<(string Scope, string Month, string Metric), long>[] Drain()
    {
        lock (_gate)
        {
            List<KeyValuePair<(string Scope, string Month, string Metric), long>> drained = [];
            foreach (var key in _pending.Keys)
            {
                if (_pending.TryRemove(key, out var value) && value > 0)
                {
                    _inFlight[key] = value;
                    drained.Add(new(key, value));
                }
            }

            foreach (var month in drained.Select(static item => item.Key.Month).Distinct(StringComparer.Ordinal))
            {
                _moves[month] = _moves.GetValueOrDefault(month) + 1;
            }

            return [.. drained];
        }
    }

    private UnflushedCounters Unflushed(string month)
    {
        lock (_gate)
        {
            Dictionary<(string Scope, string Metric), long> values = [];
            foreach (var (key, value) in _pending.Where(item => string.Equals(item.Key.Month, month, StringComparison.Ordinal)).Concat(
                _inFlight.Where(item => string.Equals(item.Key.Month, month, StringComparison.Ordinal))))
            {
                values[(key.Scope, key.Metric)] = values.GetValueOrDefault((key.Scope, key.Metric)) + value;
            }

            return new UnflushedCounters(values, _moves.GetValueOrDefault(month), _inFlight.Keys.Any(key => string.Equals(key.Month, month, StringComparison.Ordinal)));
        }
    }

    private bool Settled(UnflushedCounters before, string month)
    {
        lock (_gate)
        {
            return !before.InFlight && before.Moves == _moves.GetValueOrDefault(month);
        }
    }

    private static OrganizationCounters Organization(IReadOnlyList<IReadOnlyList<SqlValue>> rows, UnflushedCounters unflushed, bool settled)
    {
        List<ProjectCounters> projects = [];
        foreach (var group in rows.GroupBy(static row => row.TextAt(0), StringComparer.Ordinal).Take(OrganizationProjectLimit))
        {
            var stored = group.Where(static row => row.OptionalTextAt(1) is not null).Select(static row => (row.TextAt(1), row.IntegerAt(2)));
            projects.Add(new ProjectCounters(group.First().IdAt(0), Totals(stored, unflushed, string.Concat("project:", group.Key))));
        }

        return new OrganizationCounters(projects, rows.Select(static row => row.TextAt(0)).Distinct(StringComparer.Ordinal).Count() > OrganizationProjectLimit, settled);
    }

    private static Dictionary<string, long> Totals(IEnumerable<(string Metric, long Value)> stored, UnflushedCounters unflushed, string scope)
    {
        Dictionary<string, long> totals = new(StringComparer.Ordinal);
        foreach (var metric in UsageMetrics.All)
        {
            totals[metric] = unflushed.Values.GetValueOrDefault((scope, metric));
        }

        foreach (var (metric, value) in stored)
        {
            totals[metric] = totals.GetValueOrDefault(metric) + value;
        }

        return totals;
    }

    private static UsageMonthsOutcome Months(IReadOnlyList<IReadOnlyList<SqlValue>> rows) => new([.. rows.Select(static row => row.TextAt(0))]);

    private sealed record UnflushedCounters(IReadOnlyDictionary<(string Scope, string Metric), long> Values, long Moves, bool InFlight);
}
