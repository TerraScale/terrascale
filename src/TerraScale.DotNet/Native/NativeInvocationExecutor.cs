using System.Collections.Concurrent;

namespace TerraScale.DotNet.Native;

// Owned by one client's/router's isolation. Threads grow on demand up to the
// operation limit, and never run on the shared timer/continuation thread pool.
internal sealed class NativeInvocationExecutor(int maximumWorkers) : IDisposable
{
    private readonly BlockingCollection<Action> queue = new();
    private readonly SemaphoreSlim queueSlots = new(maximumWorkers * 2, maximumWorkers * 2);
    private readonly object gate = new();
    private int workers;
    private int busy;
    private bool stopping;

    internal async Task<T> ExecuteAsync<T>(Func<Task<T>> invoke)
    {
        await queueSlots.WaitAsync().ConfigureAwait(false);
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        var context = ExecutionContext.Capture();
        lock (gate)
        {
            if (stopping) { queueSlots.Release(); throw new ObjectDisposedException(nameof(NativeInvocationExecutor)); }
            queue.Add(() =>
            {
                try
                {
                    if (context is null) Start();
                    else ExecutionContext.Run(context, _ => Start(), null);
                }
                catch (Exception exception) { completion.TrySetException(exception); }
            });
            Grow();
        }
        return await completion.Task.ConfigureAwait(false);

        void Start()
        {
            try { _ = CompleteAsync(invoke()); }
            catch (Exception exception) { completion.TrySetException(exception); }
        }
        async Task CompleteAsync(Task<T> pending)
        {
            try { completion.TrySetResult(await pending.ConfigureAwait(false)); }
            catch (Exception exception) { completion.TrySetException(exception); }
        }
    }

    private void Grow()
    {
        if (workers < maximumWorkers && queue.Count > 0 && busy == workers)
        {
            workers++;
            var thread = new Thread(Run) { IsBackground = true, Name = "TerraScale native invocation" };
            // Capture each invocation's context, never pin the first caller's
            // ambient state to a long-lived worker.
            if (ExecutionContext.IsFlowSuppressed()) thread.Start();
            else { using var flow = ExecutionContext.SuppressFlow(); thread.Start(); }
        }
    }

    private void Run()
    {
        foreach (var work in queue.GetConsumingEnumerable())
        {
            queueSlots.Release();
            lock (gate) { busy++; Grow(); }
            try { work(); }
            finally { lock (gate) busy--; }
        }
    }

    public void Dispose()
    {
        lock (gate)
        {
            if (stopping) return;
            stopping = true;
            queue.CompleteAdding();
        }
    }
}
