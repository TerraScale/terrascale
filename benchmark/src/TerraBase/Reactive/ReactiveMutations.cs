using System.Collections.Immutable;
using System.Text.Json;

namespace TerraBase.Reactive;

/// <summary>A transaction boundary used by a typed mutation.</summary>
public interface IReactiveTransaction : IAsyncDisposable
{
    /// <summary>Reads one document inside the transaction.</summary>
    ValueTask<Document?> ReadAsync(DocumentKey key, CancellationToken cancellationToken = default);

    /// <summary>Creates or replaces one document inside the transaction.</summary>
    ValueTask<StorageWriteResult> UpsertAsync(
        DocumentKey key,
        JsonElement body,
        DocumentPrecondition precondition = default,
        CancellationToken cancellationToken = default);

    /// <summary>Deletes one document inside the transaction.</summary>
    ValueTask<StorageWriteResult> DeleteAsync(
        DocumentKey key,
        DocumentPrecondition precondition = default,
        CancellationToken cancellationToken = default);

    /// <summary>Commits the transaction and returns its durable changes.</summary>
    ValueTask<IReadOnlyList<ReactiveChange>> CommitAsync(CancellationToken cancellationToken = default);

    /// <summary>Aborts the transaction without publishing changes.</summary>
    ValueTask RollbackAsync(CancellationToken cancellationToken = default);
}

/// <summary>Factory used by typed mutations to obtain an atomic transaction.</summary>
public interface IReactiveTransactionFactory
{
    /// <summary>Begins a transaction for one authorization scope.</summary>
    ValueTask<IReactiveTransaction> BeginAsync(
        ReactiveAuthorization authorization,
        CancellationToken cancellationToken = default);
}

/// <summary>Context supplied to a typed mutation handler.</summary>
public sealed class ReactiveMutationContext
{
    internal ReactiveMutationContext(ReactiveAuthorization authorization, IReactiveTransaction transaction)
    {
        Authorization = authorization;
        Transaction = transaction;
    }

    /// <summary>The authenticated scope used by this mutation.</summary>
    public ReactiveAuthorization Authorization { get; }

    /// <summary>The atomic transaction owned by the executor.</summary>
    public IReactiveTransaction Transaction { get; }

    /// <summary>Reads one document in the current transaction.</summary>
    public ValueTask<Document?> ReadAsync(DocumentKey key, CancellationToken cancellationToken = default) =>
        Transaction.ReadAsync(EnsureTenant(key), cancellationToken);

    /// <summary>Upserts one document in the current transaction.</summary>
    public ValueTask<StorageWriteResult> UpsertAsync(
        DocumentKey key,
        JsonElement body,
        DocumentPrecondition precondition = default,
        CancellationToken cancellationToken = default) =>
        Transaction.UpsertAsync(EnsureTenant(key), body, precondition, cancellationToken);

    /// <summary>Deletes one document in the current transaction.</summary>
    public ValueTask<StorageWriteResult> DeleteAsync(
        DocumentKey key,
        DocumentPrecondition precondition = default,
        CancellationToken cancellationToken = default) =>
        Transaction.DeleteAsync(EnsureTenant(key), precondition, cancellationToken);

    private DocumentKey EnsureTenant(DocumentKey key)
    {
        if (key.Tenant != Authorization.Tenant)
        {
            throw new UnauthorizedAccessException("A mutation cannot access another tenant.");
        }

        return key;
    }
}

/// <summary>Executes typed queries and serializes mutation commits.</summary>
public sealed class ReactiveMutationExecutor
{
    private readonly IReactiveTransactionFactory transactions;
    private readonly SemaphoreSlim commitGate = new(1, 1);
    private readonly Func<ReactiveChangeSet, ValueTask>? publish;
    private long sequence;
    private int disposed;

    /// <summary>Creates a mutation executor over an atomic transaction factory.</summary>
    public ReactiveMutationExecutor(
        IReactiveTransactionFactory transactions,
        Func<ReactiveChangeSet, ValueTask>? publish = null)
    {
        this.transactions = transactions ?? throw new ArgumentNullException(nameof(transactions));
        this.publish = publish;
    }

