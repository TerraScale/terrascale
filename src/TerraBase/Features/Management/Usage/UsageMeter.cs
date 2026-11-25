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

internal sealed record UsageSubject(string Scope, string StorageScope, string StorageFrom, string StorageTo)
{
    public static UsageSubject Project(Ulid projectId) => new(UsageMeter.ProjectScope(projectId), UsageMeter.StorageScope(projectId), "", "~");

    public static UsageSubject Database(Ulid projectId, Ulid databaseId) =>
        new(UsageMeter.DatabaseScope(databaseId), UsageMeter.StorageScope(projectId), string.Concat(databaseId.ToString(), ":"), string.Concat(databaseId.ToString(), ";"));
}

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

internal sealed class UsageMeter(SystemDatabase system, UsageCommits commits, ClusterOptions cluster, TimeProvider time, IMeterFactory meters, ILogger<UsageMeter> logger)
    : BackgroundService
{
    private static readonly TimeSpan FlushInterval = TimeSpan.FromSeconds(10);

    private const int OrganizationProjectLimit = 1000;

    private const int RetainedBatches = 64;

    private const string BatchSlot = "-";

    private const string BatchMetric = "batch";

    private const string BatchRow = "#batch";

    private const string StorageRow = "#storage";

    private const string StoragePrefix = "storage:";

    private readonly ConcurrentDictionary<(string Scope, string Month, string Metric), long> _pending = new();
    private readonly List<FlushBatch> _batches = [];
    private readonly Lock _gate = new();
    private readonly Counter<long> _recorded = meters.Create(Telemetry.MeterName).CreateCounter<long>("terrabase.usage.recorded");
    private long _committed;
    private long _sequence;
    private bool _watermarkLoaded;
    private CommitMetadata? _unconfirmed;
    private volatile bool _flushFailing;

    public bool FlushFailing => _flushFailing;

    private string BatchScope => string.Concat("flush:", cluster.NodeId);

    public static string DatabaseScope(Ulid databaseId) => string.Concat("db:", databaseId.ToString());

    public static string ProjectScope(Ulid projectId) => string.Concat("project:", projectId.ToString());

    public static string StorageScope(Ulid projectId) => string.Concat(StoragePrefix, projectId.ToString());

    public static string StorageMetric(Ulid databaseId, long hour) => string.Concat(databaseId.ToString(), ":", hour.ToString("D10", CultureInfo.InvariantCulture));

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
            + "UNION ALL SELECT substr(s.scope, 9), ?4, sum(s.value) FROM usage_counters AS s WHERE s.month = ?3 "
            + "AND s.scope IN (SELECT 'storage:' || id FROM projects WHERE organization_id = ?1 ORDER BY id LIMIT ?2) GROUP BY s.scope "
            + "UNION ALL SELECT ?5, ?5, value FROM usage_counters WHERE scope = ?6 AND month = ?7 AND metric = ?8",
            SqlBuild.Id(organizationId), SqlBuild.Integer(OrganizationProjectLimit + 1), SqlBuild.Text(month), SqlBuild.Text(StorageRow), SqlBuild.Text(BatchRow),
            SqlBuild.Text(BatchScope), SqlBuild.Text(BatchSlot), SqlBuild.Text(BatchMetric)), cancellationToken);
        return outcome switch
        {
            IReadOnlyList<IReadOnlyList<SqlValue>> rows => Organization(rows, month),
            BackendUnavailable unavailable => unavailable,
        };
    }

    public async ValueTask<UsageTotalsOutcome> ReadAsync(UsageSubject subject, string month, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(subject);
        var outcome = await system.QueryAsync(SqlBuild.Statement(
            "SELECT metric, value FROM usage_counters WHERE scope = ?1 AND month = ?2 "
            + "UNION ALL SELECT ?3, value FROM usage_counters WHERE scope = ?4 AND month = ?5 AND metric = ?6 "
            + "UNION ALL SELECT ?7, coalesce(sum(value), 0) FROM usage_counters WHERE scope = ?8 AND month = ?2 AND metric >= ?9 AND metric < ?10",
            SqlBuild.Text(subject.Scope), SqlBuild.Text(month), SqlBuild.Text(BatchRow), SqlBuild.Text(BatchScope), SqlBuild.Text(BatchSlot), SqlBuild.Text(BatchMetric),
            SqlBuild.Text(StorageRow), SqlBuild.Text(subject.StorageScope), SqlBuild.Text(subject.StorageFrom), SqlBuild.Text(subject.StorageTo)), cancellationToken);
        return outcome switch
        {
            IReadOnlyList<IReadOnlyList<SqlValue>> rows => (IReadOnlyDictionary<string, long>)Totals(
                rows.Where(static row => row.TextAt(0) != BatchRow).Select(static row => (row.TextAt(0), row.IntegerAt(1))),
                Unflushed(month, Committed(rows.Where(static row => row.TextAt(0) == BatchRow).Select(static row => row.IntegerAt(1)))), subject.Scope),
            BackendUnavailable unavailable => unavailable,
        };
    }

    public async ValueTask<UsageMonthsOutcome> MonthsAsync(UsageSubject subject, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(subject);
        var outcome = await system.QueryAsync(SqlBuild.Statement(
            "SELECT month FROM usage_counters WHERE scope = ?1 UNION SELECT month FROM usage_counters WHERE scope = ?2 AND metric >= ?3 AND metric < ?4 "
            + "ORDER BY month DESC LIMIT 36",
            SqlBuild.Text(subject.Scope), SqlBuild.Text(subject.StorageScope), SqlBuild.Text(subject.StorageFrom), SqlBuild.Text(subject.StorageTo)), cancellationToken);
        return outcome switch
        {
            IReadOnlyList<IReadOnlyList<SqlValue>> rows => Months(rows),
            BackendUnavailable unavailable => unavailable,
        };
    }

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
        if (_unconfirmed is { } unconfirmed)
        {
            if (!await commits.ConfirmAsync(unconfirmed, cancellationToken))
            {
                Fail("the previous usage commit is still not replicated in the region");
                return;
            }

            _unconfirmed = null;
        }

        if (!_watermarkLoaded)
        {
            var loaded = await system.QueryAsync(SqlBuild.Statement("SELECT value FROM usage_counters WHERE scope = ?1 AND month = ?2 AND metric = ?3",
                SqlBuild.Text(BatchScope), SqlBuild.Text(BatchSlot), SqlBuild.Text(BatchMetric)), cancellationToken);
            var persisted = loaded switch
            {
                IReadOnlyList<IReadOnlyList<SqlValue>> rows => rows is [var row, ..] ? row.IntegerAt(0) : 0,
                BackendUnavailable => -1,
            };
            if (persisted < 0)
            {
                Fail("the usage flush watermark is unreadable");
                return;
            }

            Seed(persisted);
            _watermarkLoaded = true;
        }

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
            var outcome = await commits.WriteAsync(statements, cancellationToken);
            var written = outcome switch
            {
                UsageCommitted committed => committed,
                BackendUnavailable => null,
            };
            if (written is null)
            {
                Fail("the usage batch did not commit and is retried");
                return;
            }

            Commit(batch.Sequence);
            if (!written.Durable)
            {
                _unconfirmed = written.Written.Commit;
                Fail("the usage batch committed locally and its regional replication is confirmed again");
                return;
            }
        }

        _flushFailing = false;
    }

    private void Fail(string reason)
    {
        _flushFailing = true;
        logger.ZLogWarning($"Usage flush incomplete: {reason:@Reason}");
    }

    private void Seed(long persisted)
    {
        lock (_gate)
        {
            _sequence = Math.Max(_sequence, persisted);
            _committed = Math.Max(_committed, persisted);
        }
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

    private static Dictionary<string, long> Totals(IEnumerable<(string Metric, long Value)> stored, Dictionary<(string Scope, string Metric), long> unflushed, string scope)
    {
        Dictionary<string, long> totals = new(StringComparer.Ordinal);
        foreach (var metric in UsageMetrics.All)
        {
            totals[metric] = unflushed.GetValueOrDefault((scope, metric));
        }

        foreach (var (metric, value) in stored)
        {
            var name = metric == StorageRow ? UsageMetrics.StorageByteHours : metric;
            totals[name] = totals.GetValueOrDefault(name) + value;
        }

        return totals;
    }

    private static UsageMonthsOutcome Months(IReadOnlyList<IReadOnlyList<SqlValue>> rows) => new([.. rows.Select(static row => row.TextAt(0))]);

    private sealed record FlushBatch(long Sequence, IReadOnlyDictionary<(string Scope, string Month, string Metric), long> Counters);
}
