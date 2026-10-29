using System.Buffers;
using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Channels;
using Amazon.Runtime;
using Amazon.Runtime.Internal;
using Amazon.Runtime.Internal.Auth;
using Amazon.S3;
using Amazon.S3.Model;
using TerraBase.Configuration;
using TerraBase.Engine;
using TerraBase.Features.Backup;
using TerraBase.Features.Memory;
using TerraBase.Tenancy;
using ZLogger;

namespace TerraBase.Features.Shapes;

/// <summary>Background transport of published immutable shapes; refusal leaves origin delivery available.</summary>
internal interface IShapeMirror
{
    void Warm(TenantContext database, string shapeId, string handle, string offset, string nextOffset);
    bool Queue(TenantContext database, string shapeId, string handle, string offset, string nextOffset,
        long coveredOffset, long valuesAt, ReadOnlyMemory<byte> sealedBody, DateTimeOffset sealedAt);
    ValueTask<ShapeMirrorManifest> ManifestAsync(TenantContext database, string shapeId, string handle,
        string offset, long floor, CancellationToken cancellationToken);
    ValueTask<int> TrimAsync(TenantContext database, string handle, long floor, CancellationToken cancellationToken);
}

internal sealed record ShapeMirrorManifest(string Handle, IReadOnlyList<ShapeMirrorObject> Objects);

internal sealed record ShapeMirrorObject(string Offset, string NextOffset, string Binding, string Object,
    string Url, DateTimeOffset ExpiresAt, string Epoch, string KeyId, string Sha256, long Size);

[JsonSourceGenerationOptions(JsonSerializerDefaults.Web, PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower)]
[JsonSerializable(typeof(ShapeMirrorManifest))]
internal sealed partial class ShapeMirrorJsonContext : JsonSerializerContext;

