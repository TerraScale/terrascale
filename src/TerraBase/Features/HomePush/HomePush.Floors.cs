using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using NATS.Client.Core;
using ZLogger;

namespace TerraBase.Features.HomePush;

internal partial class HomePush
{
    private readonly ConcurrentDictionary<string, string> _sources = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<(string Source, string Database), PurgeFloor> _purgeFloors = new();

    private sealed class PurgeFloor
    {
        internal Lock Gate { get; } = new();
        internal ulong Through { get; set; }
        internal ulong? Applying { get; set; }
    }

    private PurgeFloor FloorOf(string source, string database) => _purgeFloors.GetOrAdd((source, database), static _ => new PurgeFloor());

    private void Sourced(string origin, string source) => _sources[origin] = source;

    private void Purged(string origin, string source, string database, ulong through)
    {
        var floor = FloorOf(source, database);
        ulong? covered;
        lock (floor.Gate)
        {
            if (through <= floor.Through)
            {
                return;
            }

            floor.Through = through;
            covered = floor.Applying is { } applying && applying <= through ? applying : null;
        }

        if (covered is { } from)
        {
            logger.ZLogWarning($"A purge marker of {origin:@Origin} through stream sequence {through:@Through} covers pushes of {database:@Database} this node is applying from stream sequence {from:@Sequence}; none of them applies after this point");
        }
    }

    private bool Admits(string source, string database, ulong sequence)
    {
        var floor = FloorOf(source, database);
        lock (floor.Gate)
        {
            floor.Applying = sequence;
            return sequence > floor.Through;
        }
    }

    private void Settled(string source, string database)
    {
        var floor = FloorOf(source, database);
        lock (floor.Gate)
        {
            floor.Applying = null;
        }
    }

    private async Task PurgeMarkersAsync(NatsConnection nats, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await foreach (var message in nats.SubscribeAsync<byte[]>(PushNames.PurgedAll, cancellationToken: cancellationToken).ConfigureAwait(false))
                {
                    if (PushNames.Origin(message.Subject) is { } origin && PushNames.Database(message.Subject) is { DatabaseId: { } database }
                        && message.Data is { } data && _sources.TryGetValue(origin, out var source)
                        && ulong.TryParse(Encoding.ASCII.GetString(data), NumberStyles.None, CultureInfo.InvariantCulture, out var through))
                    {
                        Purged(origin, source, database, through);
                    }
                }
            }
            catch (Exception error) when (error is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                logger.ZLogInformation($"Purge markers resubscribe: {error.GetType().Name:@Error}");
                await Task.Delay(TimeSpan.FromSeconds(1), time, cancellationToken).ConfigureAwait(false);
            }
        }
    }
}
