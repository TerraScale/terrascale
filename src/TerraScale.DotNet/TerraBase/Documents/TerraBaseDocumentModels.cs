using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TerraScale.DotNet.TerraBase;

/// <summary>A database as the Documents API returns it.</summary>
/// <param name="Id">Database id, a lowercase hyphenated UUID.</param>
/// <param name="ProjectId">Owning project.</param>
/// <param name="Name">Database name, unique within the project.</param>
public sealed record TerraBaseDatabaseInfo(string Id, string ProjectId, string Name);

/// <summary>A collection. Collections are schemaless.</summary>
/// <param name="Name">Collection name: <c>^[a-z][a-z0-9_-]{0,62}$</c>.</param>
/// <param name="CreatedAt">Creation time.</param>
public sealed record TerraBaseCollection(
    string Name,
    [property: JsonPropertyName("created_at"), JsonConverter(typeof(UnixMillisecondsConverter))] DateTimeOffset CreatedAt);

/// <summary>One page of collections in name order.</summary>
/// <param name="Items">Collections of this page.</param>
/// <param name="NextCursor">Cursor of the next page; <see langword="null"/> on the last page.</param>
public sealed record TerraBaseCollectionPage(
    IReadOnlyList<TerraBaseCollection> Items,
    [property: JsonPropertyName("next_cursor")] string? NextCursor) : IJsonOnDeserialized
{
    void IJsonOnDeserialized.OnDeserialized() => TerraBaseJsonContext.RequireNonNullItems(Items);
}

/// <summary>A stored JSON document.</summary>
/// <param name="Id">Document id.</param>
/// <param name="Version">
/// Opaque version (<c>tbv1-…</c>), also the document's <c>ETag</c>. Pass it to <see cref="TerraBasePrecondition.Matches"/>
/// for optimistic concurrency.
/// </param>
/// <param name="State">Always <c>value</c> on a single-writer TerraBase.</param>
/// <param name="Document">The JSON object, or its projection for queries that project.</param>
public sealed record TerraBaseDocument(string Id, string Version, string State, JsonElement Document);

/// <summary>A count of query matches.</summary>
/// <param name="Value">Number of matches; a lower bound when <paramref name="Exact"/> is false.</param>
/// <param name="Exact">Whether every match was counted (TerraBase examines at most 100000 documents).</param>
public sealed record TerraBaseCount(long Value, bool Exact);

/// <summary>One page of documents.</summary>
/// <param name="Items">Documents in id order (lists) or query order.</param>
/// <param name="NextCursor">Cursor of the next page; <see langword="null"/> on the last page.</param>
/// <param name="Count">Match count, when the query asked for it.</param>
public sealed record TerraBaseDocumentPage(
    IReadOnlyList<TerraBaseDocument> Items,
    [property: JsonPropertyName("next_cursor")] string? NextCursor,
    TerraBaseCount? Count = null) : IJsonOnDeserialized
{
    void IJsonOnDeserialized.OnDeserialized() => TerraBaseJsonContext.RequireNonNullItems(Items);
}

/// <summary>The result of one document write.</summary>
/// <param name="Id">Document id.</param>
/// <param name="Version">The new version; for a delete, the tombstone's.</param>
/// <param name="Offset">The database commit offset of the write.</param>
/// <param name="Durability">Durability label, <c>local_durable</c>.</param>
public sealed record TerraBaseWriteResult(string Id, string Version, long Offset, string Durability)
{
    /// <summary>Whether the write created the document (it was absent or deleted). Set for creates and puts.</summary>
    [JsonIgnore]
    public bool Created { get; init; }
}

/// <summary>
/// The condition a write requires. TerraBase compares <c>If-Match</c> strongly and accepts only <c>*</c> for
/// <c>If-None-Match</c> on writes; a failed condition throws <see cref="TerraBasePreconditionFailedException"/>.
/// </summary>
public sealed class TerraBasePrecondition
{
    private readonly EntityTagHeaderValue? ifMatch;
    private readonly bool ifNoneMatch;

    private TerraBasePrecondition(EntityTagHeaderValue? ifMatch, bool ifNoneMatch)
    {
        this.ifMatch = ifMatch;
        this.ifNoneMatch = ifNoneMatch;
    }

    /// <summary>Create-only: the document must be absent or deleted (<c>If-None-Match: *</c>).</summary>
    public static TerraBasePrecondition Absent { get; } = new(null, ifNoneMatch: true);

    /// <summary>The document must exist (<c>If-Match: *</c>).</summary>
    public static TerraBasePrecondition Exists { get; } = new(EntityTagHeaderValue.Any, ifNoneMatch: false);

