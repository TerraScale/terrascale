using System.Globalization;
using TerraBase.Cluster;
using TerraBase.Engine;
using TerraBase.Features.Catalog;
using TerraBase.Features.Regions;
using TerraBase.Hosting;
using TerraBase.Storage;
using TerraScale.Contracts.Cluster;
using ZLogger;

namespace TerraBase.Features.Management.Usage;

internal readonly union StorageSampleOutcome(StorageSample, BackendUnavailable);

internal readonly union StorageRecordOutcome(StorageRecorded, BackendUnavailable);

internal readonly union StorageCoverOutcome(Completed, BackendUnavailable);

internal readonly union StorageCoverageOutcome(IReadOnlyDictionary<string, long>, BackendUnavailable);

internal sealed record StorageSample(DateTimeOffset Boundary, string Month, int Databases, long ByteHours);

internal sealed record StorageRecorded(int Databases, long ByteHours);

internal sealed record StoredDatabase(Ulid ProjectId, Ulid DatabaseId, long Bytes);

internal sealed class StorageOwnership(RegionDirectory regions, InRegionRaftRouter router, ClusterOptions cluster, IClusterCoordinator coordinator)
{
    public string NodeId => cluster.NodeId;

    public IReadOnlyList<string> Nodes() => [.. coordinator.Peers.Select(static peer => peer.Peer.NodeId).Append(cluster.NodeId).Distinct(StringComparer.Ordinal)];

    public IEnumerable<DatabaseRecord> Owned(IReadOnlyList<DatabaseRecord> page)
    {
        var everywhere = regions.List()[0];
        var regional = router.IsReplicated ? null : LowestHealthyRegionNode();
        return page.Where(database => string.Equals(Home(database, everywhere), cluster.Region, StringComparison.Ordinal)
            && string.Equals(regional ?? router.LeaderOf(database.Id), cluster.NodeId, StringComparison.Ordinal));
    }

    private string LowestHealthyRegionNode() =>
        coordinator.Peers.Where(peer => peer.Status == PeerStatus.Healthy && string.Equals(peer.Peer.Region, cluster.Region, StringComparison.Ordinal))
            .Select(static peer => peer.Peer.NodeId).Append(cluster.NodeId).Order(StringComparer.Ordinal).First();

    private static string? Home(DatabaseRecord database, string everywhere) =>
        database.AllRegions ? everywhere : database.Regions is [var home, ..] ? home : null;
}

internal sealed class StorageLedger(SystemDatabase system)
{
    private const string HourScope = "storage-hour:";

    private const string NodeHourScope = "storage-node-hour:";

    private const string NodeScope = "storage-node:";

    private const string NodeMetric = "storage_hours";

    private static readonly TimeSpan MarkerRetention = TimeSpan.FromHours(48);

    public static string Hour(DateTimeOffset at) => at.ToString("yyyy-MM-dd'T'HH", CultureInfo.InvariantCulture);

    public async ValueTask<StorageRecordOutcome> RecordAsync(string hour, string month, IReadOnlyList<StoredDatabase> stored, CancellationToken cancellationToken)
    {
        if (stored.Count == 0)
        {
            return new StorageRecorded(0, 0);
        }

        List<SqlStatement> statements = [];
        foreach (var item in stored)
        {
            var marker = string.Concat(HourScope, item.DatabaseId.ToString());
            statements.Add(Guarded(UsageMeter.DatabaseScope(item.DatabaseId), month, UsageMetrics.StorageByteHours, item.Bytes, marker, hour));
            statements.Add(Guarded(UsageMeter.ProjectScope(item.ProjectId), month, UsageMetrics.StorageByteHours, item.Bytes, marker, hour));
            statements.Add(Marker(marker, hour, item.Bytes));
        }

        var outcome = await system.WriteAsync(statements, ConsistencyMode.Fast, cancellationToken);
        return outcome switch
        {
            SqlWriteResult written => Recorded(written, stored),
            StateConflict conflict => new BackendUnavailable(conflict.Message, conflict.Code),
            BackendUnavailable unavailable => unavailable,
        };
    }

    public async ValueTask<StorageCoverOutcome> CoverAsync(string nodeId, DateTimeOffset boundary, CancellationToken cancellationToken)
    {
        var hour = Hour(boundary.AddTicks(-1));
        var marker = string.Concat(NodeHourScope, nodeId);
        var expired = Hour(boundary - MarkerRetention);
        var outcome = await system.WriteAsync(
        [
            Guarded(string.Concat(NodeScope, nodeId), UsageMeter.Month(boundary.AddTicks(-1)), NodeMetric, 1, marker, hour),
            Marker(marker, hour, 1),
            SqlBuild.Statement("DELETE FROM usage_counters WHERE scope >= ?1 AND scope < ?2 AND month < ?3", SqlBuild.Text(HourScope), SqlBuild.Text("storage-hour;"), SqlBuild.Text(expired)),
            SqlBuild.Statement("DELETE FROM usage_counters WHERE scope = ?1 AND month < ?2", SqlBuild.Text(marker), SqlBuild.Text(expired)),
        ], ConsistencyMode.Fast, cancellationToken);
        return outcome switch
        {
            SqlWriteResult => new Completed(),
            StateConflict conflict => new BackendUnavailable(conflict.Message, conflict.Code),
            BackendUnavailable unavailable => unavailable,
        };
    }

