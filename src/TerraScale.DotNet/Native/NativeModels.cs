using System.Collections.Immutable;

namespace TerraScale.DotNet.Native;

/// <summary>Provisioned canonical tenant and database identifiers with an exact unsigned shard; zero is valid for direct native clients.</summary>
/// <param name="Tenant">The canonical lowercase tenant UUID.</param>
/// <param name="Database">The canonical lowercase database UUID.</param>
/// <param name="Shard">The exact provisioned unsigned shard identifier.</param>
public sealed record NativeScope(string Tenant, string Database, ulong Shard);
/// <summary>An immutable expiry and canonical nonce supplied before the first mutation attempt.</summary>
/// <param name="ExpiresAtUnixMs">The original unsigned Unix millisecond expiry.</param>
/// <param name="Nonce">The original 16-byte lowercase hexadecimal nonce.</param>
public sealed record NativeTransactionId(ulong ExpiresAtUnixMs, string Nonce);
/// <summary>The writer identity supplied by an admitted single-writer authority.</summary>
/// <param name="Replica">The canonical lowercase replica UUID.</param>
/// <param name="Incarnation">The 16-byte lowercase hexadecimal incarnation.</param>
public sealed record NativeWriter(string Replica, string Incarnation);
/// <summary>The exact authority mode included in mutation identity and effect hashing.</summary>
public abstract record NativeAuthority
{
    /// <summary>Leaderless authority without an inferred writer fence.</summary>
    public sealed record Leaderless : NativeAuthority;
    /// <summary>An explicit generation and writer; encoding does not establish admission.</summary>
    /// <param name="Generation">The positive admitted authority generation.</param>
    /// <param name="Writer">The admitted writer identity.</param>
    public sealed record SingleWriter(ulong Generation, NativeWriter Writer) : NativeAuthority;
}

/// <summary>An atomic document precondition evaluated by the native producer.</summary>
public abstract record NativeCondition
{
    /// <summary>An unconditional write where the admitted authority permits it.</summary>
    public sealed record None : NativeCondition;
    /// <summary>A create-only precondition requiring the document to be absent.</summary>
    public sealed record Absent : NativeCondition;
    /// <summary>An exact producer-issued state token for a version-checked write.</summary>
    /// <param name="Token">The opaque producer-issued condition token.</param>
    public sealed record Version(string Token) : NativeCondition;
}

/// <summary>An immutable typed native value; string formatting never prints its payload.</summary>
public abstract record NativeValue
{
    /// <inheritdoc />
    public sealed override string ToString() => "Native typed value";
    /// <summary>Explicit native null, distinct from a missing field.</summary>
    public sealed record Null : NativeValue;
    /// <summary>A native Boolean value.</summary>
    /// <param name="Value">The Boolean scalar.</param>
    public sealed record Boolean(bool Value) : NativeValue;
    /// <summary>An exact signed 64-bit native integer.</summary>
    /// <param name="Value">The signed integer scalar.</param>
    public sealed record Integer(long Value) : NativeValue;
    /// <summary>Strict UTF-8 text without numeric or JSON coercion.</summary>
    /// <param name="Value">The text scalar, including an empty string.</param>
    public sealed record Text(string Value) : NativeValue;
    /// <summary>Immutable bytes encoded as canonical base64url on the wire.</summary>
    /// <param name="Value">The immutable byte scalar, including an empty sequence.</param>
    public sealed record Bytes(ImmutableArray<byte> Value) : NativeValue;
    /// <summary>A map with strictly ordered positive numeric field identifiers.</summary>
    /// <param name="Fields">Fields in strictly increasing numeric identifier order.</param>
    public sealed record Map(ImmutableArray<NativeField> Fields) : NativeValue;
    /// <summary>A bounded native array; descendant arrays are rejected by the codec.</summary>
    /// <param name="Items">The immutable ordered values.</param>
    public sealed record Array(ImmutableArray<NativeValue> Items) : NativeValue;
}

