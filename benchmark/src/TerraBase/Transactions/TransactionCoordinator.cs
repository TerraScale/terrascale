using System.Collections.ObjectModel;
using System.Text.Json;

namespace TerraBase;

/// <summary>Coordinates uniqueness and referential-integrity policy decisions.</summary>
public interface ITransactionConstraintCoordinator
{
    /// <summary>Evaluates the policies against the requested transaction boundary.</summary>
    IReadOnlyList<ConstraintDecision> Coordinate(TransactionOptions options);
}

/// <summary>
/// Applies TerraBase's local constraint contract. Unique keys and strict
/// relationship checks are part of a serializable single-partition commit.
/// Cross-partition strict foreign keys are deferred until a distributed
/// coordination contract exists; they are never presented as synchronously
/// enforced by a local transaction.
/// </summary>
public sealed class TransactionConstraintCoordinator : ITransactionConstraintCoordinator
{
    /// <inheritdoc />
    public IReadOnlyList<ConstraintDecision> Coordinate(TransactionOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();

        var scope = options.ParticipatingShards.Count > 1
            ? ConstraintScope.CrossPartition
            : ConstraintScope.LocalPartition;
        var decisions = new List<ConstraintDecision>(capacity: 2)
        {
            scope == ConstraintScope.LocalPartition
                ? ConstraintDecision.Enforced("unique", "Unique keys are checked in the atomic local batch.")
                : ConstraintDecision.Deferred("unique", "A unique key spanning shards requires a coordinated index owner."),
        };

        if (options.ReferentialIntegrity == ReferentialIntegrityMode.Soft)
        {
            decisions.Add(ConstraintDecision.Deferred(
                "referential-integrity",
                "Soft referential integrity permits independently converging endpoints."));
        }
        else if (scope == ConstraintScope.CrossPartition)
        {
            decisions.Add(ConstraintDecision.Deferred(
                "referential-integrity",
                "Strict cross-partition foreign keys are deferred until distributed coordination is available."));
        }
        else if (options.IsolationLevel == TransactionIsolationLevel.Serializable)
        {
            decisions.Add(ConstraintDecision.Enforced(
                "referential-integrity",
                "Strict endpoint and delete checks run in the serializable local transaction."));
        }
        else
        {
            decisions.Add(ConstraintDecision.Rejected(
                "referential-integrity",
                "Strict referential integrity requires serializable isolation."));
        }

        return new ReadOnlyCollection<ConstraintDecision>(decisions);
    }
}

/// <summary>Coordinates transaction lifecycle over one storage participant.</summary>
public sealed class TransactionCoordinator
{
    private readonly IStorageProvider provider;
    private readonly ShardId shard;
    private readonly ITransactionConstraintCoordinator constraints;

    /// <summary>Creates a coordinator over a storage provider.</summary>
    /// <param name="provider">The storage provider receiving atomic batches.</param>
    /// <param name="shard">The participant identity used in commit metadata.</param>
    /// <param name="constraints">The constraint policy coordinator.</param>
    public TransactionCoordinator(
        IStorageProvider provider,
        ShardId shard,
        ITransactionConstraintCoordinator? constraints = null)
    {
        this.provider = provider ?? throw new ArgumentNullException(nameof(provider));
        if (!shard.IsInitialized)
        {
            throw new ArgumentException("The shard must be initialized.", nameof(shard));
        }

        this.shard = shard;
        this.constraints = constraints ?? new TransactionConstraintCoordinator();
    }

    /// <summary>Begins a transaction over this participant.</summary>
    public ValueTask<Transaction> BeginAsync(
        TransactionOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var selected = options ?? TransactionOptions.Default;
        selected.Validate();
        var shards = selected.ParticipatingShards.ToHashSet();
        shards.Add(shard);
        selected = selected with { ParticipatingShards = shards };
        return ValueTask.FromResult(new Transaction(provider, shard, selected, constraints));
    }

