using System.Diagnostics.Metrics;
using System.Text.Json;
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
    private readonly SemaphoreSlim _anchoring = new(1, 1);
    private readonly TaskCompletionSource _opened = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private volatile bool _open;
    private bool _started;
    private bool _fresh;
    private Ulid _startIncarnation;
    private long _startOffset;

    public bool IsOpen => _open;
    public Task Opened => _opened.Task;
    private string AnchorKey => $"catalog-anchor/{options.MachineId}";
    private static string KeyScope => BackupLayout.KeyScope(null);

    public override async Task StartAsync(CancellationToken cancellationToken)
    {
        meters.Create(Telemetry.MeterName).CreateObservableGauge("terrabase.catalog.verified", () => IsOpen ? 1 : 0,
            description: "One after the local catalog restore anchor has been verified.");
        _fresh = !engine.SystemDatabaseExists;
        (_startIncarnation, _startOffset) = await PositionAsync(cancellationToken).ConfigureAwait(false);

        _started = true;
        await VerifyAsync(cancellationToken).ConfigureAwait(false);
        await base.StartAsync(cancellationToken).ConfigureAwait(false);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(15), time);
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
        {
            if (!_open)
            {
                await VerifyAsync(stoppingToken).ConfigureAwait(false);
            }
            else
            {
                _ = await TryAnchorAsync(stoppingToken).ConfigureAwait(false);
            }
        }
    }

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
                    return;
                }
            }
            else
            {
                Ulid incarnation = default;
                long offset = -1;
                var found = await anchors.ReadAsync(AnchorKey, async (stream, token) =>
                {
                    using var plain = new AnchorBody();
                    await BackupObject.ReadAsync(stream, plain, AnchorKey, id => keys.GetAsync(KeyScope, id, token), token).ConfigureAwait(false);
                    using var document = JsonDocument.Parse(plain.ToArray());
                    var root = document.RootElement;
                    if (root.GetProperty("version").GetInt32() != 2
                        || !Ulid.TryParse(root.GetProperty("incarnation").GetString(), out incarnation)
                        || incarnation == Ulid.Empty || (offset = root.GetProperty("offset").GetInt64()) < 0)
                    {
                        throw new InvalidDataException("invalid catalog restore anchor");
                    }
                }, bounded.Token).ConfigureAwait(false);
                if (found ? incarnation != _startIncarnation || offset > _startOffset : !_fresh)
                {
                    return;
                }

                await AnchorAsync(bounded.Token).ConfigureAwait(false);
            }

            _open = true;
            _opened.TrySetResult();
        }
        catch (Exception error) when (!cancellationToken.IsCancellationRequested)
        {
            logger.ZLogWarning($"Catalog restore verification failed ({error.GetType().Name:@Error}); the catalog remains closed");
        }
    }

    public async Task<bool> TryAnchorAsync(CancellationToken cancellationToken)
    {
        if (!_started || !_open)
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
            var (keyId, dataKey) = await keys.CurrentAsync(KeyScope, cancellationToken).ConfigureAwait(false);
            using var sealedAnchor = new MemoryStream();
            await BackupObject.WriteAsync(body, sealedAnchor, AnchorKey, keyId, dataKey, cancellationToken).ConfigureAwait(false);
            sealedAnchor.Position = 0;
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
        if (_open)
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