/// <summary>A numeric field binding and its native typed value.</summary>
/// <param name="Id">The positive provisioned field identifier.</param>
/// <param name="Value">The typed native field value.</param>
public sealed record NativeField(uint Id, NativeValue Value);
/// <summary>An exact observed origin sequence from a producer Point.</summary>
/// <param name="Replica">The canonical lowercase replica UUID.</param>
/// <param name="Incarnation">The 16-byte lowercase hexadecimal incarnation.</param>
/// <param name="Sequence">The exact positive sequence for this origin.</param>
public sealed record NativeObserved(string Replica, string Incarnation, ulong Sequence);
/// <summary>A native event scoped to its database, shard and origin.</summary>
/// <param name="Database">The canonical lowercase database UUID.</param>
/// <param name="Shard">The exact unsigned shard.</param>
/// <param name="Replica">The canonical lowercase replica UUID.</param>
/// <param name="Incarnation">The 16-byte lowercase hexadecimal incarnation.</param>
/// <param name="Sequence">The exact positive sequence for this origin.</param>
public sealed record NativeEvent(string Database, ulong Shard, string Replica, string Incarnation, ulong Sequence);
/// <summary>The complete live or tombstone effect for one document.</summary>
public abstract record NativeWriteValue
{
    /// <summary>A complete live document with ordered typed fields.</summary>
    /// <param name="Fields">Complete fields in strictly increasing identifier order.</param>
    public sealed record Live(ImmutableArray<NativeField> Fields) : NativeWriteValue;
    /// <summary>A native document tombstone.</summary>
    public sealed record Tombstone : NativeWriteValue;
}

/// <summary>One scoped, schema-bound document effect with an explicit precondition and causal context.</summary>
/// <param name="Collection">The canonical lowercase provisioned collection UUID.</param>
/// <param name="Document">The canonical lowercase document UUID.</param>
/// <param name="SchemaGeneration">The positive provisioned schema generation.</param>
/// <param name="SchemaDigest">The 32-byte lowercase hexadecimal schema digest.</param>
/// <param name="Condition">The explicit atomic precondition.</param>
/// <param name="Observed">The ordered causal context, preserving exact sequences.</param>
/// <param name="Value">The complete live or tombstone effect.</param>
public sealed record NativeWrite(
    string Collection,
    string Document,
    ulong SchemaGeneration,
    string SchemaDigest,
    NativeCondition Condition,
    ImmutableArray<NativeObserved> Observed,
    NativeWriteValue Value);

/// <summary>An immutable ordered atomic group and transaction identity supplied before dispatch.</summary>
/// <param name="TransactionId">The original expiry and nonce, fixed before dispatch.</param>
/// <param name="Authority">The explicitly admitted authority mode.</param>
/// <param name="Writes">The immutable group in collection/document identifier order.</param>
public sealed record NativeCommit(
    NativeTransactionId TransactionId,
    NativeAuthority Authority,
    ImmutableArray<NativeWrite> Writes);

/// <summary>The original transaction, authority and effect digest used for commit and status recovery.</summary>
/// <param name="TransactionId">The original expiry and nonce.</param>
/// <param name="Authority">The original authority envelope.</param>
/// <param name="EffectDigest">The 32-byte lowercase hexadecimal canonical effect digest.</param>
public sealed record NativeTransactionIdentity(
    NativeTransactionId TransactionId,
    NativeAuthority Authority,
    string EffectDigest);

/// <summary>A complete native sibling, including its event, schema binding and live or tombstone value.</summary>
/// <param name="Event">The scope-bound causal event.</param>
/// <param name="SchemaGeneration">The positive schema generation.</param>
/// <param name="SchemaDigest">The 32-byte lowercase hexadecimal schema digest.</param>
/// <param name="Value">The complete live or tombstone value.</param>
public sealed record NativeSibling(
    NativeEvent Event,
    ulong SchemaGeneration,
    string SchemaDigest,
    NativeWriteValue Value);

/// <summary>The complete native document state.</summary>
public enum NativePointState
{
    /// <summary>No document siblings exist.</summary>
    Absent,
    /// <summary>One tombstone sibling exists.</summary>
    Deleted,
    /// <summary>One live sibling exists.</summary>
    Value,
    /// <summary>Multiple causal siblings remain unresolved.</summary>
    Conflict
}
/// <summary>A full native Point that preserves absent, deleted, value or conflict state and causal siblings.</summary>
/// <param name="State">The complete native state.</param>
/// <param name="StateVersion">The producer-issued condition token, or null for an absent point.</param>
/// <param name="Context">The complete ordered causal context.</param>
/// <param name="Siblings">The complete ordered live and tombstone siblings.</param>
public sealed record NativePoint(
    NativePointState State,
    string? StateVersion,
    ImmutableArray<NativeObserved> Context,
    ImmutableArray<NativeSibling> Siblings);

