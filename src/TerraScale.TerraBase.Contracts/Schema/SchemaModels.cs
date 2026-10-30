using System.Text.Json;
using System.Text.Json.Serialization;
using MessagePack;

namespace TerraScale.TerraBase.Contracts.Schema
{
    /// <summary>Fields inferred from a bounded sample, with declared unique keys.</summary>
    [MessagePackObject(true)]
    public sealed record TerraBaseCollectionSchema(string Collection, long Documents, int SampledDocuments,
        IReadOnlyList<TerraBaseSchemaField> Fields, IReadOnlyList<TerraBaseUniqueConstraint>? UniqueConstraints = null);

    /// <summary>A sampled field and its observed JSON types.</summary>
    [MessagePackObject(true)]
    public sealed record TerraBaseSchemaField(string Path, int Present, IReadOnlyDictionary<string, int> Types);

    /// <summary>A declared unique key.</summary>
    [MessagePackObject(true)]
    public sealed record TerraBaseUniqueConstraint(string Name, IReadOnlyList<string> Fields, string Normalization);

    /// <summary>The declared JSON Schema, absent when detached.</summary>
    [MessagePackObject(true)]
    public sealed record TerraBaseDeclaredSchema(JsonElement? Schema, string? Dialect = null);

    /// <summary>A bounded preview page of declared validation against existing documents.</summary>
    [MessagePackObject(true)]
    public sealed record TerraBaseSchemaPreview(int Examined, int Valid, int Invalid, IReadOnlyList<TerraBaseSchemaDiagnostic> Diagnostics,
        [property: JsonPropertyName("schema_hash")] string SchemaHash, string Frontier, bool Complete,
        [property: JsonPropertyName("next_cursor")] string? NextCursor);

    /// <summary>A preview diagnostic with hashed document identity and field path.</summary>
    [MessagePackObject(true)]
    public sealed record TerraBaseSchemaDiagnostic([property: JsonPropertyName("id_hash")] string IdHash,
        [property: JsonPropertyName("path_hash")] string PathHash, string Keyword);
}
