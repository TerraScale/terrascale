using System.Text.Json;
using TerraBase.Engine.Lattices;

namespace TerraBase.Engine;

public sealed record StoredDocument(string Id, long Version, ReadOnlyMemory<byte> Json, Guid Scope, bool Deleted = false, bool UniqueConflict = false);

public sealed record DocumentPage(IReadOnlyList<StoredDocument> Documents, bool HasMore);

public readonly record struct DocumentVersion(long Version, bool Deleted);

public sealed record CollectionInfo(string Name, long CreatedAt, string Id = "");

public sealed record DocumentChange(string Collection, string Id, long Version, ReadOnlyMemory<byte>? Json);

public static class DocumentDelta
{
    internal const string Collections = "_collections";

    internal const string Indexes = "_indexes";

    internal const string Unique = "_unique";
}

public interface IStorageTransaction : IAsyncDisposable
{
    long Work { get; }

    const int MaxChangeGroupChanges = 1024;

    const int MaxChangeGroupBytes = 2 * 1024 * 1024;

    const int MaxSingleChangeBytes = 3 * 1024 * 1024;

    const int MaxSystemGroupChanges = 16_384;

    const int MaxSystemGroupBytes = 3 * 1024 * 1024;

    Guid Scope { get; }

    ValueTask<Ulid> GetIncarnationAsync(CancellationToken cancellationToken);

    ValueTask<StoredDocument?> GetAsync(string collection, string id, CancellationToken cancellationToken);

    ValueTask<long> GetDocumentBytesAsync(string collection, string id, CancellationToken cancellationToken);

    ValueTask<long> GetStoredDocumentBytesAsync(CancellationToken cancellationToken);

    ValueTask<DocumentVersion?> GetVersionAsync(string collection, string id, CancellationToken cancellationToken);

    ValueTask<long> PutAsync(string collection, string id, ReadOnlyMemory<byte> json, CancellationToken cancellationToken);

    ValueTask<(long Version, ReadOnlyMemory<byte> Stored)> PutAdmittedAsync(
    string collection, string id, ReadOnlyMemory<byte> json, CancellationToken cancellationToken);

    ValueTask<long> PatchAsync(
    string collection, string id, ReadOnlyMemory<byte> json, IReadOnlyList<FieldOperation> operations, CancellationToken cancellationToken);

    async ValueTask PutBatchAsync(
    IReadOnlyList<(string Collection, string Id, ReadOnlyMemory<byte> Json)> puts, Memory<(long Version, DocumentVersion? Previous)> results,
    CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(puts);
        ArgumentOutOfRangeException.ThrowIfLessThan(results.Length, puts.Count);
        results.Span[..puts.Count].Clear();
        for (var i = 0; i < puts.Count; i++)
        {
            var (collection, id, json) = puts[i];
            var previous = await GetVersionAsync(collection, id, cancellationToken).ConfigureAwait(false);
            var version = await PutAsync(collection, id, json, cancellationToken).ConfigureAwait(false);
            results.Span[i] = (version, previous);
        }
    }

    ValueTask<bool> DeleteAsync(string collection, string id, CancellationToken cancellationToken);

    ValueTask<long?> TombstoneAsync(string collection, string id, CancellationToken cancellationToken);

    ValueTask<bool> HoldsHistoryAtOrBelowAsync(Hlc cut, CancellationToken cancellationToken);

    ValueTask<FeedPage> ReadFeedAsync(
    FeedCursor? cursor, IReadOnlySet<string>? collections, int maxEvents, long maxBytes, CancellationToken cancellationToken);

    ValueTask<FeedPage> ReadFeedMetadataAsync(
    FeedCursor? cursor, IReadOnlySet<string>? collections, int maxEvents, long maxBytes, CancellationToken cancellationToken);

    ValueTask<IReadOnlyDictionary<ReplicaId, Hlc>> GetFeedHeadAsync(CancellationToken cancellationToken);

    ValueTask<int> TrimFeedAsync(Hlc before, int limit, CancellationToken cancellationToken);

