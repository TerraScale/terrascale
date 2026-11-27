using System.Globalization;
using TerraBase.Cluster;
using TerraBase.Engine;
using TerraBase.Features.Health;
using TerraBase.Features.Catalog;
using TerraBase.Features.Regions;
using TerraBase.Hosting;
using TerraBase.Storage;
using TerraScale.Contracts.Quic;
using TerraScale.QuicRpc;
using ZLogger;

namespace TerraBase.Features.Management.Usage;

internal readonly union StorageSampleOutcome(StorageSample, BackendUnavailable);

internal readonly union StorageRecordOutcome(StorageRecorded, BackendUnavailable);


internal readonly union StorageExistingOutcome(IReadOnlySet<Ulid>, BackendUnavailable);

internal readonly union StorageCoverOutcome(Completed, BackendUnavailable);

internal readonly union StorageCoverageOutcome(IReadOnlyDictionary<string, IReadOnlySet<long>>, BackendUnavailable);

internal sealed record StorageSample(DateTimeOffset Boundary, string Month, int Databases, long ByteHours, int Missing)
{
    public bool Complete => Missing == 0;
}

internal sealed record StorageRecorded(int Databases, long ByteHours);

internal sealed record StoredDatabase(Ulid ProjectId, Ulid DatabaseId, long Bytes);

internal sealed class StorageOwnership(RegionDirectory regions, ClusterOptions cluster, NodeReadiness readiness)
{
    public bool Ready => readiness.Blocked is null;

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

internal sealed class StorageFiles(StorageEngineOptions storage)
{
    public long? Bytes(Ulid databaseId)
    {
        var file = Path.Combine(storage.RootDirectory, databaseId.ToString(), string.Concat(databaseId.ToString(), ".db"));
        var info = new FileInfo(file);
        if (!info.Exists)
        {
            return null;
        }

        var wal = new FileInfo(string.Concat(file, "-wal"));
        return info.Length + (wal.Exists ? wal.Length : 0);
    }
}

internal sealed class StorageMeasure(StorageFiles files, ClusterOptions cluster, QuicRpcClient quic)
{
    private static readonly TimeSpan PeerTimeout = TimeSpan.FromSeconds(5);

    public long? Local(Ulid databaseId) => files.Bytes(databaseId);

    public async ValueTask<IReadOnlyDictionary<Ulid, long?>> PeersAsync(IReadOnlyList<Ulid> databases, CancellationToken cancellationToken)
    {
        var request = new StorageFileProbeRequest(databases);
        var replies = await Task.WhenAll(cluster.Peers.Select(peer => ProbeAsync(peer.NodeId, request, cancellationToken)));
        var answered = replies.All(reply => reply is { } bytes && bytes.Count == databases.Count);
        Dictionary<Ulid, long?> sizes = [];
        for (var index = 0; index < databases.Count; index++)
        {
            var position = index;
            sizes[databases[index]] = answered
                ? replies.Select(reply => reply is { } bytes ? bytes[position] : -1).Where(static bytes => bytes >= 0).DefaultIfEmpty(0).Max()
                : null;
        }

        return sizes;
    }

    private async Task<IReadOnlyList<long>?> ProbeAsync(string nodeId, StorageFileProbeRequest request, CancellationToken cancellationToken)
    {
        var outcome = await quic.CallAsync<StorageFileProbeRequest, StorageFileProbeReply>(nodeId, QuicMessageType.StorageFileProbe, request, PeerTimeout, cancellationToken);
        return outcome switch
        {
            StorageFileProbeReply reply => reply.Bytes,
            QuicUnavailable or QuicTimeout or QuicRemoteError => null,
        };
    }
}

internal sealed class StorageLedger(SystemDatabase system)
{
    public const string MissingPrefix = "storage-missing:";

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

