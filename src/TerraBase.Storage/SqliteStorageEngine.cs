using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;

namespace TerraBase.Storage;

public sealed class SqliteStorageEngine : ISqlStorageEngine
{
    private const string SnapshotDirectory = "snapshots";

    private readonly StorageEngineOptions _options;
    private readonly TimeProvider _time;
    private readonly ConcurrentDictionary<Ulid, SemaphoreSlim> _gates = new();
    private readonly Lock _sessionsGate = new();
    private readonly Dictionary<Ulid, DatabaseSession> _sessions = [];

    public SqliteStorageEngine(StorageEngineOptions options, TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(time);
        options.Validate();
        _options = options;
        _time = time;
        Clock = new HlcClock(options.NodeId, time);
        Snowflake = new SnowflakeGenerator(options.SnowflakeWorkerId, time);
    }

    public static string NativeVersion => SqliteLibrary.Version;

    public HlcClock Clock { get; }

    public SnowflakeGenerator Snowflake { get; }

    public static ValueTask<SqlReadOutcome> ExecuteServiceReadAsync(
        ISqlDatabaseSession session, SqlStatementRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        return session is DatabaseSession sqlite
            ? sqlite.ExecuteReadAsync(request, limitResults: false, cancellationToken)
            : session.ExecuteReadAsync(request, cancellationToken);
    }

    public static void SealCommittedLogs(ISqlDatabaseSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (session is DatabaseSession sqlite)
        {
            sqlite.SealCommittedLogs();
        }
    }

