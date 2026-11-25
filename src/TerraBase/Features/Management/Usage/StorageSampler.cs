using System.Globalization;
using TerraBase.Cluster;
using TerraBase.Engine;
using TerraBase.Features.Catalog;
using TerraBase.Features.Regions;
using TerraBase.Hosting;
using TerraBase.Storage;
using ZLogger;

namespace TerraBase.Features.Management.Usage;

internal readonly union StorageSampleOutcome(StorageSample, BackendUnavailable);

internal readonly union StorageRecordOutcome(StorageRecorded, StorageContended, BackendUnavailable);

internal readonly union StorageLastHoursOutcome(IReadOnlyDictionary<Ulid, long>, BackendUnavailable);

internal readonly union StorageCoverOutcome(Completed, StorageContended, BackendUnavailable);

internal readonly union StorageCoverageOutcome(IReadOnlyDictionary<string, long>, BackendUnavailable);

internal sealed record StorageSample(DateTimeOffset Boundary, string Month, int Databases, long ByteHours, bool Complete);

internal sealed record StorageRecorded(int Databases, long ByteHours);

internal sealed record StorageContended(string Reason);

internal sealed record StoredDatabase(Ulid ProjectId, Ulid DatabaseId, long Bytes);

internal sealed class StorageOwnership(RegionDirectory regions, ClusterOptions cluster)
{
    public string Region => cluster.Region;

    public int Rank => cluster.RegionPeers.Count(peer => string.CompareOrdinal(peer.NodeId, cluster.NodeId) < 0);

    public IReadOnlyList<string> Regions() => regions.List();

    public IEnumerable<DatabaseRecord> Homed(IReadOnlyList<DatabaseRecord> page)
    {
        var everywhere = regions.List()[0];
        return page.Where(database => string.Equals(Home(database, everywhere), cluster.Region, StringComparison.Ordinal));
    }

    private static string? Home(DatabaseRecord database, string everywhere) =>
        database.AllRegions ? everywhere : database.Regions is [var home, ..] ? home : null;
}

internal sealed class StorageLedger(SystemDatabase system)
{
    private const string LastScope = "storage-last:";

    private const string RegionLastScope = "storage-region-last:";

    private const string RegionScope = "storage-region:";

    private const string Slot = "-";

    private const string HourMetric = "hour";

    private const string RegionMetric = "storage_hours";

    public static long HourIndex(DateTimeOffset at) => at.ToUnixTimeSeconds() / 3600;

    public async ValueTask<StorageLastHoursOutcome> LastHoursAsync(IReadOnlyList<Ulid> databases, CancellationToken cancellationToken)
    {
        if (databases.Count == 0)
        {
            return new StorageLastHoursOutcome(new Dictionary<Ulid, long>());
        }

        SqlValue[] arguments = [SqlBuild.Text(Slot), SqlBuild.Text(HourMetric), .. databases.Select(static id => SqlBuild.Text(string.Concat(LastScope, id.ToString())))];
        var placeholders = string.Join(", ", Enumerable.Repeat("?", databases.Count));
        var outcome = await system.QueryAsync(SqlBuild.Statement(
            string.Concat("SELECT scope, value FROM usage_counters WHERE month = ? AND metric = ? AND scope IN (", placeholders, ")"), arguments), cancellationToken);
        return outcome switch
        {
            IReadOnlyList<IReadOnlyList<SqlValue>> rows => (IReadOnlyDictionary<Ulid, long>)rows.ToDictionary(static row => Ulid.Parse(row.TextAt(0)[LastScope.Length..], CultureInfo.InvariantCulture),
                static row => row.IntegerAt(1)),
            BackendUnavailable unavailable => unavailable,
        };
    }

    public async ValueTask<StorageRecordOutcome> RecordAsync(long hour, string month, IReadOnlyList<StoredDatabase> stored, CancellationToken cancellationToken)
    {
        if (stored.Count == 0)
        {
            return new StorageRecorded(0, 0);
        }

        List<SqlStatement> statements = [];
        foreach (var item in stored)
        {
            var last = string.Concat(LastScope, item.DatabaseId.ToString());
            statements.Add(Guarded(UsageMeter.DatabaseScope(item.DatabaseId), month, UsageMetrics.StorageByteHours, item.Bytes, last, hour));
            statements.Add(Guarded(UsageMeter.ProjectScope(item.ProjectId), month, UsageMetrics.StorageByteHours, item.Bytes, last, hour));
            statements.Add(Advance(last, hour));
        }

        var outcome = await system.WriteAsync(statements, ConsistencyMode.Strong, cancellationToken);
        return outcome switch
        {
            SqlWriteResult written => Recorded(written, stored),
            StateConflict conflict => new StorageContended(conflict.Code),
            BackendUnavailable unavailable => unavailable,
        };
    }

