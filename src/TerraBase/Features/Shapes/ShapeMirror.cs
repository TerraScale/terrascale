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

internal sealed class ShapeCleanupSaturatedException() : IOException("unavailable: the shape cleanup inventory is full");

internal sealed class ShapeMirror(IStorageEngine engine, IShapeStorage storage, IShapeMetrics metrics, IMemoryBudget memory, TimeProvider time,
    Func<IShapeCrypto> crypto, ShapeObjectStore? objects, IShapeCleanupStore cleanup, Func<IShapeCleanup> worker, ILogger<ShapeMirror> logger)
    : BackgroundService, IShapeMirror, IShapeKeyRetention
{
    internal const int ManifestObjects = 8;
    private const int QueueItems = 32;
    private const int Workers = 4;
    private const long QueueBytes = 64 * 1024 * 1024;
    private const int CacheItems = 4096;
    private const int ScanItems = 1024;
    private static readonly TimeSpan RemoteDeadline = TimeSpan.FromSeconds(2);
    internal const int MirrorAttempts = 3;
    internal static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(2);
    private static readonly SearchValues<char> LowerHex = SearchValues.Create("0123456789abcdef");
    private readonly Channel<Pending> _queue = Channel.CreateBounded<Pending>(new BoundedChannelOptions(QueueItems)
    {
        FullMode = BoundedChannelFullMode.Wait,
    });
    private readonly Lock _gate = new();
    private readonly HashSet<string> _pending = [];
    private readonly Dictionary<string, Cached> _cache = [];
    private readonly Dictionary<string, Missing> _missing = [];
    private long _queuedBytes;

    internal void ChunkSealed(IDatabaseStore store, ShapeSealDefinition shape, ShapeSealedChunk chunk)
    {
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

    internal async ValueTask FloorTrimmedAsync(IDatabaseStore store, string handle, long floor, CancellationToken cancellationToken)
    {
        if (store.Tenant is { } tenant && objects is not null)
        {
            ValidateHandle(handle);
            try
            {
                _ = await ScheduleAsync(tenant, handle, floor, ShapeCleanupReason.Floor, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception error) when (!cancellationToken.IsCancellationRequested && RemoteFailure(error))
            {
                logger.ZLogWarning(error, $"Shape cleanup activation failed; retention deadlines still schedule the objects");
            }
        }
    }

    public void Warm(TenantContext database, string shapeId, string handle, string offset, string nextOffset)
    {
        var key = ShapeObjectStore.Key(shapeId, handle, offset);
        lock (_gate)
        {
            if (_missing.TryGetValue(key, out var missing))
            {
                if (missing.RetryAfter > time.GetUtcNow())
                {
                    return;
                }

                _missing.Remove(key);
                missing.Admission.Dispose();
            }
        }

        _ = Enqueue(database, shapeId, handle, offset, nextOffset, 0, 0, ReadOnlyMemory<byte>.Empty, time.GetUtcNow(), adoptOnly: true);
    }

    public bool Queue(TenantContext database, string shapeId, string handle, string offset, string nextOffset,
        long coveredOffset, long valuesAt, ReadOnlyMemory<byte> sealedBody, DateTimeOffset sealedAt) =>
        Enqueue(database, shapeId, handle, offset, nextOffset, coveredOffset, valuesAt, sealedBody, sealedAt, adoptOnly: false);

    private bool Enqueue(TenantContext database, string shapeId, string handle, string offset, string nextOffset,
        long coveredOffset, long valuesAt, ReadOnlyMemory<byte> sealedBody, DateTimeOffset sealedAt, bool adoptOnly)
    {
        Validate(shapeId, handle, offset);
        if (objects is null || sealedBody.Length > ShapeCrypto.MaxPlainBytes || nextOffset.Length > 1100
            || coveredOffset < 0 || valuesAt < coveredOffset || sealedAt > time.GetUtcNow()
            || sealedAt < time.GetUtcNow() - FeedCursor.Retention)
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

            admission = memory.TryReserve(MemoryLane.Bulk, sealedBody.Length + 2048L + (offset.Length + nextOffset.Length) * 2L, database);
            if (admission is null)
            {
                return false;
            }

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
        if (objects is null)
        {
            return new(handle, entries);
        }

        var owner = ShapeObjectStore.Owner(database);
        using var deadline = new CancellationTokenSource(RemoteDeadline, time);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token);
        try
        {
            floor = Math.Max(floor, await FloorAsync(database, handle, linked.Token).ConfigureAwait(false));
            List<(string Key, string Offset, Cached Cached)> chain = [];
            for (var count = 0; count < ManifestObjects; count++)
            {
                var key = ShapeObjectStore.Key(shapeId, handle, offset);
                Cached? cached;
                lock (_gate)
                {
                    _cache.TryGetValue(key, out cached);
                }

                if (cached is null || cached.Head.Owner != owner || !Retained(cached.Head, floor))
                {
                    break;
                }

                chain.Add((key, offset, cached));
                offset = cached.NextOffset;
            }

            var (live, saturated) = await cleanup.LiveAsync([.. chain.Select(link => link.Key)], Now, linked.Token).ConfigureAwait(false);
            if (saturated)
            {
                throw new ShapeCleanupSaturatedException();
            }

            foreach (var (key, at, cached) in chain)
            {
                if (!live.Contains(key))
                {
                    Remove(key);
                    break;
                }

                var (url, expires) = objects.Link(key);
                entries.Add(new(at, cached.NextOffset, handle + "\0" + at, key, url, expires,
                    Number(cached.Head.Epoch), cached.Head.KeyId, cached.Head.Sha256, cached.Head.Size));
            }
        }
        catch (Exception error) when (!cancellationToken.IsCancellationRequested && error is not ShapeCleanupSaturatedException && RemoteFailure(error))
        {
            logger.ZLogWarning(error, $"Shape manifest stopped at an unavailable mirror");
        }

        return new(handle, entries);
    }

    public async ValueTask<int> TrimAsync(TenantContext database, string handle, long floor, CancellationToken cancellationToken)
    {
        ValidateHandle(handle);
        if (objects is null)
        {
            return 0;
        }

        var persisted = await FloorAsync(database, handle, cancellationToken).ConfigureAwait(false);
        return persisted == long.MaxValue ? 0
            : await ScheduleAsync(database, handle, Math.Max(floor, persisted), ShapeCleanupReason.Floor, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<bool> HasObjectAsync(TenantContext database, string handle, long epoch, string keyId,
        CancellationToken cancellationToken)
    {
        ValidateHandle(handle);
        if (objects is null)
        {
            return false;
        }

        using var admission = memory.TryReserve(MemoryLane.Bulk, 512 * 1024, database);
        if (admission is null)
        {
            return false;
        }

        using var deadline = new CancellationTokenSource(RemoteDeadline, time);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token);
        try
        {
            var store = await engine.OpenDatabaseAsync(database, linked.Token).ConfigureAwait(false);
            var floor = store is null ? long.MaxValue : await FloorAsync(store, handle, linked.Token).ConfigureAwait(false);
            if (floor == long.MaxValue)
            {
                return false;
            }

            if (await cleanup.ProvesAsync(database, handle, floor, epoch, keyId, Now, linked.Token).ConfigureAwait(false))
            {
                return true;
            }

            var owner = ShapeObjectStore.Owner(database);
            var scanned = 0;
            await foreach (var stored in objects.ListAsync(handle, null, linked.Token).ConfigureAwait(false))
            {
                if (++scanned > ScanItems)
                {
                    break;
                }

                var head = await RetentionHeadAsync(stored.Key, linked.Token).ConfigureAwait(false);
                if (head is not null && head.Owner == owner && head.Epoch == epoch && head.KeyId == keyId && Retained(head, floor))
                {
                    return await AdoptAsync(database, store!, stored.Key, head, linked.Token).ConfigureAwait(false);
                }
            }
        }
        catch (Exception error) when (!cancellationToken.IsCancellationRequested && RemoteFailure(error))
        {
            logger.ZLogWarning(error, $"Shape mirror could not prove a retained key pair");
        }

        return false;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.WhenAll(Enumerable.Range(0, Workers).Select(_ => Task.Run(() => DrainAsync(stoppingToken), stoppingToken))).ConfigureAwait(false);
        }
        finally
        {
            _queue.Writer.TryComplete();
            while (_queue.Reader.TryRead(out var pending))
            {
                Release(pending);
            }

            ClearCaches();
        }
    }

    private async Task DrainAsync(CancellationToken stoppingToken)
    {
        await foreach (var pending in _queue.Reader.ReadAllAsync(stoppingToken).ConfigureAwait(false))
        {
            try
            {
                for (var attempt = 1; ; attempt++)
                {
                    try
                    {
                        await MirrorAsync(pending, stoppingToken).ConfigureAwait(false);
                        break;
                    }
                    catch (Exception error) when (!stoppingToken.IsCancellationRequested && !pending.AdoptOnly && attempt < MirrorAttempts && Transient(error))
                    {
                        logger.ZLogInformation(error, $"Shape mirror attempt {attempt} of {MirrorAttempts} failed; it is tried again");
                        await Task.Delay(RetryDelay * attempt, time, stoppingToken).ConfigureAwait(false);
                    }
                }
            }
            catch (ShapeCleanupSaturatedException)
            {
                logger.ZLogError($"Shape mirror publication refused: the durable cleanup inventory is full");
            }
            catch (Exception error) when (!stoppingToken.IsCancellationRequested)
            {
                logger.ZLogWarning(error, $"Shape mirror failed; immutable origin bytes remain available");
            }
            finally
            {
                Release(pending);
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
            foreach (var missing in _missing.Values)
            {
                missing.Admission.Dispose();
            }

            _missing.Clear();
        }
    }

    public override void Dispose()
    {
        base.Dispose();
        ClearCaches();
    }

    private Task MirrorAsync(Pending pending, CancellationToken cancellationToken) =>
        !pending.AdoptOnly && pending.Body.IsEmpty && pending.Offset.StartsWith("snap.", StringComparison.Ordinal)
            ? MirrorSnapshotAsync(pending, cancellationToken)
            : MirrorBodyAsync(pending, cancellationToken);

    private async Task MirrorSnapshotAsync(Pending pending, CancellationToken cancellationToken)
    {
        var store = await engine.OpenDatabaseAsync(pending.Database, cancellationToken).ConfigureAwait(false);
        if (store is null)
        {
            return;
        }

        string? after = null;
        do
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30), time);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token);
            ShapeSnapshotPage? page;
            await using (var transaction = await storage.BeginTransactionAsync(store, write: false, linked.Token).ConfigureAwait(false))
            {
                page = await ((IShapeSnapshotStorage)transaction).ReadShapeSnapshotPageAsync(pending.Handle,
                    pending.CoveredOffset, after, memory, linked.Token).ConfigureAwait(false);
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
        if (objects is null || await engine.OpenDatabaseAsync(pending.Database, cancellationToken).ConfigureAwait(false) is not { } store)
        {
            return;
        }

        var key = ShapeObjectStore.Key(pending.ShapeId, pending.Handle, pending.Offset);
        var floor = await FloorAsync(store, pending.Handle, cancellationToken).ConfigureAwait(false);
        var floorOffset = long.TryParse(pending.Offset, NumberStyles.None, CultureInfo.InvariantCulture, out var at) ? at : pending.CoveredOffset;
        if (!pending.AdoptOnly && (floorOffset < floor || pending.SealedAt < time.GetUtcNow() - FeedCursor.Retention))
        {
            return;
        }

        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30), time);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token);
        var owner = ShapeObjectStore.Owner(pending.Database);
        var head = await objects.HeadAsync(key, linked.Token).ConfigureAwait(false);
        var uploaded = false;
        if (head is null && pending.AdoptOnly)
        {
            RememberMissing(key, pending.Database);
            return;
        }

        if (head is not null)
        {
            if (head.Owner != owner || !await ReserveAsync(pending.Database, store, key, head, linked.Token).ConfigureAwait(false))
            {
                return;
            }
        }
        else
        {
            using var cipher = await crypto().EncryptAsync(pending.Database, pending.Handle, pending.Offset,
                pending.Body, linked.Token).ConfigureAwait(false);
            var planned = new ShapeObjectHead(owner, key[(key.LastIndexOf('/') + 1)..], Convert.ToHexStringLower(SHA256.HashData(cipher.Bytes.Span)),
                cipher.Bytes.Length, cipher.Epoch, cipher.KeyId, pending.SealedAt.ToUnixTimeMilliseconds(), floorOffset);
            if (!await ReserveAsync(pending.Database, store, key, planned, linked.Token).ConfigureAwait(false))
            {
                return;
            }

            try
            {
                head = await objects.PutAsync(key, planned, cipher.Bytes, linked.Token).ConfigureAwait(false);
            }
            catch (InvalidDataException)
            {
                await cleanup.ForgetAsync(key, pending.Database, CancellationToken.None).ConfigureAwait(false);
                throw;
            }

            uploaded = ReferenceEquals(head, planned);
        }

        floor = await FloorAsync(store, pending.Handle, linked.Token).ConfigureAwait(false);
        if (floor == long.MaxValue || !Retained(head, floor))
        {
            _ = await cleanup.ActivateObjectAsync(key, floor == long.MaxValue ? ShapeCleanupReason.Database : ShapeCleanupReason.Floor,
                Now, CancellationToken.None).ConfigureAwait(false);
            worker().Wake();
            return;
        }

        if (!await cleanup.PublishAsync(key, head.Epoch, head.KeyId, Now, linked.Token).ConfigureAwait(false))
        {
            return;
        }

        lock (_gate)
        {
            if (_cache.Count >= CacheItems)
            {
                Remove(_cache.Keys.First());
            }

            var admission = memory.TryReserve(MemoryLane.Bulk, 2048L + pending.NextOffset.Length * 2L, pending.Database);
            if (admission is not null)
            {
                Remove(key);
                _cache[key] = new(head, pending.NextOffset, admission);
            }
        }

        if (uploaded)
        {
            metrics.RecordMirrorLag(TimeSpan.FromMilliseconds(Math.Max(0, time.GetUtcNow().ToUnixTimeMilliseconds() - head.SealedAt)));
        }
    }

    private async ValueTask<bool> ReserveAsync(TenantContext database, IDatabaseStore store, string key, ShapeObjectHead head, CancellationToken cancellationToken) =>
        await cleanup.ReserveAsync(new(key, database, store.Incarnation, head.FloorOffset, head.Epoch, head.KeyId,
            head.SealedAt + (long)FeedCursor.Retention.TotalMilliseconds), Now, cancellationToken).ConfigureAwait(false) switch
        {
            ShapeCleanupAdmission.Live => true,
            ShapeCleanupAdmission.Saturated => throw new ShapeCleanupSaturatedException(),
            _ => false,
        };

    private async ValueTask<bool> AdoptAsync(TenantContext database, IDatabaseStore store, string key, ShapeObjectHead head, CancellationToken cancellationToken) =>
        await ReserveAsync(database, store, key, head, cancellationToken).ConfigureAwait(false)
            && await cleanup.PublishAsync(key, head.Epoch, head.KeyId, Now, cancellationToken).ConfigureAwait(false);

    private async ValueTask<int> ScheduleAsync(TenantContext database, string handle, long floor, ShapeCleanupReason reason, CancellationToken cancellationToken)
    {
        var scheduled = await cleanup.ActivateHandleAsync(database, handle, floor, reason, Now, cancellationToken).ConfigureAwait(false);
        if (scheduled > 0)
        {
            worker().Wake();
        }

        return scheduled;
    }

    private async ValueTask<long> FloorAsync(TenantContext database, string handle, CancellationToken cancellationToken) =>
        await engine.OpenDatabaseAsync(database, cancellationToken).ConfigureAwait(false) is { } store
            ? await FloorAsync(store, handle, cancellationToken).ConfigureAwait(false) : long.MaxValue;

    private async ValueTask<long> FloorAsync(IDatabaseStore store, string handle, CancellationToken cancellationToken)
    {
        await using var transaction = await storage.BeginTransactionAsync(store, write: false, cancellationToken).ConfigureAwait(false);
        return await ((IShapeRetentionTransaction)transaction).ReadShapeFloorAsync(handle, cancellationToken).ConfigureAwait(false);
    }

    private async Task<ShapeObjectHead?> RetentionHeadAsync(string key, CancellationToken cancellationToken)
    {
        try
        {
            return await objects!.HeadAsync(key, cancellationToken).ConfigureAwait(false);
        }
        catch (InvalidDataException)
        {
            return null;
        }
    }

    private long Now => time.GetUtcNow().ToUnixTimeMilliseconds();

    private bool Retained(ShapeObjectHead head, long floor) => head.FloorOffset >= floor
        && head.SealedAt >= (time.GetUtcNow() - FeedCursor.Retention).ToUnixTimeMilliseconds();

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

            if (memory.TryReserve(MemoryLane.Bulk, 1024, database) is { } admission)
            {
                _missing[key] = new(time.GetUtcNow() + TimeSpan.FromSeconds(30), admission);
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

    private static bool Transient(Exception error) => error is IOException and not ShapeCleanupSaturatedException or HttpRequestException
        or OperationCanceledException or StorageBusyException
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
    private sealed record Cached(ShapeObjectHead Head, string NextOffset, IMemoryReservation Admission);
    private sealed record Missing(DateTimeOffset RetryAfter, IMemoryReservation Admission);
}

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

    public ShapeObjectStore(ShapeStorageSettings settings, IHttpClientFactory http, TimeProvider time)
    {
        _settings = settings;
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

    internal bool Cdn => _cdn is not null;

    public Task<bool> DeleteAsync(string key, CancellationToken cancellationToken) => _objects.DeleteAsync(key, cancellationToken);

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

    public (string Url, DateTimeOffset ExpiresAt) Link(string key)
    {
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
