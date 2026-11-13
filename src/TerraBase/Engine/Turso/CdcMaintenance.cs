using System.Diagnostics;
using ZLogger;

namespace TerraBase.Engine.Turso;

internal sealed class CdcMaintenance(TursoStorageEngine engine, TimeProvider time, ILogger<CdcMaintenance> logger) : BackgroundService
{
    private readonly Dictionary<StorageFaultLedger.Key, string> _refused = [];

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(30), time);
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
        {
            _ = await PassAsync(stoppingToken).ConfigureAwait(false);
        }
    }

    internal async Task<IdlePass> PassAsync(CancellationToken cancellationToken)
    {
        var started = Stopwatch.GetTimestamp();
        var listed = 0;
        var opened = 0;
        HashSet<StorageFaultLedger.Key> seen = [];
        await foreach (var database in engine.ListDatabasesAsync(cancellationToken).ConfigureAwait(false))
        {
            listed++;
            var key = StorageFaultLedger.Key.Of(database);
            seen.Add(key);
            if (engine.Ledger.MemoryRefusal(database) is { } refusal)
            {
                Refused(key, refusal);
                continue;
            }
            if (engine.IdleSettled(database))
            {
                continue;
            }
            try
            {
                opened++;
                var resident = engine.OpenStore(key) is not null;
                if (await engine.OpenDatabaseAsync(database, cancellationToken).ConfigureAwait(false) is TursoDatabaseStore store)
                {
                    await store.TrimIdleAsync(cancellationToken).ConfigureAwait(false);
                    if (!resident)
                    {
                        await store.EvictWriterAsync().ConfigureAwait(false);
                    }
                    if (store.IdleClean)
                    {
                        engine.NoteIdleClean(database);
                    }
                    if (engine.Ledger.Refusal(store) is { } refused)
                    {
                        Refused(key, refused);
                    }
                    else
                    {
                        _ = _refused.Remove(key);
                    }
                }
            }
            catch (StorageCopyFaultedException error)
            {
                Refused(key, error.Message);
            }
            catch (StorageProofPendingException)
            {
                Refused(key, "this copy has a pending proof");
            }
            catch (Exception error) when (error is not OperationCanceledException and not OutOfMemoryException)
            {
                logger.ZLogWarning(error, $"Idle change history maintenance failed");
            }
        }
        foreach (var gone in _refused.Keys.Where(key => !seen.Contains(key)).ToList())
        {
            _ = _refused.Remove(gone);
        }
        return new IdlePass(listed, opened, Stopwatch.GetElapsedTime(started));
    }

    private void Refused(StorageFaultLedger.Key key, string refusal)
    {
        if (!_refused.TryGetValue(key, out var previous) || previous != refusal)
        {
            _refused[key] = refusal;
            logger.ZLogWarning($"Idle change history maintenance skips a refused copy until its state changes: {refusal:@Refusal}");
        }
    }

    internal readonly record struct IdlePass(int Listed, int Opened, TimeSpan Elapsed);
}
