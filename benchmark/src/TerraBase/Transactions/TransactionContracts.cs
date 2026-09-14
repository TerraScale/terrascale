using System.Collections.ObjectModel;

namespace TerraBase;

/// <summary>Controls the visibility and conflict checks used by a transaction.</summary>
public enum TransactionIsolationLevel
{
    /// <summary>Reads may observe commits made after the transaction started.</summary>
    ReadCommitted,

    /// <summary>Reads use one logical snapshot and writes conflict with changed values.</summary>
    Snapshot,

    /// <summary>Reads and writes use one snapshot and all observed values are validated at commit.</summary>
    Serializable,
}

/// <summary>Describes the lifecycle state of a transaction.</summary>
public enum TransactionState
{
    /// <summary>The transaction accepts reads and mutations.</summary>
    Active,

    /// <summary>The transaction committed successfully.</summary>
    Committed,

    /// <summary>The transaction was rolled back before commit.</summary>
    RolledBack,

    /// <summary>The transaction could not commit because a precondition failed.</summary>
    Conflicted,
}

/// <summary>Describes the terminal result of a transaction commit.</summary>
public enum TransactionOutcome
{
    /// <summary>Every mutation committed atomically.</summary>
    Committed,

    /// <summary>A read or write precondition failed and no mutation committed.</summary>
    Conflict,

    /// <summary>The transaction was rolled back by its caller.</summary>
    RolledBack,

    /// <summary>The transaction was rejected by a constraint policy.</summary>
    ConstraintRejected,
}

/// <summary>Controls how referential constraints are coordinated.</summary>
public enum ReferentialIntegrityMode
{
    /// <summary>Edges remain valid while their endpoints converge independently.</summary>
    Soft,

    /// <summary>Endpoint and delete checks are required before a local commit.</summary>
    Strict,
}

/// <summary>Describes the scope in which a constraint can be checked atomically.</summary>
public enum ConstraintScope
{
    /// <summary>All records are owned by one transaction participant.</summary>
    LocalPartition,

    /// <summary>Records span multiple transaction participants.</summary>
    CrossPartition,
}

/// <summary>Describes the result of coordinating a constraint.</summary>
public enum ConstraintDecisionKind
{
    /// <summary>The transaction can enforce the requested constraint atomically.</summary>
    Enforced,

    /// <summary>The constraint remains soft or must be checked by a later repair.</summary>
    Deferred,

    /// <summary>The requested policy cannot be satisfied by this transaction.</summary>
    Rejected,
}

/// <summary>The decision made for one uniqueness or referential constraint.</summary>
public sealed record ConstraintDecision(
    ConstraintDecisionKind Kind,
    string ConstraintName,
    string Reason)
{
    /// <summary>Creates an enforced decision.</summary>
    public static ConstraintDecision Enforced(string name, string reason) =>
        new(ConstraintDecisionKind.Enforced, ValidateName(name), ValidateReason(reason));

    /// <summary>Creates a deferred decision.</summary>
    public static ConstraintDecision Deferred(string name, string reason) =>
        new(ConstraintDecisionKind.Deferred, ValidateName(name), ValidateReason(reason));

    /// <summary>Creates a rejected decision.</summary>
    public static ConstraintDecision Rejected(string name, string reason) =>
        new(ConstraintDecisionKind.Rejected, ValidateName(name), ValidateReason(reason));

    private static string ValidateName(string value) => IdentifierValidation.Validate(value, nameof(ConstraintName));

    private static string ValidateReason(string value) => IdentifierValidation.Validate(value, nameof(Reason));
}

/// <summary>Options that define the transaction's boundary and constraint policy.</summary>
public sealed record TransactionOptions
{
    /// <summary>The default options: a snapshot transaction with soft relationships.</summary>
    public static TransactionOptions Default { get; } = new();

    /// <summary>The requested isolation level.</summary>
    public TransactionIsolationLevel IsolationLevel { get; init; } = TransactionIsolationLevel.Snapshot;

    /// <summary>Whether relationship endpoints must be checked synchronously.</summary>
    public ReferentialIntegrityMode ReferentialIntegrity { get; init; } = ReferentialIntegrityMode.Soft;

    /// <summary>The transaction participant identities touched by the request.</summary>
    public IReadOnlySet<ShardId> ParticipatingShards { get; init; } = new HashSet<ShardId>();

    /// <summary>The maximum number of document mutations allowed in this transaction.</summary>
    public int MaximumMutations { get; init; } = 1_000;

