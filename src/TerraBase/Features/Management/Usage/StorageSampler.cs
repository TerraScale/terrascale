using TerraBase.Cluster;
using TerraBase.Features.Catalog;
using TerraBase.Features.Regions;
using TerraBase.Hosting;
using TerraBase.Storage;
using ZLogger;

namespace TerraBase.Features.Management.Usage;

internal readonly union StorageSampleOutcome(StorageSample, BackendUnavailable);

internal sealed record StorageSample(DateTimeOffset Boundary, string Month, int Databases, long ByteHours);

internal sealed record StoredDatabase(Ulid ProjectId, Ulid DatabaseId, long Bytes);

internal sealed class StorageOwnership(RegionDirectory regions, InRegionRaftRouter router, ClusterOptions cluster)
{
    public IEnumerable<DatabaseRecord> Owned(IReadOnlyList<DatabaseRecord> page)
    {
        var everywhere = regions.List()[0];
        return page.Where(database => Home(database, everywhere) is { } home
            && string.Equals(home, cluster.Region, StringComparison.Ordinal)
            && string.Equals(router.LeaderOf(database.Id), cluster.NodeId, StringComparison.Ordinal));
    }

    private static string? Home(DatabaseRecord database, string everywhere) =>
        database.AllRegions ? everywhere : database.Regions is [var home, ..] ? home : null;
}

internal sealed class StorageSampler(
    CatalogStore catalog, StorageOwnership ownership, StorageEngineOptions storage, UsageMeter usage, TimeProvider time, ILogger<StorageSampler> logger) : BackgroundService
{
    private const long SlotHours = 1;

    private const int CatalogPage = 256;

    private static readonly TimeSpan Slot = TimeSpan.FromHours(SlotHours);

    public static DateTimeOffset NextBoundary(DateTimeOffset now) => new DateTimeOffset(now.UtcTicks - (now.UtcTicks % Slot.Ticks), TimeSpan.Zero).Add(Slot);

    public async ValueTask<StorageSampleOutcome> SampleAsync(DateTimeOffset boundary, CancellationToken cancellationToken)
    {
        var month = UsageMeter.Month(boundary.AddTicks(-1));
        List<StoredDatabase> stored = [];
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

            stored.AddRange(ownership.Owned(page).Select(database => new StoredDatabase(database.ProjectId, database.Id, Bytes(database.Id))).Where(static item => item.Bytes > 0));
            if (page.Count < CatalogPage)
            {
                break;
            }

            after = page[^1].Id.ToString();
        }

        foreach (var item in stored)
        {
            usage.RecordStorage(item.ProjectId, item.DatabaseId, month, checked(item.Bytes * SlotHours));
        }

        return new StorageSample(boundary, month, stored.Count, stored.Sum(static item => checked(item.Bytes * SlotHours)));
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
                    logger.ZLogWarning($"Storage usage sample skipped: {failure:@Reason}");
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
