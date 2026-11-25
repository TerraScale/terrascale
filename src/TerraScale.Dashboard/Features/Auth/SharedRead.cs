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
        ArgumentNullException.ThrowIfNull(time);
        Lazy<Task<T>> chosen;
        Task<T>? fallback;
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
                return fresh;
            }

            fallback = good;
            if (attempt is { } running && (!running.IsValueCreated || !running.Value.IsCompleted))
            {
                chosen = running;
            }
            else if (attempt is { } last && now - attemptAt < (good is null ? ColdRetryFloor : RetryFloor))
            {
                return good ?? last.Value;
            }
            else
            {
                chosen = new Lazy<Task<T>>(read, LazyThreadSafetyMode.ExecutionAndPublication);
                attempt = chosen;
                attemptAt = now;
            }
        }

        return fallback is null ? chosen.Value : OrLastGoodAsync(chosen.Value, fallback);
    }

    private static async Task<T> OrLastGoodAsync(Task<T> refreshing, Task<T> lastGood)
    {
        await ((Task)refreshing).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        return await (refreshing.IsCompletedSuccessfully ? refreshing : lastGood);
    }
}
