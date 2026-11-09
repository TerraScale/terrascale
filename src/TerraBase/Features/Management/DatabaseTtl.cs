using System.Text.Json;
using TerraBase.Engine;
using TerraBase.Errors;
using TerraBase.Hosting;
using TerraBase.Primitives;

namespace TerraBase.Features.Management;

internal sealed record DatabaseExpiry(DateTimeOffset ExpiresAt);

public sealed record DatabaseTtlRequest(long? TtlSeconds);

public sealed record ForkRequest(string? Name, long? TtlSeconds = null);

internal static class DatabaseTtl
{
    public const string Kind = "database_expiry";
    public const long MinSeconds = 60;
    public const long MaxSeconds = 365L * 24 * 60 * 60;

    public const string Description = " ttlSeconds (60 to 31,536,000) deletes the database, its backups and its data keys within seconds after expiresAt, like a delete; a TTL database counts against the plan's database limit and is billed until then.";

    public static Validation? Invalid(long? seconds) =>
        seconds is < MinSeconds or > MaxSeconds ? new Validation($"ttlSeconds must be between {MinSeconds} and {MaxSeconds}") : null;

    public static DateTimeOffset? ExpiresAt(DateTimeOffset now, long? seconds) =>
        seconds is { } value ? now.AddSeconds(value) : null;

    public static async ValueTask<DateTimeOffset?> ReadAsync(IStorageTransaction transaction, string databaseId, CancellationToken cancellationToken) =>
        (await SystemCatalog.GetAsync(transaction, Kind, databaseId, TerraBaseJsonContext.Default.DatabaseExpiry, cancellationToken).ConfigureAwait(false))?.ExpiresAt;

    public static async ValueTask WriteAsync(IStorageTransaction transaction, string databaseId, DateTimeOffset? expiresAt, CancellationToken cancellationToken)
    {
        if (expiresAt is not { } value)
        {
            if (await transaction.GetVersionAsync(Kind, databaseId, cancellationToken).ConfigureAwait(false) is not null)
            {
                _ = await transaction.DeleteAsync(Kind, databaseId, cancellationToken).ConfigureAwait(false);
            }

            return;
        }

        _ = await transaction.PutAsync(Kind, databaseId,
            EntityPayload.WithoutId(JsonSerializer.SerializeToUtf8Bytes(new DatabaseExpiry(value), TerraBaseJsonContext.Default.DatabaseExpiry), databaseId, nested: true),
            cancellationToken).ConfigureAwait(false);
    }

    public static async ValueTask<IReadOnlyList<T>> DueAsync<T>(
        IStorageTransaction transaction, DateTimeOffset now, int limit, Func<string, ValueTask<T?>> resolve, CancellationToken cancellationToken)
        where T : class
    {
        List<T> due = [];
        string? after = null;
        while (due.Count < limit)
        {
            var page = await transaction.ListAsync(Kind, after, 256, 1024 * 1024, cancellationToken).ConfigureAwait(false);
            foreach (var stored in page.Records)
            {
                if (due.Count < limit && EntityPayload.Read(stored, TerraBaseJsonContext.Default.DatabaseExpiry) is { } expiry && expiry.ExpiresAt <= now
                    && await resolve(stored.Id).ConfigureAwait(false) is { } resolved)
                {
                    due.Add(resolved);
                }
            }

            if (!page.HasMore || page.Records.Count == 0)
            {
                break;
            }

            after = page.Records[^1].Id;
        }

        return due;
    }
}
