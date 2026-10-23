using System.Collections.Concurrent;

namespace TerraBase.E2E;

/// <summary>
/// Writes a scenario runs in the background while it gates on their progress. Every operation's id is pending, accepted
/// or failed; a failed write fails the gate at once with the writer's own exception (a real failure, never the gap),
/// instead of freezing the accepted count until the gate times out.
/// </summary>
internal sealed class Writes
{
    private const int ListedIds = 20;

    private readonly IReadOnlyList<string> _ids;
    private readonly ConcurrentDictionary<string, bool> _started = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, bool> _accepted = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, Exception> _failed = new(StringComparer.Ordinal);
    private readonly Task _all;

    private Writes(IReadOnlyList<string> ids, int parallelism, Func<int, CancellationToken, Task> write)
    {
        _ids = ids;
        _all = Parallel.ForEachAsync(Enumerable.Range(0, ids.Count), new ParallelOptions { MaxDegreeOfParallelism = parallelism }, async (index, token) =>
        {
            var id = ids[index];
            _started[id] = true;
            try
            {
                await write(index, token);
            }
            // A write cancelled because another failed stays pending: whether its region took it is unknown.
            catch (Exception error) when (!token.IsCancellationRequested)
            {
                _failed[id] = error;
                throw;
            }

            _accepted[id] = true;
        });
        // A gate may report the failure before CompletedAsync is called; still observe the background task's fault.
        _ = _all.ContinueWith(task => _ = task.Exception, CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    /// <summary>Starts <paramref name="write"/> for each index of <paramref name="ids"/>, at most <paramref name="parallelism"/> at once.</summary>
    public static Writes Start(IReadOnlyList<string> ids, int parallelism, Func<int, CancellationToken, Task> write) => new(ids, parallelism, write);

    public int Accepted => _accepted.Count;

    /// <summary>Until <paramref name="count"/> writes are accepted; a failed write fails at once, past the deadline it does not converge.</summary>
    public Task<Writes> UntilAcceptedAsync(int count, string what) =>
        Converge.UntilAsync(() => Task.FromResult(this), writes => writes.Failure() is { } failure ? throw failure : writes.Accepted >= count, what);

    /// <summary>Waits for every write; any failed write fails with the per-operation record.</summary>
    public async Task CompletedAsync()
    {
        try
        {
            await _all;
        }
        catch (Exception) when (Failure() is { } failure)
        {
            throw failure;
        }
    }

    public override string ToString()
    {
        var pending = _ids.Where(id => !_accepted.ContainsKey(id) && !_failed.ContainsKey(id)).ToList();
        var inFlight = pending.Where(_started.ContainsKey).ToList();
        return $"{_accepted.Count} of {_ids.Count} writes accepted, {_failed.Count} failed {Listed(_failed.Keys)}, {pending.Count} pending "
            + $"({inFlight.Count} in flight {Listed(inFlight)}, {pending.Count - inFlight.Count} not started)";
    }

    private WriterFailedException? Failure()
    {
        var first = _failed.FirstOrDefault();
        return first.Value is null ? null : new WriterFailedException($"write {first.Key} failed: {first.Value.GetType().Name}: {first.Value.Message}; {this}", first.Value);
    }

    private static string Listed(IEnumerable<string> ids)
    {
        var all = ids.Order(StringComparer.Ordinal).ToList();
        return $"[{string.Join(' ', all.Take(ListedIds))}{(all.Count > ListedIds ? $" +{all.Count - ListedIds}" : "")}]";
    }
}

/// <summary>A background write of a scenario failed: a real failure, never a convergence that did not come.</summary>
internal sealed class WriterFailedException(string message, Exception inner) : Exception(message, inner);