    public async ValueTask<StorageCoverOutcome> CoverAsync(string region, long hour, string month, CancellationToken cancellationToken)
    {
        var last = string.Concat(RegionLastScope, region);
        var outcome = await system.WriteAsync(
            [Guarded(string.Concat(RegionScope, region), month, RegionMetric, 1, last, hour), Advance(last, hour)], ConsistencyMode.Strong, cancellationToken);
        return outcome switch
        {
            SqlWriteResult => new Completed(),
            StateConflict conflict => new StorageContended(conflict.Code),
            BackendUnavailable unavailable => unavailable,
        };
    }

    public async ValueTask<StorageCoverageOutcome> CoverageAsync(string month, CancellationToken cancellationToken) =>
        await system.QueryAsync(SqlBuild.Statement(
            "SELECT scope, value FROM usage_counters WHERE scope >= ?1 AND scope < ?2 AND month = ?3 AND metric = ?4",
            SqlBuild.Text(RegionScope), SqlBuild.Text("storage-region;"), SqlBuild.Text(month), SqlBuild.Text(RegionMetric)), cancellationToken) switch
        {
            IReadOnlyList<IReadOnlyList<SqlValue>> rows => (IReadOnlyDictionary<string, long>)rows.ToDictionary(static row => row.TextAt(0)[RegionScope.Length..], static row => row.IntegerAt(1),
                StringComparer.Ordinal),
            BackendUnavailable unavailable => unavailable,
        };

    private static SqlStatement Guarded(string scope, string month, string metric, long value, string last, long hour) => SqlBuild.Statement(
        "INSERT INTO usage_counters (scope, month, metric, value) SELECT ?1, ?2, ?3, ?4 "
        + "WHERE NOT EXISTS (SELECT 1 FROM usage_counters WHERE scope = ?5 AND month = ?6 AND metric = ?7 AND value >= ?8) "
        + "ON CONFLICT (scope, month, metric) DO UPDATE SET value = value + excluded.value",
        SqlBuild.Text(scope), SqlBuild.Text(month), SqlBuild.Text(metric), SqlBuild.Integer(value), SqlBuild.Text(last), SqlBuild.Text(Slot),
        SqlBuild.Text(HourMetric), SqlBuild.Integer(hour));

    private static SqlStatement Advance(string last, long hour) => SqlBuild.Statement(
        "INSERT INTO usage_counters (scope, month, metric, value) VALUES (?1, ?2, ?3, ?4) "
        + "ON CONFLICT (scope, month, metric) DO UPDATE SET value = excluded.value WHERE excluded.value > usage_counters.value",
        SqlBuild.Text(last), SqlBuild.Text(Slot), SqlBuild.Text(HourMetric), SqlBuild.Integer(hour));

    private static StorageRecorded Recorded(SqlWriteResult written, IReadOnlyList<StoredDatabase> stored)
    {
        var fresh = stored.Where((_, index) => written.ResultSets.Count > index * 3 && written.ResultSets[index * 3].RowsAffected > 0).ToArray();
        return new StorageRecorded(fresh.Length, fresh.Sum(static item => item.Bytes));
    }
}

