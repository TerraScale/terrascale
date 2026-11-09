using Microsoft.EntityFrameworkCore;
using TerraScale.Authentication.Contracts;
using TerraScale.Authentication.Storage;

namespace TerraScale.Authentication.Features.RateLimiting;

public sealed record RateLimitRecord(string InstanceId, string Key, long Window, int Used);

public interface IAttemptStore
{
    Task<IReadOnlyList<Versioned<AttemptBucket>?>> GetManyAsync(string instanceId, IReadOnlyList<string> keys, CancellationToken cancellationToken);

    Task<bool> TryCreateAsync(string instanceId, string key, AttemptBucket bucket, CancellationToken cancellationToken);

    Task<bool> TryReplaceAsync(string instanceId, string key, AttemptBucket bucket, string version, CancellationToken cancellationToken);
}

public sealed class TerraBaseAttemptStore(AuthData data) : IAttemptStore
{
    public Task<IReadOnlyList<Versioned<AttemptBucket>?>> GetManyAsync(string instanceId, IReadOnlyList<string> keys, CancellationToken cancellationToken) =>
        Guard(async () =>
        {
            var rows = await data.ReadAsync((db, token) => db.RateLimits.Where(row => row.InstanceId == instanceId && keys.Contains(row.Key)).WithVersion().ToListAsync(token),
                cancellationToken).ConfigureAwait(false);
            return (IReadOnlyList<Versioned<AttemptBucket>?>)[.. keys.Select(key => rows.FirstOrDefault(row => row.Value.Key == key) is { } row
                ? new Versioned<AttemptBucket>(new AttemptBucket(row.Value.Window, row.Value.Used), row.Version)
                : null)];
        }, cancellationToken);

    public Task<bool> TryCreateAsync(string instanceId, string key, AttemptBucket bucket, CancellationToken cancellationToken) =>
        Guard(() => data.SaveAsync(Change.Insert(Record(instanceId, key, bucket)), cancellationToken), cancellationToken);

    public Task<bool> TryReplaceAsync(string instanceId, string key, AttemptBucket bucket, string version, CancellationToken cancellationToken) =>
        Guard(() => data.SaveAsync(Change.Update(Record(instanceId, key, bucket), version), cancellationToken), cancellationToken);

    private static RateLimitRecord Record(string instanceId, string key, AttemptBucket bucket)
    {
        ArgumentNullException.ThrowIfNull(bucket);
        return new(instanceId, key, bucket.Window, bucket.Used);
    }

    private static async Task<TResult> Guard<TResult>(Func<Task<TResult>> operation, CancellationToken cancellationToken)
    {
        try
        {
            return await operation().ConfigureAwait(false);
        }
        catch (Exception failure) when (failure is not (ApiException { Status: 503 } or OutOfMemoryException)
            && !(failure is OperationCanceledException && cancellationToken.IsCancellationRequested))
        {
            throw ApiException.Unavailable(failure);
        }
    }
}