/// <summary>Published native scalar and presence query operators.</summary>
public enum NativeQueryOperator
{
    /// <summary>Scalar equality.</summary>
    Equal,
    /// <summary>Strict scalar upper bound.</summary>
    LessThan,
    /// <summary>Inclusive scalar upper bound.</summary>
    LessThanOrEqual,
    /// <summary>Strict scalar lower bound.</summary>
    GreaterThan,
    /// <summary>Inclusive scalar lower bound.</summary>
    GreaterThanOrEqual,
    /// <summary>The bound field contains explicit native null.</summary>
    IsNull,
    /// <summary>The bound field is absent.</summary>
    IsMissing
}

/// <summary>A typed predicate over a bounded numeric path; presence operators omit the scalar value.</summary>
/// <param name="Operator">The published scalar or presence operator.</param>
/// <param name="Path">The ordered provisioned numeric field path.</param>
/// <param name="Value">The typed scalar for comparisons; absent for presence operators.</param>
public sealed record NativeQueryPredicate(
    NativeQueryOperator Operator,
    ImmutableArray<uint> Path,
    NativeValue? Value = null);

/// <summary>A canonical document identifier and its complete native Point.</summary>
/// <param name="Document">The canonical lowercase document UUID.</param>
/// <param name="Point">The complete native document point.</param>
public sealed record NativeQueryRow(string Document, NativePoint Point);
/// <summary>A bounded ordered page with the producer-issued opaque continuation cursor.</summary>
/// <param name="Rows">The complete ordered page rows.</param>
/// <param name="NextCursor">The opaque producer-issued continuation, or null at the end.</param>
public sealed record NativeQueryPage(ImmutableArray<NativeQueryRow> Rows, string? NextCursor);
/// <summary>The durability guaranteed by a native receipt.</summary>
public enum NativeDurability
{
    /// <summary>The local synchronized durable frontier.</summary>
    LocalDurable,
    /// <summary>The producer completed its configured object publication and readback barrier.</summary>
    ObjectDurable
}
/// <summary>An identity-bound durable receipt; its event identifies the committed database scope.</summary>
/// <param name="Durability">The producer's guaranteed durability barrier.</param>
/// <param name="Identity">The exact original transaction identity.</param>
/// <param name="Event">The scope-bound committed event.</param>
/// <param name="CommitSequence">The exact positive commit sequence.</param>
public sealed record NativeCommitReceipt(
    NativeDurability Durability,
    NativeTransactionIdentity Identity,
    NativeEvent Event,
    ulong CommitSequence);

/// <summary>Published native transaction status states.</summary>
public enum NativeTransactionState
{
    /// <summary>The original receipt is retained.</summary>
    Committed,
    /// <summary>The original precondition failure is retained.</summary>
    Rejected,
    /// <summary>An outcome is not yet established.</summary>
    Pending,
    /// <summary>No known outcome; this never proves abort.</summary>
    Unknown,
    /// <summary>The identity expired; this never proves abort.</summary>
    Expired
}
/// <summary>An authoritative status for the original identity; unresolved states have no outcome.</summary>
/// <param name="Identity">The exact original transaction identity.</param>
/// <param name="State">The retained or unresolved native status.</param>
/// <param name="Receipt">The original receipt only when committed.</param>
public sealed record NativeTransactionStatus(
    NativeTransactionIdentity Identity,
    NativeTransactionState State,
    NativeCommitReceipt? Receipt = null);

