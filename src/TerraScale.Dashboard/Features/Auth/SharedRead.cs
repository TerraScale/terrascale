namespace TerraScale.Dashboard.Features.Auth;

internal sealed class SharedRead<T>(Func<T, bool> isGood)
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

        var value = await chosen.Value;
        return isGood(value) || fallback is null ? (value, true) : (await fallback, false);
    }

    private (Lazy<Task<T>>? Chosen, Task<T>? Settled, Task<T>? Fallback) Acquire(TimeProvider time, TimeSpan reuse, bool refresh, Func<Task<T>> read)
    {
        ArgumentNullException.ThrowIfNull(time);
        lock (gate)
        {
            var now = time.GetUtcNow();
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

            var started = new Lazy<Task<T>>(() => RunAsync(read, now), LazyThreadSafetyMode.ExecutionAndPublication);
            attempt = started;
            attemptAt = now;
            return (started, null, good);
        }
    }

    private async Task<T> RunAsync(Func<Task<T>> read, DateTimeOffset startedAt)
    {
        var value = await read();
        if (isGood(value))
        {
            lock (gate)
            {
                good = Task.FromResult(value);
                goodAt = startedAt;
            }
        }

        return value;
    }

    private async Task<T> OrLastGoodAsync(Task<T> refreshing, Task<T> lastGood)
    {
        var value = await refreshing;
        return isGood(value) ? value : await lastGood;
    }
}
