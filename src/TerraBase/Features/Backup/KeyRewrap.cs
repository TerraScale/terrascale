using System.Diagnostics.Metrics;
using Amazon.S3;
using TerraBase.Engine;
using TerraBase.Features.Catalog;
using TerraBase.Hosting;
using ZLogger;

namespace TerraBase.Features.Backup;

internal sealed class KeyRewrap(
    IKeyring keyring, IEnumerable<IWrappedKeyFiles> files, CredentialKeyRewrap credentials, KeySnapshotInventory snapshots,
    IMeterFactory meters, TimeProvider time, ILogger<KeyRewrap> logger, ICatalogGate gate, KeyCustody? custody = null) : BackgroundService
{
    private readonly Meter _meter = meters.Create(Telemetry.MeterName);
    private readonly HashSet<string> _logged = new(StringComparer.Ordinal);
    private IReadOnlyDictionary<string, long>? _counts;
    private IReadOnlyDictionary<string, double>? _snapshotAges;
    private long _lastComplete;
    private long _unreadable;

    protected override async Task ExecuteAsync(CancellationToken cancellationToken)
    {
        _meter.CreateObservableGauge("terrabase.keys.wrapped", Observe, description: "Wrapped items per key id; unreadable items block retirement.");
        _meter.CreateObservableGauge("terrabase.keys.last_complete", () => Interlocked.Read(ref _lastComplete), "s",
            "Unix time of the last complete inventory, including unreadable items; zero until the first completion.");
        _meter.CreateObservableGauge("terrabase.keys.oldest_snapshot_age", ObserveSnapshots, "s",
            "Age of the oldest retained platform snapshot embedding each key id; zero means none, minus one means unknown.");
        await gate.Opened.WaitAsync(cancellationToken).ConfigureAwait(false);
        using var timer = new PeriodicTimer(TimeSpan.FromHours(1), time);
        do
        {
            Volatile.Write(ref _counts, null);
            Volatile.Write(ref _snapshotAges, null);
            try
            {
                var inventory = new KeyInventory(logger, _logged);
                foreach (var id in keyring.KeyIds)
                {
                    inventory.Counts[id] = 0;
                }
                foreach (var source in files)
                {
                    await source.RewrapAsync(inventory, cancellationToken).ConfigureAwait(false);
                }
                if (custody is not null)
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
                await credentials.RunAsync(inventory, cancellationToken).ConfigureAwait(false);
                var ages = await snapshots.RunAsync(inventory, cancellationToken).ConfigureAwait(false);
                Interlocked.Exchange(ref _unreadable, inventory.UnreadableCount);
                Volatile.Write(ref _snapshotAges, ages);
                Volatile.Write(ref _counts, inventory.Counts);
                Interlocked.Exchange(ref _lastComplete, time.GetUtcNow().ToUnixTimeSeconds());
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
            .Append(new Measurement<long>(Interlocked.Read(ref _unreadable), new KeyValuePair<string, object?>("status", "unreadable")))
        : [];

    private IEnumerable<Measurement<double>> ObserveSnapshots() => Volatile.Read(ref _snapshotAges) is { } ages
        ? ages.Select(pair => new Measurement<double>(pair.Value, new KeyValuePair<string, object?>("key_id", pair.Key)))
        : keyring.KeyIds.Select(id => new Measurement<double>(-1, new KeyValuePair<string, object?>("key_id", id)));

    public override void Dispose()
    {
        _meter.Dispose();
        base.Dispose();
    }
}