    /// <summary>The document's current version must be <paramref name="version"/> (<c>If-Match</c>).</summary>
    /// <param name="version">A <see cref="TerraBaseDocument.Version"/> or <see cref="TerraBaseWriteResult.Version"/>.</param>
    public static TerraBasePrecondition Matches(string version)
    {
        ArgumentException.ThrowIfNullOrEmpty(version);
        try
        {
            return new(new EntityTagHeaderValue($"\"{version}\""), ifNoneMatch: false);
        }
        catch (FormatException error)
        {
            throw new ArgumentException("version must be a TerraBase document version.", nameof(version), error);
        }
    }

    internal void Apply(HttpRequestHeaders headers)
    {
        if (ifMatch is not null)
        {
            headers.IfMatch.Add(ifMatch);
        }

        if (ifNoneMatch)
        {
            headers.IfNoneMatch.Add(EntityTagHeaderValue.Any);
        }
    }
}

/// <summary>One sort key of a query.</summary>
/// <param name="Path">A JSON Pointer to object members, or <c>$id</c>.</param>
/// <param name="Order"><c>asc</c> (the default) or <c>desc</c>.</param>
public sealed record TerraBaseSort(string Path, string? Order = null);

/// <summary>
/// A query (TerraBase query v1). Every member is optional. <see cref="Filter"/> uses TerraBase's JSON filter grammar,
/// for example <c>{"eq":["/status","active"]}</c>, <c>{"and":[…]}</c> or <c>{"in":["/role",["a","b"]]}</c>.
/// </summary>
public sealed record TerraBaseQuery
{
    /// <summary>The filter; every document matches when absent.</summary>
    public JsonElement? Filter { get; init; }

    /// <summary>At most 4 sort keys; results are finally ordered by id.</summary>
    public IReadOnlyList<TerraBaseSort>? Sort { get; init; }

    /// <summary>At most 64 JSON Pointers to keep in each returned document.</summary>
    public IReadOnlyList<string>? Project { get; init; }

    /// <summary>Page size, 1 to 1000 (TerraBase's default is 50). A page also stops at 8 MiB of JSON.</summary>
    public int? Limit { get; init; }

    /// <summary>The previous page's <see cref="TerraBaseDocumentPage.NextCursor"/>; valid only with the same filter, sort and projection.</summary>
    public string? Cursor { get; init; }

    /// <summary>Whether to count every match (first page only).</summary>
    public bool? Count { get; init; }
}

/// <summary>One operation of an <c>application/vnd.terrabase.ops+json</c> patch, applied in order.</summary>
public sealed record TerraBasePatchOperation
{
    private TerraBasePatchOperation(string op, string path, JsonElement? value, JsonElement? by)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        Op = op;
        Path = path;
        Value = value;
        By = by;
    }

    /// <summary><c>set</c>, <c>unset</c>, <c>inc</c> or <c>append</c>.</summary>
    public string Op { get; }

    /// <summary>JSON Pointer to object members.</summary>
    public string Path { get; }

    /// <summary>Value of <c>set</c> and <c>append</c>.</summary>
    public JsonElement? Value { get; }

    /// <summary>Number of <c>inc</c>.</summary>
    public JsonElement? By { get; }

    /// <summary>Sets <paramref name="path"/>, creating missing parents.</summary>
    public static TerraBasePatchOperation Set(string path, JsonElement value) => new("set", path, value, null);

    /// <summary>Removes <paramref name="path"/>.</summary>
    public static TerraBasePatchOperation Unset(string path) => new("unset", path, null, null);

    /// <summary>Adds the JSON number <paramref name="by"/> to <paramref name="path"/> (missing is 0; exact decimal arithmetic).</summary>
    public static TerraBasePatchOperation Increment(string path, JsonElement by) => new("inc", path, null, by);

    /// <summary>Appends <paramref name="value"/> to the array at <paramref name="path"/>, creating a missing array.</summary>
    public static TerraBasePatchOperation Append(string path, JsonElement value) => new("append", path, value, null);
}

/// <summary>The <c>{"ops":[…]}</c> patch body.</summary>
internal sealed record TerraBasePatchOperations(IReadOnlyList<TerraBasePatchOperation> Ops);

/// <summary>
/// One operation of an atomic transaction. Assertions see the state before the transaction's writes, and two
/// operations may not target the same document. Create instances with the static factories.
/// </summary>
public sealed record TerraBaseTransactionOperation
{
    private TerraBaseTransactionOperation(string op, string collection)
    {
        ArgumentException.ThrowIfNullOrEmpty(collection);
        Op = op;
        Collection = collection;
    }

    /// <summary>Operation kind: <c>create</c>, <c>put</c>, <c>patch</c>, <c>delete</c>, <c>assert</c> or <c>assert_count</c>.</summary>
    public string Op { get; }

    /// <summary>Target collection.</summary>
    public string Collection { get; }

    /// <summary>Target document id.</summary>
    public string? Id { get; private init; }

    /// <summary>Document of <c>create</c> and <c>put</c>.</summary>
    public JsonElement? Document { get; private init; }