    /// <summary>Applies a batch as one transaction and returns its commit receipt.</summary>
    public async ValueTask<TransactionCommitResult> ApplyBatchAsync(
        IReadOnlyList<StorageMutation> mutations,
        TransactionOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(mutations);
        await using var transaction = await BeginAsync(options, cancellationToken).ConfigureAwait(false);
        foreach (var mutation in mutations)
        {
            await transaction.AddAsync(mutation, cancellationToken).ConfigureAwait(false);
        }

        return await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>A single-use transaction with isolated reads and an atomic commit.</summary>
public sealed class Transaction : IAsyncDisposable
{
    private readonly IStorageProvider provider;
    private readonly ShardId shard;
    private readonly TransactionOptions options;
    private readonly ITransactionConstraintCoordinator constraintCoordinator;
    private readonly Dictionary<DocumentKey, StorageMutation> mutations = new();
    private readonly Dictionary<DocumentKey, Document?> readSet = new();
    private readonly SemaphoreSlim lifecycleGate = new(1, 1);
    private TransactionState state = TransactionState.Active;

    internal Transaction(
        IStorageProvider provider,
        ShardId shard,
        TransactionOptions options,
        ITransactionConstraintCoordinator constraintCoordinator)
    {
        this.provider = provider;
        this.shard = shard;
        this.options = options;
        this.constraintCoordinator = constraintCoordinator;
        Id = TransactionId.New();
    }

    /// <summary>The stable identity of this transaction.</summary>
    public TransactionId Id { get; }

    /// <summary>The selected isolation level.</summary>
    public TransactionIsolationLevel IsolationLevel => options.IsolationLevel;

    /// <summary>The current lifecycle state.</summary>
    public TransactionState State => state;

    /// <summary>The immutable transaction options.</summary>
    public TransactionOptions Options => options;

    /// <summary>The mutations staged for the atomic commit.</summary>
    public IReadOnlyList<StorageMutation> Mutations => new ReadOnlyCollection<StorageMutation>(mutations.Values.ToArray());

    /// <summary>Reads a value from the transaction's snapshot and read set.</summary>
    public async ValueTask<Document?> ReadAsync(
        DocumentKey key,
        CancellationToken cancellationToken = default)
    {
        await lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfInactive();
            if (mutations.TryGetValue(key, out var staged))
            {
                if (staged.Kind == StorageMutationKind.Delete)
                {
                    return null;
                }

                var current = await ReadBaseAsync(key, cancellationToken).ConfigureAwait(false);
                return new Document(
                    key,
                    staged.Body,
                    current?.Version ?? 1,
                    current?.UpdatedAt ?? DateTimeOffset.UtcNow);
            }

            if (options.IsolationLevel != TransactionIsolationLevel.ReadCommitted && readSet.TryGetValue(key, out var cached))
            {
                return cached;
            }

            var value = await provider.ReadAsync(key, cancellationToken).ConfigureAwait(false);
            if (options.IsolationLevel != TransactionIsolationLevel.ReadCommitted)
            {
                readSet[key] = value;
            }

            return value;
        }
        finally
        {
            lifecycleGate.Release();
        }
    }

    /// <summary>Stages an upsert for the atomic commit.</summary>
    public async ValueTask UpsertAsync(
        DocumentKey key,
        JsonElement body,
        DocumentPrecondition precondition = default,
        CancellationToken cancellationToken = default)
    {
        var current = await ReadAsync(key, cancellationToken).ConfigureAwait(false);
        var selected = precondition == default ? ToVersionPrecondition(current) : precondition;
        await AddAsync(StorageMutation.Upsert(key, body, selected), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Stages a delete for the atomic commit.</summary>
    public async ValueTask DeleteAsync(
        DocumentKey key,
        DocumentPrecondition precondition = default,
        CancellationToken cancellationToken = default)
    {
        var current = await ReadAsync(key, cancellationToken).ConfigureAwait(false);
        var selected = precondition == default ? ToVersionPrecondition(current) : precondition;
        await AddAsync(StorageMutation.Delete(key, selected), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Stages one storage mutation.</summary>
    public async ValueTask AddAsync(StorageMutation mutation, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(mutation);
        await lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfInactive();
            if (mutations.Count >= options.MaximumMutations)
            {
                throw new InvalidOperationException($"A transaction cannot contain more than {options.MaximumMutations} mutations.");
            }

            if (mutations.ContainsKey(mutation.Key))
            {
                throw new InvalidOperationException($"The transaction contains more than one mutation for document '{mutation.Key}'.");
            }

            mutations.Add(mutation.Key, mutation);
        }
        finally
        {
            lifecycleGate.Release();
        }
    }

    /// <summary>Commits all staged mutations atomically.</summary>
    public async ValueTask<TransactionCommitResult> CommitAsync(CancellationToken cancellationToken = default)
    {
        await lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfInactive();
            var decisions = constraintCoordinator.Coordinate(options);
            var rejected = decisions.FirstOrDefault(decision => decision.Kind == ConstraintDecisionKind.Rejected);
            if (rejected is not null)
            {
                state = TransactionState.Conflicted;
                return new(TransactionOutcome.ConstraintRejected, null, [], rejected);
            }

            if (options.IsolationLevel == TransactionIsolationLevel.Serializable && !await ValidateReadSetAsync(cancellationToken).ConfigureAwait(false))
            {
                state = TransactionState.Conflicted;
                return TransactionCommitResult.Conflict;
            }

            StorageBatchResult batch;
            if (mutations.Count == 0)
            {
                batch = new StorageBatchResult(StorageBatchOutcome.Committed, [], null, null);
            }
            else if (provider is ITransactionalStorageProvider transactional)
            {
                batch = await transactional.ApplyBatchAsync(mutations.Values.ToArray(), cancellationToken).ConfigureAwait(false);
            }
            else
            {
                throw new NotSupportedException("The configured storage provider does not support atomic transactions.");
            }

            if (batch.Outcome != StorageBatchOutcome.Committed)
            {
                state = TransactionState.Conflicted;
                return new(TransactionOutcome.Conflict, null, batch.Writes);
            }

            var sequence = batch.LastSequence is null
                ? []
                : new[] { new ShardReadVersion(shard, batch.LastSequence.Value) };
            var shardMetadata = new[] { new ShardCommitMetadata(shard, batch.FirstSequence, batch.LastSequence) };
            var commit = new CommitRecord(
                CommitId.New(),
                Id,
                options.IsolationLevel,
                DateTimeOffset.UtcNow,
                batch.Writes.Count,
                shardMetadata,
                new CrossShardConsistencyMetadata(sequence));
            state = TransactionState.Committed;
            return new(TransactionOutcome.Committed, commit, batch.Writes);
        }
        finally
        {
            lifecycleGate.Release();
        }
    }

    /// <summary>Rolls the transaction back and discards every staged mutation.</summary>
    public async ValueTask RollbackAsync(CancellationToken cancellationToken = default)
    {
        await lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (state == TransactionState.Active)
            {
                mutations.Clear();
                state = TransactionState.RolledBack;
            }
        }
        finally
        {
            lifecycleGate.Release();
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (state == TransactionState.Active)
        {
            await RollbackAsync().ConfigureAwait(false);
        }

        lifecycleGate.Dispose();
    }

    private async ValueTask<Document?> ReadBaseAsync(DocumentKey key, CancellationToken cancellationToken)
    {
        if (options.IsolationLevel != TransactionIsolationLevel.ReadCommitted && readSet.TryGetValue(key, out var cached))
        {
            return cached;
        }

        var value = await provider.ReadAsync(key, cancellationToken).ConfigureAwait(false);
        if (options.IsolationLevel != TransactionIsolationLevel.ReadCommitted)
        {
            readSet[key] = value;
        }

        return value;
    }

    private async ValueTask<bool> ValidateReadSetAsync(CancellationToken cancellationToken)
    {
        foreach (var pair in readSet)
        {
            var current = await provider.ReadAsync(pair.Key, cancellationToken).ConfigureAwait(false);
            if (!SameVersion(pair.Value, current))
            {
                return false;
            }
        }

        return true;
    }

    private static bool SameVersion(Document? left, Document? right) =>
        left?.Version == right?.Version && (left is null) == (right is null);

    private static DocumentPrecondition ToVersionPrecondition(Document? current) =>
        current is null ? DocumentPrecondition.Missing : DocumentPrecondition.Version(current.Version);

    private void ThrowIfInactive()
    {
        if (state != TransactionState.Active)
        {
            throw new InvalidOperationException($"Transaction {Id} is no longer active (state: {state}).");
        }
    }
}
