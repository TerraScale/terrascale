using System.Text;

namespace TerraBase.Storage;

public sealed record StorageEngineOptions(string RootDirectory, string Region, ushort NodeId, byte SnowflakeWorkerId, DeltaColumnRegistry DeltaColumns)
{
    public int ReaderCount { get; init; } = 4;

    public int MaxBatchSize { get; init; } = 64;

    public int QueueCapacity { get; init; } = 4096;

    public int CacheSizeKib { get; init; } = 8192;

    public int ReaderCacheSizeKib { get; init; } = 2048;

    public long SegmentMaxBytes { get; init; } = 4 * 1024 * 1024;

    public TimeSpan SegmentMaxAge { get; init; } = TimeSpan.FromSeconds(60);

    public TimeSpan BookmarkWaitTimeout { get; init; } = TimeSpan.FromSeconds(5);

    public TimeSpan LaneWaitTimeout { get; init; } = TimeSpan.FromSeconds(10);

    public TimeSpan TransactionIdleTimeout { get; init; } = TimeSpan.FromSeconds(30);

    public TimeSpan StatementTimeout { get; init; } = Timeout.InfiniteTimeSpan;

    public TimeSpan StagedWriteTtl { get; init; } = TimeSpan.FromMinutes(1);

    public int MaxPendingRemoteCommits { get; init; } = 65536;

    public bool FsyncCommitLog { get; init; } = true;

    public static bool IsValidRegion(string region) =>
        region.Length > 0
        && Encoding.UTF8.GetByteCount(region) <= CausalBookmark.MaxRegionBytes
        && region.All(static character => char.IsAsciiLetterLower(character) || char.IsAsciiDigit(character) || character is '-' or '_');

    internal void Validate()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(RootDirectory);
        ArgumentNullException.ThrowIfNull(DeltaColumns);
        if (!IsValidRegion(Region))
        {
            throw new ArgumentException("Region must be 1-64 lowercase ASCII letters, digits, '-' or '_'.", nameof(Region));
        }

        ArgumentOutOfRangeException.ThrowIfGreaterThan(SnowflakeWorkerId, SnowflakeGenerator.MaxWorkerId);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(ReaderCount);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaxBatchSize);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(QueueCapacity);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(SegmentMaxBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaxPendingRemoteCommits);
    }
}

internal sealed class VersionVector
{
    private readonly Lock _gate = new();
    private readonly Dictionary<string, long> _watermarks = new(StringComparer.Ordinal);
    private TaskCompletionSource _changed = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public long Get(string origin)
    {
        lock (_gate)
        {
            return _watermarks.GetValueOrDefault(origin);
        }
    }

    public void Set(IEnumerable<KeyValuePair<string, long>> watermarks)
    {
        TaskCompletionSource signal;
        lock (_gate)
        {
            foreach (var (origin, sequence) in watermarks)
            {
                if (sequence > _watermarks.GetValueOrDefault(origin))
                {
                    _watermarks[origin] = sequence;
                }
            }

            signal = _changed;
            _changed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        signal.TrySetResult();
    }

    public Dictionary<string, long> Copy()
    {
        lock (_gate)
        {
            return new Dictionary<string, long>(_watermarks, StringComparer.Ordinal);
        }
    }

    public CausalBookmark Snapshot()
    {
        lock (_gate)
        {
            return CausalBookmark.Create(_watermarks);
        }
    }

    public bool Satisfies(CausalBookmark bookmark)
    {
        lock (_gate)
        {
            foreach (var (origin, sequence) in bookmark.Entries)
            {
                if (_watermarks.GetValueOrDefault(origin) < sequence)
                {
                    return false;
                }
            }

            return true;
        }
    }

    public Task WhenChanged()
    {
        lock (_gate)
        {
            return _changed.Task;
        }
    }

    public void Close()
    {
        TaskCompletionSource signal;
        lock (_gate)
        {
            signal = _changed;
        }

        signal.TrySetResult();
    }
}
