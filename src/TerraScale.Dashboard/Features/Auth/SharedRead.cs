namespace TerraScale.Dashboard.Features.Auth;

internal sealed class SharedRead<T>
{
    private static readonly TimeSpan RetryFloor = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan ColdRetryFloor = TimeSpan.FromSeconds(5);
    private readonly Lock gate = new();
    private Task<T>? good;
    private DateTimeOffset goodAt;
    private Lazy<Task<T>>? attempt;
    private DateTimeOffset attemptAt;

    public Task<T> Get(TimeProvider time, TimeSpan reuse, bool refresh, Func<Task<T>> read)
    {
        var (chosen, settled, fallback) = Acquire(time, reuse, refresh, read);
        return chosen is null ? settled ?? throw new InvalidOperationException("A shared read has neither a value nor an attempt.")
            : fallback is null ? chosen.Value : OrLastGoodAsync(chosen.Value, fallback);
    }

    public async Task<(T Value, bool Fetched)> ReadAsync(TimeProvider time, TimeSpan reuse, bool refresh, Func<Task<T>> read)
    {
        var (chosen, settled, fallback) = Acquire(time, reuse, refresh, read);
        if (chosen is null)
        {
            return (await (settled ?? throw new InvalidOperationException("A shared read has neither a value nor an attempt.")), false);
        }

        var refreshing = chosen.Value;
        await ((Task)refreshing).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        return refreshing.IsCompletedSuccessfully || fallback is null ? (await refreshing, true) : (await fallback, false);
    }

    private (Lazy<Task<T>>? Chosen, Task<T>? Settled, Task<T>? Fallback) Acquire(TimeProvider time, TimeSpan reuse, bool refresh, Func<Task<T>> read)
    {
        ArgumentNullException.ThrowIfNull(time);
        lock (gate)
        {
            var now = time.GetUtcNow();
            if (attempt is { IsValueCreated: true, Value: { IsCompletedSuccessfully: true } succeeded })
            {
                good = succeeded;
                goodAt = attemptAt;
            }

            if (!refresh && good is { } fresh && now - goodAt < reuse)
            {
                return (null, fresh, null);
            }

            if (attempt is { } running && (!running.IsValueCreated || !running.Value.IsCompleted))
            {
                return (running, null, good);
            }

            if (attempt is { } last && now - attemptAt < (good is null ? ColdRetryFloor : RetryFloor))
            {
                return (null, good ?? last.Value, null);
            }

            var started = new Lazy<Task<T>>(read, LazyThreadSafetyMode.ExecutionAndPublication);
            attempt = started;
            attemptAt = now;
            return (started, null, good);
        }
    }

    private static async Task<T> OrLastGoodAsync(Task<T> refreshing, Task<T> lastGood)
    {
        await ((Task)refreshing).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        return await (refreshing.IsCompletedSuccessfully ? refreshing : lastGood);
    }
}
