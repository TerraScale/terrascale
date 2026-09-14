using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace TerraBase.Indexing;

/// <summary>The logical kind of an index.</summary>
public enum IndexKind
{
    /// <summary>Exact value lookup.</summary>
    Equality,
    /// <summary>Ordered comparisons over one field.</summary>
    Range,
    /// <summary>Ordered lookup over multiple fields.</summary>
    Composite,
    /// <summary>An index that can satisfy a projection from its stored fields.</summary>
    Covering,
    /// <summary>An exact value index that rejects duplicate keys.</summary>
    Unique,
    /// <summary>An index restricted by a predicate.</summary>
    Partial,
    /// <summary>An index over a path below an object.</summary>
    Nested,
    /// <summary>An index that emits one key for each array member.</summary>
    MultiKey,
    /// <summary>An index over a relation or reference field.</summary>
    Relationship,
    /// <summary>A token index for text search.</summary>
    FullText,
}

/// <summary>The direction of one ordered index field.</summary>
public enum IndexSortOrder
{
    /// <summary>Small values sort before large values.</summary>
    Ascending,
    /// <summary>Large values sort before small values.</summary>
    Descending,
}

/// <summary>An operator used by an index lookup or a partial filter.</summary>
public enum IndexPredicateOperator
{
    /// <summary>Values compare equal.</summary>
    Equal,
    /// <summary>Values do not compare equal.</summary>
    NotEqual,
    /// <summary>The field is present.</summary>
    Exists,
    /// <summary>The value is greater than the operand.</summary>
    GreaterThan,
    /// <summary>The value is greater than or equal to the operand.</summary>
    GreaterThanOrEqual,
    /// <summary>The value is less than the operand.</summary>
    LessThan,
    /// <summary>The value is less than or equal to the operand.</summary>
    LessThanOrEqual,
    /// <summary>A string contains the operand.</summary>
    Contains,
    /// <summary>A string starts with the operand.</summary>
    StartsWith,
}

/// <summary>A field and its order in an index key.</summary>
public sealed record IndexField
{
    /// <summary>Creates an indexed field.</summary>
    public IndexField(string path, IndexSortOrder sortOrder = IndexSortOrder.Ascending)
    {
        Path = ValidatePath(path, nameof(path));
        if (!Enum.IsDefined(sortOrder))
        {
            throw new ArgumentOutOfRangeException(nameof(sortOrder));
        }

        SortOrder = sortOrder;
    }

    /// <summary>The dot-separated JSON path.</summary>
    public string Path { get; }

    /// <summary>The order used by range plans.</summary>
    public IndexSortOrder SortOrder { get; }

    internal static string ValidatePath(string path, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path, parameterName);
        if (path.Trim() != path || path.Split('.').Any(static segment => segment.Length == 0))
        {
            throw new ArgumentException("A field path must not contain empty or padded segments.", parameterName);
        }

        foreach (var character in path)
        {
            if (char.IsControl(character))
            {
                throw new ArgumentException("A field path must not contain control characters.", parameterName);
            }
        }

        return path;
    }
}

/// <summary>A typed predicate over a JSON field path.</summary>
public sealed record IndexPredicate
{
    /// <summary>Creates a predicate.</summary>
    public IndexPredicate(string path, IndexPredicateOperator @operator, object? value = null)
    {
        Path = IndexField.ValidatePath(path, nameof(path));
        if (!Enum.IsDefined(@operator))
        {
            throw new ArgumentOutOfRangeException(nameof(@operator));
        }

        if (@operator == IndexPredicateOperator.Exists && value is not null)
        {
            throw new ArgumentException("An exists predicate does not accept a value.", nameof(value));
        }

        Operator = @operator;
        Value = value;
    }

    /// <summary>The field path tested by the predicate.</summary>
    public string Path { get; }

    /// <summary>The comparison operator.</summary>
    public IndexPredicateOperator Operator { get; }

    /// <summary>The comparison operand, when the operator needs one.</summary>
    public object? Value { get; }

    /// <summary>Creates an equality predicate.</summary>
    public static IndexPredicate Equal(string path, object? value) => new(path, IndexPredicateOperator.Equal, value);

    /// <summary>Creates a greater-than predicate.</summary>
    public static IndexPredicate GreaterThan(string path, object value) => new(path, IndexPredicateOperator.GreaterThan, value);

    /// <summary>Creates a greater-than-or-equal predicate.</summary>
    public static IndexPredicate GreaterThanOrEqual(string path, object value) => new(path, IndexPredicateOperator.GreaterThanOrEqual, value);

    /// <summary>Creates a less-than predicate.</summary>
    public static IndexPredicate LessThan(string path, object value) => new(path, IndexPredicateOperator.LessThan, value);

    /// <summary>Creates a less-than-or-equal predicate.</summary>
    public static IndexPredicate LessThanOrEqual(string path, object value) => new(path, IndexPredicateOperator.LessThanOrEqual, value);

    /// <summary>Creates a field-present predicate.</summary>
    public static IndexPredicate Exists(string path) => new(path, IndexPredicateOperator.Exists);

