using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Frozen;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Channels;
using TerraScale.Contracts.Continuity;

namespace TerraBase.Storage;

internal readonly union SessionOpenResult(DatabaseSession, StorageCorrupted, StorageBusy);

internal sealed record SessionRecovered;

internal readonly union RecoverOutcome(SessionRecovered, StorageCorrupted, StorageBusy);

internal sealed record SessionContext(
    Ulid DatabaseId,
    string Directory,
    string DatabasePath,
    byte[] RawKey,
    StorageEngineOptions Options,
    TimeProvider Time,
    HlcClock Clock,
    SnowflakeGenerator Snowflake);

internal sealed partial class DatabaseSession : ISqlDatabaseSession
{
    private const int ApplicationIdBase = 0x54420000;
    private const int HistoryPageSize = 512;
    private static readonly TimeSpan ReaderDrainTimeout = TimeSpan.FromSeconds(5);
    private static readonly BookmarkReached Reached = new();
    private static long s_nextIncarnation;

    private readonly long _sessionIncarnation = Interlocked.Increment(ref s_nextIncarnation);
    private readonly SessionContext _context;
    private readonly SqliteConnection _writer;
    private readonly PreupdateCapture _capture;
    private readonly CommitLogCipher _cipher;
    private readonly CommitLogOptions _logOptions;
    private readonly Channel<SqliteConnection?> _readers;
    private readonly Channel<LaneRequest> _requests;
    private readonly Lock _readersGate = new();
    private readonly HashSet<SqliteConnection> _rentedReaders = [];
    private readonly HashSet<ReadTransaction> _readTransactions = [];
    private readonly TaskCompletionSource _readersDrained = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Lock _logsGate = new();
    private readonly Dictionary<string, OriginLog> _logs = new(StringComparer.Ordinal);
    private readonly Lock _stagedGate = new();
    private readonly Dictionary<Ulid, StagedDescriptor> _staged = [];
    private readonly Dictionary<string, SortedDictionary<long, CommitMetadata>> _pending = new(StringComparer.Ordinal);
    private readonly VersionVector _versions = new();
    private readonly Func<DatabaseSession, Task> _release;
    private readonly bool _strict;
    private FrozenSet<string>? _excludedChangeTables;
    private SqliteOpenOptions? _readerOptions;
    private Task _lane = Task.CompletedTask;
    private LaneRequest[] _inFlight = [];
    private WriteTransaction? _activeTransaction;
    private long _schemaEpoch;
    private long _filteredChangeGeneration = 1;
    private int _references = 1;
    private int _closed;
    private int _laneFaulted;
    private int _recoveredQuarantined;
    private int _openingReaders;
    private bool _writeGroupBroken;

    private DatabaseSession(SessionContext context, SqliteConnection writer, PreupdateCapture capture, CommitLogCipher cipher, bool strict, Func<DatabaseSession, Task> release)
    {
        _context = context;
        _writer = writer;
        _capture = capture;
        _cipher = cipher;
        _strict = strict;
        _release = release;
        _logOptions = new CommitLogOptions(context.Options.SegmentMaxBytes, context.Options.SegmentMaxAge, context.Options.FsyncCommitLog);
        _readers = Channel.CreateBounded<SqliteConnection?>(new BoundedChannelOptions(context.Options.ReaderCount) { FullMode = BoundedChannelFullMode.Wait });
        for (var index = 0; index < context.Options.ReaderCount; index++)
        {
            _ = _readers.Writer.TryWrite(null);
        }

        _requests = Channel.CreateBounded<LaneRequest>(new BoundedChannelOptions(context.Options.QueueCapacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
        });
    }

    public Ulid DatabaseId => _context.DatabaseId;

    public ulong SchemaEpoch => (ulong)Interlocked.Read(ref _schemaEpoch);

    public CausalBookmark CurrentBookmark => _versions.Snapshot();

    public long ChangeGeneration => _versions.Generation;

    public long FilteredChangeGeneration =>
        Volatile.Read(ref _excludedChangeTables) is null ? ChangeGeneration : Volatile.Read(ref _filteredChangeGeneration);

    public long SessionIncarnation => _sessionIncarnation;

