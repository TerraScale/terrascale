using System.Collections.Concurrent;
using System.Globalization;
using TerraScale.Authentication.Contracts;
using TerraScale.Authentication.Storage;

namespace TerraScale.Authentication.Features.RateLimiting;

internal sealed class LocalAttemptStore(TimeProvider clock) : IAttemptStore
{
    internal const int Capacity = 100_000;

    private static readonly TimeSpan Idle = TimeSpan.FromHours(1);

    private static readonly TimeSpan SweepInterval = TimeSpan.FromSeconds(10);

    private long sweptAt = long.MinValue;

    private readonly ConcurrentDictionary<(string Instance, string Key), Entry> buckets = new();

    private long versions;

    public Task<IReadOnlyList<Versioned<AttemptBucket>?>> GetManyAsync(string instanceId, IReadOnlyList<string> keys, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<Versioned<AttemptBucket>?>>([.. keys.Select(key => this.buckets.TryGetValue((instanceId, key), out var entry)
            ? new Versioned<AttemptBucket>(entry.Body, entry.Version)
            : null)]);

    public Task<bool> TryCreateAsync(string instanceId, string key, AttemptBucket rowData, CancellationToken cancellationToken)
    {
        if (this.buckets.Count >= Capacity && (!this.ClaimSweep() || this.Sweep() >= Capacity))
        {
            return Task.FromResult(false);
        }

        return Task.FromResult(this.buckets.TryAdd((instanceId, key), this.Next(rowData)));
    }

    public Task<bool> TryReplaceAsync(string instanceId, string key, AttemptBucket rowData, string version, CancellationToken cancellationToken)
    {
        var address = (instanceId, key);
        return Task.FromResult(this.buckets.TryGetValue(address, out var current) && current.Version == version
            && this.buckets.TryUpdate(address, this.Next(rowData), current));
    }

    public Task<bool> TryChargeAsync(string instanceId, string key, IReadOnlyList<string> others, long window, int maximum, CancellationToken cancellationToken)
    {
        var address = (instanceId, key);
        while (this.buckets.TryGetValue(address, out var current) && current.Body.Window <= window
            && (current.Body.Window == window ? current.Body.Used : 0L) + others.Sum(other => this.buckets.TryGetValue((instanceId, other), out var entry) && entry.Body.Window == window ? entry.Body.Used : 0L) < maximum)
        {
            if (this.buckets.TryUpdate(address, this.Next(new AttemptBucket(window, current.Body.Window == window ? current.Body.Used + 1 : 1)), current))
            {
                return Task.FromResult(true);
            }
        }

        return Task.FromResult(false);
    }

    private bool ClaimSweep()
    {
        var now = clock.GetTimestamp();
        var last = Interlocked.Read(ref this.sweptAt);
        return (last == long.MinValue || clock.GetElapsedTime(last, now) >= SweepInterval)
            && Interlocked.CompareExchange(ref this.sweptAt, now, last) == last;
    }

    private int Sweep()
    {
        var cutoff = clock.GetUtcNow() - Idle;
        foreach (var (address, entry) in this.buckets)
        {
            if (entry.Touched < cutoff)
            {
                this.buckets.TryRemove(address, out _);
            }
        }

        return this.buckets.Count;
    }

    private Entry Next(AttemptBucket rowData) =>
        new(rowData, Interlocked.Increment(ref this.versions).ToString(CultureInfo.InvariantCulture), clock.GetUtcNow());

    private sealed record Entry(AttemptBucket Body, string Version, DateTimeOffset Touched);
}
