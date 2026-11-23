namespace TerraScale.QuicRpc;

internal sealed class InFlightTracker
{
    private readonly Lock _gate = new();
    private int _count;
    private TaskCompletionSource? _drained;

    public void Enter()
    {
        lock (_gate)
        {
            _count++;
        }
    }

    public void Exit()
    {
        TaskCompletionSource? drained = null;
        lock (_gate)
        {
            _count--;
            if (_count == 0)
            {
                drained = _drained;
                _drained = null;
            }
        }

        drained?.TrySetResult();
    }

    public Task DrainAsync()
    {
        lock (_gate)
        {
            if (_count == 0)
            {
                return Task.CompletedTask;
            }

            _drained ??= new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            return _drained.Task;
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
        var first = await Task.WhenAny(work, delay).ConfigureAwait(false);
        await timer.CancelAsync().ConfigureAwait(false);
        return first == work;
    }
}