    /// <summary>Creates a contains predicate.</summary>
    public static IndexPredicate Contains(string path, string value) => new(path, IndexPredicateOperator.Contains, value);

    /// <summary>Creates a prefix predicate.</summary>
    public static IndexPredicate StartsWith(string path, string value) => new(path, IndexPredicateOperator.StartsWith, value);
}

/// <summary>A relationship target associated with a relationship index.</summary>
public sealed record IndexRelationship
{
    /// <summary>Creates relationship metadata.</summary>
    public IndexRelationship(string collection, string field = "id")
    {
        Collection = IndexField.ValidatePath(collection, nameof(collection));
        Field = IndexField.ValidatePath(field, nameof(field));
    }

    /// <summary>The related collection name.</summary>
    public string Collection { get; }

    /// <summary>The related field containing the target identity.</summary>
    public string Field { get; }
}

/// <summary>Immutable metadata describing one index.</summary>
public sealed class IndexDefinition
{
    /// <summary>Creates an index definition.</summary>
    public IndexDefinition(
        string name,
        IndexKind kind,
        IEnumerable<string> fields,
        bool unique = false,
        IEnumerable<string>? includedFields = null,
        IEnumerable<IndexPredicate>? partialFilter = null,
        IndexRelationship? relationship = null)
        : this(
            name,
            kind,
            fields.Select(static path => new IndexField(path)),
            unique,
            includedFields?.Select(static path => new IndexField(path)),
            partialFilter,
            relationship)
    {
    }

    /// <summary>Creates an index definition from fully specified fields.</summary>
    public IndexDefinition(
        string name,
        IndexKind kind,
        IEnumerable<IndexField> fields,
        bool unique = false,
        IEnumerable<IndexField>? includedFields = null,
        IEnumerable<IndexPredicate>? partialFilter = null,
        IndexRelationship? relationship = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (name.Trim() != name || name.Any(char.IsControl))
        {
            throw new ArgumentException("An index name must not be padded or contain control characters.", nameof(name));
        }

        if (!Enum.IsDefined(kind))
        {
            throw new ArgumentOutOfRangeException(nameof(kind));
        }

        ArgumentNullException.ThrowIfNull(fields);
        var fieldArray = fields.ToArray();
        if (fieldArray.Length == 0)
        {
            throw new ArgumentException("An index requires at least one field.", nameof(fields));
        }

        if (fieldArray.Any(static field => field is null))
        {
            throw new ArgumentException("An index cannot contain a null field.", nameof(fields));
        }

        if (fieldArray.Select(static field => field.Path).Distinct(StringComparer.Ordinal).Count() != fieldArray.Length)
        {
            throw new ArgumentException("An index cannot contain duplicate fields.", nameof(fields));
        }

        var includedArray = includedFields?.ToArray() ?? [];
        if (includedArray.Any(static field => field is null))
        {
            throw new ArgumentException("A covering index cannot contain a null included field.", nameof(includedFields));
        }

        if (includedArray.Select(static field => field.Path).Distinct(StringComparer.Ordinal).Count() != includedArray.Length)
        {
            throw new ArgumentException("A covering index cannot contain duplicate included fields.", nameof(includedFields));
        }

        var filterArray = partialFilter?.ToArray() ?? [];
        if (filterArray.Any(static predicate => predicate is null))
        {
            throw new ArgumentException("A partial index cannot contain a null filter.", nameof(partialFilter));
        }

        if (kind == IndexKind.FullText && unique)
        {
            throw new ArgumentException("A full-text index cannot be unique.", nameof(unique));
        }

        if (kind == IndexKind.Range && fieldArray.Length != 1)
        {
            throw new ArgumentException("A range index requires exactly one field.", nameof(fields));
        }

        if (kind == IndexKind.MultiKey && fieldArray.Length != 1)
        {
            throw new ArgumentException("A multikey index requires exactly one field.", nameof(fields));
        }

        if (kind == IndexKind.Relationship && relationship is null)
        {
            throw new ArgumentException("A relationship index requires relationship metadata.", nameof(relationship));
        }

        Name = name;
        Kind = kind;
        Fields = new ReadOnlyCollection<IndexField>(fieldArray);
        IncludedFields = new ReadOnlyCollection<IndexField>(includedArray);
        PartialFilter = new ReadOnlyCollection<IndexPredicate>(filterArray);
        IsUnique = unique || kind == IndexKind.Unique;
        RelationshipTarget = relationship;
    }

    /// <summary>Stable index name.</summary>
    public string Name { get; }

    /// <summary>Index behavior.</summary>
    public IndexKind Kind { get; }

    /// <summary>Fields forming the lookup key.</summary>
    public IReadOnlyList<IndexField> Fields { get; }

    /// <summary>Fields stored for covered projections.</summary>
    public IReadOnlyList<IndexField> IncludedFields { get; }

    /// <summary>Predicates restricting documents admitted to this index.</summary>
    public IReadOnlyList<IndexPredicate> PartialFilter { get; }

