using System.Diagnostics.Metrics;
using Microsoft.Extensions.Logging.Abstractions;
using TerraBase.Continuity;
using TerraBase.Storage;
using TerraScale.Contracts.Consistency;

namespace TerraBase.Benchmarks;

internal sealed class BackupCostRig : IAsyncDisposable
{
    public const string Bucket = "bench-backups";

    private readonly BackupCostMeterFactory _meters = new();

    private BackupCostRig(StorageEngineOptions storage, ContinuityOptions options, IContinuityDatabaseCatalog catalog)
    {
        StorageOptions = storage;
        Engine = new SqliteStorageEngine(storage, TimeProvider.System);
        Store = new S3ArchiveStore(options, NullLogger<S3ArchiveStore>.Instance);
        var metrics = new ContinuityMetrics(_meters, options);
        Manifests = new ArchiveManifests(Store, metrics, TimeProvider.System, NullLogger<ArchiveManifests>.Instance);
        var sessions = new ContinuitySessions(Engine, catalog, TimeProvider.System, NullLogger<ContinuitySessions>.Instance);
        Restorer = new DatabaseRestorer(sessions, storage, Manifests, TimeProvider.System, NullLogger<DatabaseRestorer>.Instance);
        Continuity = new BackupContinuityService(sessions, Manifests, Restorer, options, metrics, TimeProvider.System);
    }

    public StorageEngineOptions StorageOptions { get; }

    public SqliteStorageEngine Engine { get; }

    public S3ArchiveStore Store { get; }

    public ArchiveManifests Manifests { get; }

    public DatabaseRestorer Restorer { get; }

    public BackupContinuityService Continuity { get; }

    public static BackupCostRig Create(BenchRunnerContext ctx, string name, CountingS3Server server, byte nodeId, ReadOnlyMemory<byte> rawKey)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        ArgumentNullException.ThrowIfNull(server);
        var storage = new StorageEngineOptions(ctx.RunDir(name), BackupCostBenchmarks.LocalRegion, nodeId, nodeId, new DeltaColumnRegistry())
        {
            FsyncCommitLog = false,
        };
        var options = new ContinuityOptions
        {
            Archive = new S3ArchiveSettings(server.BaseUri, BackupCostBenchmarks.LocalRegion, Bucket, "bench-access", "bench-secret"),
        }.Validate();
        return new BackupCostRig(storage, options, new SharedKeyCatalog(rawKey));
    }

    public async ValueTask DisposeAsync()
    {
        Store.Dispose();
        await Engine.DisposeAsync();
        _meters.Dispose();
    }

    private sealed class SharedKeyCatalog(ReadOnlyMemory<byte> rawKey) : IContinuityDatabaseCatalog
    {
        public ValueTask<IReadOnlyList<Ulid>> ListActiveDatabasesAsync(CancellationToken cancellationToken) =>
            ValueTask.FromResult<IReadOnlyList<Ulid>>([]);

        public ValueTask<ContinuityRawKeyOutcome> ResolveRawKeyAsync(Ulid databaseId, CancellationToken cancellationToken)
        {
            ContinuityRawKeyOutcome outcome = databaseId == Ulid.Empty
                ? new ContinuityDatabaseKeyMissing(databaseId, "system catalog is not hosted by the benchmark", "not_found")
                : new ContinuityRawKey(rawKey);
            return ValueTask.FromResult(outcome);
        }

        public ValueTask<CausalBookmark> ConfirmReplicaWatermarksAsync(Ulid databaseId, CausalBookmark local, CancellationToken cancellationToken) =>
            ValueTask.FromResult(local);

        public ValueTask DatabaseReplacedAsync(Ulid databaseId, CausalBookmark? rewoundTo, CancellationToken cancellationToken) =>
            ValueTask.CompletedTask;

        public ValueTask<DiscardCopyTargetOutcome> DiscardCopyTargetAsync(Ulid databaseId, CancellationToken cancellationToken) =>
            ValueTask.FromResult<DiscardCopyTargetOutcome>(new CopyTargetDiscarded());
    }

    private sealed class BackupCostMeterFactory : IMeterFactory
    {
        private readonly Lock _gate = new();
        private readonly List<Meter> _meters = [];

        public Meter Create(MeterOptions options)
        {
            var meter = new Meter(options);
            lock (_gate)
            {
                _meters.Add(meter);
            }

            return meter;
        }

        public void Dispose()
        {
            lock (_gate)
            {
                foreach (var meter in _meters)
                {
                    meter.Dispose();
                }

                _meters.Clear();
            }
        }
    }
}

internal sealed class BackupCostProbe(CountingS3Server? server)
{
    private S3Counters _start;
    private long _rssStart;
    private int _fdStart;

    public void Start()
    {
        _start = Read();
        _rssStart = BenchRunnerContext.ReadRssBytes();
        _fdStart = BenchRunnerContext.ReadOpenFdCount();
    }

    public BackupCostMetrics Finish(
        BenchMetrics metrics,
        long encodedManifestBytesPerOp = 0,
        long snapshotRawBytes = 0,
        long snapshotStoredBytes = 0)
    {
        ArgumentNullException.ThrowIfNull(metrics);
        var ops = Math.Max(1L, (long)metrics.Rounds * metrics.IterationsPerRound);
        var delta = Read().Since(_start);
        return new BackupCostMetrics(
            S3GetCount: PerOp(delta.Gets, ops),
            S3PutCount: PerOp(delta.Puts, ops),
            S3DeleteCount: PerOp(delta.Deletes, ops),
            S3DeleteBatchCount: PerOp(delta.DeleteBatches, ops),
            S3ListCount: PerOp(delta.Lists, ops),
            S3NotModifiedCount: PerOp(delta.NotModified, ops),
            S3ConflictCount: PerOp(delta.Conflicts, ops),
            UploadedBytes: PerOp(delta.UploadedBytes, ops),
            DownloadedBytes: PerOp(delta.DownloadedBytes, ops),
            ManifestBytesUploaded: PerOp(delta.ManifestBytesUploaded, ops) + encodedManifestBytesPerOp,
            SnapshotRawBytes: snapshotRawBytes,
            SnapshotStoredBytes: snapshotStoredBytes,
            ObjectsDeleted: PerOp(delta.ObjectsDeleted, ops),
            RssDeltaMiB: (BenchRunnerContext.ReadRssBytes() - _rssStart) / BackupCostBenchmarks.BytesPerMiB,
            OpenFdDelta: BenchRunnerContext.ReadOpenFdCount() - _fdStart);
    }

    private S3Counters Read() => server is null ? default : server.Snapshot();

    private static long PerOp(long total, long ops) => (long)Math.Round((double)total / ops, MidpointRounding.AwayFromZero);
}