    public async ValueTask<StorageRecordOutcome> RecordAsync(
        long hour, string month, IReadOnlyList<StoredDatabase> stored, IReadOnlyList<StoredDatabase> missing, CancellationToken cancellationToken)
    {
        if (stored.Count == 0 && missing.Count == 0)
        {
            return new StorageRecorded(0, 0);
        }

        SqlStatement[] statements =
        [
            .. stored.Select(item => Once(UsageMeter.StorageScope(item.ProjectId), month, UsageMeter.StorageMetric(item.DatabaseId, hour), item.Bytes)),
            .. missing.Select(item => Once(string.Concat(MissingPrefix, item.ProjectId.ToString()), month, UsageMeter.StorageMetric(item.DatabaseId, hour), 1)),
        ];
        var outcome = await system.WriteTelemetryAsync(statements, ConsistencyMode.Fast, cancellationToken);
        return outcome switch
        {
            SqlWriteResult written => Recorded(written, stored),
            StateConflict conflict => new BackendUnavailable(conflict.Message, conflict.Code),
            BackendUnavailable unavailable => unavailable,
        };
    }

    public async ValueTask<StorageCoverOutcome> CoverAsync(string region, long hour, string month, CancellationToken cancellationToken)
    {
        var outcome = await system.WriteTelemetryAsync(
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
    CatalogStore catalog, StorageOwnership ownership, StorageLedger ledger, StorageMeasure measure, TimeProvider time, ILogger<StorageSampler> logger) : BackgroundService
{
    public const int SlotHours = 1;

    private const int CatalogPage = StorageFileProbeRequest.MaxDatabases;

    public static readonly TimeSpan Slot = TimeSpan.FromHours(SlotHours);

    private static readonly TimeSpan RankDelay = TimeSpan.FromMinutes(5);

    private static readonly TimeSpan ProbeBudget = TimeSpan.FromMinutes(10);

    public static DateTimeOffset NextBoundary(DateTimeOffset now) => new DateTimeOffset(now.UtcTicks - (now.UtcTicks % Slot.Ticks), TimeSpan.Zero).Add(Slot);

    public async ValueTask<StorageSampleOutcome> SampleAsync(DateTimeOffset boundary, CancellationToken cancellationToken)
    {
        var ended = boundary.AddTicks(-1);
        var month = UsageMeter.Month(ended);
        var hour = StorageLedger.HourIndex(ended);
        var probeUntil = time.GetUtcNow() + ProbeBudget;
        var databases = 0;
        var unmeasured = 0;
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

            List<DatabaseRecord> due = [.. homed.Where(database => !existing.Contains(database.Id))];
            Dictionary<Ulid, long?> sizes = due.ToDictionary(static database => database.Id, database => measure.Local(database.Id));
            List<Ulid> absent = [.. sizes.Where(static size => size.Value is null).Select(static size => size.Key)];
            if (absent.Count > 0 && time.GetUtcNow() < probeUntil)
            {
                foreach (var (database, bytes) in await measure.PeersAsync(absent, cancellationToken))
                {
                    sizes[database] = bytes;
                }
            }

            List<StoredDatabase> measured = [];
            List<StoredDatabase> missing = [];
            foreach (var database in due)
            {
                if (sizes[database.Id] is { } bytes)
                {
                    measured.Add(new StoredDatabase(database.ProjectId, database.Id, checked(bytes * SlotHours)));
                }
                else
                {
                    missing.Add(new StoredDatabase(database.ProjectId, database.Id, 0));
                }
            }

            unmeasured += missing.Count;
            var recordedOutcome = await ledger.RecordAsync(hour, month, measured, missing, cancellationToken);
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

        var covered = await ledger.CoverAsync(ownership.Region, hour, month, cancellationToken);
        return covered switch
        {
            Completed => new StorageSample(boundary, month, databases, byteHours, unmeasured),
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

            if (!ownership.Ready)
            {
                boundary = NextBoundary(time.GetUtcNow());
                continue;
            }

            var sampled = await SampleAsync(boundary, stoppingToken);
            var failure = sampled switch
            {
                StorageSample { Complete: true } => null,
                StorageSample sample => string.Concat(sample.Missing.ToString(CultureInfo.InvariantCulture), " databases could not be measured on this node"),
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
}
