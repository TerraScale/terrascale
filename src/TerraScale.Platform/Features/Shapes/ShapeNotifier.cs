using System.Runtime.InteropServices;

namespace TerraScale.Platform.Features.Shapes;

public sealed class ShapeNotifier(TimeProvider timeProvider)
{
    private static readonly TimeSpan IdleFor = TimeSpan.FromMinutes(1);
    private readonly Lock _gate = new();
    private readonly Dictionary<Ulid, Pulse> _pulses = [];
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
        Pulse pulse;
        lock (_gate)
        {
            pulse = Touch(database);
            if (pulse.Version != seen)
            {
                return true;
            }

            pulse.Waiters++;
            signal = pulse.Signal.Task;
        }

        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        await Task.WhenAny(signal, Task.Delay(timeout, timeProvider, stop.Token));
        await stop.CancelAsync();
        lock (_gate)
        {
            pulse.Waiters--;
            pulse.TouchedAt = timeProvider.GetTimestamp();
            return pulse.Version != seen;
        }
    }

    public void Signal(Ulid database)
    {
        TaskCompletionSource? fired = null;
        lock (_gate)
        {
            if (_pulses.TryGetValue(database, out var pulse))
            {
                pulse.Version = ++_generation;
                fired = pulse.Signal;
                pulse.Signal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            }

            Sweep();
        }

        _ = fired?.TrySetResult();
    }

    private Pulse Touch(Ulid database)
    {
        Sweep();
        ref var pulse = ref CollectionsMarshal.GetValueRefOrAddDefault(_pulses, database, out _);
        pulse ??= new Pulse(++_generation);
        pulse.TouchedAt = timeProvider.GetTimestamp();
        return pulse;
    }

    private void Sweep()
    {
        if (timeProvider.GetElapsedTime(_sweptAt) < IdleFor)
        {
            return;
        }

        _sweptAt = timeProvider.GetTimestamp();
        foreach (var (database, pulse) in _pulses.Where(entry => entry.Value.Waiters == 0 && timeProvider.GetElapsedTime(entry.Value.TouchedAt) >= IdleFor).ToArray())
        {
            _ = _pulses.Remove(database);
        }
    }

    private sealed class Pulse(long version)
    {
        public long Version { get; set; } = version;

        public int Waiters { get; set; }

        public long TouchedAt { get; set; }

        public TaskCompletionSource Signal { get; set; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
