using System.Text.Json;
using System.Text.Json.Serialization;

namespace TerraScale.DotNet;

/// <summary>Types supported by extracted TerraScale collection fields.</summary>
[JsonConverter(typeof(TerraScaleFieldTypeJsonConverter))]
public enum TerraScaleFieldType
{
    /// <summary>UTF-8 string.</summary>
    String = 0,
    /// <summary>64-bit integer.</summary>
    Int = 1,
    /// <summary>Alias for <see cref="Int" />.</summary>
    Integer = Int,
    /// <summary>Floating-point number.</summary>
    Float = 2,
    /// <summary>Boolean value.</summary>
    Bool = 3,
    /// <summary>Alias for <see cref="Bool" />.</summary>
    Boolean = Bool,
    /// <summary>Binary value.</summary>
    Bytes = 6,
    /// <summary>JSON value.</summary>
    Json = 4,
    /// <summary>Timestamp value.</summary>
    Timestamp = 7,
    /// <summary>Full-text value.</summary>
    Text = 5,
    /// <summary>PN-counter CRDT value.</summary>
    PnCounter = 8,
    /// <summary>Alias for <see cref="PnCounter" />.</summary>
    CounterPn = PnCounter,
    /// <summary>Add-wins set CRDT value.</summary>
    AddWinsSet = 9,
    /// <summary>Alias for <see cref="AddWinsSet" />.</summary>
    SetAddWins = AddWinsSet,
    /// <summary>Remove-wins set CRDT value.</summary>
    RemoveWinsSet = 10,
    /// <summary>Alias for <see cref="RemoveWinsSet" />.</summary>
    SetRemoveWins = RemoveWinsSet,
    /// <summary>Multi-value register CRDT value.</summary>
    MvRegister = 11,
    /// <summary>Alias for <see cref="MvRegister" />.</summary>
    RegisterMv = MvRegister,
}

/// <summary>Default conflict policy for fields without a typed CRDT kernel.</summary>
[JsonConverter(typeof(TerraScaleMergePolicyJsonConverter))]
public enum TerraScaleMergePolicy
{
    /// <summary>Concurrent removals win over assignments.</summary>
    RemoveWins,
    /// <summary>Concurrent assignments use HLC and origin ordering.</summary>
    HlcTiebreak,
    /// <summary>Alias for HLC tiebreak terminology.</summary>
    LastWriteWins,
}

/// <summary>Kind of an explicitly declared index.</summary>
[JsonConverter(typeof(TerraScaleIndexKindJsonConverter))]
public enum TerraScaleIndexKind
{
    /// <summary>Extracted-column value index.</summary>
    Value,
    /// <summary>FTS5 term index.</summary>
    FullText,
    /// <summary>Alias for <see cref="FullText" />.</summary>
    Text = FullText,
}

/// <summary>Options applied to a field while using the fluent collection builder.</summary>
public sealed class TerraScaleFieldOptions
{
    /// <summary>Whether the field is required in every document.</summary>
    public bool Required { get; private set; }

    /// <summary>Whether the write path maintains an index for the field.</summary>
    public bool Indexed { get; private set; }

    /// <summary>Whether values must be unique within a Region.</summary>
    public bool Unique { get; private set; }

    /// <summary>Optional typed CRDT kernel for the field.</summary>
    public TerraScaleFieldType? MergePolicy { get; private set; }

    /// <summary>Marks this field as required.</summary>
    public TerraScaleFieldOptions AsRequired()
    {
        Required = true;
        return this;
    }

    /// <summary>Marks this field as indexed.</summary>
    public TerraScaleFieldOptions AsIndexed()
    {
        Indexed = true;
        return this;
    }

    /// <summary>Marks this field as unique and indexed.</summary>
    public TerraScaleFieldOptions AsUnique()
    {
        Unique = true;
        Indexed = true;
        return this;
    }

    /// <summary>Chooses a typed CRDT kernel for this field.</summary>
    public TerraScaleFieldOptions Merge(TerraScaleFieldType policy)
    {
        MergePolicy = policy;
        return this;
    }

    // Fluent aliases keep the options pleasant to use in a lambda.
    /// <inheritdoc cref="AsRequired" />
    public TerraScaleFieldOptions RequiredField() => AsRequired();

    /// <inheritdoc cref="AsIndexed" />
    public TerraScaleFieldOptions IndexedField() => AsIndexed();