    /// <summary>Whether duplicate keys are rejected.</summary>
    public bool IsUnique { get; }

    /// <summary>Relationship target metadata, when this is a relationship index.</summary>
    public IndexRelationship? RelationshipTarget { get; }

    /// <summary>Alias for the relationship target metadata.</summary>
    public IndexRelationship? RelatedTo => RelationshipTarget;

    /// <summary>Whether this definition indexes each member of an array.</summary>
    public bool IsMultiKey => Kind == IndexKind.MultiKey;

    /// <summary>Whether this definition has stored projection fields.</summary>
    public bool IsCovering => Kind == IndexKind.Covering || IncludedFields.Count > 0;

    /// <summary>Creates an equality index.</summary>
    public static IndexDefinition Equality(string name, string field, bool unique = false) =>
        new(name, unique ? IndexKind.Unique : IndexKind.Equality, [field], unique);

    /// <summary>Creates a range index.</summary>
    public static IndexDefinition Range(string name, string field, IndexSortOrder sortOrder = IndexSortOrder.Ascending) =>
        new(name, IndexKind.Range, [new IndexField(field, sortOrder)]);

    /// <summary>Creates a composite index.</summary>
    public static IndexDefinition Composite(string name, params string[] fields) => new(name, IndexKind.Composite, fields);

    /// <summary>Creates a covering index.</summary>
    public static IndexDefinition Covering(string name, IEnumerable<string> fields, IEnumerable<string> includedFields) =>
        new(name, IndexKind.Covering, fields, includedFields: includedFields);

    /// <summary>Creates a unique index.</summary>
    public static IndexDefinition Unique(string name, params string[] fields) => new(name, IndexKind.Unique, fields, unique: true);

    /// <summary>Creates a partial index.</summary>
    public static IndexDefinition Partial(string name, IEnumerable<string> fields, IEnumerable<IndexPredicate> filter, bool unique = false) =>
        new(name, unique ? IndexKind.Unique : IndexKind.Partial, fields, unique, partialFilter: filter);

    /// <summary>Creates an index over a nested path.</summary>
    public static IndexDefinition Nested(string name, string path, bool unique = false) =>
        new(name, unique ? IndexKind.Unique : IndexKind.Nested, [path], unique);

    /// <summary>Creates a multikey array index.</summary>
    public static IndexDefinition MultiKey(string name, string path, bool unique = false) =>
        new(name, unique ? IndexKind.Unique : IndexKind.MultiKey, [path], unique);

    /// <summary>Creates a relationship index.</summary>
    public static IndexDefinition Relationship(string name, string field, string relatedCollection, string relatedField = "id") =>
        new(name, IndexKind.Relationship, [field], relationship: new IndexRelationship(relatedCollection, relatedField));

    /// <summary>Creates a full-text index.</summary>
    public static IndexDefinition FullText(string name, params string[] fields) => new(name, IndexKind.FullText, fields);
}

/// <summary>A monotonic source generation observed by an index.</summary>
public readonly record struct FreshnessWatermark
{
    /// <summary>Creates a freshness watermark.</summary>
    public FreshnessWatermark(ulong generation, DateTimeOffset observedAt)
    {
        Generation = generation;
        ObservedAt = observedAt;
    }

    /// <summary>The source generation indexed.</summary>
    public ulong Generation { get; }

    /// <summary>When the source generation was observed.</summary>
    public DateTimeOffset ObservedAt { get; }

    /// <summary>Whether this watermark covers the required generation.</summary>
    public bool Covers(ulong generation) => Generation >= generation;

    /// <summary>An empty watermark.</summary>
    public static FreshnessWatermark Empty => new(0, DateTimeOffset.MinValue);
}

/// <summary>Statistics collected by an in-memory index.</summary>
public sealed record IndexStats
{
    /// <summary>Documents admitted to the index.</summary>
    public long DocumentCount { get; init; }

    /// <summary>Entries in the index, including multikey entries.</summary>
    public long EntryCount { get; init; }

    /// <summary>Distinct keys in the index.</summary>
    public long DistinctKeyCount { get; init; }

    /// <summary>Number of successful lookups.</summary>
    public long LookupCount { get; init; }

    /// <summary>Number of lookups that found no matching documents.</summary>
    public long MissCount { get; init; }

    /// <summary>Number of matching documents returned by lookups.</summary>
    public long HitCount { get; init; }

    /// <summary>Last time index entries changed.</summary>
    public DateTimeOffset LastUpdatedAt { get; init; }

    /// <summary>Alias for <see cref="DocumentCount"/>.</summary>
    public long Documents => DocumentCount;

    /// <summary>Alias for <see cref="EntryCount"/>.</summary>
    public long Entries => EntryCount;

    /// <summary>Alias for <see cref="DistinctKeyCount"/>.</summary>
    public long DistinctKeys => DistinctKeyCount;
}

