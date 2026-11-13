using System.Runtime.CompilerServices;
using TerraBase.Tenancy;

namespace TerraBase.Engine;

public interface IDatabaseStore
{
    TenantContext? Tenant { get; }

    bool IsClosed { get; }

    Ulid Incarnation { get; }

    ValueTask<IStorageTransaction> BeginTransactionAsync(
        bool write, CancellationToken cancellationToken, [CallerFilePath] string file = "", [CallerLineNumber] int line = 0);

    ValueTask<IStorageTransaction> BeginPromotionTransactionAsync(
        CancellationToken cancellationToken, [CallerFilePath] string file = "", [CallerLineNumber] int line = 0);

    ValueTask<IStorageTransaction> BeginDurableTransactionAsync(
        CancellationToken cancellationToken, [CallerFilePath] string file = "", [CallerLineNumber] int line = 0);

    async ValueTask<(T Result, CommitResult Commit)> WriteAsync<T>(
        Func<IStorageTransaction, CancellationToken, ValueTask<T>> work, Func<T, bool> keep, CancellationToken cancellationToken,
        [CallerFilePath] string file = "", [CallerLineNumber] int line = 0)
    {
        ArgumentNullException.ThrowIfNull(work);
        ArgumentNullException.ThrowIfNull(keep);
        var transaction = await BeginTransactionAsync(write: true, cancellationToken, file, line).ConfigureAwait(false);
        await using (transaction.ConfigureAwait(false))
        {
            var result = await work(transaction, cancellationToken).ConfigureAwait(false);
            return keep(result) ? (result, await transaction.CommitAsync(cancellationToken).ConfigureAwait(false)) : (result, default);
        }
    }

    long CommitSequence { get; }

    Task WaitForCommitAsync(long sequence, CancellationToken cancellationToken);
}
