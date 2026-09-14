using System.Text.Json;
using TerraBase;

internal static class TransactionTests
{
    public static async Task RunAsync()
    {
        await CommitsOneAtomicBatchAndPublishesConsistencyMetadata();
        await RejectsAStaleSerializableReadWithoutPartialWrites();
        ConstraintDecisionsKeepStrictReferentialChecksLocal();
    }

    private static async Task CommitsOneAtomicBatchAndPublishesConsistencyMetadata()
    {
        await using var provider = new TransactionTestProvider();
        var coordinator = new TransactionCoordinator(provider, new ShardId("shard-a"));
        var firstKey = Key("one");
        var secondKey = Key("two");
        using var firstBody = JsonDocument.Parse("{\"value\":1}");
        using var secondBody = JsonDocument.Parse("{\"value\":2}");
        await using var transaction = await coordinator.BeginAsync();

        await transaction.UpsertAsync(firstKey, firstBody.RootElement, DocumentPrecondition.Missing);
        await transaction.UpsertAsync(secondKey, secondBody.RootElement, DocumentPrecondition.Missing);
        var result = await transaction.CommitAsync();

        Assert(result.Outcome == TransactionOutcome.Committed, "an atomic transaction must commit every mutation");
        Assert(result.Commit is { MutationCount: 2 }, "the commit record must contain the committed mutation count");
        Assert(result.Commit.Consistency.Sequences[new ShardId("shard-a")] == 2, "the commit must publish the shard sequence cut");
        Assert((await provider.ReadAsync(firstKey))?.Body.GetProperty("value").GetInt32() == 1, "the first mutation must be durable");
        Assert((await provider.ReadAsync(secondKey))?.Body.GetProperty("value").GetInt32() == 2, "the second mutation must be durable");
    }

    private static async Task RejectsAStaleSerializableReadWithoutPartialWrites()
    {
        await using var provider = new TransactionTestProvider();
        var key = Key("serializable");
        using var initialBody = JsonDocument.Parse("{\"value\":1}");
        await provider.UpsertAsync(key, initialBody.RootElement, DocumentPrecondition.Missing);
        var coordinator = new TransactionCoordinator(provider, new ShardId("shard-a"));
        await using var transaction = await coordinator.BeginAsync(new TransactionOptions
        {
            IsolationLevel = TransactionIsolationLevel.Serializable,
        });

        _ = await transaction.ReadAsync(key);
        using var changedBody = JsonDocument.Parse("{\"value\":2}");
        await provider.UpsertAsync(key, changedBody.RootElement, DocumentPrecondition.Version(1));
        using var stagedBody = JsonDocument.Parse("{\"value\":3}");
        await transaction.UpsertAsync(Key("other"), stagedBody.RootElement, DocumentPrecondition.Missing);
        var result = await transaction.CommitAsync();

        Assert(result.Outcome == TransactionOutcome.Conflict, "a serializable transaction must reject a changed read set");
        Assert(await provider.ReadAsync(Key("other")) is null, "a read conflict must leave every staged mutation uncommitted");
        Assert((await provider.ReadAsync(key))?.Body.GetProperty("value").GetInt32() == 2, "the concurrent writer must remain visible");
    }

    private static void ConstraintDecisionsKeepStrictReferentialChecksLocal()
    {
        var coordinator = new TransactionConstraintCoordinator();
        var local = coordinator.Coordinate(new TransactionOptions
        {
            IsolationLevel = TransactionIsolationLevel.Serializable,
            ReferentialIntegrity = ReferentialIntegrityMode.Strict,
            ParticipatingShards = new HashSet<ShardId> { new("shard-a") },
        });
        var crossShard = coordinator.Coordinate(new TransactionOptions
        {
            IsolationLevel = TransactionIsolationLevel.Serializable,
            ReferentialIntegrity = ReferentialIntegrityMode.Strict,
            ParticipatingShards = new HashSet<ShardId> { new("shard-a"), new("shard-b") },
        });

        Assert(local.Single(decision => decision.ConstraintName == "referential-integrity").Kind == ConstraintDecisionKind.Enforced, "strict local referential checks require serializable isolation");
        Assert(crossShard.Single(decision => decision.ConstraintName == "referential-integrity").Kind == ConstraintDecisionKind.Deferred, "strict cross-shard referential checks must be deferred");
    }

    private static DocumentKey Key(string id) => new(new TenantId("transaction-tests"), new CollectionId("documents"), id);

    private static void Assert(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }

    private sealed class TransactionTestProvider : IStorageProvider, ITransactionalStorageProvider
    {
        private readonly object gate = new();
        private readonly Dictionary<DocumentKey, Document> documents = new();
        private readonly Dictionary<DocumentKey, ulong> watermarks = new();
        private ulong sequence;

        public ValueTask<Document?> ReadAsync(DocumentKey key, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (gate)
            {
                return ValueTask.FromResult(documents.GetValueOrDefault(key));
            }
        }

        public ValueTask<StorageWriteResult> UpsertAsync(DocumentKey key, JsonElement body, DocumentPrecondition precondition, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (gate)
            {
                return ValueTask.FromResult(ApplyUpsert(key, body, precondition));
            }
        }

        public ValueTask<StorageWriteResult> DeleteAsync(DocumentKey key, DocumentPrecondition precondition, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (gate)
            {
                var current = documents.GetValueOrDefault(key);
                if (current is null)
                {
                    return ValueTask.FromResult(StorageWriteResult.NotFound);
                }

                if (!precondition.MatchesForTests(current))
                {
                    return ValueTask.FromResult(StorageWriteResult.Conflict);
                }

                documents.Remove(key);
                sequence++;
                return ValueTask.FromResult(new StorageWriteResult(WriteOutcome.Deleted, current));
            }
        }

        public async IAsyncEnumerable<Document> ScanAsync(TenantId tenant, CollectionId collection, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            Document[] values;
            lock (gate)
            {
                values = documents.Where(pair => pair.Key.Tenant == tenant && pair.Key.Collection == collection).Select(pair => pair.Value).ToArray();
            }

            foreach (var value in values)
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return value;
                await Task.CompletedTask;
            }
        }

        public ValueTask<StorageBatchResult> ApplyBatchAsync(IReadOnlyList<StorageMutation> mutations, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (gate)
            {
                var snapshot = documents.ToDictionary(pair => pair.Key, pair => pair.Value);
                var snapshotWatermarks = watermarks.ToDictionary(pair => pair.Key, pair => pair.Value);
                var snapshotSequence = sequence;
                var writes = new List<StorageWriteResult>(mutations.Count);
                foreach (var mutation in mutations)
                {
                    var result = mutation.Kind == StorageMutationKind.Upsert
                        ? ApplyUpsert(mutation.Key, mutation.Body, mutation.Precondition)
                        : ApplyDelete(mutation.Key, mutation.Precondition);
                    if (result.Outcome is WriteOutcome.Conflict or WriteOutcome.NotFound)
                    {
                        documents.Clear();
                        foreach (var pair in snapshot) documents.Add(pair.Key, pair.Value);
                        watermarks.Clear();
                        foreach (var pair in snapshotWatermarks) watermarks.Add(pair.Key, pair.Value);
                        sequence = snapshotSequence;
                        return ValueTask.FromResult(result.Outcome == WriteOutcome.NotFound ? StorageBatchResult.NotFound : StorageBatchResult.Conflict);
                    }

                    writes.Add(result);
                }

                var first = sequence - (ulong)writes.Count + 1;
                return ValueTask.FromResult(new StorageBatchResult(StorageBatchOutcome.Committed, writes, writes.Count == 0 ? null : first, writes.Count == 0 ? null : sequence));
            }
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        private StorageWriteResult ApplyUpsert(DocumentKey key, JsonElement body, DocumentPrecondition precondition)
        {
            var current = documents.GetValueOrDefault(key);
            if (!precondition.MatchesForTests(current))
            {
                return StorageWriteResult.Conflict;
            }

            var version = watermarks.GetValueOrDefault(key) + 1;
            var document = new Document(key, body, version, DateTimeOffset.UtcNow);
            documents[key] = document;
            watermarks[key] = version;
            sequence++;
            return new(current is null ? WriteOutcome.Created : WriteOutcome.Updated, document);
        }

        private StorageWriteResult ApplyDelete(DocumentKey key, DocumentPrecondition precondition)
        {
            var current = documents.GetValueOrDefault(key);
            if (current is null)
            {
                return StorageWriteResult.NotFound;
            }

            if (!precondition.MatchesForTests(current))
            {
                return StorageWriteResult.Conflict;
            }

            documents.Remove(key);
            sequence++;
            return new(WriteOutcome.Deleted, current);
        }
    }
}

internal static class TransactionTestPreconditions
{
    public static bool MatchesForTests(this DocumentPrecondition precondition, Document? current) =>
        (precondition.MustBeMissing && current is null)
        || (!precondition.MustBeMissing && !precondition.MustExist)
        || (precondition.MustExist && current is not null && current.Version == precondition.ExpectedVersion);
}