    /// <summary>Runs a mutation and publishes its changes only after commit succeeds.</summary>
    public async ValueTask<TResult> ExecuteAsync<TInput, TResult>(
        ReactiveMutation<TInput, TResult> mutation,
        TInput input,
        ReactiveAuthorization authorization,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(mutation);
        ArgumentNullException.ThrowIfNull(authorization);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        cancellationToken.ThrowIfCancellationRequested();

        await commitGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        IReactiveTransaction? transaction = null;
        try
        {
            transaction = await transactions.BeginAsync(authorization, cancellationToken).ConfigureAwait(false);
            var context = new ReactiveMutationContext(authorization, transaction);
            var result = await mutation.ExecuteAsync(input, context, cancellationToken).ConfigureAwait(false);
            var changes = await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            if (changes.Count != 0 && publish is not null)
            {
                var currentSequence = checked(Interlocked.Increment(ref sequence));
                await publish(new ReactiveChangeSet(currentSequence, changes)).ConfigureAwait(false);
            }

            await transaction.DisposeAsync().ConfigureAwait(false);
            transaction = null;
            return result;
        }
        catch
        {
            if (transaction is not null)
            {
                try
                {
                    await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
                }
                finally
                {
                    await transaction.DisposeAsync().ConfigureAwait(false);
                }
            }

            throw;
        }
        finally
        {
            commitGate.Release();
        }
    }

    /// <summary>Stops accepting new mutations.</summary>
    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) == 0)
        {
            commitGate.Dispose();
        }

        return ValueTask.CompletedTask;
    }
}

/// <summary>A transaction factory backed by the document store's individual operations.</summary>
/// <remarks>
/// This adapter is useful for local development and tests. It serializes the
/// operation sequence and publishes only a successful sequence; durable stores
/// should provide an implementation backed by their native transaction API.
/// </remarks>
public sealed class DocumentStoreTransactionFactory : IReactiveTransactionFactory
{
    private readonly IDocumentStore documents;

    /// <summary>Creates an adapter for one document store.</summary>
    public DocumentStoreTransactionFactory(IDocumentStore documents) => this.documents = documents ?? throw new ArgumentNullException(nameof(documents));

    /// <inheritdoc />
    public ValueTask<IReactiveTransaction> BeginAsync(
        ReactiveAuthorization authorization,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(authorization);
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult<IReactiveTransaction>(new DocumentStoreTransaction(documents, authorization));
    }

    private sealed class DocumentStoreTransaction : IReactiveTransaction
    {
        private readonly IDocumentStore documents;
        private readonly ReactiveAuthorization authorization;
        private readonly List<ReactiveChange> changes = new();
        private bool completed;

        public DocumentStoreTransaction(IDocumentStore documents, ReactiveAuthorization authorization)
        {
            this.documents = documents;
            this.authorization = authorization;
        }

        public ValueTask<Document?> ReadAsync(DocumentKey key, CancellationToken cancellationToken = default)
        {
            EnsureTenant(key);
            return documents.ReadAsync(key, cancellationToken);
        }

        public async ValueTask<StorageWriteResult> UpsertAsync(
            DocumentKey key,
            JsonElement body,
            DocumentPrecondition precondition = default,
            CancellationToken cancellationToken = default)
        {
            EnsureTenant(key);
            var before = await documents.ReadAsync(key, cancellationToken).ConfigureAwait(false);
            var result = await documents.UpsertAsync(key, body, precondition, cancellationToken).ConfigureAwait(false);
            if (result.Outcome is WriteOutcome.Created or WriteOutcome.Updated && result.Document is not null)
            {
                changes.Add(ReactiveChange.ForDocument(key, result.Outcome, result.Document.Version, before, result.Document));
            }

            return result;
        }

        public async ValueTask<StorageWriteResult> DeleteAsync(
            DocumentKey key,
            DocumentPrecondition precondition = default,
            CancellationToken cancellationToken = default)
        {
            EnsureTenant(key);
            var before = await documents.ReadAsync(key, cancellationToken).ConfigureAwait(false);
            var result = await documents.DeleteAsync(key, precondition, cancellationToken).ConfigureAwait(false);
            if (result.Outcome == WriteOutcome.Deleted && result.Document is not null)
            {
                changes.Add(ReactiveChange.ForDocument(key, result.Outcome, result.Document.Version, before, null));
            }

            return result;
        }

        public ValueTask<IReadOnlyList<ReactiveChange>> CommitAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            EnsureOpen();
            completed = true;
            return ValueTask.FromResult<IReadOnlyList<ReactiveChange>>(changes.ToImmutableArray());
        }

        public ValueTask RollbackAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            changes.Clear();
            completed = true;
            return ValueTask.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            changes.Clear();
            completed = true;
            return ValueTask.CompletedTask;
        }

        private void EnsureTenant(DocumentKey key)
        {
            if (key.Tenant != authorization.Tenant)
            {
                throw new UnauthorizedAccessException("A transaction cannot access another tenant.");
            }

            EnsureOpen();
        }

        private void EnsureOpen() => ObjectDisposedException.ThrowIf(completed, this);
    }
}
