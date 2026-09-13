using System.Text.Json;

namespace TerraBase;

/// <summary>The kind of mutation contained in a storage batch.</summary>
public enum StorageMutationKind
{
    /// <summary>Creates or replaces one document.</summary>
    Upsert,

    /// <summary>Deletes one document and records its tombstone.</summary>
    Delete,
}

/// <summary>One document mutation evaluated inside an atomic storage batch.</summary>
public sealed record StorageMutation
{
    private StorageMutation(
        StorageMutationKind kind,
        DocumentKey key,
        JsonElement body,
        DocumentPrecondition precondition)
    {
        Kind = kind;
        Key = key;
        Body = body;
        Precondition = precondition;
    }

    /// <summary>Creates an upsert mutation.</summary>
    /// <param name="key">The document key.</param>
    /// <param name="body">The JSON document body.</param>
    /// <param name="precondition">The version precondition.</param>
    public static StorageMutation Upsert(
        DocumentKey key,
        JsonElement body,
        DocumentPrecondition precondition = default) =>
        new(StorageMutationKind.Upsert, key, body.Clone(), precondition == default ? DocumentPrecondition.Any : precondition);

    /// <summary>Creates a delete mutation.</summary>
    /// <param name="key">The document key.</param>
    /// <param name="precondition">The version precondition.</param>
    public static StorageMutation Delete(
        DocumentKey key,
        DocumentPrecondition precondition = default) =>
        new(StorageMutationKind.Delete, key, default, precondition == default ? DocumentPrecondition.Any : precondition);

    /// <summary>The mutation kind.</summary>
    public StorageMutationKind Kind { get; }

    /// <summary>The document key affected by the mutation.</summary>
    public DocumentKey Key { get; }

    /// <summary>The upsert body, or an undefined JSON value for deletes.</summary>
    public JsonElement Body { get; }

    /// <summary>The precondition evaluated against the current batch state.</summary>
    public DocumentPrecondition Precondition { get; }
}

/// <summary>The result of an atomic storage batch.</summary>
public enum StorageBatchOutcome
{
    /// <summary>Every mutation committed in one durable transaction.</summary>
    Committed,

    /// <summary>A mutation precondition failed and no mutation committed.</summary>
    Conflict,

    /// <summary>A delete targeted a document that was absent.</summary>
    NotFound,
}

/// <summary>The durable receipt returned by an atomic storage batch.</summary>
public sealed record StorageBatchResult(
    StorageBatchOutcome Outcome,
    IReadOnlyList<StorageWriteResult> Writes,
    ulong? FirstSequence,
    ulong? LastSequence)
{
    /// <summary>Creates a conflict result with no committed writes.</summary>
    public static StorageBatchResult Conflict { get; } = new(StorageBatchOutcome.Conflict, [], null, null);

    /// <summary>Creates a not-found result with no committed writes.</summary>
    public static StorageBatchResult NotFound { get; } = new(StorageBatchOutcome.NotFound, [], null, null);
}

/// <summary>
/// Optional storage capability for applying several document mutations as one
/// atomic transaction. Providers that do not support durable transactions can
/// continue implementing <see cref="IStorageProvider"/> alone.
/// </summary>
public interface ITransactionalStorageProvider
{
    /// <summary>Applies every mutation atomically, or commits none of them.</summary>
    /// <param name="mutations">The ordered mutations to evaluate.</param>
    /// <param name="cancellationToken">The cancellation signal.</param>
    /// <returns>The committed writes and journal sequence range.</returns>
    ValueTask<StorageBatchResult> ApplyBatchAsync(
        IReadOnlyList<StorageMutation> mutations,
        CancellationToken cancellationToken = default);
}