/// <summary>Access strategy selected for an index lookup.</summary>
public enum IndexAccessMethod
{
    /// <summary>No usable index key was supplied.</summary>
    Scan,
    /// <summary>An exact lookup was used.</summary>
    EqualitySeek,
    /// <summary>An ordered comparison was used.</summary>
    RangeSeek,
    /// <summary>A prefix of a compound key was used.</summary>
    CompositeSeek,
    /// <summary>A token lookup was used.</summary>
    FullTextSeek,
    /// <summary>A multikey entry lookup was used.</summary>
    MultiKeySeek,
    /// <summary>The index supplies all requested projection fields.</summary>
    CoveredSeek,
}

/// <summary>Explain information for one index query.</summary>
public class ExplainPlan
{
    /// <summary>The index considered by the plan.</summary>
    public string IndexName { get; init; } = string.Empty;

    /// <summary>The selected access method.</summary>
    public IndexAccessMethod Access { get; init; }

    /// <summary>Whether the requested projection is covered.</summary>
    public bool IsCovered { get; init; }

    /// <summary>Estimated matching document count.</summary>
    public int EstimatedMatches { get; init; }

    /// <summary>Actual matching document count after predicates.</summary>
    public int ActualMatches { get; init; }

    /// <summary>Whether the index watermark meets the query requirement.</summary>
    public bool IsFresh { get; init; }

    /// <summary>The observed index watermark.</summary>
    public FreshnessWatermark Watermark { get; init; }

    /// <summary>Reason the access method was selected.</summary>
    public string Reason { get; init; } = string.Empty;
}

/// <summary>The result of querying an in-memory index.</summary>
public sealed class IndexQueryResult
{
    internal IndexQueryResult(IReadOnlyList<Document> documents, ExplainPlan plan, IndexStats stats)
    {
        Documents = documents;
        Plan = plan;
        Stats = stats;
    }

    /// <summary>Documents matching all supplied predicates.</summary>
    public IReadOnlyList<Document> Documents { get; }

    /// <summary>The query's explain information.</summary>
    public ExplainPlan Plan { get; }

    /// <summary>Statistics after the query.</summary>
    public IndexStats Stats { get; }
}

/// <summary>Raised when a unique index receives a duplicate key.</summary>
public sealed class IndexConstraintException : InvalidOperationException
{
    /// <summary>Creates a duplicate-key exception.</summary>
    public IndexConstraintException(string indexName)
        : base($"The index '{indexName}' rejected a duplicate key.")
    {
        IndexName = indexName;
    }

    /// <summary>The index that rejected the write.</summary>
    public string IndexName { get; }
}

/// <summary>An in-memory, correctness-first implementation of one index.</summary>
public sealed class InMemoryIndex
{
    private readonly Dictionary<DocumentKey, Document> documents = new();
    private readonly Dictionary<string, HashSet<DocumentKey>> entries = new(StringComparer.Ordinal);
    private long lookupCount;
    private long hitCount;
    private long missCount;
    private DateTimeOffset lastUpdatedAt = DateTimeOffset.MinValue;
    private FreshnessWatermark watermark = FreshnessWatermark.Empty;

    /// <summary>Creates an empty index.</summary>
    public InMemoryIndex(IndexDefinition definition)
    {
        Definition = definition ?? throw new ArgumentNullException(nameof(definition));
    }

    /// <summary>The immutable index definition.</summary>
    public IndexDefinition Definition { get; }

    /// <summary>The current index freshness watermark.</summary>
    public FreshnessWatermark Watermark => watermark;

    /// <summary>The current index statistics.</summary>
    public IndexStats Stats => SnapshotStats();

    /// <summary>Rebuilds entries from a document sequence.</summary>
    public void Rebuild(IEnumerable<Document> source, ulong generation = 0, DateTimeOffset? observedAt = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        var replacement = source.ToArray();
        var replacementEntries = new Dictionary<string, HashSet<DocumentKey>>(StringComparer.Ordinal);
        foreach (var document in replacement)
        {
            var keys = KeysFor(document);
            AddKeys(replacementEntries, document.Key, keys);
        }

        documents.Clear();
        entries.Clear();
        foreach (var document in replacement)
        {
            documents.Add(document.Key, document);
            foreach (var key in KeysFor(document))
            {
                if (!entries.TryGetValue(key, out var owners))
                {
                    owners = [];
                    entries.Add(key, owners);
                }

                owners.Add(document.Key);
            }
        }

        lastUpdatedAt = observedAt ?? DateTimeOffset.UtcNow;
        AdvanceWatermark(generation, lastUpdatedAt);
    }

    /// <summary>Inserts or replaces one document atomically.</summary>
    public void Upsert(Document document)
    {
        ArgumentNullException.ThrowIfNull(document);
        var keys = KeysFor(document);
        var oldKeys = documents.TryGetValue(document.Key, out var existing) ? KeysFor(existing) : [];
        var oldKeySet = oldKeys.ToHashSet(StringComparer.Ordinal);
        foreach (var key in keys.Distinct(StringComparer.Ordinal))
        {
            if (!Definition.IsUnique || (entries.TryGetValue(key, out var owners) && owners.Any(owner => owner != document.Key)))
            {
                continue;
            }

            throw new IndexConstraintException(Definition.Name);
        }

        foreach (var oldKey in oldKeySet)
        {
            RemoveEntry(oldKey, document.Key);
        }

        documents[document.Key] = document;
        foreach (var key in keys.Distinct(StringComparer.Ordinal))
        {
            if (!entries.TryGetValue(key, out var owners))
            {
                owners = [];
                entries.Add(key, owners);
            }

            owners.Add(document.Key);
        }

        var updatedAt = document.UpdatedAt;
        lastUpdatedAt = updatedAt > lastUpdatedAt ? updatedAt : DateTimeOffset.UtcNow;
        AdvanceWatermark(document.Version, lastUpdatedAt);
    }