/// <summary>What a failure establishes about the original mutation.</summary>
public enum NativeMutationOutcome
{
    /// <summary>A retained precondition rejection.</summary>
    Rejected,
    /// <summary>The original attempt remains unresolved.</summary>
    Uncertain,
    /// <summary>This failure does not establish a mutation outcome.</summary>
    Unknown
}
/// <summary>Fixed safe error metadata and the original mutation identity, without raw producer text.</summary>
/// <param name="Code">The fixed native or local failure code.</param>
/// <param name="Status">The original HTTP status, when available.</param>
/// <param name="RetryAfter">The producer's retry delay metadata, when available.</param>
/// <param name="Transaction">The original identity for transaction requests.</param>
/// <param name="MutationOutcome">The outcome established by this failure, when relevant.</param>
public sealed record NativeFailure(
    string Code,
    int? Status = null,
    TimeSpan? RetryAfter = null,
    NativeTransactionIdentity? Transaction = null,
    NativeMutationOutcome? MutationOutcome = null);

/// <summary>A typed successful value or safe failure; no partial response is returned.</summary>
/// <typeparam name="T">The complete decoded native response type.</typeparam>
public abstract record NativeResult<T>
{
    /// <summary>A complete validated native result with its HTTP status.</summary>
    /// <param name="Value">The complete decoded value.</param>
    /// <param name="Status">The original successful HTTP status.</param>
    public sealed record Success(T Value, int Status = 200) : NativeResult<T>;
    /// <summary>A safe native failure with retained uncertainty where applicable.</summary>
    /// <param name="Error">The safe failure metadata.</param>
    public sealed record Failure(NativeFailure Error) : NativeResult<T>;
}

/// <summary>Immutable bytes and identity prepared before any mutation is dispatched.</summary>
public sealed class NativePreparedCommit
{
    internal NativePreparedCommit(Guid owner, NativeScope scope, string? explicitOrigin, NativeTransactionIdentity identity, string requestBody)
    {
        Owner = owner;
        Scope = scope;
        ExplicitOrigin = explicitOrigin;
        Identity = identity;
        RequestBody = requestBody;
    }

    internal Guid Owner { get; }
    /// <summary>The immutable provisioned scope bound to this operation.</summary>
    public NativeScope Scope { get; }
    /// <summary>The optional previously admitted direct-origin binding.</summary>
    public string? ExplicitOrigin { get; }
    /// <summary>The exact original transaction identity.</summary>
    public NativeTransactionIdentity Identity { get; }
    /// <summary>The immutable native request body; it contains sensitive document payloads.</summary>
    public string RequestBody { get; }
    /// <inheritdoc />
    public override string ToString() => $"Prepared native transaction {Identity.TransactionId.Nonce}";
}

/// <summary>The predicate, limit and collection remain fixed across opaque cursor pages.</summary>
public sealed class NativePreparedQuery
{
    internal NativePreparedQuery(Guid owner, string collection, NativeQueryPredicate predicate, int limit)
    {
        Owner = owner;
        Collection = collection;
        Predicate = predicate;
        Limit = limit;
    }

    internal Guid Owner { get; }
    /// <summary>The canonical collection identifier.</summary>
    public string Collection { get; }
    /// <summary>The immutable typed predicate.</summary>
    public NativeQueryPredicate Predicate { get; }
    /// <summary>The immutable requested page limit.</summary>
    public int Limit { get; }
}

/// <summary>A terminal outcome requires an authoritative receipt or retained rejection.</summary>
/// <param name="Identity">The exact original transaction identity.</param>
/// <param name="State">The last authoritative native state, or unknown.</param>
/// <param name="Receipt">The original receipt when committed.</param>
/// <param name="LastFailure">The last safe status request failure, when available.</param>
public sealed record NativeRecoveryResult(
    NativeTransactionIdentity Identity,
    NativeTransactionState State,
    NativeCommitReceipt? Receipt,
    NativeFailure? LastFailure)
{
    /// <summary>Whether an authoritative commit or retained rejection resolved the original transaction.</summary>
    public bool IsResolved => State is NativeTransactionState.Committed or NativeTransactionState.Rejected;
    /// <summary>The retained rejection or uncertainty; committed outcomes have no failure classification.</summary>
    public NativeMutationOutcome? MutationOutcome => State == NativeTransactionState.Committed ? null
        : State == NativeTransactionState.Rejected ? NativeMutationOutcome.Rejected : NativeMutationOutcome.Uncertain;
}