    public static StorageBusy? CopyHistory(ISqlDatabaseSession source, string targetDirectory, Ulid targetDatabaseId, ReadOnlyMemory<byte> targetRawKey, CausalBookmark through)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(through);
        return source is DatabaseSession sqlite
            ? sqlite.CopyHistoryTo(sqlite.TargetContext(targetDirectory, targetDatabaseId, targetRawKey), through)
            : null;
    }

    public async ValueTask<OpenDatabaseOutcome> OpenAsync(DatabaseOpenRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.RawKey.Length != DatabaseOpenRequest.RawKeyLength)
        {
            return InvalidKey();
        }

        var gate = Gate(request.DatabaseId);
        await gate.WaitAsync(cancellationToken);
        try
        {
            if (Tracked(request.DatabaseId) is { } existing)
            {
                if (!CryptographicOperations.FixedTimeEquals(existing.RawKey, request.RawKey.Span))
                {
                    return new InvalidDatabaseRequest("raw key does not match the open database", "invalid_key");
                }

                existing.AddReference();
                return Session(existing);
            }

            if (!File.Exists(DatabasePath(request.DatabaseId)))
            {
                return new DatabaseNotFound(request.DatabaseId);
            }

            var opened = DatabaseSession.Open(Context(request.DatabaseId, request.RawKey), create: false, strict: false, ReleaseAsync);
            return opened switch
            {
                DatabaseSession session => Session(Track(session)),
                StorageCorrupted corrupted => corrupted,
                StorageBusy busy => busy,
            };
        }
        finally
        {
            gate.Release();
        }

        static OpenDatabaseOutcome Session(ISqlDatabaseSession session) => session;
    }

    public async ValueTask<CreateDatabaseOutcome> CreateAsync(DatabaseCreateRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.RawKey.Length != DatabaseOpenRequest.RawKeyLength)
        {
            return InvalidKey();
        }

        var gate = Gate(request.DatabaseId);
        await gate.WaitAsync(cancellationToken);
        try
        {
            if (Tracked(request.DatabaseId) is not null || File.Exists(DatabasePath(request.DatabaseId)))
            {
                return new DatabaseAlreadyExists(request.DatabaseId);
            }

            if (FileIo.CreateDirectory(DatabaseDirectory(request.DatabaseId)) is { } error)
            {
                return new StorageBusy(error, TimeSpan.FromSeconds(1));
            }

            var opened = DatabaseSession.Open(Context(request.DatabaseId, request.RawKey), create: true, request.Strict, ReleaseAsync);
            return opened switch
            {
                DatabaseSession session => Session(Track(session)),
                StorageCorrupted corrupted => new StorageBusy(corrupted.Message, TimeSpan.FromSeconds(1)),
                StorageBusy busy => busy,
            };
        }
        finally
        {
            gate.Release();
        }

        static CreateDatabaseOutcome Session(ISqlDatabaseSession session) => session;
    }

    public async ValueTask<BranchDatabaseOutcome> BranchAsync(DatabaseBranchRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.BranchRawKey.Length != DatabaseOpenRequest.RawKeyLength)
        {
            return InvalidKey();
        }

        if (Referenced(request.SourceDatabaseId) is not { } source)
        {
            return new DatabaseNotFound(request.SourceDatabaseId, "source database must be open to branch");
        }

        var gate = Gate(request.BranchDatabaseId);
        await using var sourceLease = source;
        await gate.WaitAsync(cancellationToken);
        DatabaseSession? branch = null;
        List<CommitMetadata> replay = [];
        try
        {
            if (Tracked(request.BranchDatabaseId) is not null || File.Exists(DatabasePath(request.BranchDatabaseId)))
            {
                return new DatabaseAlreadyExists(request.BranchDatabaseId, "branch database already exists");
            }

            var current = source.CurrentBookmark;
            var target = request.At.IsEmpty ? current : request.At;
            if (!current.Dominates(target))
            {
                return new InvalidDatabaseRequest("branch bookmark is ahead of the source database");
            }

            if (FileIo.CreateDirectory(DatabaseDirectory(request.BranchDatabaseId)) is { } error)
            {
                return new StorageBusy(error, TimeSpan.FromSeconds(1));
            }

            var branchContext = Context(request.BranchDatabaseId, request.BranchRawKey);
            if (SqliteConnection.OpenOrNull(ConnectionOptions(branchContext)) is not { } destination)
            {
                _ = FileIo.DeleteDirectory(branchContext.Directory);
                return new StorageBusy("branch database could not be created", TimeSpan.FromSeconds(1));
            }

            var copied = target.Dominates(current)
                ? await CopyLiveAsync(source, destination, cancellationToken)
                : CopySnapshot(source, destination, target);
            destination.Dispose();
            var createEmpty = false;
            CausalBookmark baseline;
            if (copied is { } snapshotBaseline)
            {
                baseline = snapshotBaseline;
            }
            else if (source.HasFullHistory(target))
            {
                _ = FileIo.DeleteDirectory(branchContext.Directory);
                if (FileIo.CreateDirectory(branchContext.Directory) is { } recreateError)
                {
                    return new StorageBusy(recreateError, TimeSpan.FromSeconds(1));
                }

                baseline = CausalBookmark.Empty;
                createEmpty = true;
            }
            else
            {
                _ = FileIo.DeleteDirectory(branchContext.Directory);
                return new InvalidDatabaseRequest("no snapshot precedes the requested bookmark");
            }

            if (!createEmpty && source.CopyHistoryTo(branchContext, baseline) is { } history)
            {
                _ = FileIo.DeleteDirectory(branchContext.Directory);
                return history;
            }

            replay = CollectReplay(source, baseline, target);
            var opened = DatabaseSession.Open(branchContext, create: createEmpty, strict: createEmpty && source.Strict, ReleaseAsync);
            branch = opened switch
            {
                DatabaseSession session => Track(session),
                StorageCorrupted or StorageBusy => null,
            };
            if (branch is null)
            {
                _ = FileIo.DeleteDirectory(branchContext.Directory);
                return new StorageBusy("branch database could not be opened", TimeSpan.FromSeconds(1));
            }
        }
        finally
        {
            gate.Release();
        }

        if (replay.Count > 0)
        {
            var replayed = await branch.ReplayAsync(replay, cancellationToken);
            var failure = replayed switch
            {
                RemoteApplyOutcome => null,
                SchemaMismatch mismatch => new StorageBusy(string.Concat("branch replay schema mismatch at epoch ", mismatch.ActualEpoch.ToString(CultureInfo.InvariantCulture)), TimeSpan.Zero),
                StorageCorrupted corrupted => new StorageBusy(corrupted.Message, TimeSpan.Zero),
                StorageBusy busy => busy,
            };
            if (failure is not null)
            {
                await branch.DisposeAsync();
                return failure;
            }
        }

        return Branch(branch);

        static BranchDatabaseOutcome Branch(ISqlDatabaseSession session) => session;
    }

    public async ValueTask<SnapshotDatabaseOutcome> SnapshotAsync(Ulid databaseId, CancellationToken cancellationToken)
    {
        if (Referenced(databaseId) is not { } session)
        {
            return new DatabaseNotFound(databaseId, "database must be open to snapshot");
        }

        try
        {
            var directory = Path.Combine(DatabaseDirectory(databaseId), SnapshotDirectory);
            if (FileIo.CreateDirectory(directory) is { } error)
            {
                return new StorageBusy(error, TimeSpan.FromSeconds(1));
            }

            var taken = _time.GetUtcNow();
            var path = Path.Combine(directory, string.Concat(Ulid.NewUlid(taken).ToString(), ".db"));
            var context = Context(databaseId, session.RawKey) with { DatabasePath = path };
            if (SqliteConnection.OpenOrNull(ConnectionOptions(context)) is not { } destination)
            {
                return new StorageBusy("snapshot file could not be created", TimeSpan.FromSeconds(1));
            }

            var outcome = await session.BackupToAsync(destination, cancellationToken);
            destination.Dispose();
            return outcome switch
            {
                SnapshotCapture capture => new DatabaseSnapshot(databaseId, capture.Bookmark, capture.SchemaEpoch, new FileInfo(path).Length, taken, path),
                StorageBusy busy => busy,
            };
        }
        finally
        {
            await session.DisposeAsync();
        }
    }

    public async ValueTask<DeleteDatabaseOutcome> DeleteAsync(Ulid databaseId, CancellationToken cancellationToken)
    {
        var gate = Gate(databaseId);
        await gate.WaitAsync(cancellationToken);
        try
        {
            await CloseLockedAsync(databaseId);
            var directory = DatabaseDirectory(databaseId);
            if (!Directory.Exists(directory))
            {
                return new DatabaseNotFound(databaseId);
            }

            return FileIo.DeleteDirectory(directory) is { } error ? new StorageBusy(error, TimeSpan.FromSeconds(1)) : new DatabaseDeleted();
        }
        finally
        {
            gate.Release();
        }
    }

    public async ValueTask<CryptoShredOutcome> CryptoShredAsync(Ulid databaseId, CancellationToken cancellationToken)
    {
        var gate = Gate(databaseId);
        await gate.WaitAsync(cancellationToken);
        try
        {
            await CloseLockedAsync(databaseId);
            var directory = DatabaseDirectory(databaseId);
            if (!Directory.Exists(directory))
            {
                return new DatabaseNotFound(databaseId);
            }

            foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories).ToArray())
            {
                if (FileIo.Shred(file) is { } error)
                {
                    return new StorageBusy(error, TimeSpan.FromSeconds(1));
                }
            }

            return FileIo.DeleteDirectory(directory) is { } failure ? new StorageBusy(failure, TimeSpan.FromSeconds(1)) : new DatabaseShredded();
        }
        finally
        {
            gate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        DatabaseSession[] sessions;
        lock (_sessionsGate)
        {
            sessions = [.. _sessions.Values];
            _sessions.Clear();
        }

        await Task.WhenAll(sessions.Select(CloseSessionAsync));
    }

    private async Task CloseSessionAsync(DatabaseSession session)
    {
        var gate = Gate(session.DatabaseId);
        await gate.WaitAsync(CancellationToken.None);
        try
        {
            await session.CloseAsync();
        }
        finally
        {
            gate.Release();
        }
    }

    private static InvalidDatabaseRequest InvalidKey() =>
        new("raw encryption key must be exactly 32 bytes", "invalid_key");

    private string DatabaseDirectory(Ulid databaseId) => Path.Combine(_options.RootDirectory, databaseId.ToString());

    private string DatabasePath(Ulid databaseId) => Path.Combine(DatabaseDirectory(databaseId), string.Concat(databaseId.ToString(), ".db"));

    private SessionContext Context(Ulid databaseId, ReadOnlyMemory<byte> rawKey) =>
        new(databaseId, DatabaseDirectory(databaseId), DatabasePath(databaseId), rawKey.ToArray(), _options, _time, Clock, Snowflake);

    private static SqliteOpenOptions ConnectionOptions(SessionContext context) =>
        new(context.DatabasePath, false, context.RawKey, context.Options.ReaderCacheSizeKib, context.DatabaseId, context.Options.DeltaColumns);

    private SemaphoreSlim Gate(Ulid databaseId) => _gates.GetOrAdd(databaseId, static _ => new SemaphoreSlim(1, 1));

    private DatabaseSession? Tracked(Ulid databaseId)
    {
        lock (_sessionsGate)
        {
            return _sessions.GetValueOrDefault(databaseId);
        }
    }

    private DatabaseSession? Referenced(Ulid databaseId)
    {
        lock (_sessionsGate)
        {
            var session = _sessions.GetValueOrDefault(databaseId);
            session?.AddReference();
            return session;
        }
    }

    private DatabaseSession Track(DatabaseSession session)
    {
        lock (_sessionsGate)
        {
            _sessions[session.DatabaseId] = session;
        }

        return session;
    }

    private async Task ReleaseAsync(DatabaseSession session)
    {
        var gate = Gate(session.DatabaseId);
        await gate.WaitAsync(CancellationToken.None);
        try
        {
            if (session.References > 0)
            {
                return;
            }

            lock (_sessionsGate)
            {
                if (_sessions.TryGetValue(session.DatabaseId, out var tracked) && ReferenceEquals(tracked, session))
                {
                    _ = _sessions.Remove(session.DatabaseId);
                }
            }

            await session.CloseAsync();
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task CloseLockedAsync(Ulid databaseId)
    {
        DatabaseSession? session;
        lock (_sessionsGate)
        {
            _ = _sessions.Remove(databaseId, out session);
        }

        if (session is not null)
        {
            await session.CloseAsync();
        }
    }

    private static async Task<CausalBookmark?> CopyLiveAsync(DatabaseSession source, SqliteConnection destination, CancellationToken cancellationToken)
    {
        var outcome = await source.BackupToAsync(destination, cancellationToken);
        return outcome switch
        {
            SnapshotCapture capture => capture.Bookmark,
            StorageBusy => null,
        };
    }

    private CausalBookmark? CopySnapshot(DatabaseSession source, SqliteConnection destination, CausalBookmark target)
    {
        var directory = Path.Combine(source.Directory, SnapshotDirectory);
        if (!Directory.Exists(directory))
        {
            return null;
        }

        (string Path, CausalBookmark Bookmark)? best = null;
        foreach (var file in Directory.EnumerateFiles(directory, "*.db"))
        {
            var options = new SqliteOpenOptions(file, true, source.RawKey, _options.ReaderCacheSizeKib, source.DatabaseId, _options.DeltaColumns);
            if (SqliteConnection.OpenOrNull(options) is not { } snapshot)
            {
                continue;
            }

            List<SqlValue[]> rows = [];
            var status = snapshot.Query(MetaStore.SelectVersionsSql, rows);
            snapshot.Dispose();
            if (!status.IsOk)
            {
                continue;
            }

            var bookmark = CausalBookmark.Create(DatabaseSession.ReadVersions(rows).Watermarks);
            if (target.Dominates(bookmark) && (best is not { } current || bookmark.Dominates(current.Bookmark)))
            {
                best = (file, bookmark);
            }
        }

        if (best is not { } chosen)
        {
            return null;
        }

        var sourceOptions = new SqliteOpenOptions(chosen.Path, true, source.RawKey, _options.ReaderCacheSizeKib, source.DatabaseId, _options.DeltaColumns);
        if (SqliteConnection.OpenOrNull(sourceOptions) is not { } origin)
        {
            return null;
        }

        var copied = origin.BackupTo(destination);
        origin.Dispose();
        return copied.IsOk ? chosen.Bookmark : null;
    }

    private static List<CommitMetadata> CollectReplay(DatabaseSession source, CausalBookmark baseline, CausalBookmark target)
    {
        List<CommitMetadata> replay = [];
        foreach (var log in source.Logs())
        {
            var from = baseline.Get(log.Origin) + 1;
            var until = target.Get(log.Origin);
            if (until < from)
            {
                continue;
            }

            var read = log.Read(from, (int)Math.Min(until - from + 1, int.MaxValue));
            var entries = read switch
            {
                IReadOnlyList<CommitMetadata> page => page,
                CommitLogGap or StorageCorrupted => [],
            };
            replay.AddRange(entries.Where(entry => entry.Sequence <= until));
        }

        return replay;
    }
}
