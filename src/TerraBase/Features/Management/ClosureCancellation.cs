using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using TerraBase.Configuration;
using TerraBase.Engine;
using TerraBase.Engine.Lattices;
using TerraBase.Hosting;
using TerraBase.Primitives;

namespace TerraBase.Features.Management;

internal sealed record ClosureCancellationRequest(int Version, string? TargetId);
internal sealed record ClosureCancellationStatus(string Generation, string Status, int PendingHolders);
internal sealed record ClosureCancellationRecord(string Generation, string? TargetId, string[] ExpectedHolders);
internal sealed record ClosureCancellationReceipt(string Generation, string MachineId, bool Committed, bool Finalized);
internal sealed record ClosureCommitRecord(string Generation, DateTimeOffset CommittedAt);

/// <summary>
/// A generation fence in the auth database, ordered with local intent writes by that file's writer. Every holder
/// acknowledges after installing it; a retained commit mark prevents a cancellation from hiding irreversible work.
/// No subject address or natural-key hash is copied into SYSTEM.
/// </summary>
internal interface IClosureCancellation
{
    ValueTask<bool> AdmitAsync(IStorageTransaction transaction, DocumentState state, CancellationToken cancellationToken);
    ValueTask ObserveAsync(IStorageTransaction transaction, IReadOnlyCollection<(string Collection, string Id)> changed, CancellationToken cancellationToken);
}

internal sealed class ClosureCancellation(TerraBaseOptions options) : IClosureCancellation
{
    internal const string Jobs = "closure_jobs";
    internal const string Fences = "closure_cancellations";
    internal const string Commits = "closure_commits";

    public async ValueTask<bool> AdmitAsync(IStorageTransaction transaction, DocumentState state, CancellationToken cancellationToken)
    {
        var generation = Generation(state.Materialize());
        return generation is null
            || await transaction.GetVersionAsync(Fences, generation + "/cancelled", cancellationToken).ConfigureAwait(false) is null
            || await transaction.GetVersionAsync(Commits, generation, cancellationToken).ConfigureAwait(false) is not null;
    }

    private static string? Generation(ReadOnlyMemory<byte>? json)
    {
        if (json is null)
        {
            return null;
        }

        try
        {
            using var body = JsonDocument.Parse(json.Value, new JsonDocumentOptions { MaxDepth = DocumentJson.MaxStoredDepth + 2 });
            return body.RootElement.ValueKind == JsonValueKind.Object && body.RootElement.TryGetProperty("generation", out var field)
                && field.ValueKind == JsonValueKind.String && EntityIds.IsValid(field.GetString()) ? field.GetString() : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    // Called after all joins, on both streaming and anti-entropy paths, before either can acknowledge the batch.
    public async ValueTask ObserveAsync(IStorageTransaction transaction, IReadOnlyCollection<(string Collection, string Id)> changed, CancellationToken cancellationToken)
    {
        HashSet<string> generations = [];
        foreach (var (collection, id) in changed)
        {
            if ((collection == Fences || collection == Commits) && EntityIds.IsValid(id))
            {
                generations.Add(id);
            }
            else if (collection == Fences && id.EndsWith("/cancelled", StringComparison.Ordinal) && EntityIds.IsValid(id[..^10]))
            {
                generations.Add(id[..^10]);
            }
            else if (collection == Jobs && await transaction.GetAsync(Jobs, id, cancellationToken).ConfigureAwait(false) is { } job)
            {
                if (Generation(job.Json) is { } generation)
                {
                    generations.Add(generation);
                }
            }
        }

        foreach (var generation in generations)
        {
            await FenceAsync(transaction, generation, cancellationToken).ConfigureAwait(false);
        }
    }

    private static async ValueTask<T?> ReadAsync<T>(IStorageTransaction transaction, string id, JsonTypeInfo<T> type, CancellationToken cancellationToken)
        where T : class
    {
        var document = await transaction.GetAsync(Fences, id, cancellationToken).ConfigureAwait(false);
        try
        {
            return document is null ? null : JsonSerializer.Deserialize(document.Json.Span, type);
        }
        catch (Exception error) when (error is JsonException or NotSupportedException)
        {
            // Malformed stored content never stalls a peer stream and never supplies an acknowledgement.
            return null;
        }
    }

    internal async ValueTask FenceAsync(IStorageTransaction transaction, string generation, CancellationToken cancellationToken, string? targetId = null)
    {
        var fence = await ReadAsync(transaction, generation, TerraBaseJsonContext.Default.ClosureCancellationRecord, cancellationToken).ConfigureAwait(false);
        targetId = fence?.TargetId ?? targetId;
        if (fence is null || fence.Generation != generation || !EntityIds.IsValid(targetId))
        {
            return;
        }

        var committed = await transaction.GetVersionAsync(Commits, generation, cancellationToken).ConfigureAwait(false) is not null;
        var finalized = await transaction.GetVersionAsync(Fences, generation + "/cancelled", cancellationToken).ConfigureAwait(false) is not null;
        if (await transaction.GetAsync(Jobs, targetId, cancellationToken).ConfigureAwait(false) is { } job && Generation(job.Json) == generation)
        {
            // The live intent itself is also positive evidence; never remove a committed job even when a caller
            // wrote it without using auth's atomic first-commit helper.
            using var body = JsonDocument.Parse(job.Json, new JsonDocumentOptions { MaxDepth = DocumentJson.MaxStoredDepth + 2 });
            if (!committed && body.RootElement.TryGetProperty("committedAt", out var field) && field.ValueKind == JsonValueKind.String
                && field.TryGetDateTimeOffset(out var at))
            {
                await SystemCatalog.PutAsync(transaction, Commits, generation, new ClosureCommitRecord(generation, at),
                    TerraBaseJsonContext.Default.ClosureCommitRecord, cancellationToken).ConfigureAwait(false);
                committed = true;
            }

            if (finalized && !committed)
            {
                // A different creation at this address is never removed. The cancellation mark outlives tombstone GC,
                // so even a later replay with a newer stamp is removed before the receiving transaction commits.
                _ = await transaction.TombstoneAsync(Jobs, targetId, cancellationToken).ConfigureAwait(false);
            }
        }

        var receiptId = generation + "/holders/" + options.MachineId;
        var previous = await ReadAsync(transaction, receiptId, TerraBaseJsonContext.Default.ClosureCancellationReceipt, cancellationToken).ConfigureAwait(false);
        var next = new ClosureCancellationReceipt(generation, options.MachineId, committed || previous?.Committed == true, finalized || previous?.Finalized == true);
        if (previous != next)
        {
            await SystemCatalog.PutAsync(transaction, Fences, receiptId, next, TerraBaseJsonContext.Default.ClosureCancellationReceipt, cancellationToken).ConfigureAwait(false);
        }
    }
}
