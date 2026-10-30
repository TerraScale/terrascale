using System.Text.Json;
using System.Text.Json.Serialization;
using MessagePack;

namespace TerraScale.TerraBase.Contracts.Documents
{
    /// <summary>A collection.</summary>
    [MessagePackObject(true)]
    public sealed record TerraBaseCollectionInfo(
        string Name,
        [property: JsonPropertyName("created_at"), JsonConverter(typeof(UnixMillisecondsConverter))] DateTimeOffset CreatedAt,
        string Id);

    /// <summary>One page of collections.</summary>
    [MessagePackObject(true)]
    public sealed record TerraBaseCollectionPage(
        IReadOnlyList<TerraBaseCollectionInfo> Items,
        [property: JsonPropertyName("next_cursor")] string? NextCursor);

    /// <summary>A stored document: its id, version (ETag value), state and JSON body.</summary>
    [MessagePackObject(true)]
    public sealed record TerraBaseDocument(string Id, string Version, string State, JsonElement Document, double? Distance = null, double? Score = null);

    /// <summary>The match count of a query.</summary>
    [MessagePackObject(true)]
    public sealed record TerraBaseCount(long Value, bool Exact);

    /// <summary>One page of documents, with the optional count, groups and plan of a query.</summary>
    [MessagePackObject(true)]
    public sealed record TerraBaseDocumentPage(
        IReadOnlyList<TerraBaseDocument> Items,
        [property: JsonPropertyName("next_cursor")] string? NextCursor,
        TerraBaseCount? Count = null,
        IReadOnlyList<TerraBaseGroup>? Groups = null,
        TerraBaseQueryExplain? Explain = null);

    /// <summary>The plan of an explained query.</summary>
    [MessagePackObject(true)]
    public sealed record TerraBaseQueryExplain(
        IReadOnlyList<string> Plan,
        IReadOnlyList<string> Indexes,
        [property: JsonPropertyName("full_scan")] bool FullScan,
        [property: JsonPropertyName("rows_scanned")] long RowsScanned,
        [property: JsonPropertyName("work_units")] long WorkUnits,
        [property: JsonPropertyName("elapsed_ms")] double ElapsedMs,
        [property: JsonPropertyName("suggested_index")] IReadOnlyList<string>? SuggestedIndex = null);

    /// <summary>One aggregate group.</summary>
    [MessagePackObject(true)]
    public sealed record TerraBaseGroup(
        IReadOnlyList<JsonElement> Key,
        long Count,
        IReadOnlyDictionary<string, double?>? Sum = null,
        IReadOnlyDictionary<string, double?>? Avg = null,
        IReadOnlyDictionary<string, double?>? Min = null,
        IReadOnlyDictionary<string, double?>? Max = null);

    /// <summary>The grouping of an aggregate query.</summary>
    [MessagePackObject(true)]
    public sealed record TerraBaseAggregate(
        [property: JsonPropertyName("group_by")] IReadOnlyList<string>? GroupBy = null,
        IReadOnlyList<string>? Sum = null,
        IReadOnlyList<string>? Avg = null,
        IReadOnlyList<string>? Min = null,
        IReadOnlyList<string>? Max = null);

    /// <summary>One sort key: a JSON Pointer, <c>$id</c> or <c>$score</c>, and <c>asc</c> or <c>desc</c>.</summary>
    [MessagePackObject(true)]
    public sealed record TerraBaseSort(string Path, string? Order = null);

    /// <summary>A query body, as <c>POST …/query</c> takes it.</summary>
    [MessagePackObject(true)]
    public sealed record TerraBaseQuery(
        JsonElement? Filter = null,
        IReadOnlyList<TerraBaseSort>? Sort = null,
        IReadOnlyList<string>? Project = null,
        int? Limit = null,
        string? Cursor = null,
        bool? Count = null,
        TerraBaseAggregate? Aggregate = null,
        bool? Explain = null);

    /// <summary>The result of one document write; <see cref="Created"/> is set for creates and puts that created it.</summary>
    [MessagePackObject(true)]
    public sealed record TerraBaseWriteResult(string Id, string Version, long Offset, string Durability, JsonElement? Document = null, bool Created = false);