    /// <inheritdoc cref="AsUnique" />
    public TerraScaleFieldOptions UniqueField() => AsUnique();
}

/// <summary>One declared extracted field.</summary>
public sealed record TerraScaleFieldDefinition
{
    /// <summary>Creates a field declaration.</summary>
    [JsonConstructor]
    public TerraScaleFieldDefinition(
        string name,
        TerraScaleFieldType type,
        bool required = false,
        bool indexed = false,
        bool unique = false,
        TerraScaleFieldType? mergePolicy = null)
    {
        Name = name;
        Type = type;
        Required = required;
        Indexed = indexed || unique;
        Unique = unique;
        MergePolicy = mergePolicy;
    }

    /// <summary>Field name and root JSON path segment.</summary>
    [JsonPropertyName("name")]
    public string Name { get; init; }

    /// <summary>Stored type used for extraction and typed queries.</summary>
    [JsonPropertyName("type")]
    public TerraScaleFieldType Type { get; init; }

    /// <summary>Whether the field must be present and non-null.</summary>
    [JsonPropertyName("required")]
    public bool Required { get; init; }

    /// <summary>Whether the write path maintains an index over this field.</summary>
    [JsonPropertyName("indexed")]
    public bool Indexed { get; init; }

    /// <summary>Whether this field is unique within a Region.</summary>
    [JsonPropertyName("unique")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool Unique { get; init; }

    /// <summary>Optional typed CRDT merge kernel.</summary>
    [JsonPropertyName("mergePolicy")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public TerraScaleFieldType? MergePolicy { get; init; }

    /// <summary>Alias matching the Rust and schema-document terminology.</summary>
    [JsonIgnore]
    public TerraScaleFieldType FieldType => Type;

    /// <summary>Inverse of <see cref="Required" />, for SDK parity.</summary>
    [JsonIgnore]
    public bool Optional => !Required;

    /// <summary>Creates a required field.</summary>
    public static TerraScaleFieldDefinition RequiredField(string name, TerraScaleFieldType type) =>
        new(name, type, required: true);

    /// <summary>Creates an optional field.</summary>
    public static TerraScaleFieldDefinition OptionalField(string name, TerraScaleFieldType type) =>
        new(name, type);

    /// <summary>Marks this field as indexed.</summary>
    public TerraScaleFieldDefinition AsIndexed() => this with { Indexed = true };

    /// <summary>Marks this field as unique and indexed.</summary>
    public TerraScaleFieldDefinition AsUnique() => this with { Unique = true, Indexed = true };

    /// <summary>Chooses a typed CRDT kernel for this field.</summary>
    public TerraScaleFieldDefinition Merge(TerraScaleFieldType policy) => this with { MergePolicy = policy };
}

/// <summary>One ordinary or full-text index declaration.</summary>
public sealed record TerraScaleIndexDefinition
{
    /// <summary>Creates an index over one or more declared fields.</summary>
    [JsonConstructor]
    public TerraScaleIndexDefinition(
        string name,
        IReadOnlyList<string> fields,
        bool unique = false,
        TerraScaleIndexKind? kind = null)
    {
        Name = name;
        Fields = fields ?? throw new ArgumentNullException(nameof(fields));
        Unique = unique;
        Kind = kind;
    }

    /// <summary>Stable name used by schema diffs and query plans.</summary>
    [JsonPropertyName("name")]
    public string Name { get; init; }

    /// <summary>Ordered field names in the index key.</summary>
    [JsonPropertyName("fields")]
    public IReadOnlyList<string> Fields { get; init; }

    /// <summary>Whether duplicate keys are rejected within a Region.</summary>
    [JsonPropertyName("unique")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool Unique { get; init; }

    /// <summary>Index kind; omitted for an ordinary value index.</summary>
    [JsonPropertyName("kind")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public TerraScaleIndexKind? Kind { get; init; }

    /// <summary>Creates an ordinary index whose name is the field name.</summary>
    public static TerraScaleIndexDefinition On(string field) => new(field, [field]);

    /// <summary>Creates a full-text index over one or more text fields.</summary>
    public static TerraScaleIndexDefinition FullText(string name, params string[] fields) =>
        new(name, fields, kind: TerraScaleIndexKind.FullText);

    /// <summary>Creates a full-text index named after a single field.</summary>
    public static TerraScaleIndexDefinition FullTextOn(string field) =>
        FullText(field, field);

    /// <summary>Marks this value index as unique.</summary>
    public TerraScaleIndexDefinition AsUnique() => this with { Unique = true };
}

/// <summary>Collection TTL declaration.</summary>
public sealed record TerraScaleTtlDefinition
{
    /// <summary>Creates a TTL declaration for a timestamp field.</summary>
    [JsonConstructor]
    public TerraScaleTtlDefinition(string field) => Field = field;

    /// <summary>Root field containing the expiry timestamp.</summary>
    [JsonPropertyName("field")]
    public string Field { get; init; }
}

/// <summary>Schema for one SQL collection/table.</summary>
public sealed class TerraScaleCollectionSchema
{
    /// <summary>The default retained history window required by D-25.</summary>
    public const int DefaultHistoryRetentionDays = 7;

    /// <summary>The minimum retained history window.</summary>
    public const int MinHistoryRetentionDays = 1;

    /// <summary>The maximum retained history window.</summary>
    public const int MaxHistoryRetentionDays = 30;

    /// <summary>Creates a collection schema.</summary>
    [JsonConstructor]
    public TerraScaleCollectionSchema(
        string name,
        IReadOnlyList<TerraScaleFieldDefinition>? fields = null,
        IReadOnlyList<TerraScaleIndexDefinition>? indexes = null,
        TerraScaleTtlDefinition? ttl = null,
        int historyRetentionDays = DefaultHistoryRetentionDays,
        TerraScaleMergePolicy mergePolicy = TerraScaleMergePolicy.RemoveWins)
    {
        Name = name;
        Fields = fields ?? [];
        Indexes = indexes ?? [];
        Ttl = ttl;
        HistoryRetentionDays = historyRetentionDays;
        MergePolicy = mergePolicy;
    }

    /// <summary>Collection/table name.</summary>
    [JsonPropertyName("name")]
    public string Name { get; }

    /// <summary>Declared extracted fields.</summary>
    [JsonPropertyName("fields")]
    public IReadOnlyList<TerraScaleFieldDefinition> Fields { get; }

    /// <summary>Explicit value and full-text indexes.</summary>
    [JsonPropertyName("indexes")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public IReadOnlyList<TerraScaleIndexDefinition> Indexes { get; }

    /// <summary>Optional expiry policy.</summary>
    [JsonPropertyName("ttl")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public TerraScaleTtlDefinition? Ttl { get; }

    /// <summary>Retained document history in days.</summary>
    [JsonPropertyName("historyRetentionDays")]
    public int HistoryRetentionDays { get; }

    /// <summary>Default policy for fields without a typed CRDT kernel.</summary>
    [JsonPropertyName("mergePolicy")]
    public TerraScaleMergePolicy MergePolicy { get; }

    /// <summary>Alias for <see cref="Ttl" />.</summary>
    [JsonIgnore]
    public TerraScaleTtlDefinition? TtlField => Ttl;

    /// <summary>Alias for <see cref="HistoryRetentionDays" />.</summary>
    [JsonIgnore]
    public int HistoryWindowDays => HistoryRetentionDays;

    /// <summary>Short alias for <see cref="HistoryRetentionDays" />.</summary>
    [JsonIgnore]
    public int HistoryWindow => HistoryRetentionDays;

    /// <summary>Alias for <see cref="MergePolicy" />.</summary>
    [JsonIgnore]
    public TerraScaleMergePolicy Policy => MergePolicy;

    /// <summary>Validates names, references, type policies, and retention bounds.</summary>
    public void Validate()
    {
        TerraScaleSchemaValidation.ValidateCollection(this);
    }

    /// <summary>Serializes this collection after validation.</summary>
    public string ToJson()
    {
        Validate();
        return TerraScaleSchemaJson.Serialize(this);
    }
}

/// <summary>Root <c>terrascale.schema.json</c> document.</summary>
public sealed class TerraScaleSchema
{
    /// <summary>Starts a fluent schema declaration.</summary>
    public static TerraScaleSchemaBuilder Define() => new();

    /// <summary>Creates a schema document from collections.</summary>
    [JsonConstructor]
    public TerraScaleSchema(IReadOnlyList<TerraScaleCollectionSchema>? collections = null) =>
        Collections = collections ?? [];

    /// <summary>Declared collections in authoring order.</summary>
    [JsonPropertyName("collections")]
    public IReadOnlyList<TerraScaleCollectionSchema> Collections { get; }

    /// <summary>Serializes this validated schema deterministically.</summary>
    public string ToJson()
    {
        TerraScaleSchemaValidation.ValidateSchema(this);
        return TerraScaleSchemaJson.Serialize(this);
    }

    /// <summary>Parses a schema document and validates it.</summary>
    public static TerraScaleSchema Parse(string json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);
        var schema = JsonSerializer.Deserialize<TerraScaleSchema>(json, TerraScaleSchemaJson.Options)
            ?? throw new ArgumentException("Schema JSON must contain a schema document.", nameof(json));
        TerraScaleSchemaValidation.ValidateSchema(schema);
        return schema;
    }
}

/// <summary>Fluent builder for a complete TerraScale schema document.</summary>
public sealed class TerraScaleSchemaBuilder
{
    private readonly List<TerraScaleCollectionSchema> collections = [];

    /// <summary>Starts an empty schema declaration.</summary>
    public static TerraScaleSchemaBuilder Create() => new();

    /// <summary>Alias for <see cref="Create" />.</summary>
    public static TerraScaleSchemaBuilder New() => Create();

    /// <summary>Adds a previously built collection.</summary>
    public TerraScaleSchemaBuilder AddCollection(TerraScaleCollectionSchema collection)
    {
        ArgumentNullException.ThrowIfNull(collection);
        collections.Add(collection);
        return this;
    }

    /// <summary>Alias for <see cref="AddCollection(TerraScaleCollectionSchema)" />.</summary>
    public TerraScaleSchemaBuilder Collection(TerraScaleCollectionSchema collection) => AddCollection(collection);

    /// <summary>Builds and adds a collection through a configuration callback.</summary>
    public TerraScaleSchemaBuilder Collection(string name, Action<TerraScaleCollectionBuilder>? configure = null)
    {
        var builder = new TerraScaleCollectionBuilder(name);
        configure?.Invoke(builder);
        return AddCollection(builder.Build());
    }

    /// <summary>Builds and adds a collection through a configuration callback.</summary>
    public TerraScaleSchemaBuilder AddCollection(string name, Action<TerraScaleCollectionBuilder>? configure = null) =>
        Collection(name, configure);

    /// <summary>Builds and adds a collection builder.</summary>
    public TerraScaleSchemaBuilder Add(TerraScaleCollectionBuilder collection)
    {
        ArgumentNullException.ThrowIfNull(collection);
        return AddCollection(collection.Build());
    }

    /// <summary>Validates and returns the complete schema document.</summary>
    public TerraScaleSchema Build()
    {
        var schema = new TerraScaleSchema(collections.ToArray());
        TerraScaleSchemaValidation.ValidateSchema(schema);
        return schema;
    }

    /// <summary>Validates and emits compact deterministic schema JSON.</summary>
    public string ToJson() => Build().ToJson();
}

/// <summary>Fluent builder for one TerraScale collection.</summary>
public sealed class TerraScaleCollectionBuilder
{
    private readonly string name;
    private readonly List<TerraScaleFieldDefinition> fields = [];
    private readonly List<TerraScaleIndexDefinition> indexes = [];
    private TerraScaleTtlDefinition? ttl;
    private int historyRetentionDays = TerraScaleCollectionSchema.DefaultHistoryRetentionDays;
    private TerraScaleMergePolicy mergePolicy = TerraScaleMergePolicy.RemoveWins;

    /// <summary>Starts a collection declaration.</summary>
    public TerraScaleCollectionBuilder(string name) => this.name = name?.Trim() ?? throw new ArgumentNullException(nameof(name));

    /// <summary>Starts a collection declaration.</summary>
    public static TerraScaleCollectionBuilder Create(string name) => new(name);

    /// <summary>Alias for <see cref="Create" />.</summary>
    public static TerraScaleCollectionBuilder New(string name) => Create(name);

    /// <summary>Adds a field declaration.</summary>
    public TerraScaleCollectionBuilder Field(
        string fieldName,
        TerraScaleFieldType type,
        bool required = false,
        bool indexed = false,
        bool unique = false,
        TerraScaleFieldType? mergePolicy = null) =>
        AddField(new TerraScaleFieldDefinition(TrimRequired(fieldName, nameof(fieldName)), type, required, indexed, unique, mergePolicy));

    /// <summary>Adds a field configured by a callback.</summary>
    public TerraScaleCollectionBuilder Field(string fieldName, TerraScaleFieldType type, Action<TerraScaleFieldOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        var options = new TerraScaleFieldOptions();
        configure(options);
        return Field(fieldName, type, options.Required, options.Indexed, options.Unique, options.MergePolicy);
    }

    /// <summary>Adds an already constructed field.</summary>
    public TerraScaleCollectionBuilder AddField(TerraScaleFieldDefinition field)
    {
        ArgumentNullException.ThrowIfNull(field);
        fields.Add(field);
        return this;
    }

    /// <summary>Adds a required field.</summary>
    public TerraScaleCollectionBuilder RequiredField(string fieldName, TerraScaleFieldType type, bool indexed = false) =>
        Field(fieldName, type, required: true, indexed: indexed);

    /// <summary>Adds an optional field.</summary>
    public TerraScaleCollectionBuilder OptionalField(string fieldName, TerraScaleFieldType type, bool indexed = false) =>
        Field(fieldName, type, indexed: indexed);

    /// <summary>Adds a string field.</summary>
    public TerraScaleCollectionBuilder String(string fieldName, bool required = false, bool indexed = false, bool unique = false) =>
        Field(fieldName, TerraScaleFieldType.String, required, indexed, unique);

    /// <summary>Adds an integer field.</summary>
    public TerraScaleCollectionBuilder Int(string fieldName, bool required = false, bool indexed = false, bool unique = false) =>
        Field(fieldName, TerraScaleFieldType.Int, required, indexed, unique);

    /// <summary>Adds a floating-point field.</summary>
    public TerraScaleCollectionBuilder Float(string fieldName, bool required = false, bool indexed = false, bool unique = false) =>
        Field(fieldName, TerraScaleFieldType.Float, required, indexed, unique);

    /// <summary>Adds a boolean field.</summary>
    public TerraScaleCollectionBuilder Bool(string fieldName, bool required = false, bool indexed = false, bool unique = false) =>
        Field(fieldName, TerraScaleFieldType.Bool, required, indexed, unique);

    /// <summary>Adds a JSON field.</summary>
    public TerraScaleCollectionBuilder Json(string fieldName, bool required = false, bool indexed = false) =>
        Field(fieldName, TerraScaleFieldType.Json, required, indexed);

    /// <summary>Adds a binary field.</summary>
    public TerraScaleCollectionBuilder Bytes(string fieldName, bool required = false, bool indexed = false) =>
        Field(fieldName, TerraScaleFieldType.Bytes, required, indexed);

    /// <summary>Adds a timestamp field.</summary>
    public TerraScaleCollectionBuilder Timestamp(string fieldName, bool required = false, bool indexed = false) =>
        Field(fieldName, TerraScaleFieldType.Timestamp, required, indexed);

    /// <summary>Adds a full-text field.</summary>
    public TerraScaleCollectionBuilder Text(string fieldName, bool required = false, bool indexed = false, bool fullText = false)
    {
        Field(fieldName, TerraScaleFieldType.Text, required, indexed);
        return fullText ? FullText(fieldName) : this;
    }

    /// <summary>Adds a PN-counter CRDT field.</summary>
    public TerraScaleCollectionBuilder PnCounter(string fieldName, bool required = false, bool indexed = false) =>
        Field(fieldName, TerraScaleFieldType.PnCounter, required, indexed);

    /// <summary>Adds an add-wins set CRDT field.</summary>
    public TerraScaleCollectionBuilder AddWinsSet(string fieldName, bool required = false, bool indexed = false) =>
        Field(fieldName, TerraScaleFieldType.AddWinsSet, required, indexed);

    /// <summary>Adds a remove-wins set CRDT field.</summary>
    public TerraScaleCollectionBuilder RemoveWinsSet(string fieldName, bool required = false, bool indexed = false) =>
        Field(fieldName, TerraScaleFieldType.RemoveWinsSet, required, indexed);

    /// <summary>Adds a multi-value register CRDT field.</summary>
    public TerraScaleCollectionBuilder MvRegister(string fieldName, bool required = false, bool indexed = false) =>
        Field(fieldName, TerraScaleFieldType.MvRegister, required, indexed);

    /// <summary>Declares an ordinary index over one field.</summary>
    public TerraScaleCollectionBuilder Index(string fieldName) => AddIndex(TerraScaleIndexDefinition.On(TrimRequired(fieldName, nameof(fieldName))));

    /// <summary>Declares an ordinary compound index.</summary>
    public TerraScaleCollectionBuilder Index(string indexName, params string[] fieldNames) =>
        AddIndex(new TerraScaleIndexDefinition(TrimRequired(indexName, nameof(indexName)), fieldNames.Select(field => TrimRequired(field, nameof(fieldNames))).ToArray()));

    /// <summary>Adds an index declaration.</summary>
    public TerraScaleCollectionBuilder AddIndex(TerraScaleIndexDefinition index)
    {
        ArgumentNullException.ThrowIfNull(index);
        indexes.Add(index);
        return this;
    }

    /// <summary>Adds an index declaration.</summary>
    public TerraScaleCollectionBuilder Index(TerraScaleIndexDefinition index) => AddIndex(index);

    /// <summary>Declares a full-text index named after one field.</summary>
    public TerraScaleCollectionBuilder FullText(string fieldName) =>
        AddIndex(TerraScaleIndexDefinition.FullTextOn(TrimRequired(fieldName, nameof(fieldName))));

    /// <summary>Declares a named full-text index.</summary>
    public TerraScaleCollectionBuilder FullText(string indexName, params string[] fieldNames) =>
        AddIndex(TerraScaleIndexDefinition.FullText(TrimRequired(indexName, nameof(indexName)), fieldNames.Select(field => TrimRequired(field, nameof(fieldNames))).ToArray()));

    /// <summary>Alias for <see cref="FullText(string)" />.</summary>
    public TerraScaleCollectionBuilder FullTextIndex(string fieldName) => FullText(fieldName);

    /// <summary>Declares a named full-text index.</summary>
    public TerraScaleCollectionBuilder FullTextIndex(string indexName, params string[] fieldNames) =>
        FullText(indexName, fieldNames);

    /// <summary>Configures the collection TTL timestamp field.</summary>
    public TerraScaleCollectionBuilder Ttl(string fieldName)
    {
        ttl = new TerraScaleTtlDefinition(TrimRequired(fieldName, nameof(fieldName)));
        return this;
    }

    /// <summary>Alias for <see cref="Ttl(string)" />.</summary>
    public TerraScaleCollectionBuilder TTL(string fieldName) => Ttl(fieldName);

    /// <summary>Alias for <see cref="Ttl(string)" />.</summary>
    public TerraScaleCollectionBuilder TtlField(string fieldName) => Ttl(fieldName);

    /// <summary>Configures the collection TTL declaration.</summary>
    public TerraScaleCollectionBuilder Ttl(TerraScaleTtlDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ttl = definition;
        return this;
    }

    /// <summary>Sets retained document history in days.</summary>
    public TerraScaleCollectionBuilder HistoryRetentionDays(int days)
    {
        historyRetentionDays = days;
        return this;
    }

    /// <summary>Alias for <see cref="HistoryRetentionDays" />.</summary>
    public TerraScaleCollectionBuilder HistoryRetention(int days) => HistoryRetentionDays(days);

    /// <summary>Alias for <see cref="HistoryRetentionDays" />.</summary>
    public TerraScaleCollectionBuilder HistoryWindowDays(int days) => HistoryRetentionDays(days);

    /// <summary>Sets the default conflict policy for untyped fields.</summary>
    public TerraScaleCollectionBuilder MergePolicy(TerraScaleMergePolicy policy)
    {
        mergePolicy = policy;
        return this;
    }

    /// <summary>Alias for <see cref="MergePolicy" />.</summary>
    public TerraScaleCollectionBuilder Merge(TerraScaleMergePolicy policy) => MergePolicy(policy);

    /// <summary>Validates and returns this collection schema.</summary>
    public TerraScaleCollectionSchema Build()
    {
        var collection = new TerraScaleCollectionSchema(name, fields.ToArray(), indexes.ToArray(), ttl, historyRetentionDays, mergePolicy);
        collection.Validate();
        return collection;
    }

    /// <summary>Validates and emits this collection's JSON.</summary>
    public string ToJson() => Build().ToJson();

    private static string TrimRequired(string value, string parameterName)
    {
        ArgumentNullException.ThrowIfNull(value, parameterName);
        return value.Trim();
    }

}

internal static class TerraScaleSchemaValidation
{
    public static void ValidateSchema(TerraScaleSchema schema)
    {
        ArgumentNullException.ThrowIfNull(schema);
        var collectionNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (var collection in schema.Collections)
        {
            ArgumentNullException.ThrowIfNull(collection);
            ValidateCollection(collection);
            if (!collectionNames.Add(collection.Name))
            {
                throw new ArgumentException($"Duplicate collection {collection.Name}.", nameof(schema));
            }
        }
    }

    public static void ValidateCollection(TerraScaleCollectionSchema collection)
    {
        if (!IsIdentifier(collection.Name))
        {
            throw new ArgumentException($"Collection name {collection.Name ?? "<null>"} must match [A-Za-z_][A-Za-z0-9_]*.", nameof(collection));
        }

        if (collection.HistoryRetentionDays is < TerraScaleCollectionSchema.MinHistoryRetentionDays or > TerraScaleCollectionSchema.MaxHistoryRetentionDays)
        {
            throw new ArgumentOutOfRangeException(nameof(collection), collection.HistoryRetentionDays,
                "History retention must be between 1 and 30 days.");
        }

        if (!Enum.IsDefined(collection.MergePolicy))
        {
            throw new ArgumentException("Collection has an unknown merge policy.", nameof(collection));
        }

        var fieldNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (var field in collection.Fields)
        {
            ArgumentNullException.ThrowIfNull(field);
            if (!IsIdentifier(field.Name))
            {
                throw new ArgumentException($"Field name {field.Name ?? "<null>"} must match [A-Za-z_][A-Za-z0-9_]*.", nameof(collection));
            }

            if (!fieldNames.Add(field.Name))
            {
                throw new ArgumentException($"Duplicate field {field.Name}.", nameof(collection));
            }

            if (!Enum.IsDefined(field.Type))
            {
                throw new ArgumentException($"Field {field.Name} has an unknown field type.", nameof(collection));
            }

            if (field.MergePolicy is not null && !IsCrdt(field.MergePolicy.Value))
            {
                throw new ArgumentException($"Field {field.Name} has a non-CRDT merge policy.", nameof(collection));
            }
        }

        var indexNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (var index in collection.Indexes)
        {
            ArgumentNullException.ThrowIfNull(index);
            if (!IsIdentifier(index.Name) || index.Fields.Count == 0)
            {
                throw new ArgumentException($"Index {index.Name ?? "<null>"} must have a valid name and at least one field.", nameof(collection));
            }

            if (index.Kind is not null && !Enum.IsDefined(index.Kind.Value))
            {
                throw new ArgumentException($"Index {index.Name} has an unknown index kind.", nameof(collection));
            }

            if (index.Kind == TerraScaleIndexKind.FullText && index.Unique)
            {
                throw new ArgumentException($"Full-text index {index.Name} cannot be unique.", nameof(collection));
            }

            if (!indexNames.Add(index.Name))
            {
                throw new ArgumentException($"Duplicate index {index.Name}.", nameof(collection));
            }

            foreach (var fieldName in index.Fields)
            {
                if (!IsIdentifier(fieldName) || !fieldNames.Contains(fieldName))
                {
                    throw new ArgumentException($"Index {index.Name} references unknown or invalid field {fieldName}.", nameof(collection));
                }

                if (index.Kind == TerraScaleIndexKind.FullText)
                {
                    var field = collection.Fields.First(declared => declared.Name == fieldName);
                    if (field.Type is not (TerraScaleFieldType.String or TerraScaleFieldType.Text))
                    {
                        throw new ArgumentException($"Full-text index {index.Name} requires string or text fields.", nameof(collection));
                    }
                }
            }
        }

        if (collection.Ttl is not null)
        {
            var ttlField = collection.Ttl.Field;
            if (!IsIdentifier(ttlField) || !fieldNames.Contains(ttlField))
            {
                throw new ArgumentException($"TTL references unknown field {ttlField}.", nameof(collection));
            }

            var field = collection.Fields.First(declared => declared.Name == ttlField);
            if (field.Type != TerraScaleFieldType.Timestamp)
            {
                throw new ArgumentException($"TTL field {ttlField} must have type timestamp.", nameof(collection));
            }
        }
    }

    private static bool IsCrdt(TerraScaleFieldType type) => type is
        TerraScaleFieldType.PnCounter or TerraScaleFieldType.AddWinsSet or
        TerraScaleFieldType.RemoveWinsSet or TerraScaleFieldType.MvRegister;

    private static bool IsIdentifier(string? value)
    {
        if (string.IsNullOrEmpty(value) || value.Length > 255 || !(char.IsAsciiLetter(value[0]) || value[0] == '_'))
        {
            return false;
        }

        return value.Skip(1).All(character => char.IsAsciiLetterOrDigit(character) || character == '_');
    }
}

internal static class TerraScaleSchemaJson
{
    internal static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    internal static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Options);
}

internal sealed class TerraScaleFieldTypeJsonConverter : JsonConverter<TerraScaleFieldType>
{
    public override TerraScaleFieldType Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        reader.GetString() switch
        {
            "string" => TerraScaleFieldType.String,
            "int" or "integer" => TerraScaleFieldType.Int,
            "float" => TerraScaleFieldType.Float,
            "bool" or "boolean" => TerraScaleFieldType.Bool,
            "bytes" => TerraScaleFieldType.Bytes,
            "json" => TerraScaleFieldType.Json,
            "timestamp" => TerraScaleFieldType.Timestamp,
            "text" => TerraScaleFieldType.Text,
            "counter-pn" or "pn-counter" => TerraScaleFieldType.PnCounter,
            "set-add-wins" or "add-wins-set" => TerraScaleFieldType.AddWinsSet,
            "set-remove-wins" or "remove-wins-set" => TerraScaleFieldType.RemoveWinsSet,
            "register-mv" or "mv-register" => TerraScaleFieldType.MvRegister,
            var value => throw new JsonException($"Unknown TerraScale field type {value ?? "<null>"}.")
        };

    public override void Write(Utf8JsonWriter writer, TerraScaleFieldType value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value switch
        {
            TerraScaleFieldType.String => "string",
            TerraScaleFieldType.Int => "int",
            TerraScaleFieldType.Float => "float",
            TerraScaleFieldType.Bool => "bool",
            TerraScaleFieldType.Bytes => "bytes",
            TerraScaleFieldType.Json => "json",
            TerraScaleFieldType.Timestamp => "timestamp",
            TerraScaleFieldType.Text => "text",
            TerraScaleFieldType.PnCounter => "counter-pn",
            TerraScaleFieldType.AddWinsSet => "set-add-wins",
            TerraScaleFieldType.RemoveWinsSet => "set-remove-wins",
            TerraScaleFieldType.MvRegister => "register-mv",
            _ => throw new JsonException($"Unknown TerraScale field type {value}.")
        });
}

internal sealed class TerraScaleMergePolicyJsonConverter : JsonConverter<TerraScaleMergePolicy>
{
    public override TerraScaleMergePolicy Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        reader.GetString() switch
        {
            "remove-wins" => TerraScaleMergePolicy.RemoveWins,
            "hlc-tiebreak" => TerraScaleMergePolicy.HlcTiebreak,
            "last-write-wins" => TerraScaleMergePolicy.LastWriteWins,
            var value => throw new JsonException($"Unknown TerraScale merge policy {value ?? "<null>"}.")
        };

    public override void Write(Utf8JsonWriter writer, TerraScaleMergePolicy value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value switch
        {
            TerraScaleMergePolicy.RemoveWins => "remove-wins",
            TerraScaleMergePolicy.HlcTiebreak => "hlc-tiebreak",
            TerraScaleMergePolicy.LastWriteWins => "last-write-wins",
            _ => throw new JsonException($"Unknown TerraScale merge policy {value}.")
        });
}

internal sealed class TerraScaleIndexKindJsonConverter : JsonConverter<TerraScaleIndexKind>
{
    public override TerraScaleIndexKind Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        reader.GetString() switch
        {
            "value" => TerraScaleIndexKind.Value,
            "full-text" or "text" or "fts" => TerraScaleIndexKind.FullText,
            var value => throw new JsonException($"Unknown TerraScale index kind {value ?? "<null>"}.")
        };

    public override void Write(Utf8JsonWriter writer, TerraScaleIndexKind value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value switch
        {
            TerraScaleIndexKind.Value => "value",
            TerraScaleIndexKind.FullText => "full-text",
            _ => throw new JsonException($"Unknown TerraScale index kind {value}.")
        });
}
