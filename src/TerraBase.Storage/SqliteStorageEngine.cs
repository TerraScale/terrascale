using System.Globalization;
using System.Security.Cryptography;

namespace TerraBase.Storage;

public sealed class SqliteStorageEngine : ISqlStorageEngine
{
    private const string SnapshotDirectory = "snapshots";

    private readonly StorageEngineOptions _options;
    private readonly TimeProvider _time;
    private readonly SemaphoreSlim _lifecycle = new(1, 1);
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

    public async ValueTask<Result<ISqlDatabaseSession, NotFound, Validation, StorageCorrupted, StorageBusy>> OpenAsync(DatabaseOpenRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.RawKey.Length != DatabaseOpenRequest.RawKeyLength)
        {
            return InvalidKey();
        }

        await _lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_sessions.TryGetValue(request.DatabaseId, out var existing))
            {
                if (!CryptographicOperations.FixedTimeEquals(existing.RawKey, request.RawKey.Span))
                {
                    return new Validation("raw key does not match the open database", "invalid_key");
                }

                existing.AddReference();
                return Session(existing);
            }

            if (!File.Exists(DatabasePath(request.DatabaseId)))
            {
                return new NotFound("database not found");
            }

            return DatabaseSession.Open(Context(request.DatabaseId, request.RawKey), create: false, strict: false, ReleaseAsync) switch
            {
                DatabaseSession session => Session(Track(session)),
                StorageCorrupted corrupted => corrupted,
                StorageBusy busy => busy,
            };
        }
        finally
        {
            _lifecycle.Release();
        }

        static Result<ISqlDatabaseSession, NotFound, Validation, StorageCorrupted, StorageBusy> Session(ISqlDatabaseSession session) => session;
    }

    public async ValueTask<Result<ISqlDatabaseSession, Conflict, Validation, StorageBusy>> CreateAsync(DatabaseCreateRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.RawKey.Length != DatabaseOpenRequest.RawKeyLength)
        {
            return InvalidKey();
        }

        await _lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_sessions.ContainsKey(request.DatabaseId) || File.Exists(DatabasePath(request.DatabaseId)))
            {
                return new Conflict("database already exists");
            }

            if (FileIo.CreateDirectory(DatabaseDirectory(request.DatabaseId)) is { } error)
            {
                return new StorageBusy(error, TimeSpan.FromSeconds(1));
            }

            return DatabaseSession.Open(Context(request.DatabaseId, request.RawKey), create: true, request.Strict, ReleaseAsync) switch
            {
                DatabaseSession session => Session(Track(session)),
                StorageCorrupted corrupted => new StorageBusy(corrupted.Message, TimeSpan.FromSeconds(1)),
                StorageBusy busy => busy,
            };
        }
        finally
        {
            _lifecycle.Release();
        }

        static Result<ISqlDatabaseSession, Conflict, Validation, StorageBusy> Session(ISqlDatabaseSession session) => session;
    }

    public async ValueTask<Result<ISqlDatabaseSession, NotFound, Conflict, Validation, StorageBusy>> BranchAsync(DatabaseBranchRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.BranchRawKey.Length != DatabaseOpenRequest.RawKeyLength)
        {
            return InvalidKey();
        }

        await _lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
        DatabaseSession? branch = null;
        List<CommitMetadata> replay = [];
        try
        {
            if (!_sessions.TryGetValue(request.SourceDatabaseId, out var source))
            {
                return new NotFound("source database must be open to branch");
            }

            if (_sessions.ContainsKey(request.BranchDatabaseId) || File.Exists(DatabasePath(request.BranchDatabaseId)))
            {
                return new Conflict("branch database already exists");
            }

            var current = source.CurrentBookmark;
            var target = request.At.IsEmpty ? current : request.At;
            if (!current.Dominates(target))
            {
                return new Validation("branch bookmark is ahead of the source database");
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
                ? await CopyLiveAsync(source, destination, cancellationToken).ConfigureAwait(false)
                : CopySnapshot(source, destination, target);
            destination.Dispose();
            if (copied is not { } baseline)
            {
                _ = FileIo.DeleteDirectory(branchContext.Directory);
                return new Validation("no snapshot precedes the requested bookmark");
            }

            replay = CollectReplay(source, baseline, target);
            branch = DatabaseSession.Open(branchContext, create: false, strict: false, ReleaseAsync) switch
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
            _lifecycle.Release();
        }

        if (replay.Count > 0)
        {
            var replayed = await branch.ReplayAsync(replay, cancellationToken).ConfigureAwait(false);
            var failure = replayed switch
            {
                RemoteApplyOutcome => null,
                SchemaMismatch mismatch => new StorageBusy(string.Concat("branch replay schema mismatch at epoch ", mismatch.ActualEpoch.ToString(CultureInfo.InvariantCulture)), TimeSpan.Zero),
                StorageCorrupted corrupted => new StorageBusy(corrupted.Message, TimeSpan.Zero),
                StorageBusy busy => busy,
            };
            if (failure is not null)
            {
                await branch.DisposeAsync().ConfigureAwait(false);
                return failure;
            }
        }

        return Branch(branch);

        static Result<ISqlDatabaseSession, NotFound, Conflict, Validation, StorageBusy> Branch(ISqlDatabaseSession session) => session;
    }

    public async ValueTask<Result<DatabaseSnapshot, NotFound, StorageBusy>> SnapshotAsync(Ulid databaseId, CancellationToken cancellationToken)
    {
        DatabaseSession? session;
        await _lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            session = _sessions.GetValueOrDefault(databaseId);
            session?.AddReference();
        }
        finally
        {
            _lifecycle.Release();
        }

        if (session is null)
        {
            return new NotFound("database must be open to snapshot");
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

            var outcome = await session.BackupToAsync(destination, cancellationToken).ConfigureAwait(false);
            destination.Dispose();
            return outcome switch
            {
                SnapshotCapture capture => new DatabaseSnapshot(databaseId, capture.Bookmark, capture.SchemaEpoch, new FileInfo(path).Length, taken, path),
                StorageBusy busy => busy,
            };
        }
        finally
        {
            await session.DisposeAsync().ConfigureAwait(false);
        }
    }

    public async ValueTask<Result<Done, NotFound, StorageBusy>> DeleteAsync(Ulid databaseId, CancellationToken cancellationToken)
    {
        await _lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await CloseLockedAsync(databaseId).ConfigureAwait(false);
            var directory = DatabaseDirectory(databaseId);
            if (!Directory.Exists(directory))
            {
                return new NotFound("database not found");
            }

            return FileIo.DeleteDirectory(directory) is { } error ? new StorageBusy(error, TimeSpan.FromSeconds(1)) : new Done();
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    public async ValueTask<Result<Done, NotFound, StorageBusy>> CryptoShredAsync(Ulid databaseId, CancellationToken cancellationToken)
    {
        await _lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await CloseLockedAsync(databaseId).ConfigureAwait(false);
            var directory = DatabaseDirectory(databaseId);
            if (!Directory.Exists(directory))
            {
                return new NotFound("database not found");
            }

            foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories).ToArray())
            {
                if (FileIo.Shred(file) is { } error)
                {
                    return new StorageBusy(error, TimeSpan.FromSeconds(1));
                }
            }

            return FileIo.DeleteDirectory(directory) is { } failure ? new StorageBusy(failure, TimeSpan.FromSeconds(1)) : new Done();
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _lifecycle.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            foreach (var session in _sessions.Values.ToArray())
            {
                await session.CloseAsync().ConfigureAwait(false);
            }

            _sessions.Clear();
        }
        finally
        {
            _lifecycle.Release();
        }

        _lifecycle.Dispose();
    }

    private static Validation InvalidKey() =>
        new("raw encryption key must be exactly 32 bytes", "invalid_key", [new ValidationDetail("rawKey", "length")]);

    private string DatabaseDirectory(Ulid databaseId) => Path.Combine(_options.RootDirectory, databaseId.ToString());

    private string DatabasePath(Ulid databaseId) => Path.Combine(DatabaseDirectory(databaseId), string.Concat(databaseId.ToString(), ".db"));

    private SessionContext Context(Ulid databaseId, ReadOnlyMemory<byte> rawKey) =>
        new(databaseId, DatabaseDirectory(databaseId), DatabasePath(databaseId), rawKey.ToArray(), _options, _time, Clock, Snowflake);

    private static SqliteOpenOptions ConnectionOptions(SessionContext context) =>
        new(context.DatabasePath, false, context.RawKey, context.Options.ReaderCacheSizeKib, context.DatabaseId, context.Options.DeltaColumns);

    private DatabaseSession Track(DatabaseSession session)
    {
        _sessions[session.DatabaseId] = session;
        return session;
    }

    private async Task ReleaseAsync(DatabaseSession session)
    {
        await _lifecycle.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            if (session.References > 0)
            {
                return;
            }

            if (_sessions.TryGetValue(session.DatabaseId, out var tracked) && ReferenceEquals(tracked, session))
            {
                _ = _sessions.Remove(session.DatabaseId);
            }

            await session.CloseAsync().ConfigureAwait(false);
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    private async Task CloseLockedAsync(Ulid databaseId)
    {
        if (_sessions.Remove(databaseId, out var session))
        {
            await session.CloseAsync().ConfigureAwait(false);
        }
    }

    private static async Task<CausalBookmark?> CopyLiveAsync(DatabaseSession source, SqliteConnection destination, CancellationToken cancellationToken) =>
        await source.BackupToAsync(destination, cancellationToken).ConfigureAwait(false) switch
        {
            SnapshotCapture capture => capture.Bookmark,
            StorageBusy => null,
        };

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

            var entries = log.Read(from, (int)Math.Min(until - from + 1, int.MaxValue)) switch
            {
                IReadOnlyList<CommitMetadata> page => page,
                CommitLogGap or StorageCorrupted => [],
            };
            replay.AddRange(entries.Where(entry => entry.Sequence <= until));
        }

        return replay;
    }
}
