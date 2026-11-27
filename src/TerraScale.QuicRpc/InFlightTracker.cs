namespace TerraScale.QuicRpc;

internal sealed class InFlightTracker
{
    private readonly Lock _gate = new();
    private int _count;
    private TaskCompletionSource? _drained;

    public void Enter() => Interlocked.Increment(ref _count);

    public void Exit()
    {
        if (Interlocked.Decrement(ref _count) != 0 || Volatile.Read(ref _drained) is null)
        {
            return;
        }

        TaskCompletionSource? drained = null;
        lock (_gate)
        {
            if (Volatile.Read(ref _count) == 0)
            {
                drained = _drained;
                Volatile.Write(ref _drained, null);
            }
        }

        drained?.TrySetResult();
    }

    public Task DrainAsync()
    {
        lock (_gate)
        {
            if (Volatile.Read(ref _count) == 0)
            {
                return Task.CompletedTask;
            }

            var tcs = _drained ?? new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            Volatile.Write(ref _drained, tcs);
            Interlocked.MemoryBarrier();
            if (Volatile.Read(ref _count) == 0)
            {
                Volatile.Write(ref _drained, null);
                return Task.CompletedTask;
            }

            return tcs.Task;
        }
    }

    public static async Task<bool> WaitAsync(Task work, TimeSpan grace, TimeProvider timeProvider)
    {
        if (work.IsCompleted)
        {
            return true;
        }

        using var timer = new CancellationTokenSource();
        var delay = Task.Delay(grace, timeProvider, timer.Token);
        var first = await Task.WhenAny(work, delay);
        await timer.CancelAsync();
        return first == work;
    }
}
