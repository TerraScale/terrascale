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

internal readonly union StorageRecordOutcome(StorageRecorded, BackendUnavailable);

internal readonly union StorageExistingOutcome(IReadOnlySet<Ulid>, BackendUnavailable);

internal readonly union StorageCoverOutcome(Completed, BackendUnavailable);

internal readonly union StorageCoverageOutcome(IReadOnlyDictionary<string, IReadOnlySet<long>>, BackendUnavailable);

internal sealed record StorageSample(DateTimeOffset Boundary, string Month, int Databases, long ByteHours, bool Complete);

internal sealed record StorageRecorded(int Databases, long ByteHours);

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
    private const string CoverPrefix = "storage-cover:";

    public static long HourIndex(DateTimeOffset at) => at.ToUnixTimeSeconds() / 3600;

    public async ValueTask<StorageExistingOutcome> ExistingAsync(long hour, string month, IReadOnlyList<DatabaseRecord> databases, CancellationToken cancellationToken)
    {
        if (databases.Count == 0)
        {
            return new StorageExistingOutcome(new HashSet<Ulid>());
        }

        SqlValue[] arguments =
        [
            SqlBuild.Text(month),
            .. databases.SelectMany(database => new[] { SqlBuild.Text(UsageMeter.StorageScope(database.ProjectId)), SqlBuild.Text(UsageMeter.StorageMetric(database.Id, hour)) }),
        ];
        var pairs = string.Join(", ", Enumerable.Repeat("(?, ?)", databases.Count));
        var outcome = await system.QueryAsync(SqlBuild.Statement(
            string.Concat("SELECT metric FROM usage_counters WHERE month = ? AND (scope, metric) IN (VALUES ", pairs, ")"), arguments), cancellationToken);
        return outcome switch
        {
            IReadOnlyList<IReadOnlyList<SqlValue>> rows => (IReadOnlySet<Ulid>)rows.Select(static row => Ulid.TryParse(row.TextAt(0).Split(':')[0], out var id) ? id : Ulid.Empty).ToHashSet(),
            BackendUnavailable unavailable => unavailable,
        };
    }

    public async ValueTask<StorageRecordOutcome> RecordAsync(long hour, string month, IReadOnlyList<StoredDatabase> stored, CancellationToken cancellationToken)
    {
        if (stored.Count == 0)
        {
            return new StorageRecorded(0, 0);
        }

        SqlStatement[] statements = [.. stored.Select(item => Once(UsageMeter.StorageScope(item.ProjectId), month, UsageMeter.StorageMetric(item.DatabaseId, hour), item.Bytes))];
        var outcome = await system.WriteAsync(statements, ConsistencyMode.Fast, cancellationToken);
        return outcome switch
        {
            SqlWriteResult written => Recorded(written, stored),
            StateConflict conflict => new BackendUnavailable(conflict.Message, conflict.Code),
            BackendUnavailable unavailable => unavailable,
        };
    }

    public async ValueTask<StorageCoverOutcome> CoverAsync(string region, long hour, string month, CancellationToken cancellationToken)
    {
        var outcome = await system.WriteAsync(
            Once(string.Concat(CoverPrefix, region), month, hour.ToString("D10", CultureInfo.InvariantCulture), 1), ConsistencyMode.Fast, cancellationToken);
        return outcome switch
        {
            SqlWriteResult => new Completed(),
            StateConflict conflict => new BackendUnavailable(conflict.Message, conflict.Code),
            BackendUnavailable unavailable => unavailable,
        };
    }

    public async ValueTask<StorageCoverageOutcome> CoverageAsync(string month, CancellationToken cancellationToken) =>
        await system.QueryAsync(SqlBuild.Statement(
            "SELECT scope, metric FROM usage_counters WHERE scope >= ?1 AND scope < ?2 AND month = ?3",
            SqlBuild.Text(CoverPrefix), SqlBuild.Text("storage-cover;"), SqlBuild.Text(month)), cancellationToken) switch
        {
            IReadOnlyList<IReadOnlyList<SqlValue>> rows => Coverage(rows),
            BackendUnavailable unavailable => unavailable,
        };

    private static StorageCoverageOutcome Coverage(IReadOnlyList<IReadOnlyList<SqlValue>> rows) =>
        new(rows.GroupBy(static row => row.TextAt(0)[CoverPrefix.Length..], StringComparer.Ordinal).ToDictionary(
            static group => group.Key,
            static group => (IReadOnlySet<long>)group.Select(static row => long.TryParse(row.TextAt(1), CultureInfo.InvariantCulture, out var hour) ? hour : -1).ToHashSet(),
            StringComparer.Ordinal));

    private static SqlStatement Once(string scope, string month, string metric, long value) => SqlBuild.Statement(
        "INSERT INTO usage_counters (scope, month, metric, value) VALUES (?, ?, ?, ?) ON CONFLICT (scope, month, metric) DO NOTHING",
        SqlBuild.Text(scope), SqlBuild.Text(month), SqlBuild.Text(metric), SqlBuild.Integer(value));

    private static StorageRecorded Recorded(SqlWriteResult written, IReadOnlyList<StoredDatabase> stored)
    {
        var fresh = stored.Where((_, index) => written.ResultSets.Count > index && written.ResultSets[index].RowsAffected > 0).ToArray();
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
            var existingOutcome = await ledger.ExistingAsync(hour, month, homed, cancellationToken);
            var existing = existingOutcome switch
            {
                IReadOnlySet<Ulid> found => found,
                BackendUnavailable => null,
            };
            if (existing is null)
            {
                return new BackendUnavailable("the usage store is unavailable; storage was not sampled", "usage_unavailable");
            }

            List<StoredDatabase> due = [.. homed.Where(database => !existing.Contains(database.Id))
                .Select(database => new StoredDatabase(database.ProjectId, database.Id, checked(Bytes(database.Id) * SlotHours)))];
            complete &= due.All(static item => item.Bytes > 0);
            var recordedOutcome = await ledger.RecordAsync(hour, month, [.. due.Where(static item => item.Bytes > 0)], cancellationToken);
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

        if (!complete)
        {
            return new StorageSample(boundary, month, databases, byteHours, Complete: false);
        }

        var covered = await ledger.CoverAsync(ownership.Region, hour, month, cancellationToken);
        return covered switch
        {
            Completed => new StorageSample(boundary, month, databases, byteHours, Complete: true),
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