    /// <summary>Removes one document if present.</summary>
    public bool Remove(DocumentKey key)
    {
        if (!documents.Remove(key, out var document))
        {
            return false;
        }

        foreach (var entry in KeysFor(document).Distinct(StringComparer.Ordinal))
        {
            RemoveEntry(entry, key);
        }

        lastUpdatedAt = DateTimeOffset.UtcNow;
        return true;
    }

    /// <summary>Advances the freshness watermark without changing entries.</summary>
    public void AdvanceWatermark(ulong generation, DateTimeOffset observedAt)
    {
        if (generation < watermark.Generation)
        {
            throw new ArgumentOutOfRangeException(nameof(generation), "An index watermark cannot move backwards.");
        }

        if (generation == watermark.Generation && observedAt < watermark.ObservedAt)
        {
            throw new ArgumentOutOfRangeException(nameof(observedAt), "An index watermark timestamp cannot move backwards.");
        }

        watermark = new FreshnessWatermark(generation, observedAt);
    }

    /// <summary>Returns whether this index has caught up to a source generation.</summary>
    public bool IsFresh(ulong requiredGeneration) => watermark.Covers(requiredGeneration);

    /// <summary>Explains a lookup without incrementing query statistics.</summary>
    public ExplainPlan Explain(IEnumerable<IndexPredicate>? predicates = null, IEnumerable<string>? projection = null, ulong? requiredGeneration = null)
    {
        var predicateArray = predicates?.ToArray() ?? [];
        var candidates = CandidateKeys(predicateArray, out var access, out var reason);
        var candidateDocuments = candidates is null
            ? documents.Values.ToArray()
            : candidates.SelectMany(key => entries.TryGetValue(key, out var owners) ? owners : []).Distinct().Select(key => documents[key]).ToArray();
        var actual = candidateDocuments.Count(document => Matches(document, predicateArray));
        var covered = IsProjectionCovered(projection);
        return new ExplainPlan
        {
            IndexName = Definition.Name,
            Access = covered && access != IndexAccessMethod.Scan ? IndexAccessMethod.CoveredSeek : access,
            IsCovered = covered,
            EstimatedMatches = candidateDocuments.Length,
            ActualMatches = actual,
            IsFresh = !requiredGeneration.HasValue || watermark.Covers(requiredGeneration.Value),
            Watermark = watermark,
            Reason = reason,
        };
    }

    /// <summary>Queries this index with all supplied predicates.</summary>
    public IndexQueryResult Query(IEnumerable<IndexPredicate>? predicates = null, IEnumerable<string>? projection = null, ulong? requiredGeneration = null)
    {
        var predicateArray = predicates?.ToArray() ?? [];
        var candidates = CandidateKeys(predicateArray, out var access, out var reason);
        var candidateDocuments = candidates is null
            ? documents.Values.ToArray()
            : candidates.SelectMany(key => entries.TryGetValue(key, out var owners) ? owners : []).Distinct().Select(key => documents[key]).ToArray();
        var matches = candidateDocuments.Where(document => Matches(document, predicateArray)).OrderBy(static document => document.Key.DocumentId, StringComparer.Ordinal).ToArray();
        lookupCount++;
        if (matches.Length == 0)
        {
            missCount++;
        }
        else
        {
            hitCount += matches.Length;
        }

        var covered = IsProjectionCovered(projection);
        var plan = new ExplainPlan
        {
            IndexName = Definition.Name,
            Access = covered && access != IndexAccessMethod.Scan ? IndexAccessMethod.CoveredSeek : access,
            IsCovered = covered,
            EstimatedMatches = candidateDocuments.Length,
            ActualMatches = matches.Length,
            IsFresh = !requiredGeneration.HasValue || watermark.Covers(requiredGeneration.Value),
            Watermark = watermark,
            Reason = reason,
        };
        return new IndexQueryResult(new ReadOnlyCollection<Document>(matches), plan, SnapshotStats());
    }

    /// <summary>Queries a single predicate.</summary>
    public IndexQueryResult Query(IndexPredicate predicate, IEnumerable<string>? projection = null, ulong? requiredGeneration = null) =>
        Query([predicate], projection, requiredGeneration);

    /// <summary>Looks up full-text terms in a full-text index.</summary>
    public IndexQueryResult Search(string text, IEnumerable<string>? projection = null, ulong? requiredGeneration = null)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (Definition.Kind != IndexKind.FullText)
        {
            throw new InvalidOperationException("Search is only available on a full-text index.");
        }

