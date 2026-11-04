using TerraBase.Engine;
using TerraBase.Errors;
using TerraBase.Features.Management.Usage;
using TerraBase.Primitives;
using TerraBase.Tenancy;

namespace TerraBase.Features.Functions;

/// <summary>One write a function asked for: a put of <paramref name="Json"/>, or a delete when it is null.</summary>
internal sealed record FunctionWrite(string Collection, string Id, byte[]? Json);

/// <summary>
/// Applies a function run's writes in one durable transaction, creating a missing collection. The last user of the
/// engine's document storage on client databases; the functions SQL binding replaces it.
/// </summary>
internal sealed class FunctionWriter(IStorageEngine engine, IOperationAllowance allowance, IUsageMeter usage, TimeProvider time)
{
    /// <summary>Null when every write committed; otherwise why none did.</summary>
    public async ValueTask<string?> WriteAsync(TenantContext database, IReadOnlyList<FunctionWrite> writes, CancellationToken cancellationToken)
    {
        if (await allowance.CheckAsync(database, MeteredOperation.Write, cancellationToken).ConfigureAwait(false) is not Done)
        {
            return "the plan's write allowance is spent";
        }

        if (await engine.OpenDatabaseAsync(database, cancellationToken).ConfigureAwait(false) is not { } store)
        {
            return "the database does not exist";
        }

        var transaction = await store.BeginDurableTransactionAsync(cancellationToken).ConfigureAwait(false);
        await using (transaction.ConfigureAwait(false))
        {
            foreach (var collection in writes.Select(write => write.Collection).Distinct(StringComparer.Ordinal))
            {
                if (await transaction.GetCollectionAsync(collection, cancellationToken).ConfigureAwait(false) is null)
                {
                    _ = await transaction.CreateCollectionAsync(
                        new CollectionInfo(collection, time.GetUtcNow().ToUnixTimeMilliseconds(), EntityIds.New().ToString()), cancellationToken).ConfigureAwait(false);
                }
            }

            foreach (var write in writes)
            {
                if (write.Json is { } json)
                {
                    _ = await transaction.PutAsync(write.Collection, write.Id, json, cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    _ = await transaction.DeleteAsync(write.Collection, write.Id, cancellationToken).ConfigureAwait(false);
                }
            }

            _ = await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }

        usage.RecordWrites(database, writes.Count, writes.Sum(write => Math.Max(1, OperationUnits.Of(write.Json?.Length ?? 0))));
        return null;
    }
}
