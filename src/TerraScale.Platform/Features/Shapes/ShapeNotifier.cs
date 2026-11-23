using System.Collections.Concurrent;

namespace TerraScale.Platform.Features.Shapes;

public sealed class ShapeNotifier(TimeProvider timeProvider)
{
    private readonly ConcurrentDictionary<Ulid, TaskCompletionSource> _waits = new();

    public async Task WaitAsync(Ulid database, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var signal = _waits.GetOrAdd(database, static _ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        await Task.WhenAny(signal.Task, Task.Delay(timeout, timeProvider, stop.Token));
        await stop.CancelAsync();
    }

    public void Signal(Ulid database)
    {
        if (_waits.TryRemove(database, out var signal))
        {
            signal.TrySetResult();
        }
    }
}