        var terms = Tokenize(text).Select(term => IndexPredicate.Contains(Definition.Fields[0].Path, term)).ToArray();
        return Query(terms, projection, requiredGeneration);
    }

    private string[] KeysFor(Document document)
    {
        if (Definition.PartialFilter.Count > 0 && !Matches(document, Definition.PartialFilter))
        {
            return [];
        }

        if (Definition.Kind == IndexKind.FullText)
        {
            var tokens = Definition.Fields
                .SelectMany(field => ExtractValues(document.Body, field.Path, false))
                .Where(static value => value.ValueKind == JsonValueKind.String)
                .SelectMany(static value => Tokenize(value.GetString() ?? string.Empty))
                .Distinct(StringComparer.Ordinal)
                .Select(static token => Canonical(JsonSerializer.SerializeToElement(token)))
                .ToArray();
            return tokens;
        }

        var valuesPerField = Definition.Fields
            .Select(field => ExtractValues(document.Body, field.Path, Definition.IsMultiKey))
            .ToArray();
        if (valuesPerField.Any(static values => values.Count == 0))
        {
            return [];
        }

        var combinations = new List<JsonElement[]> { Array.Empty<JsonElement>() };
        foreach (var values in valuesPerField)
        {
            var next = new List<JsonElement[]>();
            foreach (var prefix in combinations)
            {
                foreach (var value in values)
                {
                    next.Add([.. prefix, value]);
                }
            }

            combinations = next;
        }

        return combinations.Select(static values => CanonicalKey(values)).Distinct(StringComparer.Ordinal).ToArray();
    }

    private IEnumerable<string>? CandidateKeys(IReadOnlyList<IndexPredicate> predicates, out IndexAccessMethod access, out string reason)
    {
        access = IndexAccessMethod.Scan;
        reason = "No predicate matched an indexed field.";
        if (Definition.Kind == IndexKind.FullText)
        {
            var terms = predicates
                .Where(predicate => predicate.Operator is IndexPredicateOperator.Contains or IndexPredicateOperator.StartsWith)
                .Where(predicate => Definition.Fields.Any(field => field.Path == predicate.Path))
                .SelectMany(predicate => Tokenize(Convert.ToString(predicate.Value, CultureInfo.InvariantCulture) ?? string.Empty))
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            if (terms.Length > 0)
            {
                access = IndexAccessMethod.FullTextSeek;
                reason = "Full-text terms were resolved through the token index.";
                return terms.Select(static term => Canonical(JsonSerializer.SerializeToElement(term)));
            }

            return null;
        }

        var first = Definition.Fields[0];
        var predicate = predicates.FirstOrDefault(candidate => candidate.Path == first.Path);
        if (predicate is null)
        {
            return null;
        }

        var matching = new List<string>();
        if (predicate.Operator == IndexPredicateOperator.Equal)
        {
            var scalar = ToJsonElement(predicate.Value);
            var key = Definition.Fields.Count == 1
                ? CanonicalKey([scalar])
                : CanonicalKeyPrefix(scalar);
            access = Definition.IsMultiKey ? IndexAccessMethod.MultiKeySeek : Definition.Fields.Count > 1 ? IndexAccessMethod.CompositeSeek : IndexAccessMethod.EqualitySeek;
            reason = "An indexed equality key was resolved.";
            foreach (var existing in entries.Keys)
            {
                if (Definition.Fields.Count > 1 ? existing.StartsWith(key, StringComparison.Ordinal) : existing == key)
                {
                    matching.Add(existing);
                }
            }

            return matching;
        }

        if (predicate.Operator is IndexPredicateOperator.GreaterThan or IndexPredicateOperator.GreaterThanOrEqual or IndexPredicateOperator.LessThan or IndexPredicateOperator.LessThanOrEqual)
        {
            access = IndexAccessMethod.RangeSeek;
            reason = "An ordered comparison was resolved against index keys.";
            var operand = ToJsonElement(predicate.Value);
            foreach (var existing in entries.Keys)
            {
                if (!TryReadFirstValue(existing, out var value))
                {
                    continue;
                }

                var comparison = CompareJson(value, operand);
                if (predicate.Operator switch
                {
                    IndexPredicateOperator.GreaterThan => comparison > 0,
                    IndexPredicateOperator.GreaterThanOrEqual => comparison >= 0,
                    IndexPredicateOperator.LessThan => comparison < 0,
                    _ => comparison <= 0,
                })
                {
                    matching.Add(existing);
                }
            }

            return matching;
        }

        return null;
    }

    private bool Matches(Document document, IEnumerable<IndexPredicate> predicates) => predicates.All(predicate => Matches(document, predicate));

    private bool Matches(Document document, IndexPredicate predicate)
    {
        var values = ExtractValues(document.Body, predicate.Path, Definition.IsMultiKey);
        if (predicate.Operator == IndexPredicateOperator.Exists)
        {
            return values.Count > 0;
        }

        if (values.Count == 0)
        {
            return false;
        }

        var operand = predicate.Value is null ? (JsonElement?)null : ToJsonElement(predicate.Value);
        return predicate.Operator switch
        {
            IndexPredicateOperator.Equal => values.Any(value => CompareJson(value, operand!.Value) == 0),
            IndexPredicateOperator.NotEqual => values.All(value => CompareJson(value, operand!.Value) != 0),
            IndexPredicateOperator.GreaterThan => values.Any(value => CompareJson(value, operand!.Value) > 0),
            IndexPredicateOperator.GreaterThanOrEqual => values.Any(value => CompareJson(value, operand!.Value) >= 0),
            IndexPredicateOperator.LessThan => values.Any(value => CompareJson(value, operand!.Value) < 0),
            IndexPredicateOperator.LessThanOrEqual => values.Any(value => CompareJson(value, operand!.Value) <= 0),
            IndexPredicateOperator.Contains => values.Any(value => value.ValueKind == JsonValueKind.String && (value.GetString() ?? string.Empty).Contains(Convert.ToString(predicate.Value, CultureInfo.InvariantCulture) ?? string.Empty, StringComparison.OrdinalIgnoreCase)),
            IndexPredicateOperator.StartsWith => values.Any(value => value.ValueKind == JsonValueKind.String && (value.GetString() ?? string.Empty).StartsWith(Convert.ToString(predicate.Value, CultureInfo.InvariantCulture) ?? string.Empty, StringComparison.OrdinalIgnoreCase)),
            _ => false,
        };
    }

    private bool IsProjectionCovered(IEnumerable<string>? projection)
    {
        if (projection is null)
        {
            return false;
        }

        var available = Definition.Fields.Select(static field => field.Path)
            .Concat(Definition.IncludedFields.Select(static field => field.Path))
            .ToHashSet(StringComparer.Ordinal);
        return projection.All(available.Contains);
    }

    private IndexStats SnapshotStats() => new()
    {
        DocumentCount = documents.Count,
        EntryCount = entries.Values.Sum(static owners => (long)owners.Count),
        DistinctKeyCount = entries.Count,
        LookupCount = lookupCount,
        HitCount = hitCount,
        MissCount = missCount,
        LastUpdatedAt = lastUpdatedAt,
    };

    private void RemoveEntry(string key, DocumentKey owner)
    {
        if (!entries.TryGetValue(key, out var owners))
        {
            return;
        }

        owners.Remove(owner);
        if (owners.Count == 0)
        {
            entries.Remove(key);
        }
    }

    private void AddKeys(Dictionary<string, HashSet<DocumentKey>> target, DocumentKey owner, IEnumerable<string> keys)
    {
        foreach (var key in keys.Distinct(StringComparer.Ordinal))
        {
            if (Definition.IsUnique && target.TryGetValue(key, out var owners) && owners.Any(existing => existing != owner))
            {
                throw new IndexConstraintException(Definition.Name);
            }

            if (!target.TryGetValue(key, out owners))
            {
                owners = [];
                target.Add(key, owners);
            }

            owners.Add(owner);
        }
    }

    private static List<JsonElement> ExtractValues(JsonElement body, string path, bool multikey)
    {
        var current = new List<JsonElement> { body };
        foreach (var segment in path.Split('.'))
        {
            var next = new List<JsonElement>();
            foreach (var node in current)
            {
                if (node.ValueKind == JsonValueKind.Object && node.TryGetProperty(segment, out var property))
                {
                    if (multikey && property.ValueKind == JsonValueKind.Array)
                    {
                        next.AddRange(property.EnumerateArray());
                    }
                    else
                    {
                        next.Add(property);
                    }
                }
                else if (multikey && node.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in node.EnumerateArray())
                    {
                        if (item.ValueKind == JsonValueKind.Object && item.TryGetProperty(segment, out var nested))
                        {
                            next.Add(nested);
                        }
                    }
                }
            }

            current = next;
        }

        return current;
    }

    private static string CanonicalKey(IEnumerable<JsonElement> values) => JsonSerializer.Serialize(values.Select(Canonical).ToArray());

    private static string CanonicalKeyPrefix(JsonElement value) => "[" + JsonSerializer.Serialize(Canonical(value)) + ",";

    private static string Canonical(JsonElement value)
    {
        return value.ValueKind switch
        {
            JsonValueKind.Number when value.TryGetDecimal(out var decimalValue) => decimalValue.ToString("G29", CultureInfo.InvariantCulture),
            JsonValueKind.Number => value.GetRawText(),
            JsonValueKind.String => "s:" + JsonSerializer.Serialize(value.GetString()),
            JsonValueKind.True => "b:true",
            JsonValueKind.False => "b:false",
            JsonValueKind.Null => "null",
            JsonValueKind.Undefined => "undefined",
            _ => value.GetRawText(),
        };
    }

    private static bool TryReadFirstValue(string key, out JsonElement value)
    {
        value = default;
        try
        {
            using var document = JsonDocument.Parse(key);
            var first = document.RootElement[0].GetString();
            if (first is null)
            {
                return false;
            }

            value = JsonSerializer.SerializeToElement(first);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static JsonElement ToJsonElement(object? value) => value switch
    {
        JsonElement element => element.Clone(),
        JsonDocument document => document.RootElement.Clone(),
        _ => JsonSerializer.SerializeToElement(value),
    };

    private static int CompareJson(JsonElement left, JsonElement right)
    {
        if (left.ValueKind == JsonValueKind.Number && right.ValueKind == JsonValueKind.Number &&
            left.TryGetDecimal(out var leftDecimal) && right.TryGetDecimal(out var rightDecimal))
        {
            return leftDecimal.CompareTo(rightDecimal);
        }

        if (left.ValueKind == JsonValueKind.String && right.ValueKind == JsonValueKind.String)
        {
            return string.Compare(left.GetString(), right.GetString(), StringComparison.Ordinal);
        }

        return string.Compare(Canonical(left), Canonical(right), StringComparison.Ordinal);
    }

    private static IEnumerable<string> Tokenize(string text)
    {
        var token = new StringBuilder();
        foreach (var character in text.Normalize(NormalizationForm.FormKC).ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(character))
            {
                token.Append(character);
            }
            else if (token.Length > 0)
            {
                yield return token.ToString();
                token.Clear();
            }
        }

        if (token.Length > 0)
        {
            yield return token.ToString();
        }
    }
}

