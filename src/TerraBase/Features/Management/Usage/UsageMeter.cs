using System.Diagnostics.Metrics;
using System.Globalization;
using System.Runtime.InteropServices;
using TerraBase.Cluster;
using TerraBase.Engine;
using TerraBase.Hosting;
using ZLogger;

namespace TerraBase.Features.Management.Usage;

internal readonly union UsageTotalsOutcome(IReadOnlyDictionary<string, long>, BackendUnavailable);

internal readonly union UsageMonthsOutcome(IReadOnlyList<string>, BackendUnavailable);

internal readonly union OrganizationCountersOutcome(OrganizationCounters, BackendUnavailable);

internal sealed record OrganizationCounters(IReadOnlyList<ProjectCounters> Projects, bool Truncated, bool Dropped, IReadOnlyDictionary<string, DateTimeOffset> Writers);

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

    private static readonly TimeSpan BatchRetention = TimeSpan.FromMinutes(1);

    private const int OrganizationProjectLimit = 1000;

    private const string WriterSlot = "-";

    private const string BatchMetric = "batch";

    private const string HeartbeatMetric = "at";

    private const string WriterPrefix = "flush:";

    private const string BatchRow = "#batch";

    private const string StorageRow = "#storage";

    private const string StoragePrefix = "storage:";

    private const string DroppedRow = "#dropped";

    private const string WriterRow = "#writer";

    private const string DroppedScope = "usage-dropped";

    private const int MaxPendingCounters = 100_000;

    private const int MaxBatchCounters = 1000;

    private readonly Dictionary<(string Scope, string Month, string Metric), long> _pending = [];
    private readonly HashSet<string> _droppedMonths = new(StringComparer.Ordinal);
    private readonly List<FlushBatch> _batches = [];
    private readonly Lock _gate = new();
    private readonly Counter<long> _recorded = meters.Create(Telemetry.MeterName).CreateCounter<long>("terrabase.usage.recorded");
    private readonly Counter<long> _dropped = meters.Create(Telemetry.MeterName).CreateCounter<long>("terrabase.usage.dropped");
    private Task _flushing = Task.CompletedTask;
    private long _committed;
    private long _evicted;
    private long _sequence;
    private bool _watermarkLoaded;
    private volatile CommitMetadata? _unconfirmed;
    private volatile bool _lastFailed;

    public bool FlushFailing => _lastFailed || _unconfirmed is not null;

    private string WriterScope => string.Concat(WriterPrefix, cluster.NodeId);

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
        var database = (DatabaseScope(databaseId), month, metric);
        var project = (ProjectScope(projectId), month, metric);
        bool admitted;
        lock (_gate)
        {
            var growth = (_pending.ContainsKey(database) ? 0 : 1) + (_pending.ContainsKey(project) ? 0 : 1);
            admitted = _pending.Count + growth <= MaxPendingCounters;
            if (admitted)
            {
                CollectionsMarshal.GetValueRefOrAddDefault(_pending, database, out _) += value;
                CollectionsMarshal.GetValueRefOrAddDefault(_pending, project, out _) += value;
            }
            else
            {
                _droppedMonths.Add(month);
            }
        }

        (admitted ? _recorded : _dropped).Add(value, new KeyValuePair<string, object?>("metric", metric));
    }

    public async ValueTask<OrganizationCountersOutcome> OrganizationAsync(Ulid organizationId, string month, CancellationToken cancellationToken)
    {
        var outcome = await system.QueryAsync(SqlBuild.Statement(
            "SELECT p.id, u.metric, u.value FROM (SELECT id FROM projects WHERE organization_id = ?1 ORDER BY id LIMIT ?2) AS p "
            + "LEFT JOIN usage_counters AS u ON u.scope = 'project:' || p.id AND u.month = ?3 "
            + "UNION ALL SELECT substr(s.scope, 9), ?4, sum(s.value) FROM usage_counters AS s WHERE s.month = ?3 "
            + "AND s.scope IN (SELECT 'storage:' || id FROM projects WHERE organization_id = ?1 ORDER BY id LIMIT ?2) GROUP BY s.scope "
            + "UNION ALL SELECT ?5, ?5, value FROM usage_counters WHERE scope = ?6 AND month = ?7 AND metric = ?8 "
            + "UNION ALL SELECT ?9, ?9, count(*) FROM usage_counters WHERE scope = ?10 AND month = ?3 "
            + "UNION ALL SELECT ?11, scope, value FROM usage_counters WHERE scope >= ?12 AND scope < ?13 AND month = ?7 AND metric = ?14",
            SqlBuild.Id(organizationId), SqlBuild.Integer(OrganizationProjectLimit + 1), SqlBuild.Text(month), SqlBuild.Text(StorageRow), SqlBuild.Text(BatchRow),
            SqlBuild.Text(WriterScope), SqlBuild.Text(WriterSlot), SqlBuild.Text(BatchMetric), SqlBuild.Text(DroppedRow), SqlBuild.Text(DroppedScope),
            SqlBuild.Text(WriterRow), SqlBuild.Text(WriterPrefix), SqlBuild.Text("flush;"), SqlBuild.Text(HeartbeatMetric)), cancellationToken);
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
            SqlBuild.Text(subject.Scope), SqlBuild.Text(month), SqlBuild.Text(BatchRow), SqlBuild.Text(WriterScope), SqlBuild.Text(WriterSlot), SqlBuild.Text(BatchMetric),
            SqlBuild.Text(StorageRow), SqlBuild.Text(subject.StorageScope), SqlBuild.Text(subject.StorageFrom), SqlBuild.Text(subject.StorageTo)), cancellationToken);
        return outcome switch
        {
            IReadOnlyList<IReadOnlyList<SqlValue>> rows => Subject(rows, subject, month),
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

    private Task FlushAsync(CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            _flushing = AfterAsync(_flushing, cancellationToken);
            return _flushing;
        }
    }

    private async Task AfterAsync(Task previous, CancellationToken cancellationToken)
    {
        _ = await Task.WhenAny(previous);
        await FlushOnceAsync(cancellationToken);
    }

    private async Task FlushOnceAsync(CancellationToken cancellationToken)
    {
        if (_unconfirmed is { } unconfirmed)
        {
            if (!await commits.FinalizeAsync(unconfirmed, cancellationToken))
            {
                Fail("the previous usage commit is still not replicated in the region");
                return;
            }

            _unconfirmed = null;
        }

        if (!_watermarkLoaded)
        {
            var loaded = await system.QueryAsync(SqlBuild.Statement("SELECT value FROM usage_counters WHERE scope = ?1 AND month = ?2 AND metric = ?3",
                SqlBuild.Text(WriterScope), SqlBuild.Text(WriterSlot), SqlBuild.Text(BatchMetric)), cancellationToken);
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

        var wrote = false;
        while (!cancellationToken.IsCancellationRequested && NextBatch() is { } batch)
        {
            List<SqlStatement> statements =
            [
                .. batch.Counters.Select(item => SqlBuild.Statement(
                    "INSERT INTO usage_counters (scope, month, metric, value) SELECT ?1, ?2, ?3, ?4 "
                    + "WHERE NOT EXISTS (SELECT 1 FROM usage_counters WHERE scope = ?5 AND month = ?6 AND metric = ?7 AND value >= ?8) "
                    + "ON CONFLICT (scope, month, metric) DO UPDATE SET value = value + excluded.value",
                    SqlBuild.Text(item.Key.Scope), SqlBuild.Text(item.Key.Month), SqlBuild.Text(item.Key.Metric), SqlBuild.Integer(item.Value),
                    SqlBuild.Text(WriterScope), SqlBuild.Text(WriterSlot), SqlBuild.Text(BatchMetric), SqlBuild.Integer(batch.Sequence))),
                Absolute(BatchMetric, batch.Sequence),
                .. Heartbeat(),
            ];
            if (!await CommitAsync(statements, batch.Sequence, cancellationToken))
            {
                return;
            }

            wrote = true;
        }

        if (!wrote && !cancellationToken.IsCancellationRequested && !await CommitAsync(Heartbeat(), null, cancellationToken))
        {
            return;
        }

        _lastFailed = false;
    }

    private async ValueTask<bool> CommitAsync(IReadOnlyList<SqlStatement> statements, long? sequence, CancellationToken cancellationToken)
    {
        var outcome = await commits.CommitLocalAsync(statements, CancellationToken.None);
        var commit = outcome switch
        {
            CommitMetadata committed => committed,
            BackendUnavailable => null,
        };
        if (commit is null)
        {
            Fail("the usage batch did not commit and is retried");
            return false;
        }

        _unconfirmed = commit;
        if (sequence is { } committedSequence)
        {
            Commit(committedSequence);
        }

        if (!await commits.FinalizeAsync(commit, cancellationToken))
        {
            Fail("the usage batch committed locally and its regional replication is confirmed again");
            return false;
        }

        _unconfirmed = null;
        return true;
    }

    private List<SqlStatement> Heartbeat()
    {
        string[] months;
        lock (_gate)
        {
            months = [.. _droppedMonths];
        }

        return
        [
            .. months.Select(month => SqlBuild.Statement(
                "INSERT INTO usage_counters (scope, month, metric, value) VALUES (?1, ?2, ?3, 1) ON CONFLICT (scope, month, metric) DO NOTHING",
                SqlBuild.Text(DroppedScope), SqlBuild.Text(month), SqlBuild.Text(cluster.NodeId))),
            Absolute(HeartbeatMetric, time.GetUtcNow().ToUnixTimeMilliseconds()),
        ];
    }

    private SqlStatement Absolute(string metric, long value) => SqlBuild.Statement(
        "INSERT INTO usage_counters (scope, month, metric, value) VALUES (?1, ?2, ?3, ?4) "
        + "ON CONFLICT (scope, month, metric) DO UPDATE SET value = excluded.value WHERE excluded.value > usage_counters.value",
        SqlBuild.Text(WriterScope), SqlBuild.Text(WriterSlot), SqlBuild.Text(metric), SqlBuild.Integer(value));

    private void Fail(string reason)
    {
        _lastFailed = true;
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

            if (_pending.Count == 0)
            {
                return null;
            }

            Dictionary<(string Scope, string Month, string Metric), long> drained = [];
            foreach (var key in _pending.Keys.Take(MaxBatchCounters).ToArray())
            {
                drained[key] = _pending[key];
                _ = _pending.Remove(key);
            }

            _sequence = Math.Max(_sequence, time.GetUtcNow().ToUnixTimeMilliseconds()) + 1;
            var batch = new FlushBatch(_sequence, drained, null);
            _batches.Add(batch);
            return batch;
        }
    }

    private void Commit(long sequence)
    {
        var now = time.GetUtcNow();
        lock (_gate)
        {
            _committed = Math.Max(_committed, sequence);
            for (var index = 0; index < _batches.Count; index++)
            {
                if (_batches[index] is { CommittedAt: null } batch && batch.Sequence <= sequence)
                {
                    _batches[index] = batch with { CommittedAt = now };
                }
            }

            foreach (var expired in _batches.Where(batch => batch.CommittedAt is { } at && now - at > BatchRetention).ToArray())
            {
                _evicted = Math.Max(_evicted, expired.Sequence);
                _ = _batches.Remove(expired);
            }
        }
    }

    private static long Committed(IEnumerable<long> rows) => rows.DefaultIfEmpty(0).Max();

    private Dictionary<(string Scope, string Metric), long>? Unflushed(string month, long committed)
    {
        lock (_gate)
        {
            if (committed < _evicted)
            {
                return null;
            }

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

    private UsageTotalsOutcome Subject(IReadOnlyList<IReadOnlyList<SqlValue>> rows, UsageSubject subject, string month)
    {
        if (Unflushed(month, Committed(rows.Where(static row => row.TextAt(0) == BatchRow).Select(static row => row.IntegerAt(1)))) is not { } unflushed)
        {
            return Stale();
        }

        return Totals(rows.Where(static row => row.TextAt(0) != BatchRow).Select(static row => (row.TextAt(0), row.IntegerAt(1))), unflushed, subject.Scope);
    }

    private OrganizationCountersOutcome Organization(IReadOnlyList<IReadOnlyList<SqlValue>> rows, string month)
    {
        if (Unflushed(month, Committed(rows.Where(static row => row.TextAt(0) == BatchRow).Select(static row => row.IntegerAt(2)))) is not { } unflushed)
        {
            return Stale();
        }

        var dropped = rows.Any(static row => row.TextAt(0) == DroppedRow && row.IntegerAt(2) > 0);
        var writers = rows.Where(static row => row.TextAt(0) == WriterRow)
            .ToDictionary(static row => row.TextAt(1)[WriterPrefix.Length..], static row => DateTimeOffset.FromUnixTimeMilliseconds(row.IntegerAt(2)), StringComparer.Ordinal);
        var groups = rows.Where(static row => row.TextAt(0) is not (BatchRow or DroppedRow or WriterRow)).GroupBy(static row => row.TextAt(0), StringComparer.Ordinal)
            .OrderBy(static group => group.Key, StringComparer.Ordinal).ToArray();
        List<ProjectCounters> projects = [];
        foreach (var group in groups.Take(OrganizationProjectLimit))
        {
            var stored = group.Where(static row => row.OptionalTextAt(1) is not null).Select(static row => (row.TextAt(1), row.IntegerAt(2)));
            projects.Add(new ProjectCounters(group.First().IdAt(0), Totals(stored, unflushed, string.Concat("project:", group.Key))));
        }

        bool droppedLocally;
        lock (_gate)
        {
            droppedLocally = _droppedMonths.Contains(month);
        }

        return new OrganizationCounters(projects, groups.Length > OrganizationProjectLimit, dropped || droppedLocally, writers);
    }

    private static BackendUnavailable Stale() => new("usage changed while it was read; retry", "usage_unavailable");

    private static Dictionary<string, long> Totals(IEnumerable<(string Metric, long Value)> stored, Dictionary<(string Scope, string Metric), long> unflushed, string scope)
    {
        Dictionary<string, long> totals = new(StringComparer.Ordinal);
        foreach (var metric in UsageMetrics.All)
        {
            totals[metric] = unflushed.GetValueOrDefault((scope, metric));
        }

        foreach (var (metric, value) in stored)
        {
            var name = metric == StorageRow ? UsageMetrics.StorageByteHours : metric.IndexOf('@', StringComparison.Ordinal) is var writer and >= 0 ? metric[..writer] : metric;
            totals[name] = totals.GetValueOrDefault(name) + value;
        }

        return totals;
    }

    private static UsageMonthsOutcome Months(IReadOnlyList<IReadOnlyList<SqlValue>> rows) => new([.. rows.Select(static row => row.TextAt(0))]);

    private sealed record FlushBatch(long Sequence, IReadOnlyDictionary<(string Scope, string Month, string Metric), long> Counters, DateTimeOffset? CommittedAt);
}