/// <summary>Bounded worker and metadata cache. Tigris remains optional to every individual origin read.</summary>
internal sealed class ShapeMirror(IStorageEngine engine, IShapeStorage storage, IShapeMetrics metrics, IMemoryBudget memory, TimeProvider time,
    Func<IShapeCrypto> crypto, ShapeObjectStore? objects, ILogger<ShapeMirror> logger) : BackgroundService, IShapeMirror, IShapeKeyRetention
{
    internal const int ManifestObjects = 8;
    private const int QueueItems = 32;
    // Uploads run concurrently: one shape seals up to a chunk per commit, and a single bucket round trip at a time falls
    // behind that, refusing (and so never mirroring) the overflow.
    private const int Workers = 4;
    private const int TrimItems = 64;
    private const long TrimBytes = 1024;
    private const long QueueBytes = 64 * 1024 * 1024;
    private const int CacheItems = 4096;
    private const int ScanItems = 1024;
    private static readonly TimeSpan RemoteDeadline = TimeSpan.FromSeconds(2);
    // A CI runner saw one bucket HEAD answer 408 RequestTimeout, and the chunk never reached the bucket (#838).
    internal const int MirrorAttempts = 3;
    internal static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(2);
    private static readonly SearchValues<char> LowerHex = SearchValues.Create("0123456789abcdef");
    private readonly Channel<Pending> _queue = Channel.CreateBounded<Pending>(new BoundedChannelOptions(QueueItems)
    {
        FullMode = BoundedChannelFullMode.Wait,
    });
    private readonly Channel<PendingTrim> _trims = Channel.CreateBounded<PendingTrim>(new BoundedChannelOptions(TrimItems)
    {
        FullMode = BoundedChannelFullMode.Wait,
        SingleReader = true,
    });
    private readonly Lock _gate = new();
    private readonly HashSet<string> _pending = [];
    private readonly HashSet<string> _trimming = [];
    private readonly Dictionary<string, Cached> _cache = [];
    private readonly Dictionary<string, Proven> _proofs = [];
    private readonly Dictionary<string, Missing> _missing = [];
    private readonly Dictionary<string, ScanCursor> _trimCursors = [];
    private readonly IStorageEngine _engine = engine;
    private readonly IShapeStorage _storage = storage;
    private readonly IShapeMetrics _metrics = metrics;
    private readonly IMemoryBudget _memory = memory;
    private readonly TimeProvider _time = time;
    private readonly Func<IShapeCrypto> _crypto = crypto;
    private readonly ShapeObjectStore? _objects = objects;
    private readonly ILogger<ShapeMirror> _logger = logger;
    private long _queuedBytes;

    internal void ChunkSealed(IDatabaseStore store, ShapeSealDefinition shape, ShapeSealedChunk chunk)
    {
        // shortcut: refused work, and work that failed every attempt, stays origin-only; add bounded backfill when complete
        // T1 coverage is required.
        if (store.Tenant is { } tenant)
        {
            _ = Queue(tenant, shape.ShapeId, shape.Handle, Number(chunk.Offset), Number(chunk.EndOffset),
                chunk.EndOffset, chunk.ValuesAt, chunk.Body, DateTimeOffset.FromUnixTimeMilliseconds(chunk.SealedAt));
        }
    }

    internal void SnapshotSealed(IDatabaseStore store, ShapeSnapshotDefinition shape, ShapeSnapshot snapshot)
    {
        if (store.Tenant is { } tenant)
        {
            _ = Queue(tenant, shape.ShapeId, shape.Handle, snapshot.FirstOffset, string.Empty,
                snapshot.Offset, snapshot.Offset, ReadOnlyMemory<byte>.Empty, DateTimeOffset.FromUnixTimeMilliseconds(snapshot.SealedAt));
        }
    }

    // Runs inside origin maintenance, so it only queues: Tigris I/O belongs to the worker, and a refusal waits for the next trim or hourly sweep.
    internal ValueTask FloorTrimmedAsync(IDatabaseStore store, string handle, long floor, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (store.Tenant is { } tenant && _objects is not null)
        {
            ValidateHandle(handle);
            lock (_gate)
            {
                if (!_trimming.Contains(handle) && _trimming.Count < TrimItems
                    && _memory.TryReserve(MemoryLane.Bulk, TrimBytes, tenant) is { } admission)
                {
                    if (_trims.Writer.TryWrite(new(tenant, handle, floor, admission)))
                    {
                        _trimming.Add(handle);
                    }
                    else
                    {
                        admission.Dispose();
                    }
                }
            }
        }

        return ValueTask.CompletedTask;
    }

    public void Warm(TenantContext database, string shapeId, string handle, string offset, string nextOffset)
    {
        var key = ShapeObjectStore.Key(shapeId, handle, offset);
        lock (_gate)
        {
            if (_missing.TryGetValue(key, out var missing))
            {
                if (missing.RetryAfter > _time.GetUtcNow())
                {
                    return;
                }

                _missing.Remove(key);
                missing.Admission.Dispose();
            }
        }

        _ = Enqueue(database, shapeId, handle, offset, nextOffset, 0, 0, ReadOnlyMemory<byte>.Empty, _time.GetUtcNow(), adoptOnly: true);
    }

    public bool Queue(TenantContext database, string shapeId, string handle, string offset, string nextOffset,
        long coveredOffset, long valuesAt, ReadOnlyMemory<byte> sealedBody, DateTimeOffset sealedAt) =>
        Enqueue(database, shapeId, handle, offset, nextOffset, coveredOffset, valuesAt, sealedBody, sealedAt, adoptOnly: false);

    private bool Enqueue(TenantContext database, string shapeId, string handle, string offset, string nextOffset,
        long coveredOffset, long valuesAt, ReadOnlyMemory<byte> sealedBody, DateTimeOffset sealedAt, bool adoptOnly)
    {
        Validate(shapeId, handle, offset);
        if (_objects is null || sealedBody.Length > ShapeCrypto.MaxPlainBytes || nextOffset.Length > 1100
            || coveredOffset < 0 || valuesAt < coveredOffset || sealedAt > _time.GetUtcNow()
            || sealedAt < _time.GetUtcNow() - FeedCursor.Retention)
        {
            return false;
        }

        var key = ShapeObjectStore.Key(shapeId, handle, offset);
        IMemoryReservation? admission;
        lock (_gate)
        {
            if (_cache.ContainsKey(key) || _pending.Contains(key))
            {
                return true;
            }

            if (_pending.Count >= QueueItems || _queuedBytes + sealedBody.Length > QueueBytes)
            {
                return false;
            }

            admission = _memory.TryReserve(MemoryLane.Bulk, sealedBody.Length + 2048L + (offset.Length + nextOffset.Length) * 2L, database);
            if (admission is null)
            {
                return false;
            }

            // The claimed slot keeps duplicates and the byte bound exact while the body is copied outside the lock.
            _pending.Add(key);
            _queuedBytes += sealedBody.Length;
        }

        try
        {
            if (_queue.Writer.TryWrite(new(database, shapeId, handle, offset, nextOffset, coveredOffset, valuesAt,
                sealedAt, sealedBody.ToArray(), admission, adoptOnly)))
            {
                return true;
            }
        }
        catch
        {
            Release(key, sealedBody.Length, admission);
            throw;
        }

        Release(key, sealedBody.Length, admission);
        return false;
    }

    public async ValueTask<ShapeMirrorManifest> ManifestAsync(TenantContext database, string shapeId, string handle,
        string offset, long floor, CancellationToken cancellationToken)
    {
        Validate(shapeId, handle, offset);
        List<ShapeMirrorObject> entries = [];
        if (_objects is null)
        {
            return new(handle, entries);
        }

        // The caller already checked publication and lineage; always recheck the persisted retention floor.
        var owner = ShapeObjectStore.Owner(database);
        using var deadline = new CancellationTokenSource(RemoteDeadline, _time);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token);
        try
        {
            floor = Math.Max(floor, await FloorAsync(database, handle, linked.Token).ConfigureAwait(false));
            for (var count = 0; count < ManifestObjects; count++)
            {
                var key = ShapeObjectStore.Key(shapeId, handle, offset);
                Cached? cached;
                lock (_gate)
                {
                    _cache.TryGetValue(key, out cached);
                }

                // Cold metadata is adopted with HEAD after an origin read; plaintext continuation ids stay local.
                if (cached is null || cached.Head.Owner != owner || !Retained(cached.Head, floor))
                {
                    break;
                }

                linked.Token.ThrowIfCancellationRequested();
                var (url, expires) = _objects.Link(key);
                entries.Add(new(offset, cached.NextOffset, handle + "\0" + offset, key, url, expires,
                    Number(cached.Head.Epoch), cached.Head.KeyId, cached.Head.Sha256, cached.Head.Size));
                offset = cached.NextOffset;
            }
        }
        catch (Exception error) when (!cancellationToken.IsCancellationRequested && RemoteFailure(error))
        {
            _logger.ZLogWarning(error, $"Shape manifest stopped at an unavailable mirror");
        }

        return new(handle, entries);
    }

    public async ValueTask<int> TrimAsync(TenantContext database, string handle, long floor, CancellationToken cancellationToken)
    {
        ValidateHandle(handle);
        if (_objects is null)
        {
            return 0;
        }

        var deleted = 0;
        var owner = ShapeObjectStore.Owner(database);
        using var admission = _memory.TryReserve(MemoryLane.Bulk, 512 * 1024, database);
        if (admission is null)
        {
            return 0;
        }

        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30), _time);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token);
        try
        {
            var persistedFloor = await FloorAsync(database, handle, linked.Token).ConfigureAwait(false);
            if (persistedFloor == long.MaxValue)
            {
                return 0;
            }

            floor = Math.Max(floor, persistedFloor);
            var scanned = 0;
            string? after;
            lock (_gate)
            {
                after = _trimCursors.GetValueOrDefault(handle)?.After;
            }

            var more = false;
            await foreach (var stored in _objects.ListAsync(handle, after, linked.Token).ConfigureAwait(false))
            {
                if (++scanned > ScanItems)
                {
                    more = true;
                    break;
                }

                after = stored.Key;
                var head = await RetentionHeadAsync(stored.Key, linked.Token).ConfigureAwait(false);
                if (head is not null && head.Owner == owner && !Retained(head, floor)
                    && await _objects.DeleteAsync(stored.Key, linked.Token).ConfigureAwait(false))
                {
                    Remove(stored.Key);
                    deleted++;
                }
            }

            lock (_gate)
            {
                if (_trimCursors.Remove(handle, out var previous))
                {
                    previous.Admission.Dispose();
                }

                // shortcut: continuation waits for another floor notification; refused cursor admission falls back to the hourly expiry sweep.
                if (more && _trimCursors.Count < CacheItems && _memory.TryReserve(MemoryLane.Bulk, 1024, database) is { } cursorAdmission)
                {
                    _trimCursors[handle] = new(after!, cursorAdmission);
                }
            }
        }
        catch (Exception error) when (!cancellationToken.IsCancellationRequested && RemoteFailure(error))
        {
            _logger.ZLogWarning(error, $"Shape mirror retention will retry on the next pass");
        }

        return deleted;
    }

    public async ValueTask<bool> HasObjectAsync(TenantContext database, string handle, long epoch, string keyId,
        CancellationToken cancellationToken)
    {
        ValidateHandle(handle);
        if (_objects is null)
        {
            return false;
        }

        using var admission = _memory.TryReserve(MemoryLane.Bulk, 512 * 1024, database);
        if (admission is null)
        {
            return false;
        }

        using var deadline = new CancellationTokenSource(RemoteDeadline, _time);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token);
        try
        {
            var floor = await FloorAsync(database, handle, linked.Token).ConfigureAwait(false);
            if (floor == long.MaxValue)
            {
                return false;
            }

            var owner = ShapeObjectStore.Owner(database);
            lock (_gate)
            {
                if (_cache.Any(pair => pair.Key.StartsWith("v1/shapes/" + handle + "/", StringComparison.Ordinal)
                    && pair.Value.Head.Owner == owner && pair.Value.Head.Epoch == epoch
                    && pair.Value.Head.KeyId == keyId && Retained(pair.Value.Head, floor)))
                {
                    return true;
                }

                if (_proofs.Any(pair => pair.Key.StartsWith("v1/shapes/" + handle + "/", StringComparison.Ordinal)
                    && pair.Value.Head.Owner == owner && pair.Value.Head.Epoch == epoch
                    && pair.Value.Head.KeyId == keyId && Retained(pair.Value.Head, floor)))
                {
                    return true;
                }
            }

            // shortcut: cold proofs scan at most 1024 objects; an origin read warms a missed object without rewriting it.
            var scanned = 0;
            await foreach (var stored in _objects.ListAsync(handle, null, linked.Token).ConfigureAwait(false))
            {
                if (++scanned > ScanItems)
                {
                    break;
                }

                var head = await RetentionHeadAsync(stored.Key, linked.Token).ConfigureAwait(false);
                if (head is not null && head.Owner == owner && head.Epoch == epoch && head.KeyId == keyId && Retained(head, floor))
                {
                    RememberProof(stored.Key, head, database);
                    return true;
                }
            }
        }
        catch (Exception error) when (!cancellationToken.IsCancellationRequested && RemoteFailure(error))
        {
            _logger.ZLogWarning(error, $"Shape mirror could not prove a retained key pair");
        }

        return false;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var sweep = SweepAsync(stoppingToken);
        var trims = TrimQueueAsync(stoppingToken);
        try
        {
            await Task.WhenAll(Enumerable.Range(0, Workers).Select(_ => Task.Run(() => DrainAsync(stoppingToken), stoppingToken))).ConfigureAwait(false);
        }
        finally
        {
            _queue.Writer.TryComplete();
            _trims.Writer.TryComplete();
            try
            {
                await Task.WhenAll(sweep, trims).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                // Every owned worker finishes before queued reservations are released.
            }

            finally
            {
                while (_queue.Reader.TryRead(out var pending))
                {
                    Release(pending);
                }

                while (_trims.Reader.TryRead(out var trim))
                {
                    Dequeued(trim);
                    trim.Admission.Dispose();
                }

                ClearCaches();
            }
        }
    }

    private async Task DrainAsync(CancellationToken stoppingToken)
    {
        await foreach (var pending in _queue.Reader.ReadAllAsync(stoppingToken).ConfigureAwait(false))
        {
            try
            {
                // A sealed body is queued once: a bucket that timed out or failed itself is tried again after a pause, or
                // the object stays origin-only for good. Each attempt starts with HEAD and writes If-None-Match, so a
                // write that landed unacknowledged is adopted, never stored twice.
                for (var attempt = 1; ; attempt++)
                {
                    try
                    {
                        await MirrorAsync(pending, stoppingToken).ConfigureAwait(false);
                        break;
                    }
                    catch (Exception error) when (!stoppingToken.IsCancellationRequested && !pending.AdoptOnly && attempt < MirrorAttempts && Transient(error))
                    {
                        _logger.ZLogInformation(error, $"Shape mirror attempt {attempt} of {MirrorAttempts} failed; it is tried again");
                        await Task.Delay(RetryDelay * attempt, _time, stoppingToken).ConfigureAwait(false);
                    }
                }
            }
            catch (Exception error) when (!stoppingToken.IsCancellationRequested)
            {
                _logger.ZLogWarning(error, $"Shape mirror failed; immutable origin bytes remain available");
            }
            finally
            {
                Release(pending);
            }
        }
    }

    private async Task TrimQueueAsync(CancellationToken cancellationToken)
    {
        await foreach (var trim in _trims.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            // Dequeued first so a floor raised during this pass queues one more pass for the handle.
            Dequeued(trim);
            try
            {
                _ = await TrimAsync(trim.Database, trim.Handle, trim.Floor, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception error) when (!cancellationToken.IsCancellationRequested)
            {
                _logger.ZLogWarning(error, $"Shape mirror retention will retry on the next pass");
            }
            finally
            {
                trim.Admission.Dispose();
            }
        }
    }

    private void Dequeued(PendingTrim trim)
    {
        lock (_gate)
        {
            _trimming.Remove(trim.Handle);
        }
    }

    // Also collects expired objects belonging to handles that are no longer active after a restart or rotation.
    private async Task SweepAsync(CancellationToken cancellationToken)
    {
        if (_objects is null)
        {
            return;
        }

        string? after = null;
        using var timer = new PeriodicTimer(TimeSpan.FromHours(1), _time);
        while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
        {
            using var admission = _memory.TryReserve(MemoryLane.Bulk, 512 * 1024, null);
            if (admission is null)
            {
                continue;
            }

            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30), _time);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token);
            try
            {
                var scanned = 0;
                var more = false;
                await foreach (var stored in _objects.ListAsync(null, after, linked.Token).ConfigureAwait(false))
                {
                    if (++scanned > ScanItems)
                    {
                        more = true;
                        break;
                    }

                    after = stored.Key;
                    var head = await RetentionHeadAsync(stored.Key, linked.Token).ConfigureAwait(false);
                    if (head is not null && head.SealedAt < (_time.GetUtcNow() - FeedCursor.Retention).ToUnixTimeMilliseconds()
                        && await _objects.DeleteAsync(stored.Key, linked.Token).ConfigureAwait(false))
                    {
                        Remove(stored.Key);
                    }
                }

                if (!more)
                {
                    after = null;
                }
            }
            catch (Exception error) when (!cancellationToken.IsCancellationRequested && RemoteFailure(error))
            {
                _logger.ZLogWarning(error, $"Shape mirror expiry sweep will retry on the next pass");
            }
        }
    }

    private void ClearCaches()
    {
        lock (_gate)
        {
            foreach (var cached in _cache.Values)
            {
                cached.Admission.Dispose();
            }

            _cache.Clear();
            foreach (var proof in _proofs.Values)
            {
                proof.Admission.Dispose();
            }

            _proofs.Clear();
            foreach (var missing in _missing.Values)
            {
                missing.Admission.Dispose();
            }

            _missing.Clear();
            foreach (var cursor in _trimCursors.Values)
            {
                cursor.Admission.Dispose();
            }

            _trimCursors.Clear();
        }
    }

    public override void Dispose()
    {
        base.Dispose();
        ClearCaches();
        _objects?.Dispose();
    }

    private Task MirrorAsync(Pending pending, CancellationToken cancellationToken) =>
        !pending.AdoptOnly && pending.Body.IsEmpty && pending.Offset.StartsWith("snap.", StringComparison.Ordinal)
            ? MirrorSnapshotAsync(pending, cancellationToken)
            : MirrorBodyAsync(pending, cancellationToken);

    private async Task MirrorSnapshotAsync(Pending pending, CancellationToken cancellationToken)
    {
        var store = await _engine.OpenDatabaseAsync(pending.Database, cancellationToken).ConfigureAwait(false);
        if (store is null)
        {
            return;
        }

        string? after = null;
        do
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30), _time);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token);
            ShapeSnapshotPage? page;
            await using (var transaction = await _storage.BeginTransactionAsync(store, write: false, linked.Token).ConfigureAwait(false))
            {
                page = await ((IShapeSnapshotStorage)transaction).ReadShapeSnapshotPageAsync(pending.Handle,
                    pending.CoveredOffset, after, _memory, linked.Token).ConfigureAwait(false);
            }

            if (page is null)
            {
                return;
            }

            using (page)
            {
                var offset = ShapeSnapshots.PageOffset(pending.CoveredOffset, after);
                var next = page.NextId is null ? Number(pending.CoveredOffset) : ShapeSnapshots.PageOffset(pending.CoveredOffset, page.NextId);
                await MirrorBodyAsync(pending with { Offset = offset, NextOffset = next, Body = page.Body }, linked.Token).ConfigureAwait(false);
                after = page.NextId;
            }
        }
        while (after is not null);
    }

    private async Task MirrorBodyAsync(Pending pending, CancellationToken cancellationToken)
    {
        if (_objects is null)
        {
            return;
        }

        var key = ShapeObjectStore.Key(pending.ShapeId, pending.Handle, pending.Offset);
        var floor = await FloorAsync(pending.Database, pending.Handle, cancellationToken).ConfigureAwait(false);
        var floorOffset = long.TryParse(pending.Offset, NumberStyles.None, CultureInfo.InvariantCulture, out var at) ? at : pending.CoveredOffset;
        if (!pending.AdoptOnly && (floorOffset < floor || pending.SealedAt < _time.GetUtcNow() - FeedCursor.Retention))
        {
            return;
        }

        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30), _time);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token);
        var owner = ShapeObjectStore.Owner(pending.Database);
        var head = await _objects.HeadAsync(key, linked.Token).ConfigureAwait(false);
        var uploaded = false;
        if (head is null && pending.AdoptOnly)
        {
            RememberMissing(key, pending.Database);
            return;
        }

        if (head is null)
        {
            using var cipher = await _crypto().EncryptAsync(pending.Database, pending.Handle, pending.Offset,
                pending.Body, linked.Token).ConfigureAwait(false);
            head = new(owner, key[(key.LastIndexOf('/') + 1)..], Convert.ToHexStringLower(SHA256.HashData(cipher.Bytes.Span)),
                cipher.Bytes.Length, cipher.Epoch, cipher.KeyId, pending.SealedAt.ToUnixTimeMilliseconds(), floorOffset);
            var written = await _objects.PutAsync(key, head, cipher.Bytes, linked.Token).ConfigureAwait(false);
            uploaded = ReferenceEquals(written, head);
            head = written;
        }

        floor = await FloorAsync(pending.Database, pending.Handle, linked.Token).ConfigureAwait(false);
        if (floor == long.MaxValue)
        {
            return;
        }

        if (head.Owner != owner || !Retained(head, floor))
        {
            if (head.Owner == owner)
            {
                _ = await _objects.DeleteAsync(key, linked.Token).ConfigureAwait(false);
            }

            return;
        }

        lock (_gate)
        {
            if (_cache.Count >= CacheItems)
            {
                Remove(_cache.Keys.First());
            }

            var admission = _memory.TryReserve(MemoryLane.Bulk, 2048L + pending.NextOffset.Length * 2L, pending.Database);
            if (admission is not null)
            {
                Remove(key);
                _cache[key] = new(head, pending.NextOffset, admission);
            }
        }

        if (uploaded)
        {
            _metrics.RecordMirrorLag(TimeSpan.FromMilliseconds(Math.Max(0, _time.GetUtcNow().ToUnixTimeMilliseconds() - head.SealedAt)));
        }
    }

    private async ValueTask<long> FloorAsync(TenantContext database, string handle, CancellationToken cancellationToken)
    {
        var store = await _engine.OpenDatabaseAsync(database, cancellationToken).ConfigureAwait(false);
        if (store is null)
        {
            return long.MaxValue;
        }

        await using var transaction = await _storage.BeginTransactionAsync(store, write: false, cancellationToken).ConfigureAwait(false);
        return await ((IShapeRetentionTransaction)transaction).ReadShapeFloorAsync(handle, cancellationToken).ConfigureAwait(false);
    }

    private async Task<ShapeObjectHead?> RetentionHeadAsync(string key, CancellationToken cancellationToken)
    {
        try
        {
            return await _objects!.HeadAsync(key, cancellationToken).ConfigureAwait(false);
        }
        catch (InvalidDataException)
        {
            // Unproven metadata neither releases keys nor prevents scanning subsequent objects.
            return null;
        }
    }

    private bool Retained(ShapeObjectHead head, long floor) => head.FloorOffset >= floor
        && head.SealedAt >= (_time.GetUtcNow() - FeedCursor.Retention).ToUnixTimeMilliseconds();

    private void RememberProof(string key, ShapeObjectHead head, TenantContext database)
    {
        lock (_gate)
        {
            if (_proofs.Remove(key, out var previous))
            {
                previous.Admission.Dispose();
            }

            if (_proofs.Count >= CacheItems)
            {
                var first = _proofs.First();
                _proofs.Remove(first.Key);
                first.Value.Admission.Dispose();
            }

            if (_memory.TryReserve(MemoryLane.Bulk, 2048, database) is { } admission)
            {
                _proofs[key] = new(head, admission);
            }
        }
    }

    private void RememberMissing(string key, TenantContext database)
    {
        lock (_gate)
        {
            if (_missing.Remove(key, out var previous))
            {
                previous.Admission.Dispose();
            }

            if (_missing.Count >= CacheItems)
            {
                var first = _missing.First();
                _missing.Remove(first.Key);
                first.Value.Admission.Dispose();
            }

            if (_memory.TryReserve(MemoryLane.Bulk, 1024, database) is { } admission)
            {
                _missing[key] = new(_time.GetUtcNow() + TimeSpan.FromSeconds(30), admission);
            }
        }
    }

    private void Remove(string key)
    {
        lock (_gate)
        {
            if (_cache.Remove(key, out var cached))
            {
                cached.Admission.Dispose();
            }

            if (_proofs.Remove(key, out var proof))
            {
                proof.Admission.Dispose();
            }

            if (_missing.Remove(key, out var missing))
            {
                missing.Admission.Dispose();
            }
        }
    }

    private void Release(Pending pending) =>
        Release(ShapeObjectStore.Key(pending.ShapeId, pending.Handle, pending.Offset), pending.Body.Length, pending.Admission);

    private void Release(string key, long bytes, IMemoryReservation admission)
    {
        lock (_gate)
        {
            _pending.Remove(key);
            _queuedBytes -= bytes;
        }

        admission.Dispose();
    }

    // What a later attempt can outlast: a bucket that timed out, throttled or failed itself, a broken connection, or the
    // attempt's own deadline. Refusals (403, an object that cannot be adopted) and invalid metadata are final.
    private static bool Transient(Exception error) => error is IOException or HttpRequestException or OperationCanceledException or StorageBusyException
        or AmazonServiceException { StatusCode: HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests or >= HttpStatusCode.InternalServerError };

    private static bool RemoteFailure(Exception error) => error is IOException or InvalidDataException or HttpRequestException or AmazonServiceException or OperationCanceledException or StorageBusyException;
    private static string Number(long value) => value.ToString(CultureInfo.InvariantCulture);

    private static void Validate(string shapeId, string handle, string offset)
    {
        ValidateHandle(handle);
        if (shapeId.Length != 32 || shapeId.AsSpan().ContainsAnyExcept(LowerHex) || offset.Length is 0 or > 1100)
        {
            throw new ArgumentException("a mirror requires a canonical shape identity and bounded offset");
        }
    }

    private static void ValidateHandle(string handle)
    {
        if (handle.Length != 19 || !handle.StartsWith("h1-", StringComparison.Ordinal) || handle.AsSpan(3).ContainsAnyExcept(LowerHex))
        {
            throw new ArgumentException("a mirror requires a canonical shape handle", nameof(handle));
        }
    }

    private sealed record Pending(TenantContext Database, string ShapeId, string Handle, string Offset, string NextOffset,
        long CoveredOffset, long ValuesAt, DateTimeOffset SealedAt, ReadOnlyMemory<byte> Body, IMemoryReservation Admission, bool AdoptOnly);
    private sealed record PendingTrim(TenantContext Database, string Handle, long Floor, IMemoryReservation Admission);
    private sealed record Cached(ShapeObjectHead Head, string NextOffset, IMemoryReservation Admission);
    private sealed record Proven(ShapeObjectHead Head, IMemoryReservation Admission);
    private sealed record Missing(DateTimeOffset RetryAfter, IMemoryReservation Admission);
    private sealed record ScanCursor(string After, IMemoryReservation Admission);
}

