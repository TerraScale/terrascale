using TerraScale.Authentication.Store;

namespace TerraScale.Authentication.Tests;

/// <summary>A controllable storage-authority probe for readiness tests.</summary>
/// <param name="healthy">The answer the fake authority gives.</param>
/// <param name="region">The region reported when healthy.</param>
/// <param name="delay">How long each check takes.</param>
/// <param name="ignoreCancellation">Keep running after cancellation, like a stuck dependency.</param>
/// <param name="blockBeforeReturning">Block the calling thread for <paramref name="delay"/> before returning a task.</param>
public sealed class FakeStorageHealth(bool healthy, string? region = "gru", TimeSpan? delay = null, bool ignoreCancellation = false, bool blockBeforeReturning = false) : IStorageHealth
{
    private int calls;
    private int completed;

    /// <summary>How many probes reached the fake authority.</summary>
    public int Calls => Volatile.Read(ref this.calls);

    /// <summary>How many delayed probes have finished, by answering or by being cancelled.</summary>
    public int Completed => Volatile.Read(ref this.completed);

    /// <inheritdoc />
    public Task<StorageHealthResult> CheckAsync(CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref this.calls);
        if (blockBeforeReturning && delay is { } blocking)
        {
            Thread.Sleep(blocking);
            return Task.FromResult(new StorageHealthResult(healthy, healthy ? region : null));
        }

        return this.CheckAfterDelayAsync(cancellationToken);
    }

    private async Task<StorageHealthResult> CheckAfterDelayAsync(CancellationToken cancellationToken)
    {
        try
        {
            if (delay is { } wait)
            {
                await Task.Delay(wait, ignoreCancellation ? CancellationToken.None : cancellationToken);
            }

            return new StorageHealthResult(healthy, healthy ? region : null);
        }
        finally
        {
            Interlocked.Increment(ref this.completed);
        }
    }
}
