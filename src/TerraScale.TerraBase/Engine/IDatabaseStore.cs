using TerraBase.Tenancy;

namespace TerraBase.Engine;

/// <summary>One open database. The engine owns its lifetime.</summary>
public interface IDatabaseStore
{
    /// <summary>The organization, project and database identity, with the plan normalized to hobby; null only for TerraBase's own database.</summary>
    TenantContext? Tenant { get; }

    /// <summary>Whether deletion or engine shutdown ended this store's lifetime.</summary>
    bool IsClosed { get; }

    /// <summary>Persisted identity shared by database-scoped handles and versions; stable across reopen, fresh for each new database or copy.</summary>
    Guid Incarnation { get; }

    /// <summary>
    /// Starts a transaction. Write transactions are serialized per database; read transactions see
    /// a consistent snapshot and refuse all mutations (<see cref="InvalidOperationException"/>). Dispose without
    /// <see cref="IStorageTransaction.CommitAsync"/> to roll back.
    /// </summary>
    ValueTask<IStorageTransaction> BeginTransactionAsync(bool write, CancellationToken cancellationToken);

    /// <summary>
    /// Starts a write transaction whose commit is synced to disk when <see cref="IStorageTransaction.CommitAsync"/> returns, so
    /// it survives a power loss; other commits are synced at the next checkpoint (docs/decisions/0003, 0010).
    /// </summary>
    ValueTask<IStorageTransaction> BeginDurableTransactionAsync(CancellationToken cancellationToken);

    /// <summary>Counts this process's write commits to this database; read it before a read to wait for later commits.</summary>
    long CommitSequence { get; }

    /// <summary>
    /// Completes once <see cref="CommitSequence"/> exceeds <paramref name="sequence"/>: right away when a
    /// write transaction committed since it was read, otherwise at the next one. Callers re-read to see what changed.
    /// </summary>
    Task WaitForCommitAsync(long sequence, CancellationToken cancellationToken);
}