    public void ConfigureFilteredChangeTables(IReadOnlySet<string> excludedTables)
    {
        ArgumentNullException.ThrowIfNull(excludedTables);
        Volatile.Write(ref _excludedChangeTables, excludedTables.ToFrozenSet(StringComparer.OrdinalIgnoreCase));
        _ = Interlocked.Increment(ref _filteredChangeGeneration);
    }

    public bool IsClosed => Volatile.Read(ref _closed) != 0;

    internal bool IsFaulted => Volatile.Read(ref _laneFaulted) != 0;

    public bool IsUsable => !IsClosed && !IsFaulted;

    internal int TakeRecoveredQuarantined() => Interlocked.Exchange(ref _recoveredQuarantined, 0);

    internal DatabaseSession WithQuarantined(int quarantined)
    {
        _ = Interlocked.Add(ref _recoveredQuarantined, quarantined);
        return this;
    }

    internal int References => Volatile.Read(ref _references);

    internal string Directory => _context.Directory;

    internal byte[] RawKey => _context.RawKey;

    private string Region => _context.Options.Region;

    private StorageEngineOptions Options => _context.Options;

    public static SessionOpenResult Open(SessionContext context, bool create, bool strict, Func<DatabaseSession, Task> release)
    {
        var options = new SqliteOpenOptions(context.DatabasePath, false, context.RawKey, context.Options.CacheSizeKib, context.DatabaseId, context.Options.DeltaColumns)
        {
            StatementTimeout = context.Options.StatementTimeout,
            Creating = create,
            SkipSchemaProbe = true,
        };
        var opened = SqliteConnection.Open(options);
        return opened switch
        {
            SqliteConnection writer => Initialize(context, options, writer, create, strict, release),
            SqliteStatus status => OpenFailure(context, status),
        };
    }

    private static SessionOpenResult OpenFailure(SessionContext context, SqliteStatus status) =>
        status.IsBusy ? new StorageBusy(status.Message, TimeSpan.FromMilliseconds(50)) : new StorageCorrupted(context.DatabaseId, status.Message);

    private static SessionOpenResult Initialize(SessionContext context, SqliteOpenOptions options, SqliteConnection writer, bool create, bool strict, Func<DatabaseSession, Task> release)
    {
        SqliteStatus setup;
        var applicationId = 0L;
        if (create)
        {
            applicationId = ApplicationIdBase | (strict ? 1 : 0);
            setup = writer.Begin("BEGIN IMMEDIATE;");
            if (setup.IsOk)
            {
                setup = writer.Exec(MetaStore.SchemaSql);
            }

            if (setup.IsOk)
            {
                setup = writer.Exec(string.Concat("PRAGMA application_id = ", applicationId.ToString(CultureInfo.InvariantCulture), ";"));
            }

            if (setup.IsOk)
            {
                setup = writer.Exec("COMMIT;");
            }

            if (!setup.IsOk && writer.InTransaction)
            {
                _ = writer.Exec("ROLLBACK;");
            }

            if (setup.IsOk)
            {
                setup = writer.Exec("PRAGMA journal_mode = WAL; PRAGMA synchronous = NORMAL; PRAGMA foreign_keys = ON; PRAGMA journal_size_limit = 1048576;");
            }

            if (setup.IsOk)
            {
                if (FileIo.SyncFile(context.DatabasePath) is { } syncError)
                {
                    writer.Dispose();
                    return new StorageBusy(syncError, TimeSpan.FromMilliseconds(50));
                }

                if (Path.GetDirectoryName(context.DatabasePath) is { Length: > 0 } directory)
                {
                    FileIo.SyncDirectory(directory);
                }
            }
        }
        else
        {
            setup = writer.QueryFirst("PRAGMA application_id", out var row);
            _ = row is not null && row[0].TryGetIntegral(out applicationId);
            if (setup.IsOk && (applicationId & ~1L) != ApplicationIdBase)
            {
                setup = writer.Exec(MetaStore.SchemaSql);
            }
        }

        if (setup.IsOk)
        {
            setup = writer.RegisterSnowflake(context.Snowflake);
        }

        if (!setup.IsOk)
        {
            writer.Dispose();
            return OpenFailure(context, setup);
        }

        var capture = writer.AttachCapture();
        var cipher = new CommitLogCipher(context.RawKey, context.DatabaseId);
        var session = new DatabaseSession(context, writer, capture, cipher, (applicationId & 1) == 1, release);
        var readerOptions = options with
        {
            ReadOnly = true,
            Creating = false,
            SkipSchemaProbe = true,
            CacheSizeKib = context.Options.ReaderCacheSizeKib,
        };
        if (create)
        {
            return session.OpenReaders(readerOptions);
        }

        var recovered = session.Recover();
        return recovered switch
        {
            SessionRecovered => session.OpenReaders(readerOptions),
            StorageCorrupted corrupted => session.Abandon(corrupted),
            StorageBusy busy => session.Abandon(busy),
        };
    }

