using System.Collections.Concurrent;
using System.Globalization;
using TerraScale.Authentication.Store;

namespace TerraScale.Authentication.Features.RateLimiting;

/// <summary>
/// This replica's in-memory copy of rate-limit buckets, used by <see cref="AttemptLimiter"/> while its TerraBase
/// storage fails. The same algorithms and budgets run on it, so brute-force protection holds per replica instead of
/// every sign-in failing closed. Bounded: past <see cref="Capacity"/> buckets, those untouched for an hour are dropped,
/// and if none can be, new buckets are refused (the limiter then fails closed).
/// </summary>
internal sealed class LocalAttemptStore(TimeProvider clock) : IDocumentStore
{
    /// <summary>Most buckets kept at once.</summary>
    internal const int Capacity = 100_000;

    private static readonly TimeSpan Idle = TimeSpan.FromHours(1);

    private readonly ConcurrentDictionary<(string Collection, string Instance, string Key), Entry> buckets = new();

    private long versions;

    /// <inheritdoc />
    public Task<Versioned<T>?> GetAsync<T>(string collection, string instanceId, string key, CancellationToken cancellationToken) =>
        Task.FromResult(this.buckets.TryGetValue((collection, instanceId, key), out var entry) && entry.Document is T document
            ? new Versioned<T>(document, entry.Version)
            : null);

    /// <inheritdoc />
    public Task<bool> TryCreateAsync<T>(string collection, string instanceId, string key, T document, CancellationToken cancellationToken)
    {
        if (this.buckets.Count >= Capacity)
        {
            var cutoff = clock.GetUtcNow() - Idle;
            foreach (var (address, entry) in this.buckets)
            {
                if (entry.Touched < cutoff)
                {
                    this.buckets.TryRemove(address, out _);
                }
            }

            if (this.buckets.Count >= Capacity)
            {
                return Task.FromResult(false);
            }
        }

        return Task.FromResult(this.buckets.TryAdd((collection, instanceId, key), this.Next(document)));
    }

    /// <inheritdoc />
    public Task<bool> TryReplaceAsync<T>(string collection, string instanceId, string key, T document, string version, CancellationToken cancellationToken)
    {
        var address = (collection, instanceId, key);
        return Task.FromResult(this.buckets.TryGetValue(address, out var current) && current.Version == version
            && this.buckets.TryUpdate(address, this.Next(document), current));
    }

    private Entry Next(object? document) =>
        new(document, Interlocked.Increment(ref this.versions).ToString(CultureInfo.InvariantCulture), clock.GetUtcNow());

    private sealed record Entry(object? Document, string Version, DateTimeOffset Touched);
}