/// <summary>Metadata contains hashes and numeric retention positions, never plaintext document identifiers.</summary>
internal sealed record ShapeObjectHead(string Owner, string ObjectHash, string Sha256, long Size, long Epoch,
    string KeyId, long SealedAt, long FloorOffset);

internal sealed class ShapeObjectStore : IDisposable
{
    internal const string CacheControl = "public, max-age=31536000, immutable";
    private static readonly SearchValues<char> LowerHex = SearchValues.Create("0123456789abcdef");
    internal const string HttpClientName = "shapes-s3";
    private readonly AmazonS3Client _s3;
    private readonly ShapeStorageSettings _settings;
    private readonly S3ObjectStore _objects;
    private readonly TimeProvider _time;
    private readonly Uri? _cdn;
    private readonly IShapeCdnPurge _purge;

    public ShapeObjectStore(ShapeStorageSettings settings, IHttpClientFactory http, TimeProvider time, IShapeCdnPurge purge)
    {
        _settings = settings;
        _purge = purge;
        _cdn = settings.Cdn;
        _objects = new S3ObjectStore(settings.Endpoint, settings.Region, settings.Bucket, settings.AccessKeyId, settings.SecretKey, http, HttpClientName, time);
        _time = time;
        _s3 = new(new BasicAWSCredentials(settings.AccessKeyId, settings.SecretKey), new AmazonS3Config
        {
            ServiceURL = settings.Endpoint.ToString(),
            AuthenticationRegion = settings.Region,
            ForcePathStyle = true,
            RequestChecksumCalculation = RequestChecksumCalculation.WHEN_REQUIRED,
            ResponseChecksumValidation = ResponseChecksumValidation.WHEN_REQUIRED,
            Timeout = TimeSpan.FromSeconds(30),
            HttpClientFactory = new NamedClient(http),
        });
    }

