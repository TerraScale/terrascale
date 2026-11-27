using BenchmarkDotNet.Attributes;
using TerraBase.Storage;
using TerraScale.Contracts.Consistency;
using TerraScale.Contracts.Storage;

namespace TerraBase.Benchmarks;

[MemoryDiagnoser]
public class MicroBenchmarks
{
    private readonly DeltaBatch _batch;
    private readonly byte[] _encodedBatch;
    private readonly CausalBookmark _bookmark;
    private readonly CausalBookmark _subsetBookmark;
    private readonly string _encodedBookmark;
    private readonly HlcClock _clock;
    private readonly SnowflakeGenerator _snowflake;

    private string? _storageDir;
    private SqliteStorageEngine? _engine;
    private ISqlDatabaseSession? _session;
    private Ulid _dbId;
    private long _readCounter;

    public MicroBenchmarks()
    {
        _batch = CrdtCommitLogBenchmarks.BuildSampleDeltaBatch(50);
        _encodedBatch = MutationBatchCodec.Encode(_batch);
        _bookmark = CausalBookmark.Create(
        [
            new("fra", 4201),
            new("gru", 9812),
            new("iad", 15003),
            new("nrt", 3199),
            new("syd", 1044),
        ]);
        _subsetBookmark = CausalBookmark.Create(
        [
            new("fra", 4200),
            new("gru", 9812),
            new("iad", 15000),
        ]);
        _encodedBookmark = _bookmark.Encode();
        _clock = new HlcClock(1, TimeProvider.System);
        _snowflake = new SnowflakeGenerator(1, TimeProvider.System);
    }

    [GlobalSetup]
    public async Task SetupAsync()
    {
        _storageDir = Path.Combine(Path.GetTempPath(), string.Concat("terrabase-bdn-", Ulid.NewUlid().ToString()));
        _ = Directory.CreateDirectory(_storageDir);
        _dbId = Ulid.NewUlid();
        var key = StorageBenchmarks.CreateRawKey();
        _engine = StorageBenchmarks.CreateEngine(_storageDir, fsyncCommitLog: false);
        _session = await StorageBenchmarks.CreateDatabaseAsync(_engine, _dbId, key, CancellationToken.None);
        await StorageBenchmarks.CreateItemsTableAsync(_session, _dbId, CancellationToken.None);
        await StorageBenchmarks.SeedItemsAsync(_session, _dbId, 1000, batchSize: 100, CancellationToken.None);
    }

    [GlobalCleanup]
    public async Task CleanupAsync()
    {
        if (_session is not null)
        {
            await _session.DisposeAsync();
            _session = null;
        }

        if (_engine is not null)
        {
            await _engine.DisposeAsync();
            _engine = null;
        }

        if (_storageDir is not null && Directory.Exists(_storageDir))
        {
            Directory.Delete(_storageDir, recursive: true);
            _storageDir = null;
        }
    }

    [Benchmark]
    public int MutationBatchCodecEncode50Rows() => MutationBatchCodec.Encode(_batch).Length;

    [Benchmark]
    public int MutationBatchCodecDecode50Rows() =>
        MutationBatchCodec.TryDecode(_encodedBatch, out var decoded) ? decoded.Mutations.Count : -1;

    [Benchmark]
    public string CausalBookmarkEncode5Regions() => _bookmark.Encode();

    [Benchmark]
    public int CausalBookmarkTryDecode5Regions() =>
        CausalBookmark.TryDecode(_encodedBookmark, out var decoded) ? decoded.Entries.Count : -1;

    [Benchmark]
    public bool CausalBookmarkDominates5Regions() => _bookmark.Dominates(_subsetBookmark);

    [Benchmark]
    public HlcTimestamp HlcClockTick() => _clock.Tick();

    [Benchmark]
    public long SnowflakeNext() => _snowflake.Next();

    [Benchmark]
    public async ValueTask<int> StoragePointReadSingleRow()
    {
        if (_session is not { } session)
        {
            throw new InvalidOperationException("Benchmark session was not initialized.");
        }

        var id = (Interlocked.Increment(ref _readCounter) % 1000L) + 1L;
        var result = await StorageBenchmarks.ExecuteReadAsync(
            session,
            _dbId,
            [StorageBenchmarks.Statement("SELECT id, name, score, payload FROM items WHERE id = ?1", SqlValue.FromIntegral(id))],
            CancellationToken.None);
        return result.ResultSets[0].Rows.Count;
    }
}
