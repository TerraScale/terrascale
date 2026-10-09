using System.Text.Json.Serialization;

namespace TerraScale.DotNet.TerraBase;

/// <summary>Replaces an empty collection's uniqueness declarations. Identical declarations are idempotent.</summary>
/// <param name="Fields">Single-field declarations; at most 128 input fields.</param>
/// <param name="Indexes">Compound declarations; at most 32 unique keys in total.</param>
public sealed record TerraBaseUniqueSchemaRequest(
    IReadOnlyList<TerraBaseUniqueSchemaField>? Fields = null,
    IReadOnlyList<TerraBaseUniqueSchemaIndex>? Indexes = null);

/// <summary>One single-field declaration.</summary>
/// <param name="Name">Dotted member path; metadata fields cannot be unique keys.</param>
/// <param name="Unique">Whether to enforce uniqueness.</param>
/// <param name="Normalization"><c>none</c> (default), <c>lowercase</c>, or <c>trim_lowercase</c>; applies to string key components, not stored values.</param>
public sealed record TerraBaseUniqueSchemaField(string Name, bool Unique = false, string? Normalization = null);

/// <summary>One compound declaration. Missing or null components claim no key; arrays and objects are invalid.</summary>
/// <param name="Name">Lowercase constraint name, following collection-name rules.</param>
/// <param name="Fields">One to four distinct dotted member paths.</param>
/// <param name="Unique">Whether to enforce uniqueness.</param>
/// <param name="Normalization"><c>none</c> (default), invariant <c>lowercase</c>, or <c>trim_lowercase</c> (Unicode-whitespace trimming followed by invariant lowercasing).</param>
public sealed record TerraBaseUniqueSchemaIndex(string Name, IReadOnlyList<string> Fields, bool Unique = false, string? Normalization = null);

/// <summary>A persisted unique scalar or compound key. Key encoding is bounded to 16 KiB per key and 64 KiB per document.</summary>
/// <param name="Name">Stable constraint name.</param>
/// <param name="Fields">One to four dotted member paths.</param>
/// <param name="Normalization"><c>none</c>, <c>lowercase</c>, or <c>trim_lowercase</c>.</param>
public sealed record TerraBaseUniqueConstraint(string Name, IReadOnlyList<string> Fields, string Normalization) : IJsonOnDeserialized
{
    void IJsonOnDeserialized.OnDeserialized() => TerraBaseJsonContext.RequireNonNullItems(Fields);
}

/// <summary>The persisted declaration returned by schema PUT.</summary>
/// <param name="Constraints">Declared unique keys, in name order.</param>
public sealed record TerraBaseUniqueSchema(IReadOnlyList<TerraBaseUniqueConstraint> Constraints) : IJsonOnDeserialized
{
    void IJsonOnDeserialized.OnDeserialized() => TerraBaseJsonContext.RequireNonNullItems(Constraints);
}

/// <summary>Declared unique keys alongside fields inferred from up to 1000 live documents and 8 MiB of JSON.</summary>
/// <param name="Collection">Collection name.</param>
/// <param name="Documents">Live document count.</param>
/// <param name="SampledDocuments">Number of documents included in the schema sample.</param>
/// <param name="Fields">Inferred fields, in path order; arrays are not descended into.</param>
/// <param name="UniqueConstraints">Declared unique keys.</param>
public sealed record TerraBaseCollectionSchema(
    string Collection, long Documents, int SampledDocuments,
    IReadOnlyList<TerraBaseSchemaField> Fields,
    IReadOnlyList<TerraBaseUniqueConstraint>? UniqueConstraints = null) : IJsonOnDeserialized
{
    void IJsonOnDeserialized.OnDeserialized()
    {
        TerraBaseJsonContext.RequireNonNullItems(Fields);
        if (UniqueConstraints is not null) TerraBaseJsonContext.RequireNonNullItems(UniqueConstraints);
    }
}

/// <summary>One field observed in the schema sample.</summary>
/// <param name="Path">Dotted object-member path.</param>
/// <param name="Present">Number of sampled documents containing the path.</param>
/// <param name="Types">Counts by JSON type: string, integer, number, boolean, null, object, or array.</param>
public sealed record TerraBaseSchemaField(string Path, int Present, IReadOnlyDictionary<string, int> Types);
