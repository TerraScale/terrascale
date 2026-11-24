using System.Collections.Frozen;

namespace TerraBase.Storage;

public sealed class HlcClock(ushort nodeId, TimeProvider time)
{
    public const long MaxFutureSkewMs = 30_000;

    private readonly Lock _gate = new();
    private long _wall;
    private ushort _logical;

    public ushort NodeId { get; } = nodeId;

    public HlcTimestamp Current
    {
        get
        {
            lock (_gate)
            {
                return new HlcTimestamp(_wall, _logical, NodeId);
            }
        }
    }

    public void Restore(HlcTimestamp persisted)
    {
        lock (_gate)
        {
            if (persisted.WallMs > _wall)
            {
                _wall = persisted.WallMs;
                _logical = persisted.Logical;
            }
            else if (persisted.WallMs == _wall && persisted.Logical > _logical)
            {
                _logical = persisted.Logical;
            }
        }
    }

    public HlcTimestamp Tick()
    {
        var wall = time.GetUtcNow().ToUnixTimeMilliseconds();
        lock (_gate)
        {
            if (wall > _wall)
            {
                _wall = wall;
                _logical = 0;
            }
            else
            {
                Increment();
            }

            return new HlcTimestamp(_wall, _logical, NodeId);
        }
    }

    public HlcTimestamp Observe(HlcTimestamp remote)
    {
        var wall = time.GetUtcNow().ToUnixTimeMilliseconds();
        var maxWall = wall + MaxFutureSkewMs;
        var remoteWall = remote.WallMs > maxWall ? maxWall : remote.WallMs;
        var remoteLogical = remote.WallMs > maxWall ? (ushort)0 : remote.Logical;
        lock (_gate)
        {
            var next = Math.Max(Math.Max(_wall, remoteWall), wall);
            if (next == _wall && next == remoteWall)
            {
                _logical = Math.Max(_logical, remoteLogical);
                Increment();
            }
            else if (next == _wall)
            {
                Increment();
            }
            else if (next == remoteWall)
            {
                _wall = next;
                _logical = remoteLogical;
                Increment();
            }
            else
            {
                _wall = next;
                _logical = 0;
            }

            return new HlcTimestamp(_wall, _logical, NodeId);
        }
    }

    private void Increment()
    {
        if (_logical == ushort.MaxValue)
        {
            _wall++;
            _logical = 0;
            return;
        }

        _logical++;
    }
}

public sealed class SnowflakeGenerator
{
    public const long EpochMs = 1735689600000;
    public const int WorkerBits = 6;
    public const int SequenceBits = 16;
    public const int MaxWorkerId = (1 << WorkerBits) - 1;

    private readonly Lock _gate = new();
    private readonly TimeProvider _time;
    private readonly long _worker;
    private long _lastMs;
    private long _sequence;

    public SnowflakeGenerator(byte workerId, TimeProvider time)
    {
        ArgumentOutOfRangeException.ThrowIfGreaterThan(workerId, MaxWorkerId);
        _worker = workerId;
        _time = time;
    }

    public long Next()
    {
        var now = Math.Max(_time.GetUtcNow().ToUnixTimeMilliseconds() - EpochMs, 0);
        lock (_gate)
        {
            if (now > _lastMs)
            {
                _lastMs = now;
                _sequence = 0;
            }
            else if (++_sequence > (1L << SequenceBits) - 1)
            {
                _lastMs++;
                _sequence = 0;
            }

            return (_lastMs << (WorkerBits + SequenceBits)) | (_worker << SequenceBits) | _sequence;
        }
    }
}

public sealed class DeltaColumnRegistry
{
    private readonly Lock _gate = new();
    private FrozenSet<string> _columns = FrozenSet<string>.Empty;
    private int _version;

    public int Version => Volatile.Read(ref _version);

    public void Register(Ulid databaseId, string table, string column)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(table);
        ArgumentException.ThrowIfNullOrWhiteSpace(column);
        var key = Key(databaseId, table, column);
        lock (_gate)
        {
            if (_columns.Contains(key))
            {
                return;
            }

            _columns = _columns.Append(key).ToFrozenSet(StringComparer.OrdinalIgnoreCase);
            Interlocked.Increment(ref _version);
        }
    }

    public bool IsDeltaColumn(Ulid databaseId, string table, string column) =>
        Volatile.Read(ref _columns).Contains(Key(databaseId, table, column));

    private static string Key(Ulid databaseId, string table, string column) =>
        string.Concat(databaseId.ToString(), "\u001f", table, "\u001f", column);
}