    private SessionOpenResult OpenReaders(SqliteOpenOptions readerOptions)
    {
        _readerOptions = readerOptions;
        _lane = Task.Run(RunGuardedLaneAsync);
        return this;
    }

    private SessionOpenResult Abandon(SessionOpenResult failure)
    {
        DisposeResources();
        return failure;
    }

    public void AddReference() => Interlocked.Increment(ref _references);

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Decrement(ref _references) == 0)
        {
            await _release(this);
        }
    }

    public async Task CloseAsync()
    {
        if (Interlocked.Exchange(ref _closed, 1) != 0)
        {
            return;
        }

        _requests.Writer.TryComplete();
        Volatile.Read(ref _activeTransaction)?.Abandon();
        ReadTransaction[] activeTransactions;
        List<SqliteConnection> idleReaders = [];
        lock (_readersGate)
        {
            _readers.Writer.TryComplete();
            activeTransactions = [.. _readTransactions];
            while (_readers.Reader.TryRead(out var idleReader))
            {
                if (idleReader is not null)
                {
                    idleReaders.Add(idleReader);
                }
            }

            foreach (var rented in _rentedReaders)
            {
                rented.Interrupt();
            }

            if (_rentedReaders.Count == 0 && _openingReaders == 0)
            {
                _readersDrained.TrySetResult();
            }
        }

        foreach (var transaction in activeTransactions)
        {
            transaction.Abandon();
        }

        foreach (var idleReader in idleReaders)
        {
            idleReader.Dispose();
        }

        await _lane.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        while (!_readersDrained.Task.IsCompleted)
        {
            using var drainTimeout = new CancellationTokenSource();
            var delay = Task.Delay(ReaderDrainTimeout, _context.Time, drainTimeout.Token);
            var completed = await Task.WhenAny(_readersDrained.Task, delay);
            await drainTimeout.CancelAsync();
            if (completed == _readersDrained.Task)
            {
                break;
            }

            lock (_readersGate)
            {
                foreach (var rented in _rentedReaders)
                {
                    rented.Interrupt();
                }
            }
        }

        _versions.Close();
        DisposeResources();
    }

    public ValueTask<WaitForBookmarkOutcome> WaitForBookmarkAsync(CausalBookmark bookmark, TimeSpan timeout, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(bookmark);
        return bookmark.IsEmpty || _versions.Satisfies(bookmark)
            ? ValueTask.FromResult<WaitForBookmarkOutcome>(Reached)
            : WaitForBookmarkSlowAsync(bookmark, timeout, cancellationToken);
    }

    private async ValueTask<WaitForBookmarkOutcome> WaitForBookmarkSlowAsync(CausalBookmark bookmark, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var started = _context.Time.GetTimestamp();
        while (!_versions.Satisfies(bookmark))
        {
            var changed = _versions.WhenChanged();
            if (_versions.Satisfies(bookmark))
            {
                break;
            }

            var remaining = timeout - _context.Time.GetElapsedTime(started);
            if (remaining <= TimeSpan.Zero || IsClosed)
            {
                return new StorageTimeout(string.Concat("bookmark ", bookmark.Encode(), " was not reached in time"));
            }

            using var delayCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var delay = Task.Delay(remaining, _context.Time, delayCancellation.Token);
            _ = await Task.WhenAny(changed, delay);
            await delayCancellation.CancelAsync();
            cancellationToken.ThrowIfCancellationRequested();
        }

        return Reached;
    }

    public async IAsyncEnumerable<ChangefeedEntry> SubscribeChangesAsync(CausalBookmark after, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(after);
        Dictionary<string, long> cursor = new(StringComparer.Ordinal);
        foreach (var (origin, sequence) in after.Entries)
        {
            cursor[origin] = sequence;
        }

        while (!IsClosed)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var signal = _versions.WhenChanged();
            var watermarks = _versions.Copy();
            var progressed = false;
            foreach (var log in Logs())
            {
                var from = cursor.GetValueOrDefault(log.Origin) + 1;
                var visible = watermarks.GetValueOrDefault(log.Origin);
                if (visible < from)
                {
                    continue;
                }

                var read = log.Read(from, 256);
                IReadOnlyList<CommitMetadata> entries = read switch
                {
                    IReadOnlyList<CommitMetadata> page => page,
                    CommitLogGap or StorageCorrupted => [],
                };
                var resume = read switch
                {
                    IReadOnlyList<CommitMetadata> => -1L,
                    CommitLogGap gap => gap.OldestAvailable - 1,
                    StorageCorrupted => long.MaxValue,
                };
                if (resume == long.MaxValue)
                {
                    yield break;
                }

                if (resume >= 0)
                {
                    cursor[log.Origin] = resume;
                    progressed = true;
                    continue;
                }

                foreach (var entry in entries)
                {
                    if (entry.Sequence > visible)
                    {
                        break;
                    }

                    cursor[log.Origin] = entry.Sequence;
                    progressed = true;
                    yield return new ChangefeedEntry(
                        DatabaseId,
                        entry.TxId,
                        entry.OriginRegion,
                        entry.Sequence,
                        entry.SchemaEpoch,
                        entry.Mode,
                        entry.DeltaPayload,
                        entry.Hlc.WallTime);
                }
            }

            if (!progressed)
            {
                await signal.WaitAsync(cancellationToken);
            }
        }
    }

    public ValueTask<ReadCommitLogOutcome> ReadCommitLogAsync(string originRegion, long fromSequence, int maxEntries, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxEntries);
        cancellationToken.ThrowIfCancellationRequested();
        if (Log(originRegion) is not { } log)
        {
            return ValueTask.FromResult<ReadCommitLogOutcome>(new CommitLogPage(DatabaseId, originRegion, [], fromSequence));
        }

        var visible = _versions.Get(originRegion);
        var read = log.Read(Math.Max(fromSequence, 1), maxEntries);
        ReadCommitLogOutcome result = read switch
        {
            IReadOnlyList<CommitMetadata> page => FilterVisiblePage(originRegion, fromSequence, page, visible),
            CommitLogGap gap => gap,
            StorageCorrupted corrupted => corrupted,
        };
        return ValueTask.FromResult(result);
    }

    private CommitLogPage FilterVisiblePage(string origin, long fromSequence, IReadOnlyList<CommitMetadata> page, long visible)
    {
        if (page.Count == 0 || page[^1].Sequence <= visible)
        {
            return Page(origin, fromSequence, page);
        }

        var count = 0;
        while (count < page.Count && page[count].Sequence <= visible)
        {
            count++;
        }

        if (count == 0)
        {
            return Page(origin, fromSequence, []);
        }

        var filtered = new CommitMetadata[count];
        for (var index = 0; index < count; index++)
        {
            filtered[index] = page[index];
        }

        return Page(origin, fromSequence, filtered);
    }

    public ValueTask<ListSegmentsOutcome> ListSealedSegmentsAsync(string originRegion, long afterSequence, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        IReadOnlyList<CommitLogSegment> segments = Log(originRegion) is { } log ? log.ListSealed(afterSequence) : [];
        return ValueTask.FromResult<ListSegmentsOutcome>(segments);
    }

    public ValueTask<OpenSegmentOutcome> OpenSegmentAsync(CommitLogSegment segment, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(segment);
        cancellationToken.ThrowIfCancellationRequested();
        var result = Log(segment.OriginRegion) is { } log
            ? log.OpenSegment(segment)
            : new OpenSegmentOutcome(new SegmentNotFound(segment.DatabaseId, segment.OriginRegion, segment.FirstSequence, segment.LastSequence));
        return ValueTask.FromResult(result);
    }

    public async ValueTask<ReadArchivedSegmentOutcome> ReadArchivedSegmentAsync(
        CommitLogSegment segment, Stream content, ReplayArchivedCommitBatch replay, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(segment);
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(replay);
        if (segment.DatabaseId != DatabaseId || !StorageEngineOptions.IsValidRegion(segment.OriginRegion) || string.Equals(segment.OriginRegion, Region, StringComparison.Ordinal))
        {
            return new InvalidSegment("segment does not belong to a remote origin of this database");
        }

        if (segment.SizeBytes <= 0 || !content.CanSeek || content.Length - content.Position != segment.SizeBytes)
        {
            return new InvalidSegment("segment size does not match the manifest");
        }

        if (segment.FirstSequence <= 0 || segment.LastSequence < segment.FirstSequence)
        {
            return new InvalidSegment("segment sequence range is invalid");
        }

        var remaining = segment.SizeBytes;
        var remainingToRead = segment.SizeBytes;
        var through = segment.FirstSequence - 1;
        var batchBytes = 0L;
        var maxEntries = Math.Min(Options.MaxPendingRemoteCommits, HistoryPageSize);
        var estimatedFrames = (int)Math.Min(segment.LastSequence - segment.FirstSequence + 1, maxEntries);
        List<List<CommitMetadata>>? extraBatches = null;
        var firstBatch = new List<CommitMetadata>(Math.Max(estimatedFrames, 1));
        var batch = firstBatch;
        var originUtf8 = Encoding.UTF8.GetBytes(segment.OriginRegion);
        var initialBufferSize = Math.Clamp((int)Math.Min(segment.SizeBytes, 64 * 1024), CommitFrame.PrefixSize, 64 * 1024);
        var rented = ArrayPool<byte>.Shared.Rent(initialBufferSize);
        var buffered = 0;
        var offset = 0;
        try
        {
            while (remaining > 0)
            {
                if (remaining < CommitFrame.PrefixSize)
                {
                    return new InvalidSegment("segment ended inside a commit log frame");
                }

                if (buffered - offset < CommitFrame.PrefixSize)
                {
                    var carry = buffered - offset;
                    if (carry > 0 && offset > 0)
                    {
                        rented.AsSpan(offset, carry).CopyTo(rented);
                    }

                    buffered = carry;
                    offset = 0;
                    while (buffered < CommitFrame.PrefixSize)
                    {
                        var toRead = (int)Math.Min(rented.Length - buffered, remainingToRead);
                        if (toRead <= 0)
                        {
                            return new InvalidSegment("segment ended inside a commit log frame");
                        }

                        var read = await content.ReadAsync(rented.AsMemory(buffered, toRead), cancellationToken);
                        if (read == 0)
                        {
                            return new InvalidSegment("segment ended inside a commit log frame");
                        }

                        buffered += read;
                        remainingToRead -= read;
                    }
                }

                var bodySize = BinaryPrimitives.ReadUInt32LittleEndian(rented.AsSpan(offset, CommitFrame.PrefixSize));
                var frameSize = CommitFrame.PrefixSize + (long)bodySize + CommitFrame.CrcSize;
                if (bodySize > CommitFrame.MaxBodySize || frameSize > remaining)
                {
                    return new InvalidSegment("segment contains a frame outside its declared size");
                }

                var frameBytes = (int)frameSize;
                if (buffered - offset < frameBytes)
                {
                    var carry = buffered - offset;
                    if (rented.Length < frameBytes)
                    {
                        var grown = ArrayPool<byte>.Shared.Rent(frameBytes);
                        if (carry > 0)
                        {
                            rented.AsSpan(offset, carry).CopyTo(grown);
                        }

                        ArrayPool<byte>.Shared.Return(rented);
                        rented = grown;
                    }
                    else if (carry > 0 && offset > 0)
                    {
                        rented.AsSpan(offset, carry).CopyTo(rented);
                    }

                    buffered = carry;
                    offset = 0;
                    while (buffered < frameBytes)
                    {
                        var toRead = (int)Math.Min(rented.Length - buffered, remainingToRead);
                        if (toRead <= 0)
                        {
                            return new StorageCorrupted(DatabaseId, "segment failed CRC32C or authentication");
                        }

                        var read = await content.ReadAsync(rented.AsMemory(buffered, toRead), cancellationToken);
                        if (read == 0)
                        {
                            return new StorageCorrupted(DatabaseId, "segment failed CRC32C or authentication");
                        }

                        buffered += read;
                        remainingToRead -= read;
                    }
                }

                var bodyAndCrc = rented.AsSpan(offset + CommitFrame.PrefixSize, (int)bodySize + CommitFrame.CrcSize);
                if (!CommitFrame.TryDecodeChecked(bodyAndCrc, segment.OriginRegion, originUtf8, _cipher, out var commit))
                {
                    return new StorageCorrupted(DatabaseId, "segment failed CRC32C or authentication");
                }

                if (through >= segment.LastSequence || commit.Sequence != through + 1)
                {
                    return new InvalidSegment("segment sequences are not contiguous");
                }

                offset += frameBytes;
                remaining -= frameSize;
                through = commit.Sequence;
                if (batch.Count > 0 && (batch.Count >= maxEntries || batchBytes + frameSize > Options.SegmentMaxBytes))
                {
                    extraBatches ??= [firstBatch];
                    batch = new List<CommitMetadata>(Math.Min(maxEntries, 64));
                    extraBatches.Add(batch);
                    batchBytes = 0;
                }

                batch.Add(commit);
                batchBytes += frameSize;
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }

        if (through != segment.LastSequence)
        {
            return new InvalidSegment("segment range does not match its content");
        }

        if (extraBatches is null)
        {
            if (firstBatch.Count > 0 && !await replay(firstBatch, cancellationToken))
            {
                return new ArchivedSegmentReplayStopped();
            }

            return new ArchivedSegmentRead();
        }

        foreach (var validated in extraBatches)
        {
            if (validated.Count > 0 && !await replay(validated, cancellationToken))
            {
                return new ArchivedSegmentReplayStopped();
            }
        }

        return new ArchivedSegmentRead();
    }

    internal OriginLog? Log(string origin)
    {
        lock (_logsGate)
        {
            return _logs.GetValueOrDefault(origin);
        }
    }

    internal void SealCommittedLogs()
    {
        foreach (var log in Logs())
        {
            _ = log.SealCommitted(_versions.Get(log.Origin));
        }
    }

    internal CausalBookmark PrunableThrough(DateTimeOffset cutoff, CausalBookmark archiveFloor) =>
        CausalBookmark.Create(Logs().Select(log => new KeyValuePair<string, long>(
            log.Origin, log.PrunableThrough(cutoff, Math.Min(_versions.Get(log.Origin), archiveFloor.Get(log.Origin))))));

    internal StorageBusy? PruneArchivedSegments(DateTimeOffset cutoff, CausalBookmark archiveFloor, CausalBookmark confirmed)
    {
        foreach (var log in Logs())
        {
            var through = Math.Min(_versions.Get(log.Origin), Math.Min(archiveFloor.Get(log.Origin), confirmed.Get(log.Origin)));
            if (log.PruneSealed(cutoff, through) is { } busy)
            {
                return busy;
            }
        }

        return null;
    }

    internal bool Strict => _strict;

    internal bool HasFullHistory(CausalBookmark target) =>
        target.Entries.All(entry => entry.Value <= 0 || (Log(entry.Key) is { FirstAvailable: 1 } log && log.LastSequence >= entry.Value));

    internal SessionContext TargetContext(string directory, Ulid databaseId, ReadOnlyMemory<byte> rawKey) => _context with
    {
        DatabaseId = databaseId,
        Directory = directory,
        DatabasePath = Path.Combine(directory, string.Concat(databaseId.ToString(), ".db")),
        RawKey = rawKey.ToArray(),
    };

    internal StorageBusy? CopyHistoryTo(SessionContext target, CausalBookmark through)
    {
        using var cipher = new CommitLogCipher(target.RawKey, target.DatabaseId);
        foreach (var (origin, last) in through.Entries)
        {
            if (last <= 0 || Log(origin) is not { FirstAvailable: 1 } source)
            {
                continue;
            }

            var opened = OriginLog.Open(target.Directory, target.DatabaseId, origin, 0, cipher, _logOptions, target.Time);
            var failure = opened switch
            {
                OriginLog destination => CopyOriginHistory(source, destination, last),
                StorageCorrupted corrupted => new StorageBusy(corrupted.Message, TimeSpan.Zero),
            };
            if (failure is not null)
            {
                return failure;
            }
        }

        return null;
    }

    private StorageBusy? CopyOriginHistory(OriginLog source, OriginLog destination, long last)
    {
        using (destination)
        {
            for (var next = 1L; next <= last;)
            {
                var read = source.Read(next, HistoryPageSize);
                List<CommitMetadata> page = read switch
                {
                    IReadOnlyList<CommitMetadata> entries => [.. entries.Where(entry => entry.Sequence <= last)],
                    CommitLogGap or StorageCorrupted => [],
                };
                if (page.Count == 0)
                {
                    return new StorageBusy(read switch
                    {
                        IReadOnlyList<CommitMetadata> => string.Concat("commit log for origin ", source.Origin, " ends before sequence ", next.ToString(CultureInfo.InvariantCulture)),
                        CommitLogGap gap => string.Concat("commit log for origin ", gap.OriginRegion, " has a gap before sequence ", gap.OldestAvailable.ToString(CultureInfo.InvariantCulture)),
                        StorageCorrupted corrupted => corrupted.Message,
                    }, TimeSpan.Zero);
                }

                foreach (var chunk in CommitFrame.Batches(page, Options.SegmentMaxBytes))
                {
                    var appended = destination.AppendNoSync(chunk, out _);
                    if (!appended.IsOk)
                    {
                        return appended.ToBusy();
                    }

                    destination.MarkCommitted();
                }

                next = page[^1].Sequence + 1;
            }

            var synced = destination.Sync();
            if (!synced.IsOk)
            {
                return synced.ToBusy();
            }

            return destination.SealCommitted(last) ? null : new StorageBusy(string.Concat("copied commit log for origin ", source.Origin, " could not be sealed"), TimeSpan.Zero);
        }
    }

    internal OriginLog[] Logs()
    {
        lock (_logsGate)
        {
            return [.. _logs.Values];
        }
    }

    private CommitLogPage Page(string origin, long fromSequence, IReadOnlyList<CommitMetadata> entries) =>
        new(DatabaseId, origin, entries, entries.Count > 0 ? entries[^1].Sequence + 1 : Math.Max(fromSequence, 1));

    private OriginLog? EnsureLog(string origin, long baseSequence)
    {
        lock (_logsGate)
        {
            if (_logs.TryGetValue(origin, out var existing))
            {
                return existing;
            }

            var log = OriginLog.Open(_context.Directory, DatabaseId, origin, baseSequence, _cipher, _logOptions, _context.Time) switch
            {
                OriginLog opened => opened,
                StorageCorrupted => null,
            };
            if (log is not null)
            {
                _logs[origin] = log;
            }

            return log;
        }
    }

    private CausalBookmark BookmarkWith(string origin, long sequence) => _versions.Snapshot().With(origin, sequence);

    private void DisposeResources()
    {
        while (_readers.Reader.TryRead(out var reader))
        {
            reader?.Dispose();
        }

        _writer.Dispose();
        lock (_logsGate)
        {
            foreach (var log in _logs.Values)
            {
                log.Dispose();
            }

            _logs.Clear();
        }

        _cipher.Dispose();
    }

    private void RecordFilteredChange(DeltaBatch batch)
    {
        if (ShouldBumpFilteredGeneration(batch))
        {
            _ = Interlocked.Increment(ref _filteredChangeGeneration);
        }
    }

    private void RecordFilteredChange(List<PendingApply> applied)
    {
        for (var index = 0; index < applied.Count; index++)
        {
            var pending = applied[index];
            if (!pending.Quarantined && ShouldBumpFilteredGeneration(pending.Batch))
            {
                _ = Interlocked.Increment(ref _filteredChangeGeneration);
                return;
            }
        }
    }

    private void RecordFilteredChange(Dictionary<string, HlcTimestamp> touchedTables, bool hasDdl)
    {
        if (hasDdl)
        {
            _ = Interlocked.Increment(ref _filteredChangeGeneration);
            return;
        }

        if (touchedTables.Count == 0)
        {
            return;
        }

        if (Volatile.Read(ref _excludedChangeTables) is not { } excluded)
        {
            _ = Interlocked.Increment(ref _filteredChangeGeneration);
            return;
        }

        foreach (var entry in touchedTables)
        {
            if (!excluded.Contains(entry.Key))
            {
                _ = Interlocked.Increment(ref _filteredChangeGeneration);
                return;
            }
        }
    }

    private bool ShouldBumpFilteredGeneration(DeltaBatch batch)
    {
        if (batch.Ddl.Count > 0)
        {
            return true;
        }

        if (batch.Mutations.Count == 0)
        {
            return false;
        }

        if (Volatile.Read(ref _excludedChangeTables) is not { } excluded)
        {
            return true;
        }

        for (var index = 0; index < batch.Mutations.Count; index++)
        {
            if (!excluded.Contains(batch.Mutations[index].Table))
            {
                return true;
            }
        }

        return false;
    }

    private async ValueTask<SqliteConnection?> RentReaderAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            SqliteConnection? rented = null;
            SqliteOpenOptions? openOptions = null;
            lock (_readersGate)
            {
                if (IsClosed)
                {
                    return null;
                }

                if (_readers.Reader.TryRead(out var slot))
                {
                    if (slot is not null)
                    {
                        _ = _rentedReaders.Add(slot);
                        rented = slot;
                    }
                    else if (_readerOptions is { } configured)
                    {
                        _openingReaders++;
                        openOptions = configured;
                    }
                    else
                    {
                        _ = _readers.Writer.TryWrite(null);
                        return null;
                    }
                }
            }

            if (rented is not null)
            {
                return rented;
            }

            if (openOptions is not null)
            {
                var opened = SqliteConnection.Open(openOptions);
                var connection = opened switch
                {
                    SqliteConnection created => created,
                    SqliteStatus => null,
                };
                if (connection is not null)
                {
                    lock (_readersGate)
                    {
                        if (!IsClosed)
                        {
                            _openingReaders--;
                            _ = _rentedReaders.Add(connection);
                            return connection;
                        }
                    }

                    connection.Dispose();
                }

                lock (_readersGate)
                {
                    _openingReaders--;
                    if (IsClosed)
                    {
                        if (_rentedReaders.Count == 0 && _openingReaders == 0)
                        {
                            _readersDrained.TrySetResult();
                        }
                    }
                    else
                    {
                        _ = _readers.Writer.TryWrite(null);
                    }
                }

                return null;
            }

            if (!await _readers.Reader.WaitToReadAsync(cancellationToken))
            {
                return null;
            }
        }
    }

    internal bool RegisterReadTransaction(ReadTransaction transaction)
    {
        lock (_readersGate)
        {
            if (IsClosed)
            {
                return false;
            }

            _ = _readTransactions.Add(transaction);
            return true;
        }
    }

    internal void UnregisterReadTransaction(ReadTransaction transaction)
    {
        lock (_readersGate)
        {
            _ = _readTransactions.Remove(transaction);
        }
    }

    internal void ReleaseReader(SqliteConnection reader) => ReturnReader(reader);

    private void ReturnReader(SqliteConnection reader)
    {
        var dispose = IsClosed;
        if (!dispose && reader.InTransaction)
        {
            _ = reader.Exec("ROLLBACK;");
            if (reader.InTransaction)
            {
                dispose = true;
            }
        }

        if (!dispose)
        {
            lock (_readersGate)
            {
                if (!IsClosed && _readers.Writer.TryWrite(reader))
                {
                    _ = _rentedReaders.Remove(reader);
                    return;
                }
            }
        }

        if (reader.InTransaction)
        {
            _ = reader.Exec("ROLLBACK;");
        }

        reader.Dispose();
        lock (_readersGate)
        {
            _ = _rentedReaders.Remove(reader);
            if (IsClosed)
            {
                if (_rentedReaders.Count == 0 && _openingReaders == 0)
                {
                    _readersDrained.TrySetResult();
                }
            }
            else
            {
                _ = _readers.Writer.TryWrite(null);
            }
        }
    }

    private bool HasActiveReaders()
    {
        lock (_readersGate)
        {
            return _rentedReaders.Count > 0;
        }
    }

    private ValueTask<bool> EnqueueAsync(LaneRequest request, CancellationToken cancellationToken)
    {
        if (IsClosed || IsFaulted)
        {
            return ValueTask.FromResult(false);
        }

        if (_requests.Writer.TryWrite(request))
        {
            return ValueTask.FromResult(true);
        }

        return EnqueueSlowAsync(request, cancellationToken);
    }

    private async ValueTask<bool> EnqueueSlowAsync(LaneRequest request, CancellationToken cancellationToken)
    {
        while (await _requests.Writer.WaitToWriteAsync(cancellationToken))
        {
            if (_requests.Writer.TryWrite(request))
            {
                return true;
            }
        }

        return false;
    }

    private StorageBusy Closing() => IsFaulted ? LaneFaulted() : new("database session is closing", TimeSpan.FromMilliseconds(100));

    private static StorageBusy LaneFaulted() => new("the database writer lane faulted; the session reopens on the next request", TimeSpan.FromMilliseconds(100));
}