internal sealed class StorageSampler(
    CatalogStore catalog, StorageOwnership ownership, StorageLedger ledger, StorageEngineOptions storage, TimeProvider time, ILogger<StorageSampler> logger) : BackgroundService
{
    public const int SlotHours = 1;

    private const int CatalogPage = 64;

    public static readonly TimeSpan Slot = TimeSpan.FromHours(SlotHours);

    private static readonly TimeSpan RankDelay = TimeSpan.FromMinutes(5);

    public static DateTimeOffset NextBoundary(DateTimeOffset now) => new DateTimeOffset(now.UtcTicks - (now.UtcTicks % Slot.Ticks), TimeSpan.Zero).Add(Slot);

    public async ValueTask<StorageSampleOutcome> SampleAsync(DateTimeOffset boundary, CancellationToken cancellationToken)
    {
        var ended = boundary.AddTicks(-1);
        var month = UsageMeter.Month(ended);
        var hour = StorageLedger.HourIndex(ended);
        var databases = 0;
        long byteHours = 0;
        var complete = true;
        string? after = null;
        while (true)
        {
            var outcome = await catalog.AllDatabasesAsync(after, CatalogPage, cancellationToken);
            var page = outcome switch
            {
                IReadOnlyList<DatabaseRecord> records => records,
                BackendUnavailable => null,
            };
            if (page is null)
            {
                return new BackendUnavailable("the catalog is unavailable; storage was not sampled", "usage_unavailable");
            }

            List<DatabaseRecord> homed = [.. ownership.Homed(page)];
            var lastOutcome = await ledger.LastHoursAsync([.. homed.Select(static database => database.Id)], cancellationToken);
            var last = lastOutcome switch
            {
                IReadOnlyDictionary<Ulid, long> hours => hours,
                BackendUnavailable => null,
            };
            if (last is null)
            {
                return new BackendUnavailable("the usage store is unavailable; storage was not sampled", "usage_unavailable");
            }

            List<StoredDatabase> due = [.. homed.Where(database => last.GetValueOrDefault(database.Id, long.MinValue) < hour)
                .Select(database => new StoredDatabase(database.ProjectId, database.Id, checked(Bytes(database.Id) * SlotHours)))];
            complete &= due.All(static item => item.Bytes > 0);
            var recordedOutcome = await ledger.RecordAsync(hour, month, [.. due.Where(static item => item.Bytes > 0)], cancellationToken);
            var failed = recordedOutcome switch
            {
                BackendUnavailable unavailable => unavailable,
                StorageRecorded or StorageContended => null,
            };
            if (failed is not null)
            {
                return failed;
            }

            var recorded = recordedOutcome switch
            {
                StorageRecorded done => done,
                StorageContended or BackendUnavailable => null,
            };
            if (recorded is { } fresh)
            {
                databases += fresh.Databases;
                byteHours = checked(byteHours + fresh.ByteHours);
            }
            else
            {
                complete = false;
            }

            if (page.Count < CatalogPage)
            {
                break;
            }

            after = page[^1].Id.ToString();
        }

        if (!complete)
        {
            return new StorageSample(boundary, month, databases, byteHours, Complete: false);
        }

        var covered = await ledger.CoverAsync(ownership.Region, hour, month, cancellationToken);
        return covered switch
        {
            Completed => new StorageSample(boundary, month, databases, byteHours, Complete: true),
            StorageContended => new StorageSample(boundary, month, databases, byteHours, Complete: false),
            BackendUnavailable unavailable => unavailable,
        };
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var boundary = NextBoundary(time.GetUtcNow());
        while (!stoppingToken.IsCancellationRequested)
        {
            var wait = boundary + (RankDelay * ownership.Rank) - time.GetUtcNow();
            if (wait > TimeSpan.Zero)
            {
                await Task.Delay(wait, time, stoppingToken);
            }

            var sampled = await SampleAsync(boundary, stoppingToken);
            var failure = sampled switch
            {
                StorageSample { Complete: true } => null,
                StorageSample => "a database could not be sampled on this node",
                BackendUnavailable unavailable => unavailable.Message,
            };
            if (failure is not null)
            {
                logger.ZLogWarning($"Storage usage sample incomplete: {failure:@Reason}");
            }

            var next = NextBoundary(time.GetUtcNow());
            boundary = next > boundary ? next : boundary.Add(Slot);
        }
    }

    private long Bytes(Ulid databaseId)
    {
        var directory = Path.Combine(storage.RootDirectory, databaseId.ToString());
        var file = string.Concat(databaseId.ToString(), ".db");
        return Length(Path.Combine(directory, file)) + Length(Path.Combine(directory, string.Concat(file, "-wal")));
    }

    private static long Length(string path)
    {
        var info = new FileInfo(path);
        return info.Exists ? info.Length : 0;
    }
}