    ValueTask<DocumentPage> ListAsync(string collection, string? afterId, int limit, long maxBytes, CancellationToken cancellationToken);

    ValueTask<QueryPage> QueryAsync(DocumentQuery query, CancellationToken cancellationToken, QueryObservation? observation = null);

    ValueTask<long> GetOffsetAsync(CancellationToken cancellationToken);

    ValueTask<(long Count, bool Complete)> CountAsync(string collection, DocumentFilter? filter, long maxExamined, CancellationToken cancellationToken, QueryObservation? observation = null);

    ValueTask<bool> HasCollectionStateAsync(string name, bool prefix, CancellationToken cancellationToken);

    ValueTask<bool> CreateCollectionAsync(CollectionInfo collection, CancellationToken cancellationToken);

    ValueTask<CollectionInfo?> GetCollectionAsync(string name, CancellationToken cancellationToken);

    ValueTask<JsonElement?> GetCollectionSchemaAsync(string name, CancellationToken cancellationToken);

    ValueTask<CollectionAccessRules?> GetCollectionRulesAsync(string name, CancellationToken cancellationToken);

    ValueTask<bool> SetCollectionRulesAsync(string name, CollectionAccessRules rules, CancellationToken cancellationToken);

    ValueTask<DocumentOwnerProof?> GetDocumentOwnerProofAsync(string collection, string id, CancellationToken cancellationToken);

    ValueTask<bool> SetCollectionSchemaAsync(string name, JsonElement? schema, CancellationToken cancellationToken);

    ValueTask<JsonElement?> GetCollectionTableAsync(string name, CancellationToken cancellationToken);

    ValueTask<bool> SetCollectionTableAsync(string name, JsonElement definition, CancellationToken cancellationToken);

    ValueTask<string?> GetCollectionTtlAsync(string name, CancellationToken cancellationToken);

    ValueTask<bool> SetCollectionTtlAsync(string name, string? path, CancellationToken cancellationToken);

    ValueTask<(IReadOnlyList<CollectionInfo> Collections, bool HasMore)> ListCollectionsAsync(
    string? afterName, int limit, CancellationToken cancellationToken);

    ValueTask<bool> DropCollectionAsync(string name, CancellationToken cancellationToken);

    ValueTask<long> CountDocumentsAsync(string collection, CancellationToken cancellationToken);

    ValueTask<IReadOnlyList<CollectionStats>> ListCollectionStatsAsync(CancellationToken cancellationToken);

    IAsyncEnumerable<StoredDocument> QuerySqlAsync(SqlQuery query, CancellationToken cancellationToken, QueryObservation? observation = null);

    ValueTask<SqlProgramResult> ExecuteSqlAsync(SqlProgram program, CancellationToken cancellationToken);

    ValueTask<SqlQueryPlan> ExplainSqlAsync(SqlQuery query, CancellationToken cancellationToken);

    ValueTask<long> DeleteCollectionsAsync(string prefix, CancellationToken cancellationToken);

    ValueTask<IReadOnlyList<IndexDefinition>> ListIndexesAsync(CancellationToken cancellationToken);

    ValueTask<bool> CreateIndexAsync(IndexDefinition index, CancellationToken cancellationToken);

    ValueTask<bool> BuildVectorIndexAsync(CancellationToken cancellationToken);

    ValueTask<bool> DropIndexAsync(string name, CancellationToken cancellationToken);

    ValueTask<IReadOnlyList<UniqueConstraint>> GetUniqueConstraintsAsync(string collection, CancellationToken cancellationToken);

    ValueTask<IReadOnlyList<(string Constraint, string Id)>> GetUniqueConflictsAsync(
    string collection, ReadOnlyMemory<byte> json, int limit, CancellationToken cancellationToken);

    ValueTask SetUniqueConstraintsAsync(string collection, IReadOnlyList<UniqueConstraint> constraints, bool reindex, CancellationToken cancellationToken);

    ValueTask<CommitResult> CommitAsync(CancellationToken cancellationToken);
}
