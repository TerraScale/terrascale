using Dapper;
using TerraScale.Authentication.Contracts;
using TerraScale.Authentication.Storage;
using TerraScale.DotNet.TerraBase;

namespace TerraScale.Authentication.Features.RateLimiting;

public sealed record RateLimitRecord(string InstanceId, string Key, long Window, int Used)
{
    public string? Version { get; init; }
}

public interface IAttemptStore
{
    Task<IReadOnlyList<Versioned<AttemptBucket>?>> GetManyAsync(string instanceId, IReadOnlyList<string> keys, CancellationToken cancellationToken);

    Task<bool> TryCreateAsync(string instanceId, string key, AttemptBucket bucket, CancellationToken cancellationToken);

    Task<bool> TryReplaceAsync(string instanceId, string key, AttemptBucket bucket, string version, CancellationToken cancellationToken);

    Task<bool> TryChargeAsync(string instanceId, string key, IReadOnlyList<string> others, long window, int maximum, CancellationToken cancellationToken);
}

public sealed class TerraBaseAttemptStore(AuthData data) : IAttemptStore
{
    public Task<IReadOnlyList<Versioned<AttemptBucket>?>> GetManyAsync(string instanceId, IReadOnlyList<string> keys, CancellationToken cancellationToken) =>
        Guard(async () =>
        {
            var rows = Versioned.All(await data.ReadAsync(connection => connection.QueryAsync<RateLimitRecord>(
                "SELECT * FROM rate_limits WHERE instance_id = @instanceId AND \"key\" IN @keys", new { instanceId, keys }), cancellationToken));
            return (IReadOnlyList<Versioned<AttemptBucket>?>)[.. keys.Select(key => rows.FirstOrDefault(row => row.Value.Key == key) is { } row
                ? new Versioned<AttemptBucket>(new AttemptBucket(row.Value.Window, row.Value.Used), row.Version)
                : null)];
        }, cancellationToken);

    public Task<bool> TryCreateAsync(string instanceId, string key, AttemptBucket bucket, CancellationToken cancellationToken) =>
        GuardWriteAsync(() => data.SaveAsync(Change.Insert(Record(instanceId, key, bucket)), cancellationToken), cancellationToken);

    public Task<bool> TryReplaceAsync(string instanceId, string key, AttemptBucket bucket, string version, CancellationToken cancellationToken) =>
        GuardWriteAsync(() => data.SaveAsync(Change.Update(Record(instanceId, key, bucket), version), cancellationToken), cancellationToken);

    public Task<bool> TryChargeAsync(string instanceId, string key, IReadOnlyList<string> others, long window, int maximum, CancellationToken cancellationToken) =>
        GuardWriteAsync(async () =>
        {
            var version = AuthIds.New();
            return await data.ReadAsync(connection => connection.ExecuteAsync(
                """
                UPDATE rate_limits SET used = CASE WHEN "window" = @window THEN used + 1 ELSE 1 END, "window" = @window, version = @version
                WHERE instance_id = @instanceId AND "key" = @key AND "window" <= @window
                    AND (CASE WHEN "window" = @window THEN used ELSE 0 END)
                        + (SELECT COALESCE(SUM(o.used), 0) FROM rate_limits o WHERE o.instance_id = @instanceId AND o."key" IN @others AND o."window" = @window) < @maximum
                """, new { window, version, instanceId, key, others, maximum }), cancellationToken) == 1;
        }, cancellationToken);

    private static RateLimitRecord Record(string instanceId, string key, AttemptBucket bucket)
    {
        ArgumentNullException.ThrowIfNull(bucket);
        return new(instanceId, key, bucket.Window, bucket.Used);
    }

    private static async Task<bool> GuardWriteAsync(Func<Task<bool>> write, CancellationToken cancellationToken)
    {
        try
        {
            return await Guard(write, cancellationToken);
        }
        catch (ApiException failure) when (Contended(failure))
        {
            return false;
        }
    }

    private static bool Contended(Exception? failure) => failure switch
    {
        TerraBaseException { ErrorCode: TerraBaseSqlErrorCodes.RowConflict } => true,
        ApiException { Status: StatusCodes.Status503ServiceUnavailable } unavailable => Contended(unavailable.InnerException),
        _ => AuthData.IsContention(failure),
    };

    private static async Task<TResult> Guard<TResult>(Func<Task<TResult>> operation, CancellationToken cancellationToken)
    {
        try
        {
            return await operation();
        }
        catch (Exception failure) when (failure is not (ApiException { Status: 503 } or OutOfMemoryException)
            && !(failure is OperationCanceledException && cancellationToken.IsCancellationRequested))
        {
            throw ApiException.Unavailable(failure);
        }
    }
}
