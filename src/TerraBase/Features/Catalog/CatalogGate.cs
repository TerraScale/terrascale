using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Text.Json;
using Amazon.Runtime;
using TerraBase.Configuration;
using TerraBase.Engine;
using TerraBase.Errors;
using TerraBase.Features.Backup;
using TerraBase.Hosting;
using ZLogger;

namespace TerraBase.Features.Catalog;

public interface ICatalogGate
{
    bool IsOpen { get; }
    Task Opened { get; }
    Task<bool> TryAnchorAsync(CancellationToken cancellationToken);
    CatalogGateStatus Status { get; }
}

public enum CatalogGateState
{
    Unverified = 0,

    Open = 1,

    BucketMissing = 2,

    StoreUnavailable = 3,

    AnchorAhead = 4,

    AnchorMissing = 5,

    AnchoringUnavailable = 6,
}

public sealed record CatalogGateStatus(CatalogGateState State, string? Bucket = null)
{
    public bool IsOpen => State == CatalogGateState.Open;
}

public sealed class AnchoredRevocation
{
    private AnchoredRevocation()
    {
    }

    public static AnchoredRevocation Instance { get; } = new();
}

internal sealed class CatalogGate(
    IStorageEngine engine, TerraBaseOptions options, TimeProvider time, ILogger<CatalogGate> logger, IMeterFactory meters,
    IObjectStore? anchors = null, DataKeys? keys = null) : BackgroundService, ICatalogGate
{
    internal static readonly Unavailable AnchorUnavailable = new(
        "the change is committed locally, but its restore anchor could not be written in time; retry", "catalog_anchor_unavailable");

    private static readonly TimeSpan AnchorTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan AnchorInterval = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan StoreRetry = TimeSpan.FromSeconds(1);
    private static readonly CatalogGateState[] ClosedReasons =
    [
        CatalogGateState.Unverified, CatalogGateState.BucketMissing, CatalogGateState.StoreUnavailable,
        CatalogGateState.AnchorAhead, CatalogGateState.AnchorMissing, CatalogGateState.AnchoringUnavailable,
    ];

    private readonly SemaphoreSlim _anchoring = new(1, 1);
    private readonly TaskCompletionSource _opened = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Counter<long> _bucketMissing = null!;
    private int _state;
    private string? _closure;
    private (string Operation, string Key) _step;
    private bool _started;
    private bool _fresh;
    private Ulid _startIncarnation;
    private long _startOffset;

    public bool IsOpen => State == CatalogGateState.Open;
    public Task Opened => _opened.Task;
    public CatalogGateStatus Status => State switch
    {
        CatalogGateState.Open => new(CatalogGateState.Open),
        var closed => new(closed, options.Backup?.Bucket),
    };
    private CatalogGateState State => (CatalogGateState)Volatile.Read(ref _state);
    private string AnchorKey => $"catalog-anchor/{options.MachineId}";
    private static string KeyScope => BackupLayout.KeyScope(null);

    public override async Task StartAsync(CancellationToken cancellationToken)
    {
        var meter = meters.Create(Telemetry.MeterName);
        meter.CreateObservableGauge("terrabase.catalog.verified", () => IsOpen ? 1 : 0,
            description: "One after the local catalog restore anchor has been verified.");
        meter.CreateObservableGauge("terrabase.catalog.bucket_missing", () => Status.State == CatalogGateState.BucketMissing ? 1 : 0,
            description: "One while the backup bucket holding the restore anchor does not exist.");
        meter.CreateObservableGauge("terrabase.catalog.closed", () => ClosedReasons.Select(reason =>
                new Measurement<int>(State == reason ? 1 : 0, new KeyValuePair<string, object?>("reason", Label(reason)))),
            description: "One for the reason the local catalog is closed, zero for the other reasons; all zero while it is open.");
        _bucketMissing = S3ObjectStore.BucketMissingCounter(meters);
        _fresh = !engine.SystemDatabaseExists;
        (_startIncarnation, _startOffset) = await PositionAsync(cancellationToken).ConfigureAwait(false);

        _started = true;
        await VerifyAsync(cancellationToken).ConfigureAwait(false);
        await base.StartAsync(cancellationToken).ConfigureAwait(false);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(NextCheck, time);
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
        {
            if (!IsOpen)
            {
                await VerifyAsync(stoppingToken).ConfigureAwait(false);
            }
            else
            {
                _ = await TryAnchorAsync(stoppingToken).ConfigureAwait(false);
            }

            timer.Period = NextCheck;
        }
    }

    private TimeSpan NextCheck => State switch
    {
        CatalogGateState.Unverified or CatalogGateState.StoreUnavailable => StoreRetry,
        CatalogGateState.Open or CatalogGateState.BucketMissing or CatalogGateState.AnchorAhead
            or CatalogGateState.AnchorMissing or CatalogGateState.AnchoringUnavailable => AnchorInterval,
        _ => throw new UnreachableException(),
    };

    private async Task VerifyAsync(CancellationToken cancellationToken)
    {
        using var deadline = new CancellationTokenSource(AnchorTimeout, time);
        using var bounded = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token);
        try
        {
            if (anchors is null || keys is null)
            {
                if (options.Backup is not null)
                {
                    if (Close(CatalogGateState.AnchoringUnavailable, string.Empty))
                    {
                        logger.ZLogError($"Catalog restore anchoring is unavailable while backup is configured; the catalog stays closed until the node starts with its backup store");
                    }

                    return;
                }
            }
            else
            {
                Ulid incarnation = default;
                long offset = -1;
                _step = ("read", AnchorKey);
                var found = await anchors.ReadAsync(AnchorKey, async (stream, token) =>
                {
                    using var plain = new AnchorBody();
                    await BackupObject.ReadAsync(stream, plain, AnchorKey, id =>
                    {
                        _step = ("read", BackupLayout.Key(KeyScope, id));
                        return keys.GetAsync(KeyScope, id, token);
                    }, token).ConfigureAwait(false);
                    using var document = JsonDocument.Parse(plain.ToArray());
                    var root = document.RootElement;
                    if (root.GetProperty("version").GetInt32() != 2
                        || !Ulid.TryParse(root.GetProperty("incarnation").GetString(), out incarnation)
                        || incarnation == Ulid.Empty || (offset = root.GetProperty("offset").GetInt64()) < 0)
                    {
                        throw new InvalidDataException("invalid catalog restore anchor");
                    }
                }, bounded.Token).ConfigureAwait(false);
                if (found && (incarnation != _startIncarnation || offset > _startOffset))
                {
                    if (Close(CatalogGateState.AnchorAhead, $"{incarnation}:{offset}"))
                    {
                        logger.ZLogError($"The catalog restore anchor {AnchorKey:@Key} is at incarnation {incarnation:@AnchorIncarnation} offset {offset:@AnchorOffset}, ahead of the local catalog at incarnation {_startIncarnation:@Incarnation} offset {_startOffset:@Offset}; the catalog stays closed until it is restored from backup");
                    }

                    return;
                }

                if (!found && !_fresh)
                {
                    if (Close(CatalogGateState.AnchorMissing, string.Empty))
                    {
                        logger.ZLogError($"The catalog restore anchor {AnchorKey:@Key} is missing from bucket {options.Backup?.Bucket:@Bucket} while this node has a catalog; the catalog stays closed until the anchor is restored or the node starts from a restore");
                    }

                    return;
                }

                await AnchorAsync(bounded.Token).ConfigureAwait(false);
            }

            if (_closure is not null)
            {
                logger.ZLogInformation($"The catalog restore anchor is verified; the catalog is open");
            }

            Volatile.Write(ref _state, (int)CatalogGateState.Open);
            _closure = null;
            _opened.TrySetResult();
        }
        catch (ObjectStoreBucketMissingException)
        {
            _bucketMissing.Add(1);
            if (Close(CatalogGateState.BucketMissing, string.Empty))
            {
                logger.ZLogError($"The backup bucket does not exist; the catalog restore anchor cannot be read or written (bucket {options.Backup?.Bucket:@Bucket})");
            }
        }
        catch (Exception error) when (!cancellationToken.IsCancellationRequested)
        {
            var (code, status) = error is AmazonServiceException service ? (service.ErrorCode, (int)service.StatusCode) : (null, 0);
            if (Close(CatalogGateState.StoreUnavailable, $"{error.GetType().Name}:{code}:{status}:{_step}"))
            {
                logger.ZLogWarning($"Catalog restore verification failed ({error.GetType().Name:@Error} {code:@ErrorCode}, HTTP status {status:@Status}) on {_step.Operation:@Operation} of {_step.Key:@Key}; the catalog remains closed and every pass retries");
            }
        }
    }

    private bool Close(CatalogGateState state, string detail)
    {
        Volatile.Write(ref _state, (int)state);
        var closure = $"{Label(state)}:{detail}";
        if (closure == _closure)
        {
            return false;
        }

        _closure = closure;
        return true;
    }

    private static string Label(CatalogGateState state) => state switch
    {
        CatalogGateState.Unverified => "unverified",
        CatalogGateState.Open => "open",
        CatalogGateState.BucketMissing => "bucket_missing",
        CatalogGateState.StoreUnavailable => "store_unavailable",
        CatalogGateState.AnchorAhead => "anchor_ahead",
        CatalogGateState.AnchorMissing => "anchor_missing",
        CatalogGateState.AnchoringUnavailable => "anchoring_unavailable",
        _ => throw new UnreachableException(),
    };

    public async Task<bool> TryAnchorAsync(CancellationToken cancellationToken)
    {
        if (!_started || !IsOpen)
        {
            return false;
        }

        using var deadline = new CancellationTokenSource(AnchorTimeout, time);
        using var bounded = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token);
        try
        {
            await AnchorAsync(bounded.Token).ConfigureAwait(false);
            return true;
        }
        catch (Exception error) when (!cancellationToken.IsCancellationRequested)
        {
            logger.ZLogWarning($"Catalog anchor write failed ({error.GetType().Name:@Error}); the change is not acknowledged");
            return false;
        }
    }

    private async Task AnchorAsync(CancellationToken cancellationToken)
    {
        if (anchors is null || keys is null)
        {
            if (options.Backup is not null)
            {
                throw new IOException("catalog restore anchoring is unavailable");
            }

            return;
        }

        await _anchoring.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _step = ("position", "_system");
            var (incarnation, offset) = await PositionAsync(cancellationToken).ConfigureAwait(false);
            using var body = new MemoryStream();
            await using (var json = new Utf8JsonWriter(body))
            {
                json.WriteStartObject();
                json.WriteNumber("version", 2);
                json.WriteString("incarnation", incarnation.ToString());
                json.WriteNumber("offset", offset);
                json.WriteEndObject();
            }

            body.Position = 0;
            _step = ("current data key", KeyScope);
            var (keyId, dataKey) = await keys.CurrentAsync(KeyScope, cancellationToken).ConfigureAwait(false);
            using var sealedAnchor = new MemoryStream();
            await BackupObject.WriteAsync(body, sealedAnchor, AnchorKey, keyId, dataKey, cancellationToken).ConfigureAwait(false);
            sealedAnchor.Position = 0;
            _step = ("write", AnchorKey);
            await anchors.PutAsync(AnchorKey, sealedAnchor, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _anchoring.Release();
        }
    }

    private async Task<(Ulid Incarnation, long Offset)> PositionAsync(CancellationToken cancellationToken)
    {
        var store = await engine.OpenSystemDatabaseAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await store.BeginTransactionAsync(write: false, cancellationToken).ConfigureAwait(false);
        return (await transaction.GetIncarnationAsync(cancellationToken).ConfigureAwait(false),
            await transaction.GetOffsetAsync(cancellationToken).ConfigureAwait(false));
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken).ConfigureAwait(false);
        if (IsOpen)
        {
            try
            {
                _ = await TryAnchorAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
            }
        }
    }

    public override void Dispose()
    {
        _anchoring.Dispose();
        base.Dispose();
    }

    private sealed class AnchorBody : MemoryStream
    {
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (Position + buffer.Length > 4096)
            {
                throw new InvalidDataException("the catalog anchor exceeds its byte limit");
            }

            return base.WriteAsync(buffer, cancellationToken);
        }
    }
}
