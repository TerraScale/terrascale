using System.Diagnostics.Metrics;
using Amazon.S3;
using TerraBase.Engine;
using TerraBase.Features.Catalog;
using TerraBase.Features.Replicas;
using TerraBase.Hosting;
using ZLogger;

namespace TerraBase.Features.Backup;

internal sealed class KeyRewrap(
    IKeyring keyring, IStorageEngine storage, IEnumerable<IWrappedKeyFiles> files, CredentialKeyRewrap credentials,
    IMeterFactory meters, TimeProvider time, ILogger<KeyRewrap> logger, ICatalogGate gate, RegionalOwners owners, KeyRewrapFaults faults, KeyCustody? custody = null) : BackgroundService
{
    private readonly Meter _meter = meters.Create(Telemetry.MeterName);
    private readonly HashSet<string> _logged = new(StringComparer.Ordinal);
    private IReadOnlyDictionary<string, long>? _counts;
    private long _lastComplete;
    private long _unreadable;

    protected override async Task ExecuteAsync(CancellationToken cancellationToken)
    {
        _meter.CreateObservableGauge("terrabase.keys.wrapped", Observe, description: "Wrapped items per key id; unreadable items block retirement.");
        _meter.CreateObservableGauge("terrabase.keys.last_complete", () => Interlocked.Read(ref _lastComplete), "s",
            "Unix time of the last complete writer inventory, including unreadable items; zero until the first writer completion.");
        if (keyring.KeyIds.Count == 1)
        {
            Volatile.Write(ref _counts, keyring.KeyIds.ToDictionary(id => id, _ => -1L, StringComparer.Ordinal));
            return;
        }
        await gate.Opened.WaitAsync(cancellationToken).ConfigureAwait(false);
        using var timer = new PeriodicTimer(TimeSpan.FromHours(1), time);
        do
        {
            Volatile.Write(ref _counts, null);
            try
            {
                var writer = owners.Writer;
                var inventory = new KeyInventory(logger, _logged, faults);
                foreach (var id in keyring.KeyIds)
                {
                    inventory.Counts[id] = 0;
                }
                foreach (var source in files)
                {
                    await source.RewrapAsync(inventory, cancellationToken).ConfigureAwait(false);
                }
                if (writer && custody is not null)
                {
                    await foreach (var item in custody.Store.ListAsync("v1/keys/", null, cancellationToken).ConfigureAwait(false))
                    {
                        if (item.Key.EndsWith("/" + BackupLayout.ShreddedName, StringComparison.Ordinal))
                        {
                            continue;
                        }
                        try
                        {
                            if (item.Key.Length == 0)
                            {
                                throw new InvalidDataException("custody key path is malformed");
                            }
                            if (await custody.Store.RewrapKeyAsync(item.Key, keyring, cancellationToken).ConfigureAwait(false) is { } id)
                            {
                                inventory.Count(id);
                            }
                        }
                        catch (Exception error) when (error is IOException or InvalidDataException or HttpRequestException or AmazonS3Exception)
                        {
                            inventory.Unreadable(item.Key);
                        }
                    }
                }
                if (writer)
                {
                    await credentials.RunAsync(inventory, cancellationToken).ConfigureAwait(false);
                }
                await storage.InventoryKeysAsync(cancellationToken).ConfigureAwait(false);
                Interlocked.Exchange(ref _unreadable, inventory.UnreadableCount);
                Volatile.Write(ref _counts, writer ? inventory.Counts
                    : keyring.KeyIds.ToDictionary(id => id, _ => -1L, StringComparer.Ordinal));
                if (writer)
                {
                    Interlocked.Exchange(ref _lastComplete, time.GetUtcNow().ToUnixTimeSeconds());
                }
            }
            catch (Exception error) when (error is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                logger.ZLogError(error, $"Wrapped key inventory and rotation failed; retirement counts are unavailable");
            }
        }
        while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false));
    }

    private IEnumerable<Measurement<long>> Observe() => Volatile.Read(ref _counts) is { } counts
        ? counts.Select(pair => new Measurement<long>(pair.Value, new KeyValuePair<string, object?>("key_id", pair.Key)))
            .Append(new Measurement<long>(Interlocked.Read(ref _unreadable) + faults.Count, new KeyValuePair<string, object?>("status", "unreadable")))
        : [new Measurement<long>(faults.Count, new KeyValuePair<string, object?>("status", "unreadable"))];

    public override void Dispose()
    {
        _meter.Dispose();
        base.Dispose();
    }
}
