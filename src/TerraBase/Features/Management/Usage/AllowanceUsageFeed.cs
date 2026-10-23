using TerraBase.Engine;
using TerraBase.Features.Management.Schema;
using TerraBase.Tenancy;

namespace TerraBase.Features.Management.Usage;

/// <summary>Reads bounded node-local storage observations; authoritative operation utilization is unavailable.</summary>
public interface IAllowanceUsageFeed
{
    /// <summary>The next page after an organization ULID; an empty cursor starts the scan.</summary>
    ValueTask<AllowanceUsagePage> ReadAsync(string after, CancellationToken cancellationToken);
}

internal sealed class AllowanceUsageFeed(IStorageEngine engine, ISchemaService schema, TimeProvider time) : IAllowanceUsageFeed
{
    public async ValueTask<AllowanceUsagePage> ReadAsync(string after, CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(30));
        var ct = deadline.Token;
        var now = time.GetUtcNow();
        var month = new DateTimeOffset(now.Year, now.Month, 1, 0, 0, 0, TimeSpan.Zero);
        var organizations = new SortedSet<string>(StringComparer.Ordinal);
        await foreach (var database in engine.ListDatabasesAsync(ct).ConfigureAwait(false))
        {
            if (string.CompareOrdinal(database.OrganizationId, after) <= 0)
            {
                continue;
            }
            _ = organizations.Add(database.OrganizationId);
            if (organizations.Count > 26)
            {
                _ = organizations.Remove(organizations.Max!);
            }
        }

        var selected = organizations.Take(25).ToDictionary(id => id, _ => new List<TenantContext>(), StringComparer.Ordinal);
        await foreach (var database in engine.ListDatabasesAsync(ct).ConfigureAwait(false))
        {
            if (selected.TryGetValue(database.OrganizationId, out var databases) && databases.Count <= 100)
            {
                databases.Add(database);
            }
        }

        var items = new List<AllowanceUsage>(selected.Count);
        foreach (var (id, databases) in selected)
        {
            long? bytes = null;
            if (databases.Count is > 0 and <= 100)
            {
                long total = 0;
                var complete = true;
                foreach (var database in databases)
                {
                    if (await schema.CountCollectionsAsync(database, ct).ConfigureAwait(false) is not IReadOnlyList<CollectionStats> collections)
                    {
                        complete = false;
                        break;
                    }
                    total = checked(total + collections.Sum(collection => collection.Bytes));
                }
                if (complete)
                {
                    bytes = total;
                }
            }
            // The old document counter was removed. Await the authoritative feed contract; never convert its counts here.
            items.Add(new(id, null, bytes));
        }

        if (time.GetUtcNow().Year != now.Year || time.GetUtcNow().Month != now.Month)
        {
            throw new IOException("Allowance scan crossed the UTC billing month.");
        }
        return new(month, now, items, organizations.Count > 25 ? selected.Keys.Last() : null,
            "document-operations-node-v1", "logical-bytes-node-v1");
    }
}