    /// <summary>Required current version.</summary>
    [JsonPropertyName("if_match")]
    public string? IfMatch { get; private init; }

    /// <summary><c>*</c>: the document must be absent or deleted.</summary>
    [JsonPropertyName("if_none_match")]
    public string? IfNoneMatch { get; private init; }

    /// <summary>RFC 7396 merge patch of <c>patch</c>.</summary>
    public JsonElement? Merge { get; private init; }

    /// <summary>Patch operations of <c>patch</c>.</summary>
    public IReadOnlyList<TerraBasePatchOperation>? Ops { get; private init; }

    /// <summary>Filter of <c>assert</c> and <c>assert_count</c>.</summary>
    public JsonElement? Filter { get; private init; }

    /// <summary><c>assert</c> that the document is absent or deleted.</summary>
    public bool? Absent { get; private init; }

    /// <summary>Least count of <c>assert_count</c>.</summary>
    public long? Gte { get; private init; }

    /// <summary>Greatest count of <c>assert_count</c>.</summary>
    public long? Lte { get; private init; }

    /// <summary>Creates a document that must be absent or deleted; TerraBase generates a UUIDv7 id when <paramref name="id"/> is null.</summary>
    public static TerraBaseTransactionOperation Create(string collection, JsonElement document, string? id = null) =>
        new("create", collection) { Id = id, Document = document };

    /// <summary>Inserts or replaces a document, optionally only at version <paramref name="ifMatch"/>.</summary>
    public static TerraBaseTransactionOperation Put(string collection, string id, JsonElement document, string? ifMatch = null) =>
        new("put", collection) { Id = Required(id), Document = document, IfMatch = ifMatch };

    /// <summary>Applies an RFC 7396 merge patch to an existing document.</summary>
    public static TerraBaseTransactionOperation Patch(string collection, string id, JsonElement merge, string? ifMatch = null) =>
        new("patch", collection) { Id = Required(id), Merge = merge, IfMatch = ifMatch };

    /// <summary>Applies patch operations to an existing document.</summary>
    public static TerraBaseTransactionOperation Patch(string collection, string id, IReadOnlyList<TerraBasePatchOperation> ops, string? ifMatch = null) =>
        new("patch", collection) { Id = Required(id), Ops = ops ?? throw new ArgumentNullException(nameof(ops)), IfMatch = ifMatch };

    /// <summary>Deletes an existing document.</summary>
    public static TerraBaseTransactionOperation Delete(string collection, string id, string? ifMatch = null) =>
        new("delete", collection) { Id = Required(id), IfMatch = ifMatch };

    /// <summary>Asserts that the document exists and matches <paramref name="filter"/>.</summary>
    public static TerraBaseTransactionOperation Assert(string collection, string id, JsonElement filter) =>
        new("assert", collection) { Id = Required(id), Filter = filter };

    /// <summary>Asserts that the document is absent or deleted.</summary>
    public static TerraBaseTransactionOperation AssertAbsent(string collection, string id) =>
        new("assert", collection) { Id = Required(id), Absent = true };

    /// <summary>Asserts that between <paramref name="gte"/> and <paramref name="lte"/> documents match <paramref name="filter"/> (at least one bound).</summary>
    public static TerraBaseTransactionOperation AssertCount(string collection, JsonElement? filter = null, long? gte = null, long? lte = null) =>
        gte is null && lte is null
            ? throw new ArgumentException("assert_count needs gte, lte or both.", nameof(gte))
            : new("assert_count", collection) { Filter = filter, Gte = gte, Lte = lte };

    private static string Required(string id)
    {
        ArgumentException.ThrowIfNullOrEmpty(id);
        return id;
    }
}

/// <summary>The <c>{"ops":[…]}</c> transaction body.</summary>
internal sealed record TerraBaseTransaction(IReadOnlyList<TerraBaseTransactionOperation> Ops);

/// <summary>The result of one transaction operation.</summary>
/// <param name="Id">Written document id.</param>
/// <param name="Version">Written document version.</param>
/// <param name="Asserted">True for assertions.</param>
/// <param name="Count">Counted matches of <c>assert_count</c>.</param>
public sealed record TerraBaseOperationResult(string? Id = null, string? Version = null, bool? Asserted = null, long? Count = null);

/// <summary>The result of a committed transaction.</summary>
/// <param name="Offset">The database commit offset.</param>
/// <param name="Durability">Durability label, <c>local_durable</c>.</param>
/// <param name="Results">One result per operation, in order.</param>
public sealed record TerraBaseTransactionResult(long Offset, string Durability, IReadOnlyList<TerraBaseOperationResult> Results) : IJsonOnDeserialized
{
    void IJsonOnDeserialized.OnDeserialized() => TerraBaseJsonContext.RequireNonNullItems(Results);
}
