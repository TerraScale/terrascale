using System.Collections.Concurrent;
using System.Diagnostics;
using Google.Protobuf;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using TerraBase.Configuration;
using TerraBase.Engine.Revisions;
using TerraBase.Features.Backup;
using TerraBase.Features.Memory;
using TerraBase.Features.Shapes;
using TerraBase.Features.Management;
using TerraBase.Features.Sync;
using TerraBase.Primitives;
using TerraBase.Tenancy;
using Turso;
using Turso.Data.Sqlite;
using Turso.Raw.Public;
using Turso.Raw.Public.Handles;
using Turso.Raw.Public.Value;
using ZLogger;

using TerraBase.Features.Sql;

namespace TerraBase.Engine.Turso;

internal sealed partial class TursoReplicas(
    TerraBaseOptions options, [FromKeyedServices(EngineExtensions.DatabaseMasterKey)] IKeyring master, IMemoryBudget memory, ILogger<TursoReplicas> logger, IStorageEngine storage, TimeProvider? time = null)
    : IDatabaseReplicas, IAsyncDisposable
{
    private readonly TimeProvider _time = time ?? TimeProvider.System;

    internal const string DirectoryName = ".replicas";
    internal const string KeyHeader = "TERRABASE-REPLICA-KEY-2\n";
    internal const string PreviousKeyHeader = "TERRABASE-REPLICA-KEY-1\n";
    private static readonly Lock KeyRecords = new();
    private static readonly HashSet<string> ActiveDetached = new(StringComparer.Ordinal);
    private const int MaxIdleConnections = 4;
    private const int MaxReadRows = 10_000;

    private readonly ConcurrentDictionary<(string Organization, string Project, string Database), Lazy<Task<Replica>>> _replicas = new();
    private long _position;
    private int _disposed;

    private string Root => Path.Combine(options.DataDirectory, DirectoryName);

    private IMemoryBudget Memory => memory;

    public async ValueTask OpenAsync(TenantContext database, Uri source, HttpClient http, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(http);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        var key = Key(database);
        var opening = _replicas.GetOrAdd(key, _ => new Lazy<Task<Replica>>(() => CreateAsync(key, source, http), LazyThreadSafetyMode.ExecutionAndPublication));
        try
        {
            _ = await opening.Value.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            _ = _replicas.TryRemove(new KeyValuePair<(string, string, string), Lazy<Task<Replica>>>(key, opening));
            throw;
        }
    }

    public async ValueTask<ReplicaPullResult?> PullAsync(TenantContext database, bool fromHome, CancellationToken cancellationToken)
    {
        if (await FindAsync(database).ConfigureAwait(false) is not { } replica)
        {
            return null;
        }

        return await replica.PullAsync(fromHome, cancellationToken).ConfigureAwait(false);
    }

    public bool Offer(TenantContext database, DatabaseRevision from, DatabaseRevision to, ReadOnlyMemory<byte> answer, long? cdcPosition = null)
    {
        if (!_replicas.TryGetValue(Key(database), out var opening) || !opening.IsValueCreated || !opening.Value.IsCompletedSuccessfully
            || memory.TryReserve(answer.Length, database) is not { } reservation)
        {
            return false;
        }

        opening.Value.Result.Offer(new PushedAnswer(from, to, answer, reservation, cdcPosition));
        return true;
    }

    public async ValueTask<bool> DeleteAsync(TenantContext database, CancellationToken cancellationToken)
    {
        var key = Key(database);
        if (_replicas.TryRemove(key, out var opening))
        {
            try
            {
                var replica = await opening.Value.ConfigureAwait(false);
                await replica.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                logger.ZLogWarning($"A replica that failed to open is deleted: {error.GetType().Name:@Error}");
            }
        }

        var system = await storage.OpenSystemDatabaseAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await system.BeginTransactionAsync(write: false, cancellationToken).ConfigureAwait(false);
        var erasure = await SystemCatalog.GetAsync(transaction, DatabaseErasure.Kind, key.Database,
            ManagementJsonContext.Default.DatabasePurgeRecord, cancellationToken).ConfigureAwait(false);
        var erased = erasure is { FormatVersion: 1 } && DatabasePurge.SameDatabase(erasure.Database, database);
        return await Task.Run(() =>
        {
            lock (KeyRecords)
            {
                var path = PathOf(key);
                var detached = path[..^3] + ".promote.db";
                if (!erased && Directory.Exists(Path.GetDirectoryName(path))
                    && Directory.GetFiles(Path.GetDirectoryName(path)!, Path.GetFileName(detached) + "*").Length != 0)
                {
                    return false;
                }
                if (erased)
                {
                    DeleteArchivedDetached(path);
                }
                return DeleteFiles(path, keepDetached: !erased);
            }
        }, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<string?> DetachAsync(TenantContext database, CancellationToken cancellationToken)
    {
        var key = Key(database);
        if (!_replicas.TryRemove(key, out var opening))
        {
            return null;
        }

        Replica replica;
        try
        {
            replica = await opening.Value.ConfigureAwait(false);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            return null;
        }

        var pulled = replica.Pulled;
        await replica.DisposeAsync().ConfigureAwait(false);
        var path = PathOf(key);
        if (!pulled)
        {
            return null;
        }

        return await Task.Run(() =>
        {
            lock (KeyRecords)
            {
                var detached = path[..^3] + ".promote.db";
                TryRecoverDetached(detached, rethrow: true);
                var wrapped = DataKeyRecord(path) ?? throw new InvalidDataException("the replica's data key record is missing");
                if (master.NeedsRewrap(wrapped))
                {
                    var plain = master.Unwrap(wrapped);
                    try
                    {
                        wrapped = master.Rewrap(plain, path + DatabaseKeys.Suffix);
                    }
                    finally
                    {
                        CryptographicOperations.ZeroMemory(plain);
                    }
                }
                try
                {
                    DatabaseKeys.RejectLink(path);
                    var sourceKey = master.Unwrap(wrapped);
                    try
                    {
                        var evidence = DetachedImport.Inspect(path, sourceKey, options.DataDirectory, ((TursoStorageEngine)storage).Disk);
                        new DetachedImport(1, key.Database, evidence.Incarnation, evidence.Position, wrapped, null).Write(detached + ".importing", create: true);
                    }
                    finally
                    {
                        CryptographicOperations.ZeroMemory(sourceKey);
                    }
                    using (var file = new FileStream(detached + DatabaseKeys.Suffix, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    {
                        file.Write(Encoding.ASCII.GetBytes((wrapped.StartsWith("v2.", StringComparison.Ordinal) ? DatabaseKeys.Header : DatabaseKeys.PreviousHeader) + wrapped + "\n"));
                        file.Flush(flushToDisk: true);
                    }
                    DirectorySync.Sync(Path.GetDirectoryName(path)!);
                    File.Move(path, detached);
                    foreach (var file in Directory.GetFiles(Path.GetDirectoryName(path)!, Path.GetFileName(path) + "-*"))
                    {
                        DatabaseKeys.RejectLink(file);
                        File.Move(file, detached + file[path.Length..]);
                    }
                    DirectorySync.Sync(Path.GetDirectoryName(path)!);
                    ActiveDetached.Add(detached);
                    return detached;
                }
                catch
                {
                    TryRecoverDetached(detached, rethrow: true);
                    throw;
                }
            }
        }, cancellationToken).ConfigureAwait(false);
    }

    internal static void DeleteDetached(string detached)
    {
        lock (KeyRecords)
        {
            if (!ActiveDetached.Contains(detached))
            {
                DeleteDetachedFiles(detached);
            }
        }
    }

    public void CompleteDetach(string detached)
    {
        lock (KeyRecords)
        {
            ActiveDetached.Remove(detached);
            TryRecoverDetached(detached, rethrow: true, finalizing: true);
        }
    }

    private static void ArchiveCompletedDetached(string detached)
    {
        var intentPath = detached + ".importing";
        if (!File.Exists(intentPath))
        {
            return;
        }
        var intent = DetachedImport.Read(intentPath, Path.GetFileName(detached)[..^".promote.db".Length]);
        if (!intent.Published && !intent.Restored)
        {
            return;
        }
        var directory = Path.GetDirectoryName(detached)!;
        var root = Path.Combine(directory, ".retained");
        DatabaseKeys.RejectLink(root);
        Directory.CreateDirectory(root);
        DirectorySync.Sync(directory);
        var archive = Path.Combine(root, EntityIds.New().ToString());
        Directory.CreateDirectory(archive);
        DirectorySync.Sync(root);
        foreach (var file in Directory.GetFiles(directory, Path.GetFileName(detached) + "*")
            .OrderBy(file => file == intentPath))
        {
            DatabaseKeys.RejectLink(file);
            File.Move(file, Path.Combine(archive, Path.GetFileName(file)), overwrite: false);
            DirectorySync.Sync(archive);
            DirectorySync.Sync(directory);
        }
    }

    private static void DeleteArchivedDetached(string replica)
    {
        var root = Path.Combine(Path.GetDirectoryName(replica)!, ".retained");
        DatabaseKeys.RejectLink(root);
        if (!Directory.Exists(root))
        {
            return;
        }
        foreach (var archive in Directory.GetDirectories(root))
        {
            DatabaseKeys.RejectLink(archive);
            DeleteDetachedFiles(Path.Combine(archive, Path.GetFileName(replica)[..^3] + ".promote.db"));
        }
    }

    private static void DeleteDetachedFiles(string detached)
    {
        var directory = Path.GetDirectoryName(detached)!;
        if (!Directory.Exists(directory))
        {
            return;
        }
        foreach (var file in Directory.GetFiles(directory, Path.GetFileName(detached) + "*").Where(file => file != detached + ".importing"))
        {
            DatabaseKeys.RejectLink(file);
            if (WrappedKeyFile.IsKey(Path.GetFileName(file)))
            {
                WrappedKeyFile.Retain(file);
            }
            else
            {
                File.Delete(file);
            }
        }
        DirectorySync.Sync(directory);
        File.Delete(detached + ".importing");
        DirectorySync.Sync(directory);
    }

    public bool Holds(TenantContext database) =>
        database.ProjectId is { } project && database.DatabaseId is { } id && _replicas.ContainsKey((database.OrganizationId, project, id));

    public IReadOnlyList<TenantContext> List() =>
        [.. _replicas.Keys.Select(Tenant)];

    private static TenantContext Tenant((string Organization, string Project, string Database) key) => new(key.Organization, PlanTier.Hobby, key.Project, key.Database);

    public DatabaseRevision? HeldRevision(TenantContext database) =>
        _replicas.TryGetValue(Key(database), out var opening) && opening.IsValueCreated && opening.Value.IsCompletedSuccessfully ? opening.Value.Result.Held : null;

    public ReplicaHolding Holding(TenantContext database) =>
        !_replicas.TryGetValue(Key(database), out var opening) ? new NoReplica()
        : !opening.IsValueCreated || !opening.Value.IsCompletedSuccessfully
            ? File.Exists(PathOf(Key(database)) + "-info") ? new PositionUnknown() : new NoReplica()
        : opening.Value.Result switch
        {
            { Held: { } held } => new HeldAt(held),
            { Pulled: false } => new NoReplica(),
            _ => new PositionUnknown(),
        };

    public async ValueTask<ReplicaComparison?> CompareAsync(TenantContext database, DatabaseRevision revision, IReadOnlyList<RangeChecksum> home, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(home);
        return await FindAsync(database).ConfigureAwait(false) is { Pulled: true } replica
            ? await replica.CompareAsync(revision, home, cancellationToken).ConfigureAwait(false)
            : null;
    }

    public async ValueTask<IReplicaReader?> BeginReadAsync(TenantContext database, CancellationToken cancellationToken) =>
        await FindAsync(database).ConfigureAwait(false) is { Pulled: true } replica
            ? await replica.BeginReadAsync(cancellationToken).ConfigureAwait(false)
            : null;

    public async ValueTask<IReplicaShapeReader?> BeginShapeReadAsync(TenantContext database, CancellationToken cancellationToken) =>
        await FindAsync(database).ConfigureAwait(false) is { Pulled: true } replica
            ? await replica.BeginShapeReadAsync(database, cancellationToken).ConfigureAwait(false)
            : null;

    public long Position => Volatile.Read(ref _position);

    public async ValueTask<IReplicaReader?> BeginReadAsync(TenantContext database, long position, CancellationToken cancellationToken) =>
        await FindAsync(database).ConfigureAwait(false) is { Pulled: true } replica && replica.Applied > position
            ? await replica.BeginReadAsync(cancellationToken).ConfigureAwait(false)
            : null;

    private long NextPosition() => Interlocked.Increment(ref _position);

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        foreach (var key in _replicas.Keys)
        {
            if (_replicas.TryRemove(key, out var opening) && opening.IsValueCreated)
            {
                try
                {
                    await (await opening.Value.ConfigureAwait(false)).DisposeAsync().ConfigureAwait(false);
                }
                catch (Exception error) when (error is not OperationCanceledException)
                {
                    logger.ZLogWarning($"A replica failed to close: {error.GetType().Name:@Error}");
                }
            }
        }
    }

    private async ValueTask<Replica?> FindAsync(TenantContext database)
    {
        if (!_replicas.TryGetValue(Key(database), out var opening) || !opening.IsValueCreated)
        {
            return null;
        }

        try
        {
            return await opening.Value.ConfigureAwait(false);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            return null;
        }
    }

    private static (string Organization, string Project, string Database) Key(TenantContext database)
    {
        ArgumentNullException.ThrowIfNull(database);
        return EntityIds.IsValid(database.OrganizationId) && EntityIds.IsValid(database.ProjectId) && EntityIds.IsValid(database.DatabaseId)
            ? (database.OrganizationId, database.ProjectId, database.DatabaseId)
            : throw new ArgumentException("a replica is addressed by its organization, project and database ULIDs", nameof(database));
    }

    private string PathOf((string Organization, string Project, string Database) key) =>
        Path.Combine(Root, key.Organization, key.Project, key.Database + ".db");

    private async Task<Replica> CreateAsync((string Organization, string Project, string Database) key, Uri source, HttpClient http)
    {
        var path = PathOf(key);
        if (storage is TursoStorageEngine engine)
        {
            engine.ThrowIfDetachedRefused(Path.Combine(options.DataDirectory, Path.GetRelativePath(Root, path)));
        }
        lock (KeyRecords)
        {
            var detached = path[..^3] + ".promote.db";
            TryRecoverDetached(detached, rethrow: true);
            ArchiveCompletedDetached(detached);
        }
        var hexKey = await Task.Run(() => DataKeyRecord(path)).ConfigureAwait(false) switch
        {
            { } wrapped => await UnwrapAsync(wrapped).ConfigureAwait(false),
            null => null,
        };
        var observer = new RevisionObserver(http, new Uri(source + "/pull-updates"));
        var observed = new HttpClient(observer) { Timeout = Timeout.InfiniteTimeSpan };
        if (hexKey is not null && File.Exists(path) && File.Exists(path + "-info"))
        {
            int? format = null;
            try
            {
                format = await Task.Run(() =>
                {
                    using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
                    {
                        DataSource = path,
                        Mode = SqliteOpenMode.ReadOnly,
                        Pooling = false,
                        EncryptionCipher = "aegis256",
                        EncryptionKey = hexKey,
                    }.ToString());
                    connection.Open();
                    return StorageMigrations.FileFormat(connection);
                }).ConfigureAwait(false);
            }
            catch (SqliteException error)
            {
                logger.ZLogWarning($"A replica format could not be probed; its replication driver will reopen it: {error.GetType().Name:@Error}");
            }

            if (format is { } held && !StorageFormat.IsSupported(held))
            {
                throw new InvalidOperationException(StorageFormat.Refusal("the replica cache", held));
            }

            try
            {
                return new Replica(this, Tenant(key), await TursoSyncDatabase.OpenAsync(Options(path, hexKey, source, observed)).ConfigureAwait(false), path, observer, observed, logger, _time, pulled: true);
            }
            catch (TursoException error)
            {
                logger.ZLogWarning($"A replica could not be reopened; its files are retained before it starts again: {error.GetType().Name:@Error}");
            }
        }

        await Task.Run(() => RetainReplicaFiles(path)).ConfigureAwait(false);
        hexKey = await NewKeyAsync(path).ConfigureAwait(false);
        var replica = new Replica(this, Tenant(key), await TursoSyncDatabase.CreateAsync(Options(path, hexKey, source, observed)).ConfigureAwait(false), path, observer, observed, logger, _time, pulled: false);
        try
        {
            _ = await replica.PullAsync(fromHome: true, CancellationToken.None).ConfigureAwait(false);
            return replica;
        }
        catch
        {
            await replica.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private static void RetainReplicaFiles(string path)
    {
        lock (KeyRecords)
        {
            var directory = Path.GetDirectoryName(path)!;
            if (!Directory.Exists(directory))
            {
                return;
            }
            DatabaseKeys.RejectLink(directory);
            var retained = path + ".retained." + EntityIds.New();
            foreach (var file in Directory.GetFiles(directory, Path.GetFileName(path) + "*")
                .Where(file => !file.StartsWith(path + ".retained.", StringComparison.Ordinal)))
            {
                DatabaseKeys.RejectLink(file);
                File.Move(file, retained + file[path.Length..], overwrite: false);
            }
            DirectorySync.Sync(directory);
        }
    }

    private static bool Readable(HttpResponseMessage response)
    {
        string? marker = null;
        if (response.Headers.TryGetValues(StorageFormat.Header, out var formats))
        {
            using var values = formats.GetEnumerator();
            if (!values.MoveNext())
            {
                return false;
            }
            marker = values.Current;
            if (values.MoveNext())
            {
                return false;
            }
        }
        return StorageFormat.FromMarker(marker) is { } format && StorageFormat.IsSupported(format);
    }

    private static TursoSyncDatabaseOptions Options(string path, string hexKey, Uri source, HttpClient http) => new(path, source)
    {
        HttpClient = http,
        ClientName = "terrabase-replica",
        LocalEncryption = new TursoLocalEncryptionOptions { Cipher = TursoRemoteEncryptionCipher.Aegis256, HexKey = hexKey },
    };

    private async Task<string?> UnwrapAsync(string wrapped)
    {
        var key = await master.UnwrapAsync(wrapped, CancellationToken.None).ConfigureAwait(false);
        try
        {
            return key.Length == 32 ? Convert.ToHexString(key) : throw new InvalidDataException("replica data key must contain 256 bits");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }

    private async Task<string> NewKeyAsync(string path)
    {
        var (key, wrapped) = await master.GenerateDataKeyAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            await Task.Run(() =>
            {
                lock (KeyRecords)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                    DatabaseKeys.RejectLink(path + DatabaseKeys.Suffix);
                    var temporary = path + DatabaseKeys.Suffix + ".tmp";
                    DatabaseKeys.RejectLink(temporary);
                    using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    {
                        file.Write(Encoding.ASCII.GetBytes((wrapped.StartsWith("v2.", StringComparison.Ordinal) ? KeyHeader : PreviousKeyHeader) + wrapped + "\n"));
                        file.Flush(flushToDisk: true);
                    }

                    File.Move(temporary, path + DatabaseKeys.Suffix, overwrite: false);
                }
            }).ConfigureAwait(false);
            return Convert.ToHexString(key);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }

    private static string? DataKeyRecord(string path)
    {
        lock (KeyRecords)
        {
            var file = path + DatabaseKeys.Suffix;
            DatabaseKeys.RejectLink(file);
            if (!File.Exists(file))
            {
                return null;
            }

            if (new FileInfo(file).Length > DatabaseKeys.MaxFileBytes)
            {
                throw new InvalidDataException("the replica wrapped key record is malformed");
            }

            var text = File.ReadAllText(file, Encoding.ASCII);
            var current = text.StartsWith(KeyHeader, StringComparison.Ordinal);
            var previous = text.StartsWith(PreviousKeyHeader, StringComparison.Ordinal);
            if (!(current || previous) || !text.EndsWith('\n') || text.Length <= KeyHeader.Length + 1)
            {
                throw new InvalidDataException("the replica wrapped key record is malformed");
            }

            var wrapped = text[KeyHeader.Length..^1];
            if (current && !wrapped.StartsWith("v2.", StringComparison.Ordinal) || previous && wrapped.Contains('.', StringComparison.Ordinal))
            {
                throw new InvalidDataException("the replica wrapped key version does not match its header");
            }
            return wrapped;
        }
    }

    private static bool DeleteFiles(string path, bool keepDetached)
    {
        lock (KeyRecords)
        {
            var directory = Path.GetDirectoryName(path)!;
            if (!Directory.Exists(directory))
            {
                return false;
            }

            DatabaseKeys.RejectLink(directory);
            var name = Path.GetFileName(path);
            var deleted = false;
            foreach (var file in Directory.EnumerateFiles(directory, name + "*"))
            {
                if (WrappedKeyFile.IsKey(Path.GetFileName(file)))
                {
                    WrappedKeyFile.Retain(file);
                }
                else
                {
                    File.Delete(file);
                }
                deleted = true;
            }

            if (!keepDetached)
            {
                DeleteDetached(Path.Combine(directory, Path.GetFileNameWithoutExtension(path) + ".promote.db"));
            }
            return deleted;
        }
    }

    private sealed class PushedAnswer(DatabaseRevision from, DatabaseRevision to, ReadOnlyMemory<byte> answer, IMemoryReservation reservation, long? cdcPosition) : IDisposable
    {
        private IMemoryReservation? _reservation = reservation;

        public DatabaseRevision To => to;

        public long? CdcPosition => cdcPosition;

        public bool Covers(DatabaseRevision held) =>
            held.Incarnation == from.Incarnation && to.Incarnation == from.Incarnation && DatabaseRevision.Compare(from, held) <= 0 && DatabaseRevision.Compare(held, to) < 0;

        public bool Stale(DatabaseRevision held) => held.Incarnation == to.Incarnation && DatabaseRevision.Compare(to, held) <= 0;

        public HttpResponseMessage Answer(HttpRequestMessage request)
        {
            var bytes = MemoryMarshal.TryGetArray(answer, out var segment) ? segment : new ArraySegment<byte>(answer.ToArray());
            var content = new PushedContent(bytes, Interlocked.Exchange(ref _reservation, null)
                ?? throw new InvalidOperationException("a page answer can only be consumed once"));
            content.Headers.ContentType = new MediaTypeHeaderValue(PullAnswer.MediaType);
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = content, RequestMessage = request };
            _ = response.Headers.TryAddWithoutValidation(DatabaseRevision.Header, to.ToString());
            if (cdcPosition is { } position)
            {
                _ = response.Headers.TryAddWithoutValidation(RevisionPullStart.CdcHeader, position.ToString(CultureInfo.InvariantCulture));
            }
            return response;
        }

        public void Dispose() => Interlocked.Exchange(ref _reservation, null)?.Dispose();
    }

    private sealed class PushedContent(ArraySegment<byte> bytes, IMemoryReservation reservation) : ByteArrayContent(bytes.Array!, bytes.Offset, bytes.Count)
    {
        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                reservation.Dispose();
            }

            base.Dispose(disposing);
        }
    }

    private sealed class Prefetched(DatabaseRevision revision, PushedAnswer? answer) : IDisposable
    {
        public DatabaseRevision Revision => revision;

        public PushedAnswer? Answer => answer;

        public void Dispose() => answer?.Dispose();
    }

    private sealed class RevisionObserver(HttpClient http, Uri pull) : HttpMessageHandler
    {
        private const int MaxPushes = 8;
        private const int MaxPrefetchBytes = 1024 * 1024;
        private readonly Lock _gate = new();
        private readonly List<PushedAnswer> _pushes = [];
        private Observation? _seen;
        private bool _pushedAllowed;
        private bool _served;
        private bool _servedFromHome;
        private PushedAnswer? _prefetched;

        internal sealed record Observation(string Revision, long? CdcPosition);

        public Observation? Take() => Interlocked.Exchange(ref _seen, null);

        public void Begin(bool pushedAllowed, PushedAnswer? prefetched) =>
            (_pushedAllowed, _served, _servedFromHome, _prefetched) = (pushedAllowed, false, false, prefetched);

        public bool ServedFromHome => _servedFromHome;

        public async Task<Prefetched?> PrefetchAsync(DatabaseRevision held, IMemoryBudget memory, TenantContext tenant, CancellationToken cancellationToken)
        {
            using var reservation = memory.TryReserve(MaxPrefetchBytes, tenant);
            if (reservation is null)
            {
                return null;
            }

            using var request = new HttpRequestMessage(HttpMethod.Post, pull)
            {
                Content = new ByteArrayContent(new PullUpdatesRequest { ClientRevision = held.ToString(), Encoding = PageEncoding.Raw, StreamKind = StreamKind.Pages }.ToByteArray()),
            };
            request.Content.Headers.ContentType = new MediaTypeHeaderValue(PullAnswer.MediaType);
            _ = request.Headers.TryAddWithoutValidation(StorageFormat.AcceptHeader, StorageFormat.Supported);
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode || !Readable(response) || !response.Headers.TryGetValues(DatabaseRevision.Header, out var values)
                || !DatabaseRevision.TryParse(values.FirstOrDefault(), out var to) || to.Incarnation != held.Incarnation || response.Content.Headers.ContentLength > MaxPrefetchBytes)
            {
                return null;
            }

            if (DatabaseRevision.Compare(to, held) <= 0)
            {
                return new Prefetched(held, null);
            }

            var position = response.Headers.TryGetValues(RevisionPullStart.CdcHeader, out var positions)
                && long.TryParse(positions.FirstOrDefault(), NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) ? (long?)parsed : null;
            var buffer = new byte[MaxPrefetchBytes];
            var length = 0;
            await using (var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false))
            {
                int read;
                while ((read = await stream.ReadAsync(buffer.AsMemory(length), cancellationToken).ConfigureAwait(false)) > 0)
                {
                    length += read;
                    if (length == buffer.Length)
                    {
                        return null;
                    }
                }
            }

            if (memory.TryReserve(length, tenant) is not { } kept)
            {
                return null;
            }

            return new Prefetched(to, new PushedAnswer(held, to, buffer.AsSpan(0, length).ToArray(), kept, position));
        }

        public bool End()
        {
            _pushedAllowed = false;
            _prefetched = null;
            return _served;
        }

        public void Offer(PushedAnswer push)
        {
            lock (_gate)
            {
                if (_pushes.Count == MaxPushes)
                {
                    _pushes[0].Dispose();
                    _pushes.RemoveAt(0);
                }

                _pushes.Add(push);
            }
        }

        public bool Pending(DatabaseRevision? held)
        {
            lock (_gate)
            {
                return held is { } revision && _pushes.Exists(push => push.Covers(revision));
            }
        }

        public void Clear()
        {
            lock (_gate)
            {
                _pushes.ForEach(push => push.Dispose());
                _pushes.Clear();
            }
        }

        private PushedAnswer? Covering(DatabaseRevision held)
        {
            lock (_gate)
            {
                PushedAnswer? best = null;
                foreach (var push in _pushes.Where(push => push.Covers(held)))
                {
                    best = best is null || DatabaseRevision.Compare(push.To, best.To) > 0 ? push : best;
                }

                _ = _pushes.RemoveAll(push =>
                {
                    if (ReferenceEquals(push, best))
                    {
                        return true;
                    }

                    var drop = push.Stale(best?.To ?? held);
                    if (drop)
                    {
                        push.Dispose();
                    }

                    return drop;
                });
                return best;
            }
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (_pushedAllowed && request.Method == HttpMethod.Post && request.Content is { } body
                && request.RequestUri?.AbsolutePath.EndsWith("/pull-updates", StringComparison.Ordinal) == true
                && PullAnswer.TryClientRevision(await body.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false), out var client)
                && DatabaseRevision.TryParse(client, out var held) && TakeAnswer(held) is { } push)
            {
                _served = true;
                Volatile.Write(ref _seen, new Observation(push.To.ToString(), push.CdcPosition));
                return push.Answer(request);
            }

            using var copy = new HttpRequestMessage(request.Method, request.RequestUri)
            {
                Content = request.Content,
                Version = request.Version,
                VersionPolicy = request.VersionPolicy,
            };
            foreach (var (name, values) in request.Headers)
            {
                _ = copy.Headers.TryAddWithoutValidation(name, values);
            }

            _ = copy.Headers.Remove(StorageFormat.AcceptHeader);
            _ = copy.Headers.TryAddWithoutValidation(StorageFormat.AcceptHeader, StorageFormat.Supported);
            try
            {
                var response = await http.SendAsync(copy, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
                if (response.IsSuccessStatusCode && !Readable(response))
                {
                    response.Dispose();
                    throw new HttpRequestException("the source sent pages in a storage format this build does not support; the copy waits for a build that does");
                }
                if (response.IsSuccessStatusCode && response.Headers.TryGetValues(DatabaseRevision.Header, out var values))
                {
                    var position = response.Headers.TryGetValues(RevisionPullStart.CdcHeader, out var positions)
                        && long.TryParse(positions.FirstOrDefault(), NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) ? (long?)parsed : null;
                    Volatile.Write(ref _seen, new Observation(values.First(), position));
                }

                return response;
            }
            finally
            {
                copy.Content = null;
            }
        }

        private PushedAnswer? TakeAnswer(DatabaseRevision held)
        {
            if (_prefetched is { } home && home.Covers(held))
            {
                _prefetched = null;
                _servedFromHome = true;
                return home;
            }

            return Covering(held);
        }
    }

    private sealed partial class Replica(TursoReplicas owner, TenantContext database, TursoSyncDatabase sync, string path, RevisionObserver observer, HttpClient observed, ILogger logger, TimeProvider time, bool pulled) : IAsyncDisposable
    {
        private readonly SemaphoreSlim _pulls = new(1, 1);
        private readonly SemaphoreSlim _prefetches = new(1, 1);
        private readonly CancellationTokenSource _closing = new();
        private readonly ReplicaGate _gate = new();
        private readonly ConcurrentQueue<TursoDatabaseHandle> _idle = new();
        private int _idleCount;
        private int _disposed;

        public bool Pulled { get; private set; } = pulled;

        internal TursoReplicaShapeSchema ShapeSchema { get; } = new();

        public long Applied => Volatile.Read(ref _applied);

        private long _applied;
        private long? _shapeHead;

        public DatabaseRevision? Held { get; private set; }

        public async Task<ReplicaComparison> CompareAsync(DatabaseRevision revision, IReadOnlyList<RangeChecksum> home, CancellationToken cancellationToken)
        {
            await _pulls.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
                if (Held != revision || Local)
                {
                    return new ReplicaComparison(new Moved(), Held, null);
                }

                await using var reader = await BeginReadAsync(cancellationToken).ConfigureAwait(false);
                foreach (var range in home)
                {
                    RangeChecksum? local;
                    try
                    {
                        local = await ChecksumAsync(reader, range, cancellationToken).ConfigureAwait(false);
                    }
                    catch (SqlStatementException)
                    {
                        local = null;
                    }

                    if (local != range)
                    {
                        return new ReplicaComparison(new Diverged(), revision, RangeChecksums.Mismatch(range, local));
                    }
                }

                return new ReplicaComparison(new Equal(), revision, null);
            }
            finally
            {
                _pulls.Release();
            }
        }

        private static async Task<RangeChecksum?> ChecksumAsync(IReplicaReader reader, RangeChecksum home, CancellationToken cancellationToken)
        {
            var schema = home.Table.Length == 0;
            var sql = schema ? RangeChecksums.SchemaSql : RangeChecksums.RowsSql(home.Table, home.After, home.Last, home.Rows + 1);
            var maxRows = schema ? MaxReadRows : home.Rows + 1;
            var result = await reader.ExecuteAsync(new SqlStatement(sql, [], new Dictionary<string, object?>(), maxRows, home.Bytes + RangeChecksums.RangeBytes, TimeSpan.FromSeconds(30)),
                cancellationToken).ConfigureAwait(false);
            if (result.Truncated)
            {
                return null;
            }

            using var hasher = new RangeChecksums.Hasher();
            foreach (var row in result.Rows)
            {
                hasher.Add(row);
            }

            return hasher.Finish(home.Table, home.After, home.Last);
        }

        public void Offer(PushedAnswer push) => observer.Offer(push);

        public async Task<ReplicaPullResult> PullAsync(bool fromHome, CancellationToken cancellationToken)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _closing.Token);
            var token = linked.Token;
            await _pulls.WaitAsync(token).ConfigureAwait(false);
            try
            {
                token.ThrowIfCancellationRequested();
                if (Held is null || !fromHome && observer.Pending(Held))
                {
                    return await ApplyAsync(fromHome, null, owner.NextPosition(), token).ConfigureAwait(false);
                }
            }
            finally
            {
                _pulls.Release();
            }

            await _prefetches.WaitAsync(token).ConfigureAwait(false);
            try
            {
                DatabaseRevision current;
                long position;
                await _pulls.WaitAsync(token).ConfigureAwait(false);
                try
                {
                    token.ThrowIfCancellationRequested();
                    position = owner.NextPosition();
                    if (Held is not { } held || !fromHome && observer.Pending(Held))
                    {
                        return await ApplyAsync(fromHome, null, position, token).ConfigureAwait(false);
                    }

                    current = held;
                }
                finally
                {
                    _pulls.Release();
                }

                Prefetched? fetched = null;
                try
                {
                    fetched = await observer.PrefetchAsync(current, owner.Memory, database, token).ConfigureAwait(false);
                }
                catch (Exception error) when (error is HttpRequestException or IOException || error is OperationCanceledException && !token.IsCancellationRequested)
                {
                    logger.ZLogInformation($"A replica prefetch failed; the pull fetches from the home itself: {error.GetType().Name:@Error}");
                }

                using (fetched)
                {
                    await _pulls.WaitAsync(token).ConfigureAwait(false);
                    try
                    {
                        token.ThrowIfCancellationRequested();
                        if (fetched is not null && Held is { } held && held.Incarnation == fetched.Revision.Incarnation
                            && DatabaseRevision.Compare(held, fetched.Revision) >= 0)
                        {
                            Volatile.Write(ref _applied, Math.Max(_applied, position));
                            Pulled = true;
                            return new ReplicaPullResult(false, false, observer.Pending(Held));
                        }

                        return await ApplyAsync(fromHome, fetched?.Answer, position, token).ConfigureAwait(false);
                    }
                    finally
                    {
                        _pulls.Release();
                    }
                }
            }
            finally
            {
                _prefetches.Release();
            }
        }

        private async Task<ReplicaPullResult> ApplyAsync(bool fromHome, PushedAnswer? prefetched, long position, CancellationToken cancellationToken)
        {
            _ = observer.Take();
            bool changed;
            bool pushed;
            using (await _gate.ExclusiveAsync(cancellationToken).ConfigureAwait(false))
            {
                observer.Begin(pushedAllowed: !fromHome || prefetched is not null, prefetched);
                try
                {
                    changed = await sync.PullAsync(cancellationToken).ConfigureAwait(false);
                }
                catch
                {
                    Held = null;
                    _shapeHead = null;
                    throw;
                }
                finally
                {
                    pushed = observer.End();
                }

                if (!pushed || observer.ServedFromHome)
                {
                    Volatile.Write(ref _applied, Math.Max(_applied, position));
                }

                var observation = observer.Take();
                Held = DatabaseRevision.TryParse(observation?.Revision, out var held) ? held : null;
                _shapeHead = observation?.CdcPosition;
                if (changed)
                {
                    await RestoreOverlayAsync(cancellationToken).ConfigureAwait(false);
                }
            }

            Pulled = true;
            var log = new FileInfo(path + "-wal");
            if (log.Exists && log.Length > 4L * 1024 * 1024)
            {
                try
                {
                    using (await _gate.ExclusiveAsync(cancellationToken).ConfigureAwait(false))
                    {
                        await sync.CheckpointAsync(cancellationToken).ConfigureAwait(false);
                    }
                }
                catch (TursoException error)
                {
                    logger.ZLogInformation($"A replica checkpoint waits for its readers: {error.GetType().Name:@Error}");
                }
            }

            return new ReplicaPullResult(changed, pushed, observer.Pending(Held));
        }

        internal async ValueTask<IReplicaShapeReader?> BeginShapeReadAsync(TenantContext database, CancellationToken cancellationToken)
        {
            await _pulls.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
                return _shapeHead is { } head
                    ? await TursoReplicaShapeReader.OpenAsync(await sync.ConnectAsync(cancellationToken).ConfigureAwait(false), database, ShapeSchema, head, cancellationToken).ConfigureAwait(false)
                    : null;
            }
            finally
            {
                _pulls.Release();
            }
        }

        public async Task<IReplicaReader> BeginReadAsync(CancellationToken cancellationToken)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            var lease = await _gate.ReadAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                return await OpenReaderAsync(lease, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception error)
            {
                lease.Dispose();
                logger.ZLogInformation($"A replica reader could not open: {error.GetType().Name:@Error} ({error.Message:@Detail})");
                throw;
            }
        }

        private async Task<IReplicaReader> OpenReaderAsync(IDisposable lease, CancellationToken cancellationToken)
        {
            if (!_idle.TryDequeue(out var native))
            {
                native = await sync.ConnectHandleAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    Run(native, "PRAGMA query_only = 1;");
                }
                catch
                {
                    Close(native);
                    throw;
                }
            }
            else
            {
                _ = Interlocked.Decrement(ref _idleCount);
            }

            var started = Stopwatch.GetTimestamp();
            while (true)
            {
                try
                {
                    Run(native, "BEGIN;");
                    break;
                }
                catch (TursoException error) when (Locked(error) && Stopwatch.GetElapsedTime(started) < LockedWait)
                {
                    try
                    {
                        await Task.Delay(TimeSpan.FromMilliseconds(50), time, cancellationToken).ConfigureAwait(false);
                    }
                    catch
                    {
                        Close(native);
                        throw;
                    }
                }
                catch
                {
                    Close(native);
                    throw;
                }
            }

            return new Reader(this, native, lease);
        }

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            await _closing.CancelAsync().ConfigureAwait(false);
            await _prefetches.WaitAsync().ConfigureAwait(false);
            await _pulls.WaitAsync().ConfigureAwait(false);
            while (_idle.TryDequeue(out var native))
            {
                Close(native);
            }

            if (_writer is { } writer)
            {
                _writer = null;
                Close(writer);
            }

            sync.Dispose();
            observed.Dispose();
            observer.Clear();
            _pulls.Release();
            _prefetches.Release();
            _closing.Dispose();
            _gate.Dispose();
        }

        private void Return(TursoDatabaseHandle native, bool healthy)
        {
            try
            {
                Run(native, "ROLLBACK;");
            }
            catch (TursoException)
            {
                healthy = false;
            }

            if (healthy && Volatile.Read(ref _disposed) == 0 && Interlocked.Increment(ref _idleCount) <= MaxIdleConnections)
            {
                _idle.Enqueue(native);
                return;
            }

            if (healthy && Volatile.Read(ref _disposed) == 0)
            {
                _ = Interlocked.Decrement(ref _idleCount);
            }

            Close(native);
        }

        private void Close(TursoDatabaseHandle native)
        {
            native.Dispose();
            sync.ReleaseConnection();
        }

        private static readonly TimeSpan LockedWait = TimeSpan.FromSeconds(5);
        private static bool Locked(TursoException error) => SqlErrors.IsLocked(error.Message);

        private static void Run(TursoDatabaseHandle native, string sql)
        {
            var prepared = TursoBindings.PrepareStatement(native, sql);
            try
            {
                while (TursoBindings.Read(prepared))
                {
                }
            }
            finally
            {
                TursoStorageTransaction.Release(prepared);
            }
        }

        private sealed class Reader(Replica replica, TursoDatabaseHandle native, IDisposable lease) : IReplicaReader
        {
            private bool _healthy = true;
            private int _disposed;

            public ValueTask<SqlStatementResult> ExecuteAsync(SqlStatement statement, CancellationToken cancellationToken)
            {
                ArgumentNullException.ThrowIfNull(statement);
                ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
                return new(Task.Run(() => Execute(statement, cancellationToken), cancellationToken));
            }

            public ValueTask<SqlStatementShape> DescribeAsync(string sql, CancellationToken cancellationToken)
            {
                ArgumentNullException.ThrowIfNull(sql);
                ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
                return new(Task.Run(() => Describe(sql), cancellationToken));
            }

            public ValueTask DisposeAsync()
            {
                if (Interlocked.Exchange(ref _disposed, 1) == 0)
                {
                    replica.Return(native, _healthy);
                    lease.Dispose();
                }

                return ValueTask.CompletedTask;
            }

            private SqlStatementShape Describe(string sql)
            {
                try
                {
                    var (prepared, _) = TursoStorageTransaction.Prepare(native, sql, first: false);
                    try
                    {
                        var parameters = new string?[TursoBindings.GetParameterCount(prepared)];
                        for (var i = 0; i < parameters.Length; i++)
                        {
                            parameters[i] = TursoBindings.GetParameterName(prepared, i + 1);
                        }

                        return new SqlStatementShape(parameters, TursoStorageTransaction.Columns(prepared));
                    }
                    finally
                    {
                        TursoStorageTransaction.Release(prepared);
                    }
                }
                catch (TursoException error)
                {
                    var (failure, message) = TursoStorageTransaction.Classify(native, error);
                    _healthy = failure is not Error;
                    throw new SqlStatementException(failure, message, transactionEnded: false, error);
                }
            }

            private SqlStatementResult Execute(SqlStatement statement, CancellationToken cancellationToken)
            {
                var started = Stopwatch.GetTimestamp();
                try
                {
                    var (prepared, consumed) = TursoStorageTransaction.Prepare(native, statement.Sql, statement.First);
                    TursoBindings.SetQueryTimeout(native, statement.Timeout);
                    TursoBindings.SetMaxValueLength(native, statement.MaxBytes);
                    try
                    {
                        using (cancellationToken.Register(static state => TursoBindings.Interrupt((TursoDatabaseHandle)state!), native))
                        {
                            var (columns, rows, _, truncated) = Step(prepared, statement, cancellationToken);
                            return new SqlStatementResult(columns, rows, 0, 0, 0, truncated, consumed, Stopwatch.GetElapsedTime(started));
                        }
                    }
                    finally
                    {
                        TursoBindings.SetQueryTimeout(native, TimeSpan.Zero);
                        TursoBindings.SetMaxValueLength(native, long.MaxValue);
                        TursoBindings.ClearInterrupt(native);
                    }
                }
                catch (TursoException error)
                {
                    var (failure, message) = TursoStorageTransaction.Classify(native, error);
                    _healthy = failure is not Error;
                    throw new SqlStatementException(failure, message, transactionEnded: false, error);
                }
            }

            internal static (List<SqlStatementColumn> Columns, List<object?[]> Rows, long Affected, bool Truncated) Step(
                TursoStatementHandle prepared, SqlStatement statement, CancellationToken cancellationToken)
            {
                try
                {
                    TursoStorageTransaction.Bind(prepared, statement);
                    var columns = TursoStorageTransaction.Columns(prepared);
                    var rows = new List<object?[]>();
                    long bytes = 0;
                    var maxRows = Math.Min(statement.MaxRows, MaxReadRows);
                    while (TursoBindings.Read(prepared))
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        long size = 0;
                        for (var i = 0; i < columns.Count; i++)
                        {
                            size += TursoStorageTransaction.ValueSize(prepared, i);
                        }

                        if (rows.Count == maxRows || size > statement.MaxBytes - bytes)
                        {
                            return (columns, rows, TursoBindings.RowsAffected(prepared), true);
                        }

                        bytes += size;
                        var row = new object?[columns.Count];
                        for (var i = 0; i < row.Length; i++)
                        {
                            row[i] = TursoStorageTransaction.Value(prepared, i);
                        }

                        rows.Add(row);
                    }

                    return (columns, rows, TursoBindings.RowsAffected(prepared), false);
                }
                finally
                {
                    TursoStorageTransaction.Release(prepared);
                }
            }
        }
    }
}
