namespace TerraScale.TerraBase.Engine;

/// <summary>One open database. The engine owns its lifetime.</summary>
public interface IDatabaseStore
{
    /// <summary>The database name.</summary>
    DatabaseName Name { get; }

    /// <summary>Persisted identity shared by database-scoped handles and versions; stable across reopen, fresh for each new database or copy.</summary>
    Guid Incarnation { get; }

    /// <summary>
    /// Starts a transaction. Write transactions are serialized per database; read transactions see
    /// a consistent snapshot. Dispose without <see cref="IStorageTransaction.CommitAsync"/> to roll back.
    /// </summary>
    ValueTask<IStorageTransaction> BeginTransactionAsync(bool write, CancellationToken cancellationToken);
}