    internal static string Key(string shapeId, string handle, string offset) =>
        "v1/shapes/" + handle + "/" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(shapeId + "\0" + handle + "\0" + offset)));

    internal static string Owner(TenantContext database) => Convert.ToHexStringLower(SHA256.HashData(
        Encoding.UTF8.GetBytes(database.OrganizationId + "\0" + database.ProjectId + "\0" + database.DatabaseId)));

    public IAsyncEnumerable<StoredObject> ListAsync(string? handle, string? after, CancellationToken cancellationToken) =>
        _objects.ListAsync(handle is null ? "v1/shapes/" : "v1/shapes/" + handle + "/", after, cancellationToken);

    /// <summary>Deletes the object; on an R2 bucket its Cloudflare copies are then purged in the background.</summary>
    public async Task<bool> DeleteAsync(string key, CancellationToken cancellationToken)
    {
        var deleted = await _objects.DeleteAsync(key, cancellationToken).ConfigureAwait(false);
        if (deleted && _cdn is not null)
        {
            _ = _purge.Queue(_cdn.AbsoluteUri + key);
        }

        return deleted;
    }

    public async Task<ShapeObjectHead?> HeadAsync(string key, CancellationToken cancellationToken)
    {
        GetObjectMetadataResponse response;
        try
        {
            response = await _s3.GetObjectMetadataAsync(_settings.Bucket, key, cancellationToken).ConfigureAwait(false);
        }
        catch (AmazonS3Exception error) when (error.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        var metadata = response.Metadata;
        var owner = metadata["owner"];
        var hash = metadata["object-hash"];
        var sha256 = metadata["sha256"];
        var epoch = Decimal(metadata["epoch"]);
        var sealedAt = Decimal(metadata["sealed-at"]);
        var floorOffset = Decimal(metadata["floor-offset"]);
        var keyId = metadata["key-id"];
        if (!Hex(owner) || !Hex(hash) || !Hex(sha256) || !key.EndsWith("/" + hash, StringComparison.Ordinal)
            || epoch is null || sealedAt is null || floorOffset is null || sealedAt > _time.GetUtcNow().ToUnixTimeMilliseconds()
            || epoch > _time.GetUtcNow().ToUnixTimeSeconds() / ShapeCrypto.EpochSeconds
            || !Ulid.TryParse(keyId, out var id) || id.ToString() != keyId
            || response.ContentLength < ShapeCrypto.SealedLength(0) || response.ContentLength > ShapeCrypto.SealedLength(ShapeCrypto.MaxPlainBytes))
        {
            throw new InvalidDataException("shape mirror metadata is invalid");
        }

        return new(owner, hash, sha256, response.ContentLength, epoch.Value, keyId, sealedAt.Value, floorOffset.Value);
    }

    public async Task<ShapeObjectHead> PutAsync(string key, ShapeObjectHead head, ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken)
    {
        if (!System.Runtime.InteropServices.MemoryMarshal.TryGetArray(bytes, out var segment))
        {
            throw new ArgumentException("shape ciphertext must be array-backed", nameof(bytes));
        }

        using var stream = new MemoryStream(segment.Array!, segment.Offset, segment.Count, writable: false);
        var request = new PutObjectRequest
        {
            BucketName = _settings.Bucket,
            Key = key,
            InputStream = stream,
            AutoCloseStream = false,
            UseChunkEncoding = false,
            IfNoneMatch = "*",
            ContentType = "application/octet-stream",
        };
        request.Headers.CacheControl = CacheControl;
        request.Metadata.Add("owner", head.Owner);
        request.Metadata.Add("object-hash", head.ObjectHash);
        request.Metadata.Add("sha256", head.Sha256);
        request.Metadata.Add("epoch", Number(head.Epoch));
        request.Metadata.Add("key-id", head.KeyId);
        request.Metadata.Add("sealed-at", Number(head.SealedAt));
        request.Metadata.Add("floor-offset", Number(head.FloorOffset));
        try
        {
            _ = await _s3.PutObjectAsync(request, cancellationToken).ConfigureAwait(false);
            return head;
        }
        catch (AmazonS3Exception error) when (error.StatusCode == HttpStatusCode.PreconditionFailed)
        {
            var existing = await HeadAsync(key, cancellationToken).ConfigureAwait(false);
            return existing is not null && existing.Owner == head.Owner && existing.FloorOffset == head.FloorOffset
                ? existing : throw new InvalidDataException("shape mirror existing object cannot be adopted");
        }
    }

    /// <summary>The object's URL and when the manifest that names it should be read again: the start of the next epoch.</summary>
    public (string Url, DateTimeOffset ExpiresAt) Link(string key)
    {
        // Signing and expiry share one whole-second clock snapshot, including at the epoch boundary.
        var now = DateTimeOffset.FromUnixTimeSeconds(_time.GetUtcNow().ToUnixTimeSeconds());
        var expires = DateTimeOffset.FromUnixTimeSeconds((now.ToUnixTimeSeconds() / ShapeCrypto.EpochSeconds + 1) * ShapeCrypto.EpochSeconds);
        if (_cdn is not null)
        {
            return (_cdn.AbsoluteUri + key, expires);
        }

        var request = new DefaultRequest(new GetObjectRequest(), "s3")
        {
            Endpoint = _settings.Endpoint,
            ResourcePath = "/" + _settings.Bucket + "/" + key,
            HttpMethod = "GET",
            UseQueryString = true,
        };
        request.Parameters.Add("X-Amz-Expires", Number(expires.ToUnixTimeSeconds() - now.ToUnixTimeSeconds()));
        var signature = AWS4PreSignedUrlSigner.SignRequest(request, _s3.Config, metrics: null,
            _settings.AccessKeyId, _settings.SecretKey, "s3", _settings.Region, now.UtcDateTime);
        var url = AmazonServiceClient.ComposeUrl(request).AbsoluteUri + "&" + signature.ForQueryParameters;
        return (url, expires);
    }

    public void Dispose()
    {
        _s3.Dispose();
        _objects.Dispose();
    }

    private static string Number(long value) => value.ToString(CultureInfo.InvariantCulture);
    private static long? Decimal(string? text) => long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var value)
        && value >= 0 && Number(value) == text ? value : null;
    private static bool Hex(string? value) => value is { Length: 64 } && !value.AsSpan().ContainsAnyExcept(LowerHex);

    private sealed class NamedClient(IHttpClientFactory http) : HttpClientFactory
    {
        public override HttpClient CreateHttpClient(IClientConfig clientConfig) => http.CreateClient(HttpClientName);
    }
}