    internal void Validate()
    {
        if (MaximumMutations is < 1 or > 1_000)
        {
            throw new ArgumentOutOfRangeException(nameof(MaximumMutations), "MaximumMutations must be between one and 1,000.");
        }

        ArgumentNullException.ThrowIfNull(ParticipatingShards);
        if (ParticipatingShards.Any(shard => !shard.IsInitialized))
        {
            throw new ArgumentException("ParticipatingShards must contain initialized shard identifiers.", nameof(ParticipatingShards));
        }
    }
}

/// <summary>Identifies one storage participant for cross-shard consistency.</summary>
public readonly record struct ShardId
{
    private readonly string? value;

    /// <summary>Creates a shard identifier.</summary>
    public ShardId(string value) => this.value = IdentifierValidation.Validate(value, nameof(value));

    /// <summary>The stable shard value.</summary>
    public string Value => value ?? throw new InvalidOperationException("The shard identifier is uninitialized.");

    internal bool IsInitialized => value is not null;

    /// <inheritdoc />
    public override string ToString() => Value;
}

/// <summary>A version observed from one shard at a transaction read cut.</summary>
public readonly record struct ShardReadVersion
{
    /// <summary>Creates an observed shard version.</summary>
    public ShardReadVersion(ShardId shard, ulong sequence)
    {
        if (!shard.IsInitialized)
        {
            throw new ArgumentException("The shard must be initialized.", nameof(shard));
        }

        Shard = shard;
        Sequence = sequence;
    }

    /// <summary>The participant that produced the sequence.</summary>
    public ShardId Shard { get; }

    /// <summary>The sequence observed on the participant.</summary>
    public ulong Sequence { get; }
}

/// <summary>
/// The consistency cut carried by a transaction and its commit receipt. A
/// consumer may serve a read only after every named shard has reached at least
/// the recorded sequence.
/// </summary>
public sealed class CrossShardConsistencyMetadata
{
    private readonly IReadOnlyDictionary<ShardId, ulong> sequenceByShard;

    /// <summary>Creates a normalized consistency cut.</summary>
    /// <param name="versions">The observed per-shard sequence values.</param>
    public CrossShardConsistencyMetadata(IEnumerable<ShardReadVersion> versions)
    {
        ArgumentNullException.ThrowIfNull(versions);
        var map = new Dictionary<ShardId, ulong>();
        foreach (var version in versions)
        {
            if (!version.Shard.IsInitialized)
            {
                throw new ArgumentException("Consistency metadata cannot contain an uninitialized shard.", nameof(versions));
            }

            if (map.TryGetValue(version.Shard, out var current))
            {
                map[version.Shard] = Math.Max(current, version.Sequence);
            }
            else
            {
                map.Add(version.Shard, version.Sequence);
            }
        }

        sequenceByShard = new ReadOnlyDictionary<ShardId, ulong>(map);
    }

    /// <summary>An empty consistency cut.</summary>
    public static CrossShardConsistencyMetadata Empty { get; } = new([]);

    /// <summary>The highest required sequence for each shard.</summary>
    public IReadOnlyDictionary<ShardId, ulong> Sequences => sequenceByShard;

    /// <summary>Returns whether a shard has reached the required sequence.</summary>
    public bool IsSatisfiedBy(IEnumerable<ShardReadVersion> appliedVersions)
    {
        ArgumentNullException.ThrowIfNull(appliedVersions);
        var applied = appliedVersions.ToDictionary(version => version.Shard, version => version.Sequence);
        return sequenceByShard.All(pair => applied.TryGetValue(pair.Key, out var sequence) && sequence >= pair.Value);
    }

    /// <summary>Merges two consistency cuts by taking the maximum per shard.</summary>
    public CrossShardConsistencyMetadata Merge(CrossShardConsistencyMetadata other)
    {
        ArgumentNullException.ThrowIfNull(other);
        return new(sequenceByShard
            .Concat(other.sequenceByShard)
            .Select(pair => new ShardReadVersion(pair.Key, pair.Value)));
    }
}

/// <summary>Identifies one transaction independent of its commit record.</summary>
public readonly record struct TransactionId
{
    private readonly Guid value;

    /// <summary>Creates a transaction identifier.</summary>
    public TransactionId(Guid value)
    {
        if (value == Guid.Empty)
        {
            throw new ArgumentException("A transaction identifier cannot be empty.", nameof(value));
        }

        this.value = value;
    }

    /// <summary>Allocates a new transaction identifier.</summary>
    public static TransactionId New() => new(Guid.NewGuid());

    /// <summary>The underlying identifier.</summary>
    public Guid Value => value == Guid.Empty ? throw new InvalidOperationException("The transaction identifier is uninitialized.") : value;

    /// <inheritdoc />
    public override string ToString() => Value.ToString("N");
}

