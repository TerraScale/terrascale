using System.Diagnostics.Metrics;
using TerraBase.Engine;
using TerraBase.Features.Catalog;
using TerraBase.Hosting;
using ZLogger;

namespace TerraBase.Features.Backup;

internal sealed class KeyRewrap(
    IKeyring keyring, IEnumerable<IWrappedKeyFiles> files, CredentialKeyRewrap credentials,
    IMeterFactory meters, TimeProvider time, ILogger<KeyRewrap> logger, ICatalogGate gate, KeyCustody? custody = null) : BackgroundService
{
    private readonly Meter _meter = meters.Create(Telemetry.MeterName);
    private IReadOnlyDictionary<string, long>? _counts;

    protected override async Task ExecuteAsync(CancellationToken cancellationToken)
    {
        _meter.CreateObservableGauge("terrabase.keys.wrapped", Observe, description: "Wrapped items per configured key id after a complete inventory and rewrap pass.");
        await gate.Opened.WaitAsync(cancellationToken).ConfigureAwait(false);
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1), time);
        do
        {
            Volatile.Write(ref _counts, null);
            try
            {
                Dictionary<string, long> counts = keyring.KeyIds.ToDictionary(id => id, _ => 0L, StringComparer.Ordinal);
                foreach (var source in files)
                {
                    foreach (var (id, count) in await source.RewrapAsync(cancellationToken).ConfigureAwait(false))
                    {
                        counts[id] += count;
                    }
                }

                if (custody is not null)
                {
                    await foreach (var item in custody.Store.ListAsync("v1/keys/", null, cancellationToken).ConfigureAwait(false))
                    {
                        if (item.Key.Length == 0 || item.Key.EndsWith("/" + BackupLayout.ShreddedName, StringComparison.Ordinal))
                        {
                            continue;
                        }
                        if (await custody.Store.RewrapKeyAsync(item.Key, keyring, cancellationToken).ConfigureAwait(false) is { } id)
                        {
                            counts[id] += 1;
                        }
                    }
                }
                foreach (var (id, count) in await credentials.RunAsync(cancellationToken).ConfigureAwait(false))
                {
                    counts[id] += count;
                }
                Volatile.Write(ref _counts, counts);
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
        : [];

    public override void Dispose()
    {
        _meter.Dispose();
        base.Dispose();
    }
}
