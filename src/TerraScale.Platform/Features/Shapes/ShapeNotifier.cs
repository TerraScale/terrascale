using System.Collections.Concurrent;

namespace TerraScale.Platform.Features.Shapes;

public sealed class ShapeNotifier(TimeProvider timeProvider)
{
    private readonly ConcurrentDictionary<Ulid, Feed> _feeds = new();

    public long Version(Ulid database) => FeedOf(database).Current().Version;

    public async Task<bool> WaitAsync(Ulid database, long seen, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var feed = FeedOf(database);
        var (version, signal) = feed.Current();
        if (version != seen)
        {
            return true;
        }

        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        await Task.WhenAny(signal.Task, Task.Delay(timeout, timeProvider, stop.Token));
        await stop.CancelAsync();
        return feed.Current().Version != seen;
    }

    public void Signal(Ulid database) => FeedOf(database).Advance();

    private Feed FeedOf(Ulid database) => _feeds.GetOrAdd(database, static _ => new Feed());

    private sealed class Feed
    {
        private readonly Lock _gate = new();
        private long _version;
        private TaskCompletionSource _signal = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public (long Version, TaskCompletionSource Signal) Current()
        {
            lock (_gate)
            {
                return (_version, _signal);
            }
        }

        public void Advance()
        {
            TaskCompletionSource fired;
            lock (_gate)
            {
                _version++;
                fired = _signal;
                _signal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            }

            _ = fired.TrySetResult();
        }
    }
}
