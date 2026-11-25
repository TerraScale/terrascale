using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using System.Globalization;
using TerraBase.Cluster;
using TerraBase.Engine;
using TerraBase.Hosting;
using ZLogger;

namespace TerraBase.Features.Management.Usage;

internal readonly union UsageTotalsOutcome(IReadOnlyDictionary<string, long>, BackendUnavailable);

internal readonly union UsageMonthsOutcome(IReadOnlyList<string>, BackendUnavailable);

internal readonly union OrganizationCountersOutcome(OrganizationCounters, BackendUnavailable);

internal sealed record OrganizationCounters(IReadOnlyList<ProjectCounters> Projects, bool Truncated);

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

internal sealed class UsageMeter(SystemDatabase system, ClusterOptions cluster, TimeProvider time, IMeterFactory meters, ILogger<UsageMeter> logger) : BackgroundService
{
    private static readonly TimeSpan FlushInterval = TimeSpan.FromSeconds(10);

    private const int OrganizationProjectLimit = 1000;

    private const int RetainedBatches = 64;

    private const string BatchSlot = "-";

    private const string BatchMetric = "batch";

    private const string BatchRow = "#batch";

    private readonly ConcurrentDictionary<(string Scope, string Month, string Metric), long> _pending = new();
    private readonly List<FlushBatch> _batches = [];
    private readonly Lock _gate = new();
    private readonly Counter<long> _recorded = meters.Create(Telemetry.MeterName).CreateCounter<long>("terrabase.usage.recorded");
    private long _committed;
    private long _sequence;
    private volatile bool _flushFailing;

    public bool FlushFailing => _flushFailing;