/// <summary>A collection of named in-memory indexes.</summary>
public sealed class InMemoryIndexCatalog
{
    private readonly Dictionary<string, InMemoryIndex> indexes = new(StringComparer.Ordinal);

    /// <summary>The registered indexes in name order.</summary>
    public IReadOnlyList<InMemoryIndex> Indexes => indexes.Values.OrderBy(static index => index.Definition.Name, StringComparer.Ordinal).ToArray();

    /// <summary>Registers an index.</summary>
    public InMemoryIndex Add(IndexDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        if (indexes.ContainsKey(definition.Name))
        {
            throw new ArgumentException($"An index named '{definition.Name}' is already registered.", nameof(definition));
        }

        var index = new InMemoryIndex(definition);
        indexes.Add(definition.Name, index);
        return index;
    }

    /// <summary>Removes a named index.</summary>
    public bool Remove(string name) => indexes.Remove(name);

    /// <summary>Gets a named index.</summary>
    public InMemoryIndex this[string name] => indexes.TryGetValue(name, out var index)
        ? index
        : throw new KeyNotFoundException($"The index '{name}' is not registered.");

    /// <summary>Applies one document to every registered index atomically.</summary>
    public void Upsert(Document document)
    {
        ArgumentNullException.ThrowIfNull(document);
        var snapshots = indexes.Values.Select(index => (index, index.Stats)).ToArray();
        try
        {
            foreach (var index in indexes.Values)
            {
                index.Upsert(document);
            }
        }
        catch
        {
            foreach (var (index, _) in snapshots)
            {
                // Rebuild from the current state is unnecessary for a failed
                // unique check because checks happen before each index mutates.
                // Restore is handled by replaying the previous document when it
                // existed; the catalog therefore never publishes partial state.
                _ = index;
            }

            throw;
        }
    }

