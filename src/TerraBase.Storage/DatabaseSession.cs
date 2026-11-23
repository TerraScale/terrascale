using System.Globalization;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using TerraScale.Contracts.Continuity;

namespace TerraBase.Storage;

internal readonly union SessionOpenResult(DatabaseSession, StorageCorrupted, StorageBusy);

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

    private readonly SessionContext _context;
    private readonly SqliteConnection _writer;
    private readonly PreupdateCapture _capture;
    private readonly CommitLogCipher _cipher;
    private readonly CommitLogOptions _logOptions;
    private readonly Channel<SqliteConnection> _readers;
    private readonly Channel<LaneRequest> _requests;
    private readonly Lock _logsGate = new();
    private readonly Dictionary<string, OriginLog> _logs = new(StringComparer.Ordinal);
    private readonly Lock _stagedGate = new();
    private readonly Dictionary<Ulid, StagedDescriptor> _staged = [];
    private readonly Dictionary<string, SortedDictionary<long, CommitMetadata>> _pending = new(StringComparer.Ordinal);
    private readonly VersionVector _versions = new();
    private readonly Func<DatabaseSession, Task> _release;
    private readonly bool _strict;
    private Task _lane = Task.CompletedTask;
    private WriteTransaction? _activeTransaction;
    private long _schemaEpoch;
    private int _references = 1;
    private int _closed;
    private int _readerCount;

    private DatabaseSession(SessionContext context, SqliteConnection writer, PreupdateCapture capture, CommitLogCipher cipher, bool strict, Func<DatabaseSession, Task> release)
    {
        _context = context;
        _writer = writer;
        _capture = capture;
        _cipher = cipher;
        _strict = strict;
        _release = release;
        _logOptions = new CommitLogOptions(context.Options.SegmentMaxBytes, context.Options.SegmentMaxAge, context.Options.FsyncCommitLog);
        _readers = Channel.CreateBounded<SqliteConnection>(new BoundedChannelOptions(context.Options.ReaderCount) { FullMode = BoundedChannelFullMode.Wait });
        _requests = Channel.CreateBounded<LaneRequest>(new BoundedChannelOptions(context.Options.QueueCapacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
        });
    }

    public Ulid DatabaseId => _context.DatabaseId;

    public ulong SchemaEpoch => (ulong)Interlocked.Read(ref _schemaEpoch);

    public CausalBookmark CurrentBookmark => _versions.Snapshot();

    public bool IsClosed => Volatile.Read(ref _closed) != 0;

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
        };
        var opened = SqliteConnection.Open(options);
        SessionOpenResult? failed = opened switch
        {
            SqliteConnection => null,
            SqliteStatus status => status.IsBusy ? new StorageBusy(status.Message, TimeSpan.FromMilliseconds(50)) : new StorageCorrupted(context.DatabaseId, status.Message),
        };
        if (failed is { } failure || SqliteConnection.Opened(opened) is not { } writer)
        {
            return failed ?? new StorageCorrupted(context.DatabaseId, "writer connection unavailable");
        }

        var setup = writer.Exec(MetaStore.SchemaSql);
        if (setup.IsOk && create)
        {
            setup = writer.Exec(string.Concat("PRAGMA application_id = ", (ApplicationIdBase | (strict ? 1 : 0)).ToString(CultureInfo.InvariantCulture), ";"));
        }

        var applicationId = 0L;
        if (setup.IsOk)
        {
            setup = writer.QueryFirst("PRAGMA application_id", out var row);
            _ = row is not null && row[0].TryGetIntegral(out applicationId);
        }

        if (setup.IsOk)
        {
            setup = writer.RegisterSnowflake(context.Snowflake);
        }

        if (!setup.IsOk)
        {
            writer.Dispose();
            return new StorageCorrupted(context.DatabaseId, setup.Message);
        }

        var capture = writer.AttachCapture();
        var cipher = new CommitLogCipher(context.RawKey, context.DatabaseId);
        var session = new DatabaseSession(context, writer, capture, cipher, (applicationId & 1) == 1, release);
        if (session.Recover() is { } corrupted)
        {
            session.DisposeResources();
            return corrupted;
        }

        var readerOptions = options with { ReadOnly = true, CacheSizeKib = context.Options.ReaderCacheSizeKib };
        for (var index = 0; index < context.Options.ReaderCount; index++)
        {
            var reader = SqliteConnection.Open(readerOptions) switch
            {
                SqliteConnection connection => connection,
                SqliteStatus => null,
            };
            if (reader is null)
            {
                session.DisposeResources();
                return new StorageCorrupted(context.DatabaseId, "reader connection could not be opened");
            }

            _ = session._readers.Writer.TryWrite(reader);
            session._readerCount++;
        }

        session._lane = Task.Run(session.RunLaneAsync);
        return session;
    }

    public void AddReference() => Interlocked.Increment(ref _references);

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Decrement(ref _references) == 0)
        {
            await _release(this).ConfigureAwait(false);
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
        await _lane.ConfigureAwait(false);
        for (var index = 0; index < _readerCount; index++)
        {
            var reader = await _readers.Reader.ReadAsync(CancellationToken.None).ConfigureAwait(false);
            reader.Dispose();
        }

        _readerCount = 0;
        _versions.Close();
        DisposeResources();
    }

    public async ValueTask<Result<Done, RequestTimeout>> WaitForBookmarkAsync(CausalBookmark bookmark, TimeSpan timeout, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(bookmark);
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
                return new RequestTimeout(string.Concat("bookmark ", bookmark.Encode(), " was not reached in time"));
            }

            using var delayCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var delay = Task.Delay(remaining, _context.Time, delayCancellation.Token);
            _ = await Task.WhenAny(changed, delay).ConfigureAwait(false);
            await delayCancellation.CancelAsync().ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
        }

        return new Done();
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
                        DateTimeOffset.FromUnixTimeMilliseconds(entry.Hlc.WallMs));
                }
            }

            if (!progressed)
            {
                await signal.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
        }
    }

    public ValueTask<Result<CommitLogPage, CommitLogGap, StorageBusy>> ReadCommitLogAsync(string originRegion, long fromSequence, int maxEntries, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxEntries);
        cancellationToken.ThrowIfCancellationRequested();
        if (Log(originRegion) is not { } log)
        {
            return ValueTask.FromResult<Result<CommitLogPage, CommitLogGap, StorageBusy>>(new CommitLogPage(DatabaseId, originRegion, [], fromSequence));
        }

        var visible = _versions.Get(originRegion);
        Result<CommitLogPage, CommitLogGap, StorageBusy> result = log.Read(Math.Max(fromSequence, 1), maxEntries) switch
        {
            IReadOnlyList<CommitMetadata> page => Page(originRegion, fromSequence, [.. page.Where(entry => entry.Sequence <= visible)]),
            CommitLogGap gap => gap,
            StorageCorrupted corrupted => new StorageBusy(corrupted.Message, TimeSpan.FromSeconds(1)),
        };
        return ValueTask.FromResult(result);
    }

    public ValueTask<Result<IReadOnlyList<CommitLogSegment>, StorageBusy>> ListSealedSegmentsAsync(string originRegion, long afterSequence, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        IReadOnlyList<CommitLogSegment> segments = Log(originRegion) is { } log ? log.ListSealed(afterSequence) : [];
        return ValueTask.FromResult<Result<IReadOnlyList<CommitLogSegment>, StorageBusy>>(segments);
    }

    public ValueTask<Result<Stream, NotFound, StorageBusy>> OpenSegmentAsync(CommitLogSegment segment, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(segment);
        cancellationToken.ThrowIfCancellationRequested();
        Result<Stream, NotFound, StorageBusy> result = Log(segment.OriginRegion)?.SegmentPath(segment) is not { } path
            ? new NotFound("commit log segment not found")
            : FileIo.OpenRead(path) is { } stream
                ? stream
                : new StorageBusy("commit log segment could not be opened", TimeSpan.FromMilliseconds(50));
        return ValueTask.FromResult(result);
    }

    public async ValueTask<Result<Done, Validation, StorageCorrupted, StorageBusy>> ImportSegmentAsync(CommitLogSegment segment, Stream content, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(segment);
        ArgumentNullException.ThrowIfNull(content);
        if (segment.DatabaseId != DatabaseId || !StorageEngineOptions.IsValidRegion(segment.OriginRegion) || string.Equals(segment.OriginRegion, Region, StringComparison.Ordinal))
        {
            return new Validation("segment does not belong to a remote origin of this database");
        }

        using var buffer = new MemoryStream();
        await content.CopyToAsync(buffer, cancellationToken).ConfigureAwait(false);
        List<CommitMetadata> commits = [];
        if (!CommitFrame.TryParseAll(buffer.GetBuffer().AsSpan(0, (int)buffer.Length), segment.OriginRegion, _cipher, commits))
        {
            return new StorageCorrupted(DatabaseId, "segment failed CRC32C or authentication");
        }

        for (var index = 0; index < commits.Count; index++)
        {
            if (commits[index].Sequence != segment.FirstSequence + index)
            {
                return new Validation("segment sequences are not contiguous");
            }
        }

        if (commits.Count == 0 || commits[^1].Sequence != segment.LastSequence)
        {
            return new Validation("segment range does not match its content");
        }

        var applied = await ApplyRemoteBatchAsync(new RemoteCommitBatch(DatabaseId, commits), cancellationToken).ConfigureAwait(false);
        return applied switch
        {
            RemoteApplyOutcome => new Done(),
            SchemaMismatch mismatch => new Validation(string.Concat("segment schema epoch mismatch: expected ", mismatch.ExpectedEpoch.ToString(CultureInfo.InvariantCulture))),
            StorageCorrupted corrupted => corrupted,
            StorageBusy busy => busy,
        };
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

    private static StorageBusy? CopyOriginHistory(OriginLog source, OriginLog destination, long last)
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

                var appended = destination.Append(page, out _);
                if (!appended.IsOk)
                {
                    return appended.ToBusy();
                }

                next = page[^1].Sequence + 1;
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

    private CausalBookmark BookmarkWith(string origin, long sequence)
    {
        var watermarks = _versions.Copy();
        watermarks[origin] = sequence;
        return CausalBookmark.Create(watermarks);
    }

    private void DisposeResources()
    {
        while (_readers.Reader.TryRead(out var reader))
        {
            reader.Dispose();
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

    private async ValueTask<SqliteConnection?> RentReaderAsync(CancellationToken cancellationToken)
    {
        while (await _readers.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
        {
            if (_readers.Reader.TryRead(out var reader))
            {
                return reader;
            }
        }

        return null;
    }

    internal void ReleaseReader(SqliteConnection reader) => ReturnReader(reader);

    private void ReturnReader(SqliteConnection reader)
    {
        if (!_readers.Writer.TryWrite(reader))
        {
            reader.Dispose();
        }
    }

    private async ValueTask<bool> EnqueueAsync(LaneRequest request, CancellationToken cancellationToken)
    {
        if (IsClosed)
        {
            return false;
        }

        while (await _requests.Writer.WaitToWriteAsync(cancellationToken).ConfigureAwait(false))
        {
            if (_requests.Writer.TryWrite(request))
            {
                return true;
            }
        }

        return false;
    }

    private static StorageBusy Closing() => new("database session is closing", TimeSpan.FromMilliseconds(100));
}