    public async ValueTask<StorageCoverageOutcome> CoverageAsync(string month, CancellationToken cancellationToken) =>
        await system.QueryAsync(SqlBuild.Statement(
            "SELECT scope, value FROM usage_counters WHERE scope >= ?1 AND scope < ?2 AND month = ?3 AND metric = ?4",
            SqlBuild.Text(NodeScope), SqlBuild.Text("storage-node;"), SqlBuild.Text(month), SqlBuild.Text(NodeMetric)), cancellationToken) switch
        {
            IReadOnlyList<IReadOnlyList<SqlValue>> rows => (IReadOnlyDictionary<string, long>)rows.ToDictionary(static row => row.TextAt(0)[NodeScope.Length..], static row => row.IntegerAt(1), StringComparer.Ordinal),
            BackendUnavailable unavailable => unavailable,
        };

    private static SqlStatement Guarded(string scope, string month, string metric, long value, string marker, string hour) => SqlBuild.Statement(
        "INSERT INTO usage_counters (scope, month, metric, value) SELECT ?1, ?2, ?3, ?4 "
        + "WHERE NOT EXISTS (SELECT 1 FROM usage_counters WHERE scope = ?5 AND month = ?6 AND metric = ?7) "
        + "ON CONFLICT (scope, month, metric) DO UPDATE SET value = value + excluded.value",
        SqlBuild.Text(scope), SqlBuild.Text(month), SqlBuild.Text(metric), SqlBuild.Integer(value), SqlBuild.Text(marker), SqlBuild.Text(hour),
        SqlBuild.Text(UsageMetrics.StorageByteHours));

    private static SqlStatement Marker(string marker, string hour, long value) => SqlBuild.Statement(
        "INSERT INTO usage_counters (scope, month, metric, value) VALUES (?, ?, ?, ?) ON CONFLICT (scope, month, metric) DO NOTHING",
        SqlBuild.Text(marker), SqlBuild.Text(hour), SqlBuild.Text(UsageMetrics.StorageByteHours), SqlBuild.Integer(value));

    private static StorageRecorded Recorded(SqlWriteResult written, IReadOnlyList<StoredDatabase> stored)
    {
        var fresh = stored.Where((_, index) => written.ResultSets.Count > (index * 3) + 2 && written.ResultSets[(index * 3) + 2].RowsAffected > 0).ToArray();
        return new StorageRecorded(fresh.Length, fresh.Sum(static item => item.Bytes));
    }
}

internal sealed class StorageSampler(
    CatalogStore catalog, StorageOwnership ownership, StorageLedger ledger, StorageEngineOptions storage, TimeProvider time, ILogger<StorageSampler> logger) : BackgroundService
{
    public const int SlotHours = 1;

    private const int CatalogPage = 64;

    public static readonly TimeSpan Slot = TimeSpan.FromHours(SlotHours);

    public static DateTimeOffset NextBoundary(DateTimeOffset now) => new DateTimeOffset(now.UtcTicks - (now.UtcTicks % Slot.Ticks), TimeSpan.Zero).Add(Slot);

    public async ValueTask<StorageSampleOutcome> SampleAsync(DateTimeOffset boundary, CancellationToken cancellationToken)
    {
        var ended = boundary.AddTicks(-1);
        var month = UsageMeter.Month(ended);
        var hour = StorageLedger.Hour(ended);
        var databases = 0;
        long byteHours = 0;
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

            List<StoredDatabase> stored = [.. ownership.Owned(page).Select(database => new StoredDatabase(database.ProjectId, database.Id, checked(Bytes(database.Id) * SlotHours)))
                .Where(static item => item.Bytes > 0)];
            var recordedOutcome = await ledger.RecordAsync(hour, month, stored, cancellationToken);
            var recorded = recordedOutcome switch
            {
                StorageRecorded done => done,
                BackendUnavailable => null,
            };
            if (recorded is null)
            {
                return new BackendUnavailable("the usage store is unavailable; storage was not fully sampled", "usage_unavailable");
            }

            databases += recorded.Databases;
            byteHours = checked(byteHours + recorded.ByteHours);
            if (page.Count < CatalogPage)
            {
                break;
            }

            after = page[^1].Id.ToString();
        }

        var covered = await ledger.CoverAsync(ownership.NodeId, boundary, cancellationToken);
        return covered switch
        {
            Completed => new StorageSample(boundary, month, databases, byteHours),
            BackendUnavailable unavailable => unavailable,
        };
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var boundary = NextBoundary(time.GetUtcNow());
        try
        {
            while (true)
            {
                var wait = boundary - time.GetUtcNow();
                if (wait > TimeSpan.Zero)
                {
                    await Task.Delay(wait, time, stoppingToken);
                }

                var sampled = await SampleAsync(boundary, stoppingToken);
                var failure = sampled switch
                {
                    StorageSample => null,
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
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
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