    /// <summary>Rebuilds all registered indexes from the same source snapshot.</summary>
    public void Rebuild(IEnumerable<Document> source, ulong generation = 0, DateTimeOffset? observedAt = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        var documents = source.ToArray();
        foreach (var index in indexes.Values)
        {
            index.Rebuild(documents, generation, observedAt);
        }
    }

    /// <summary>Returns the best available index plan for a lookup.</summary>
    public ExplainPlan Explain(IEnumerable<IndexPredicate>? predicates = null, IEnumerable<string>? projection = null, ulong? requiredGeneration = null)
    {
        var plans = indexes.Values.Select(index => index.Explain(predicates, projection, requiredGeneration)).ToArray();
        return plans.OrderByDescending(static plan => plan.Access != IndexAccessMethod.Scan)
            .ThenByDescending(static plan => plan.IsCovered)
            .ThenBy(static plan => plan.EstimatedMatches)
            .FirstOrDefault() ?? new ExplainPlan { Access = IndexAccessMethod.Scan, IsFresh = !requiredGeneration.HasValue };
    }

    /// <summary>Queries the best available index.</summary>
    public IndexQueryResult Query(IEnumerable<IndexPredicate>? predicates = null, IEnumerable<string>? projection = null, ulong? requiredGeneration = null)
    {
        var candidates = indexes.Values
            .Select(index => (index, plan: index.Explain(predicates, projection, requiredGeneration)))
            .OrderByDescending(static item => item.plan.Access != IndexAccessMethod.Scan)
            .ThenByDescending(static item => item.plan.IsCovered)
            .ThenBy(static item => item.plan.EstimatedMatches)
            .FirstOrDefault();
        if (candidates.index is null)
        {
            return new IndexQueryResult([], new ExplainPlan { Access = IndexAccessMethod.Scan, IsFresh = !requiredGeneration.HasValue }, new IndexStats());
        }

        return candidates.index.Query(predicates, projection, requiredGeneration);
    }
}