/// <summary>Identifies one immutable commit record.</summary>
public readonly record struct CommitId
{
    private readonly Guid value;

    /// <summary>Creates a commit identifier.</summary>
    public CommitId(Guid value)
    {
        if (value == Guid.Empty)
        {
            throw new ArgumentException("A commit identifier cannot be empty.", nameof(value));
        }

        this.value = value;
    }

    /// <summary>Allocates a new commit identifier.</summary>
    public static CommitId New() => new(Guid.NewGuid());

    /// <summary>The underlying identifier.</summary>
    public Guid Value => value == Guid.Empty ? throw new InvalidOperationException("The commit identifier is uninitialized.") : value;

    /// <inheritdoc />
    public override string ToString() => Value.ToString("N");
}

/// <summary>Commit metadata emitted for one transaction participant.</summary>
public sealed record ShardCommitMetadata
{
    /// <summary>Creates metadata and validates its sequence range.</summary>
    public ShardCommitMetadata(ShardId shard, ulong? firstSequence, ulong? lastSequence)
    {
        if (!shard.IsInitialized)
        {
            throw new ArgumentException("The shard must be initialized.", nameof(shard));
        }

        if (firstSequence is null != (lastSequence is null))
        {
            throw new ArgumentException("FirstSequence and LastSequence must be supplied together.");
        }

        if (firstSequence is not null && firstSequence > lastSequence)
        {
            throw new ArgumentOutOfRangeException(nameof(firstSequence), "FirstSequence cannot exceed LastSequence.");
        }

        Shard = shard;
        FirstSequence = firstSequence;
        LastSequence = lastSequence;
    }

    /// <summary>The participant that committed the batch.</summary>
    public ShardId Shard { get; }

    /// <summary>The first journal sequence in the batch, when available.</summary>
    public ulong? FirstSequence { get; }

    /// <summary>The last journal sequence in the batch, when available.</summary>
    public ulong? LastSequence { get; }
}

/// <summary>The durable receipt that identifies one successful transaction.</summary>
public sealed record CommitRecord
{
    /// <summary>Creates a validated immutable commit record.</summary>
    public CommitRecord(
        CommitId commitId,
        TransactionId transactionId,
        TransactionIsolationLevel isolationLevel,
        DateTimeOffset committedAt,
        int mutationCount,
        IReadOnlyList<ShardCommitMetadata> shards,
        CrossShardConsistencyMetadata consistency)
    {
        if (mutationCount < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(mutationCount));
        }

        ArgumentNullException.ThrowIfNull(shards);
        ArgumentNullException.ThrowIfNull(consistency);
        CommitId = commitId;
        TransactionId = transactionId;
        IsolationLevel = isolationLevel;
        CommittedAt = committedAt;
        MutationCount = mutationCount;
        Shards = new ReadOnlyCollection<ShardCommitMetadata>(shards.ToArray());
        Consistency = consistency;
    }

    /// <summary>The immutable commit identity.</summary>
    public CommitId CommitId { get; }

    /// <summary>The originating transaction identity.</summary>
    public TransactionId TransactionId { get; }

    /// <summary>The isolation requested by the transaction.</summary>
    public TransactionIsolationLevel IsolationLevel { get; }

    /// <summary>The server timestamp at which the commit was recorded.</summary>
    public DateTimeOffset CommittedAt { get; }

    /// <summary>The count of committed document mutations.</summary>
    public int MutationCount { get; }

    /// <summary>Per-participant sequence ranges.</summary>
    public IReadOnlyList<ShardCommitMetadata> Shards { get; }

    /// <summary>The cross-shard read-after-write consistency cut.</summary>
    public CrossShardConsistencyMetadata Consistency { get; }
}

/// <summary>Result returned when a transaction reaches a terminal state.</summary>
public sealed record TransactionCommitResult(
    TransactionOutcome Outcome,
    CommitRecord? Commit,
    IReadOnlyList<StorageWriteResult> Writes,
    ConstraintDecision? ConstraintDecision = null)
{
    /// <summary>Creates an empty conflict result.</summary>
    public static TransactionCommitResult Conflict { get; } = new(TransactionOutcome.Conflict, null, []);

    /// <summary>Creates an empty rollback result.</summary>
    public static TransactionCommitResult RolledBack { get; } = new(TransactionOutcome.RolledBack, null, []);
}
