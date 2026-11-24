using System.Runtime.InteropServices;

namespace TerraScale.Platform.Features.Shapes;

public sealed class ShapeNotifier(TimeProvider timeProvider)
{
    private static readonly TimeSpan IdleFor = TimeSpan.FromMinutes(1);
    private readonly Lock _gate = new();
    private readonly Dictionary<Ulid, Feed> _feeds = [];
    private long _generation;
    private long _sweptAt = timeProvider.GetTimestamp();

    public long Version(Ulid database)
    {
        lock (_gate)
        {
            return Touch(database).Version;
        }
    }

    public async Task<bool> WaitAsync(Ulid database, long seen, TimeSpan timeout, CancellationToken cancellationToken)
    {
        Task signal;
        Feed feed;
        lock (_gate)
        {
            feed = Touch(database);
            if (feed.Version != seen)
            {
                return true;
            }

            feed.Waiters++;
            signal = feed.Signal.Task;
        }

        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        await Task.WhenAny(signal, Task.Delay(timeout, timeProvider, stop.Token));
        await stop.CancelAsync();
        lock (_gate)
        {
            feed.Waiters--;
            feed.TouchedAt = timeProvider.GetTimestamp();
            return feed.Version != seen;
        }
    }

    public void Signal(Ulid database)
    {
        TaskCompletionSource? fired = null;
        lock (_gate)
        {
            if (_feeds.TryGetValue(database, out var feed))
            {
                feed.Version = ++_generation;
                fired = feed.Signal;
                feed.Signal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            }

            Sweep();
        }

        _ = fired?.TrySetResult();
    }

    private Feed Touch(Ulid database)
    {
        Sweep();
        ref var feed = ref CollectionsMarshal.GetValueRefOrAddDefault(_feeds, database, out _);
        feed ??= new Feed(++_generation);
        feed.TouchedAt = timeProvider.GetTimestamp();
        return feed;
    }

    private void Sweep()
    {
        if (timeProvider.GetElapsedTime(_sweptAt) < IdleFor)
        {
            return;
        }

        _sweptAt = timeProvider.GetTimestamp();
        foreach (var (database, feed) in _feeds.Where(entry => entry.Value.Waiters == 0 && timeProvider.GetElapsedTime(entry.Value.TouchedAt) >= IdleFor).ToArray())
        {
            _ = _feeds.Remove(database);
        }
    }

    private sealed class Feed(long version)
    {
        public long Version { get; set; } = version;

        public int Waiters { get; set; }

        public long TouchedAt { get; set; }

        public TaskCompletionSource Signal { get; set; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