    private string BatchScope => string.Concat("flush:", cluster.NodeId);

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
        var outcome = await system.QueryAsync(SqlBuild.Statement(
            "SELECT p.id, u.metric, u.value FROM (SELECT id FROM projects WHERE organization_id = ?1 ORDER BY id LIMIT ?2) AS p "
            + "LEFT JOIN usage_counters AS u ON u.scope = 'project:' || p.id AND u.month = ?3 "
            + "UNION ALL SELECT ?4, ?4, value FROM usage_counters WHERE scope = ?5 AND month = ?6 AND metric = ?7",
            SqlBuild.Id(organizationId), SqlBuild.Integer(OrganizationProjectLimit + 1), SqlBuild.Text(month), SqlBuild.Text(BatchRow), SqlBuild.Text(BatchScope),
            SqlBuild.Text(BatchSlot), SqlBuild.Text(BatchMetric)), cancellationToken);
        return outcome switch
        {
            IReadOnlyList<IReadOnlyList<SqlValue>> rows => Organization(rows, month),
            BackendUnavailable unavailable => unavailable,
        };
    }

    public async ValueTask<UsageTotalsOutcome> ReadAsync(string scope, string month, CancellationToken cancellationToken)
    {
        var outcome = await system.QueryAsync(SqlBuild.Statement(
            "SELECT metric, value FROM usage_counters WHERE scope = ?1 AND month = ?2 UNION ALL SELECT ?3, value FROM usage_counters WHERE scope = ?4 AND month = ?5 AND metric = ?6",
            SqlBuild.Text(scope), SqlBuild.Text(month), SqlBuild.Text(BatchRow), SqlBuild.Text(BatchScope), SqlBuild.Text(BatchSlot), SqlBuild.Text(BatchMetric)), cancellationToken);
        return outcome switch
        {
            IReadOnlyList<IReadOnlyList<SqlValue>> rows => (IReadOnlyDictionary<string, long>)Totals(
                rows.Where(static row => row.TextAt(0) != BatchRow).Select(static row => (row.TextAt(0), row.IntegerAt(1))),
                Unflushed(month, Committed(rows.Where(static row => row.TextAt(0) == BatchRow).Select(static row => row.IntegerAt(1)))), scope),
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
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            await FlushAsync(stoppingToken);
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken);
        await FlushAsync(cancellationToken);
    }

    private async Task FlushAsync(CancellationToken cancellationToken)
    {
        while (NextBatch() is { } batch)
        {
            List<SqlStatement> statements =
            [
                .. batch.Counters.Select(item => SqlBuild.Statement(
                    "INSERT INTO usage_counters (scope, month, metric, value) SELECT ?1, ?2, ?3, ?4 "
                    + "WHERE NOT EXISTS (SELECT 1 FROM usage_counters WHERE scope = ?5 AND month = ?6 AND metric = ?7 AND value >= ?8) "
                    + "ON CONFLICT (scope, month, metric) DO UPDATE SET value = value + excluded.value",
                    SqlBuild.Text(item.Key.Scope), SqlBuild.Text(item.Key.Month), SqlBuild.Text(item.Key.Metric), SqlBuild.Integer(item.Value),
                    SqlBuild.Text(BatchScope), SqlBuild.Text(BatchSlot), SqlBuild.Text(BatchMetric), SqlBuild.Integer(batch.Sequence))),
                SqlBuild.Statement(
                    "INSERT INTO usage_counters (scope, month, metric, value) VALUES (?1, ?2, ?3, ?4) "
                    + "ON CONFLICT (scope, month, metric) DO UPDATE SET value = excluded.value WHERE excluded.value > usage_counters.value",
                    SqlBuild.Text(BatchScope), SqlBuild.Text(BatchSlot), SqlBuild.Text(BatchMetric), SqlBuild.Integer(batch.Sequence)),
            ];
            var written = await system.WriteAsync(statements, ConsistencyMode.Fast, cancellationToken);
            var failure = written switch
            {
                SqlWriteResult => null,
                StateConflict conflict => conflict.Message,
                BackendUnavailable unavailable => unavailable.Message,
            };
            if (failure is not null)
            {
                _flushFailing = true;
                logger.ZLogWarning($"Usage flush failed and its batch is retried: {failure:@Reason}");
                return;
            }

            Commit(batch.Sequence);
        }

        _flushFailing = false;
    }

    private FlushBatch? NextBatch()
    {
        lock (_gate)
        {
            if (_batches.FirstOrDefault(batch => batch.Sequence > _committed) is { } retry)
            {
                return retry;
            }

            Dictionary<(string Scope, string Month, string Metric), long> drained = [];
            foreach (var key in _pending.Keys)
            {
                if (_pending.TryRemove(key, out var value) && value > 0)
                {
                    drained[key] = value;
                }
            }

            if (drained.Count == 0)
            {
                return null;
            }

            _sequence = Math.Max(_sequence + 1, time.GetUtcNow().ToUnixTimeMilliseconds());
            var batch = new FlushBatch(_sequence, drained);
            _batches.Add(batch);
            return batch;
        }
    }

    private void Commit(long sequence)
    {
        lock (_gate)
        {
            _committed = Math.Max(_committed, sequence);
            var retired = _batches.Count(batch => batch.Sequence <= sequence) - RetainedBatches;
            if (retired > 0)
            {
                _batches.RemoveRange(0, retired);
            }
        }
    }

    private static long Committed(IEnumerable<long> rows) => rows.DefaultIfEmpty(0).Max();

    private Dictionary<(string Scope, string Metric), long> Unflushed(string month, long committed)
    {
        lock (_gate)
        {
            Dictionary<(string Scope, string Metric), long> values = [];
            foreach (var (key, value) in _pending.Where(item => string.Equals(item.Key.Month, month, StringComparison.Ordinal)).Concat(
                _batches.Where(batch => batch.Sequence > committed).SelectMany(static batch => batch.Counters)
                    .Where(item => string.Equals(item.Key.Month, month, StringComparison.Ordinal))))
            {
                values[(key.Scope, key.Metric)] = values.GetValueOrDefault((key.Scope, key.Metric)) + value;
            }

            return values;
        }
    }

    private OrganizationCounters Organization(IReadOnlyList<IReadOnlyList<SqlValue>> rows, string month)
    {
        var unflushed = Unflushed(month, Committed(rows.Where(static row => row.TextAt(0) == BatchRow).Select(static row => row.IntegerAt(2))));
        var groups = rows.Where(static row => row.TextAt(0) != BatchRow).GroupBy(static row => row.TextAt(0), StringComparer.Ordinal)
            .OrderBy(static group => group.Key, StringComparer.Ordinal).ToArray();
        List<ProjectCounters> projects = [];
        foreach (var group in groups.Take(OrganizationProjectLimit))
        {
            var stored = group.Where(static row => row.OptionalTextAt(1) is not null).Select(static row => (row.TextAt(1), row.IntegerAt(2)));
            projects.Add(new ProjectCounters(group.First().IdAt(0), Totals(stored, unflushed, string.Concat("project:", group.Key))));
        }

        return new OrganizationCounters(projects, groups.Length > OrganizationProjectLimit);
    }

    private static Dictionary<string, long> Totals(IEnumerable<(string Metric, long Value)> stored, IReadOnlyDictionary<(string Scope, string Metric), long> unflushed, string scope)
    {
        Dictionary<string, long> totals = new(StringComparer.Ordinal);
        foreach (var metric in UsageMetrics.All)
        {
            totals[metric] = unflushed.GetValueOrDefault((scope, metric));
        }

        foreach (var (metric, value) in stored)
        {
            totals[metric] = totals.GetValueOrDefault(metric) + value;
        }

        return totals;
    }

    private static UsageMonthsOutcome Months(IReadOnlyList<IReadOnlyList<SqlValue>> rows) => new([.. rows.Select(static row => row.TextAt(0))]);

    private sealed record FlushBatch(long Sequence, IReadOnlyDictionary<(string Scope, string Month, string Metric), long> Counters);
}
