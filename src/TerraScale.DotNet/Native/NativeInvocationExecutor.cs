using System.Collections.Concurrent;

namespace TerraScale.DotNet.Native;

// Owned by one client's/router's isolation. Threads grow on demand up to the
// operation limit. Context-capturing callback continuations return to these
// workers, leaving the shared timer/continuation thread pool available.
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
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        var callbacks = new CallbackContext(this, exception => completion.TrySetException(exception));
        await EnqueueAsync(Start, callbacks).ConfigureAwait(false);
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

    private async Task EnqueueAsync(Action work, CallbackContext callbacks)
    {
        var context = ExecutionContext.Capture();
        await queueSlots.WaitAsync().ConfigureAwait(false);
        lock (gate)
        {
            if (stopping) { queueSlots.Release(); throw new ObjectDisposedException(nameof(NativeInvocationExecutor)); }
            queue.Add(() =>
            {
                try
                {
                    if (context is null) RunCallback();
                    else ExecutionContext.Run(context, _ => RunCallback(), null);
                }
                catch (Exception exception) { callbacks.Fail(exception); }
            });
            Grow();
        }

        void RunCallback()
        {
            var previous = SynchronizationContext.Current;
            SynchronizationContext.SetSynchronizationContext(callbacks);
            try { work(); }
            finally { SynchronizationContext.SetSynchronizationContext(previous); }
        }
    }

    private sealed class CallbackContext(NativeInvocationExecutor executor, Action<Exception> fail) : SynchronizationContext
    {
        internal void Fail(Exception exception) => fail(exception);
        public override SynchronizationContext CreateCopy() => this;
        public override void Post(SendOrPostCallback callback, object? state) => _ = DispatchAsync(callback, state);

        private async Task DispatchAsync(SendOrPostCallback callback, object? state)
        {
            // Post must not block an owned worker waiting for a full queue:
            // asynchronous backpressure lets that worker drain queued work.
            try { await executor.EnqueueAsync(() => callback(state), this).ConfigureAwait(false); }
            catch (Exception exception) { Fail(exception); }
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
