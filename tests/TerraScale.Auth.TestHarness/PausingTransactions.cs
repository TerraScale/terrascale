namespace TerraScale.Auth.Tests;

/// <summary>
/// A TerraBase client handler that holds the next transaction (a change's commit, after all its reads) until the test
/// releases it, so a competing change can commit in between, deterministically.
/// </summary>
public sealed class PausingTransactions : DelegatingHandler
{
    private TaskCompletionSource? reached;
    private TaskCompletionSource? release;

    /// <summary>
    /// Starts <paramref name="call"/> and returns it once its first transaction is held. A call that ends first (with its
    /// own exception, if it failed) or does not reach a transaction within ten seconds fails this instead, and nothing
    /// stays held.
    /// </summary>
    public async Task<Task<T>> HoldNextAsync<T>(Func<Task<T>> call)
    {
        // Kept locally: the transaction can arrive and take the field before this method waits on it.
        TaskCompletionSource reached = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        this.release = release;
        this.reached = reached;
        var running = Task.Run(call);
        var first = await Task.WhenAny(reached.Task, running, Task.Delay(TimeSpan.FromSeconds(10)));
        if (first == reached.Task)
        {
            return running;
        }

        // Nothing is held: disarm, and let a late transaction through.
        Interlocked.CompareExchange(ref this.reached, null, reached);
        release.TrySetResult();
        if (first == running)
        {
            throw new InvalidOperationException($"The call ended before its transaction, answering {await running}.");
        }

        throw new TimeoutException("The call did not reach a transaction within ten seconds.");
    }

    /// <summary>Lets the held transaction (and every later one) through and returns the call's answer.</summary>
    public async Task<T> ReleaseAsync<T>(Task<T> held)
    {
        this.release!.TrySetResult();
        return await held.WaitAsync(TimeSpan.FromSeconds(10));
    }

    /// <inheritdoc />
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (request.Method == HttpMethod.Post && request.RequestUri!.AbsolutePath.EndsWith("/transactions", StringComparison.Ordinal)
            && Interlocked.Exchange(ref this.reached, null) is { } holding)
        {
            holding.TrySetResult();
            await this.release!.Task.WaitAsync(cancellationToken);
        }

        return await base.SendAsync(request, cancellationToken);
    }
}