    /// <summary>
    /// The condition of a write: <see cref="IfMatch"/> is a version or <c>*</c> (exists); <see cref="IfNoneMatch"/> requires
    /// the document to be absent or deleted. A failed condition throws <see cref="TerraBasePreconditionFailedException"/>.
    /// </summary>
    [MessagePackObject(true)]
    public sealed record TerraBasePrecondition(string? IfMatch, bool IfNoneMatch)
    {
        /// <summary>Create-only: the document must be absent or deleted.</summary>
        public static TerraBasePrecondition Absent { get; } = new(null, IfNoneMatch: true);

        /// <summary>The document must exist.</summary>
        public static TerraBasePrecondition Exists { get; } = new("*", IfNoneMatch: false);

        /// <summary>The document's current version must be <paramref name="version"/>.</summary>
        public static TerraBasePrecondition Matches(string version)
        {
            ArgumentException.ThrowIfNullOrEmpty(version);
            return version.Contains('"', StringComparison.Ordinal) || version == "*"
                ? throw new ArgumentException("version must be a TerraBase document version.", nameof(version))
                : new(version, IfNoneMatch: false);
        }
    }

    /// <summary>One operation of an atomic transaction; create instances with the static factories.</summary>
    [MessagePackObject(true)]
    [method: JsonConstructor]
    [method: SerializationConstructor]
    public sealed record TerraBaseTransactionOperation(
        string Op,
        string Collection,
        string? Id = null,
        JsonElement? Document = null,
        [property: JsonPropertyName("if_match")] string? IfMatch = null,
        [property: JsonPropertyName("if_none_match")] string? IfNoneMatch = null,
        JsonElement? Merge = null,
        JsonElement? Filter = null,
        bool? Absent = null,
        long? Gte = null,
        long? Lte = null)
    {
        /// <summary>Creates a document that must be absent or deleted; TerraBase generates a ULID when <paramref name="id"/> is null.</summary>
        public static TerraBaseTransactionOperation Create(string collection, JsonElement document, string? id = null)
        {
            return new("create", collection, id, document);
        }

        /// <summary>Inserts or replaces a document, optionally only at version <paramref name="ifMatch"/>.</summary>
        public static TerraBaseTransactionOperation Put(string collection, string id, JsonElement document, string? ifMatch = null)
        {
            return new("put", collection, id, document, ifMatch);
        }

        /// <summary>Applies an RFC 7396 merge patch to an existing document.</summary>
        public static TerraBaseTransactionOperation Patch(string collection, string id, JsonElement merge, string? ifMatch = null)
        {
            return new("patch", collection, id, IfMatch: ifMatch, Merge: merge);
        }

        /// <summary>Deletes an existing document.</summary>
        public static TerraBaseTransactionOperation Delete(string collection, string id, string? ifMatch = null)
        {
            return new("delete", collection, id, IfMatch: ifMatch);
        }

        /// <summary>Asserts that the document exists and matches <paramref name="filter"/>.</summary>
        public static TerraBaseTransactionOperation Assert(string collection, string id, JsonElement filter)
        {
            return new("assert", collection, id, Filter: filter);
        }

        /// <summary>Asserts that the document is absent or deleted.</summary>
        public static TerraBaseTransactionOperation AssertAbsent(string collection, string id)
        {
            return new("assert", collection, id, Absent: true);
        }

        /// <summary>Asserts that between <paramref name="gte"/> and <paramref name="lte"/> documents match <paramref name="filter"/>.</summary>
        public static TerraBaseTransactionOperation AssertCount(string collection, JsonElement? filter = null, long? gte = null, long? lte = null)
        {
            return gte is null && lte is null
                ? throw new ArgumentException("assert_count needs gte, lte or both.", nameof(gte))
                : new("assert_count", collection, Filter: filter, Gte: gte, Lte: lte);
        }
    }

    /// <summary>The result of one transaction operation.</summary>
    [MessagePackObject(true)]
    public sealed record TerraBaseOperationResult(string? Id = null, string? Version = null, bool? Asserted = null, long? Count = null, JsonElement? Document = null);

    /// <summary>The result of a committed transaction.</summary>
    [MessagePackObject(true)]
    public sealed record TerraBaseTransactionResult(long Offset, string Durability, IReadOnlyList<TerraBaseOperationResult> Results);

    /// <summary>The <c>{"ops":[…]}</c> transaction body.</summary>
    [MessagePackObject(true)]
    public sealed record TerraBaseTransaction(IReadOnlyList<TerraBaseTransactionOperation> Ops);
}
